using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocalProjectBridge.Core.Gateway;

namespace LocalProjectBridge.Core.Collaboration;

public enum CollaborationStatus
{
    Pending,
    Read,
    Answered,
    Cancelled,
    Expired
}

public enum CollaborationReplyOrigin
{
    None,
    McpTool,
    BrowserVisible
}

public enum CollaborationKind
{
    General,
    Plan,
    Review
}

public enum CollaborationDeliveryState
{
    NotStarted,
    IntentRecorded,
    MessageConfirmed
}

public enum CollaborationCheckpoint
{
    None,
    Init,
    PlanReceived,
    Executing,
    ExecutedLocal,
    ExecutedSent,
    Done,
    Blocked
}

public sealed record ConversationBinding
{
    public required Guid ConnectionId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Source { get; init; }
    public required string ConversationUrl { get; init; }
    public required Guid ConfirmedByRequestId { get; init; }
    public required DateTimeOffset ConfirmedAt { get; init; }
}

/// <summary>一条由本机 Codex 发起、由已绑定网页客户端显式读取的协作请求。</summary>
public sealed record CollaborationRequest
{
    public required Guid RequestId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string ProjectRoot { get; init; }
    public required Guid ConnectionId { get; init; }
    public required string Source { get; init; }
    public required string Question { get; init; }
    public string? ConversationUrl { get; init; }
    public required string AccessCode { get; init; }
    public string? ClientId { get; init; }
    public string? Reply { get; init; }
    public CollaborationReplyOrigin ReplyOrigin { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required CollaborationStatus Status { get; init; }
    public CollaborationKind Kind { get; init; }
    public CollaborationDeliveryState DeliveryState { get; init; }
    public CollaborationCheckpoint Checkpoint { get; init; }
    public string TurnId { get; init; } = string.Empty;
    public Guid? ParentRequestId { get; init; }
    public int Iteration { get; init; }
    public int MaxIterations { get; init; } = 4;
    public DateTimeOffset? SendIntentAt { get; init; }
    public DateTimeOffset? MessageConfirmedAt { get; init; }
    public DateTimeOffset? ConversationVerifiedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? CheckpointNote { get; init; }

    /// <summary>供本机界面列表使用；不会包含访问码或本地路径。</summary>
    [JsonIgnore]
    public string DisplaySummary
    {
        get
        {
            var text = Question.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 80) text = text[..80] + "…";
            return $"协作请求（{StatusText(Status)}）：{text}";
        }
    }

    private static string StatusText(CollaborationStatus status) => status switch
    {
        CollaborationStatus.Pending => "等待读取",
        CollaborationStatus.Read => "已读取",
        CollaborationStatus.Answered => "已回复",
        CollaborationStatus.Cancelled => "已取消",
        CollaborationStatus.Expired => "已过期",
        _ => "未知"
    };
}

/// <summary>可安全展示给工具调用方的协作错误。</summary>
public sealed class CollaborationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 每个请求单独原子落盘。所有操作均在以存储目录派生的命名互斥体内重新读取记录，
/// 因而 UI 与 CLI 的多个进程不会相互覆盖更新。
/// </summary>
public sealed class CollaborationStore : IDisposable
{
    private const int MaxRecords = 500;
    private const int MaxPendingRecords = 100;
    private static readonly Regex ConversationUrlPattern = new(
        "^https://chatgpt\\.com/(?:c/[A-Za-z0-9-]+|g/[A-Za-z0-9-]+/c/[A-Za-z0-9-]+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly string _bindingsDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Mutex _mutex;
    private bool _disposed;

    public CollaborationStore(string directory, Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("协作记录目录不能为空。", nameof(directory));
        _directory = Directory.CreateDirectory(directory).FullName;
        _bindingsDirectory = Directory.CreateDirectory(Path.Combine(_directory, "bindings")).FullName;
        _clock = clock ?? (() => DateTimeOffset.Now);
        var normalized = _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        _mutex = new Mutex(false, $"LocalProjectBridge.Collaboration.{hash}");
    }

    public CollaborationRequest Submit(ProjectRecord project, Guid connectionId, string question, string source,
        string? conversationUrl = null, Guid? requestId = null, CollaborationKind kind = CollaborationKind.General,
        Guid? parentRequestId = null, int iteration = 0, int maxIterations = 4)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Id == Guid.Empty || connectionId == Guid.Empty)
            throw Invalid("请求缺少项目或连接身份。", "invalid_request");
        if (!project.AllowWebRead || !project.AllowCodexAskWeb)
            throw Invalid("该项目未开启网页协作权限。", "project_not_authorized");
        var canonicalRoot = ProjectPathGuard.CanonicalizeProjectRoot(project.Path);
        var normalizedQuestion = RequiredText(question, 16_000, "问题", "invalid_question");
        var normalizedSource = RequiredText(source, 300, "来源", "invalid_source");
        var normalizedUrl = NormalizeConversationUrl(conversationUrl);
        if (kind is CollaborationKind.Plan or CollaborationKind.Review
            && !normalizedSource.StartsWith("codex://threads/", StringComparison.OrdinalIgnoreCase))
            throw Invalid("自动协作必须绑定当前 Codex 任务 URL。", "invalid_source");
        if (iteration < 0 || maxIterations is < 1 or > 12 || iteration >= maxIterations)
            throw Invalid("协作轮次或最大轮次无效。", "invalid_iteration");
        var id = requestId.GetValueOrDefault(Guid.NewGuid());
        if (id == Guid.Empty) throw Invalid("request_id 必须是有效 UUID。", "invalid_request");

        return WithLock(() =>
        {
            var records = LoadAllUnsafe();
            CollaborationRequest? parent = null;
            if (kind == CollaborationKind.Review)
            {
                if (parentRequestId is null || !records.TryGetValue(parentRequestId.Value, out parent)
                    || parent.Kind != CollaborationKind.Plan || parent.Status != CollaborationStatus.Answered
                    || parent.Checkpoint is not (CollaborationCheckpoint.ExecutedLocal or CollaborationCheckpoint.ExecutedSent)
                    || parent.ProjectId != project.Id || parent.ConnectionId != connectionId
                    || !string.Equals(parent.Source, normalizedSource, StringComparison.Ordinal))
                    throw Invalid("复核请求必须关联同一任务中已完成的规划请求。", "invalid_parent");
            }
            if (normalizedUrl is null)
                normalizedUrl = LoadBindingUnsafe(connectionId, project.Id, normalizedSource)?.ConversationUrl;
            if (records.TryGetValue(id, out var existing))
            {
                if (existing.ProjectId == project.Id && existing.ConnectionId == connectionId
                    && string.Equals(existing.ProjectRoot, canonicalRoot, StringComparison.OrdinalIgnoreCase)
                    && existing.Question == normalizedQuestion && existing.Source == normalizedSource
                    && (existing.ConversationUrl == normalizedUrl || normalizedUrl is null) && existing.Kind == kind
                    && existing.ParentRequestId == parentRequestId && existing.Iteration == iteration
                    && existing.MaxIterations == maxIterations)
                    return existing;
                throw Invalid("request_id 已用于不同的协作请求。", "request_id_conflict");
            }

            if (records.Count >= MaxRecords)
                throw Invalid("协作记录已达到上限，请在本机处理或清理后重试。", "record_limit");
            var now = _clock();
            ExpireUnsafe(records, now);
            if (records.Values.Count(record => record.Status is CollaborationStatus.Pending or CollaborationStatus.Read) >= MaxPendingRecords)
                throw Invalid("等待中的协作请求已达到上限。", "pending_limit");

            var record = new CollaborationRequest
            {
                RequestId = id,
                ProjectId = project.Id,
                ProjectRoot = canonicalRoot,
                ConnectionId = connectionId,
                Source = normalizedSource,
                Question = normalizedQuestion,
                ConversationUrl = normalizedUrl,
                AccessCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                TurnId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                CreatedAt = now,
                ExpiresAt = now.AddHours(24),
                Status = CollaborationStatus.Pending,
                Kind = kind,
                DeliveryState = CollaborationDeliveryState.NotStarted,
                Checkpoint = kind == CollaborationKind.Plan ? CollaborationCheckpoint.Init : CollaborationCheckpoint.None,
                ParentRequestId = parentRequestId,
                Iteration = iteration,
                MaxIterations = maxIterations,
                UpdatedAt = now
            };
            SaveUnsafe(record);
            return record;
        });
    }

    public IReadOnlyList<CollaborationRequest> List(Guid? projectId = null)
        => WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            return records.Values
                .Where(record => projectId is null || record.ProjectId == projectId)
                .OrderByDescending(record => record.CreatedAt)
                .ToArray();
        });

    public CollaborationRequest? Get(Guid requestId)
    {
        if (requestId == Guid.Empty) return null;
        return WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            return records.GetValueOrDefault(requestId);
        });
    }

    public CollaborationRequest Cancel(Guid requestId)
    {
        if (requestId == Guid.Empty) throw Invalid("request_id 必须是有效 UUID。", "invalid_request");
        return WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            if (!records.TryGetValue(requestId, out var record))
                throw Invalid("未找到协作请求。", "not_found");
            if (record.Status is CollaborationStatus.Cancelled or CollaborationStatus.Answered) return record;
            record = record with { Status = CollaborationStatus.Cancelled, UpdatedAt = _clock() };
            SaveUnsafe(record);
            return record;
        });
    }

    /// <summary>撤销项目授权时调用；已答复记录保持审计状态，其余可访问请求一律失效。</summary>
    public int CancelProject(Guid projectId)
    {
        if (projectId == Guid.Empty) return 0;
        return WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            var changed = 0;
            foreach (var record in records.Values.Where(record => record.ProjectId == projectId
                         && record.Status is CollaborationStatus.Pending or CollaborationStatus.Read).ToArray())
            {
                SaveUnsafe(record with { Status = CollaborationStatus.Cancelled, UpdatedAt = _clock() });
                changed++;
            }
            return changed;
        });
    }

    public CollaborationRequest ReadRemote(Guid requestId, string accessCode, Guid connectionId, string? clientId,
        ProjectAuthorizationRegistry registry)
        => WithLock(() =>
        {
            var records = LoadAllUnsafe();
            var record = AuthorizeRemoteUnsafe(records, requestId, accessCode, connectionId, clientId, registry);
            if (record.Kind is CollaborationKind.Plan or CollaborationKind.Review
                && record.DeliveryState == CollaborationDeliveryState.NotStarted)
                throw Invalid("本机尚未记录向目标对话发送的意图。", "send_intent_required");
            if (record.Status == CollaborationStatus.Pending)
            {
                record = record with { Status = CollaborationStatus.Read, ClientId = clientId!.Trim(), UpdatedAt = _clock() };
                SaveUnsafe(record);
            }
            return record;
        });

    public CollaborationRequest ReplyRemote(Guid requestId, string accessCode, Guid connectionId, string? clientId,
        ProjectAuthorizationRegistry registry, string reply, string? turnId = null)
        => WithLock(() =>
        {
            var records = LoadAllUnsafe();
            var record = AuthorizeRemoteUnsafe(records, requestId, accessCode, connectionId, clientId, registry);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(record.TurnId), Encoding.UTF8.GetBytes(turnId?.Trim() ?? string.Empty)))
                throw Invalid("答复轮次与当前请求不匹配。", "turn_mismatch");
            var normalizedReply = RequiredText(reply, 32_000, "回复", "invalid_reply");
            if (record.Status == CollaborationStatus.Answered)
            {
                if (string.Equals(record.Reply, normalizedReply, StringComparison.Ordinal)) return record;
                throw Invalid("该协作请求已有不同回复，不能覆盖。", "reply_conflict");
            }
            if (record.Status != CollaborationStatus.Read)
                throw Invalid("请先读取协作请求后再回复。", "not_read");
            record = record with
            {
                Reply = normalizedReply,
                ReplyOrigin = CollaborationReplyOrigin.McpTool,
                Status = CollaborationStatus.Answered,
                UpdatedAt = _clock()
            };
            SaveUnsafe(record);
            return record;
        });

    /// <summary>
    /// Accepts the completed assistant message from the exact bound ChatGPT conversation,
    /// read through the app's conversation tools or the browser page.
    /// The remote request must already have been read through authenticated MCP, so browser text
    /// cannot replace the project authorization and identity checks.
    /// </summary>
    public CollaborationRequest AcceptBrowserVisibleReply(Guid requestId, string? turnId, Guid projectId,
        Guid connectionId, string conversationUrl, ProjectAuthorizationRegistry registry, string reply)
        => WithLock(() =>
        {
            ArgumentNullException.ThrowIfNull(registry);
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            if (!records.TryGetValue(requestId, out var record))
                throw Invalid("未找到协作请求。", "not_found");
            if (record.Status is CollaborationStatus.Cancelled or CollaborationStatus.Expired)
                throw Invalid("协作请求已取消或过期。", "not_available");
            if (record.ConnectionId != connectionId)
                throw Invalid("协作请求不属于当前连接。", "not_authorized");
            if (record.ProjectId != projectId)
                throw Invalid("网页回复中的 project_id 与请求不匹配。", "project_mismatch");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(record.TurnId), Encoding.UTF8.GetBytes(turnId?.Trim() ?? string.Empty)))
                throw Invalid("网页回复中的 turn_id 与请求不匹配。", "turn_mismatch");
            if (record.DeliveryState != CollaborationDeliveryState.MessageConfirmed)
                throw Invalid("页面尚未确认发送当前请求。", "message_not_confirmed");
            var normalizedUrl = NormalizeConversationUrl(conversationUrl)
                ?? throw Invalid("conversation_url 不能为空。", "invalid_conversation_url");
            if (!string.Equals(record.ConversationUrl, normalizedUrl, StringComparison.Ordinal))
                throw Invalid("网页回复不属于已记录发送意图的对话。", "conversation_mismatch");
            if (record.Status is not (CollaborationStatus.Read or CollaborationStatus.Answered)
                || string.IsNullOrWhiteSpace(record.ClientId))
                throw Invalid("协作请求尚未由当前网页通过 MCP 读取。", "not_read");
            if (!registry.TryGetReadableProject(record.ProjectId, out var project, out _)
                || !project.AllowCodexAskWeb)
                throw Invalid("项目不存在，或未授权网页协作。", "project_not_authorized");
            string canonicalRoot;
            try { canonicalRoot = ProjectPathGuard.CanonicalizeProjectRoot(project.Path); }
            catch (Exception) { throw Invalid("项目路径不可用，协作请求已拒绝。", "project_not_authorized"); }
            if (!string.Equals(record.ProjectRoot, canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw Invalid("项目路径已变更，协作请求已拒绝。", "project_not_authorized");

            var normalizedReply = RequiredText(reply, 32_000, "回复", "invalid_reply");
            if (record.Status == CollaborationStatus.Answered)
            {
                if (string.Equals(record.Reply, normalizedReply, StringComparison.Ordinal)) return record;
                throw Invalid("该协作请求已有不同回复，不能覆盖。", "reply_conflict");
            }
            record = record with
            {
                Reply = normalizedReply,
                ReplyOrigin = CollaborationReplyOrigin.BrowserVisible,
                Status = CollaborationStatus.Answered,
                UpdatedAt = _clock()
            };
            SaveUnsafe(record);
            return record;
        });

    public static string BuildPrompt(CollaborationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stage = request.Kind.ToString().ToUpperInvariant();
        return $"[PROJECTBRIDGE] {stage} request_id={request.RequestId:D} turn_id={request.TurnId}\n"
            + $"Call get_collaboration_request with request_id and access_code={request.AccessCode}. "
            + "Use project tools only when needed and permitted by that request. "
            + "Return the requested response in your visible final message. "
            + "Include the exact request_id, turn_id and project_id returned by the tool. "
            + "Do not call reply_to_collaboration_request; Codex will record the visible response locally. "
            + "Do not perform or claim permission for additional actions.";
    }

    public CollaborationRequest RecordSendIntent(Guid requestId, string conversationUrl)
        => UpdateRequest(requestId, record =>
        {
            var normalizedUrl = NormalizeConversationUrl(conversationUrl)
                ?? throw Invalid("conversation_url 不能为空。", "invalid_conversation_url");
            if (record.Status is not (CollaborationStatus.Pending or CollaborationStatus.Read))
                throw Invalid("当前请求不能再发送。", "invalid_state");
            if (record.DeliveryState == CollaborationDeliveryState.MessageConfirmed
                && string.Equals(record.ConversationUrl, normalizedUrl, StringComparison.Ordinal))
                return record;
            if (record.DeliveryState != CollaborationDeliveryState.NotStarted
                && !string.Equals(record.ConversationUrl, normalizedUrl, StringComparison.Ordinal))
                throw Invalid("已记录向另一对话发送的意图；请先核对原对话。", "delivery_conflict");
            return record with
            {
                ConversationUrl = normalizedUrl,
                DeliveryState = CollaborationDeliveryState.IntentRecorded,
                SendIntentAt = record.SendIntentAt ?? _clock(),
                UpdatedAt = _clock()
            };
        });

    public CollaborationRequest ConfirmMessageSent(Guid requestId)
        => UpdateRequest(requestId, record =>
        {
            if (record.DeliveryState == CollaborationDeliveryState.MessageConfirmed) return record;
            if (record.DeliveryState != CollaborationDeliveryState.IntentRecorded || record.ConversationUrl is null)
                throw Invalid("请先记录发送意图和精确对话链接。", "intent_required");
            return record with
            {
                DeliveryState = CollaborationDeliveryState.MessageConfirmed,
                MessageConfirmedAt = _clock(),
                Checkpoint = record.Kind == CollaborationKind.Review
                    ? CollaborationCheckpoint.ExecutedSent
                    : record.Checkpoint,
                UpdatedAt = _clock()
            };
        });

    public ConversationBinding ConfirmConversationBinding(Guid requestId, Guid confirmedProjectId)
        => WithLock(() =>
        {
            var records = LoadAllUnsafe();
            if (!records.TryGetValue(requestId, out var record))
                throw Invalid("未找到协作请求。", "not_found");
            if (confirmedProjectId != record.ProjectId)
                throw Invalid("网页返回的 project_id 与本次请求不匹配。", "project_mismatch");
            if (record.DeliveryState != CollaborationDeliveryState.MessageConfirmed
                || record.Status is not (CollaborationStatus.Read or CollaborationStatus.Answered)
                || record.ConversationUrl is null)
                throw Invalid("只有网页已读取且发送已确认的请求才能绑定对话。", "binding_not_verified");
            var binding = new ConversationBinding
            {
                ConnectionId = record.ConnectionId,
                ProjectId = record.ProjectId,
                Source = record.Source,
                ConversationUrl = record.ConversationUrl,
                ConfirmedByRequestId = record.RequestId,
                ConfirmedAt = _clock()
            };
            SaveBindingUnsafe(binding);
            SaveUnsafe(record with { ConversationVerifiedAt = binding.ConfirmedAt, UpdatedAt = binding.ConfirmedAt });
            return binding;
        });

    public ConversationBinding? GetBinding(Guid connectionId, Guid projectId, string source)
        => WithLock(() => LoadBindingUnsafe(connectionId, projectId, RequiredText(source, 300, "来源", "invalid_source")));

    public CollaborationRequest SetCheckpoint(Guid requestId, CollaborationCheckpoint checkpoint, string? note = null)
        => UpdateRequest(requestId, record =>
        {
            if (record.Kind == CollaborationKind.General)
                throw Invalid("普通协作请求没有自动执行断点。", "invalid_state");
            var normalizedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            if (normalizedNote?.Length > 500) throw Invalid("断点说明过长。", "invalid_checkpoint");
            if (checkpoint is CollaborationCheckpoint.PlanReceived or CollaborationCheckpoint.Done
                && record.Status != CollaborationStatus.Answered)
                throw Invalid("尚未收到当前请求的匹配答复。", "reply_required");
            if (record.Kind == CollaborationKind.Plan)
            {
                var valid = record.Checkpoint == checkpoint
                    || record.Checkpoint == CollaborationCheckpoint.Init && checkpoint == CollaborationCheckpoint.PlanReceived
                    || record.Checkpoint == CollaborationCheckpoint.PlanReceived && checkpoint == CollaborationCheckpoint.Executing
                    || record.Checkpoint == CollaborationCheckpoint.Executing && checkpoint == CollaborationCheckpoint.ExecutedLocal
                    || record.Checkpoint == CollaborationCheckpoint.ExecutedLocal && checkpoint == CollaborationCheckpoint.ExecutedSent
                    || checkpoint == CollaborationCheckpoint.Blocked;
                if (!valid) throw Invalid("规划请求的执行断点顺序无效。", "invalid_checkpoint");
            }
            else if (record.Kind == CollaborationKind.Review
                     && checkpoint is not (CollaborationCheckpoint.ExecutedSent or CollaborationCheckpoint.Done or CollaborationCheckpoint.Blocked))
                throw Invalid("复核请求的执行断点无效。", "invalid_checkpoint");
            return record with { Checkpoint = checkpoint, CheckpointNote = normalizedNote, UpdatedAt = _clock() };
        });

    public CollaborationRequest? FindCurrent(Guid projectId, Guid connectionId, string source)
        => WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            var latest = records.Values
                .Where(record => record.ProjectId == projectId && record.ConnectionId == connectionId
                    && string.Equals(record.Source, source, StringComparison.Ordinal)
                    && record.Kind is CollaborationKind.Plan or CollaborationKind.Review)
                .OrderByDescending(record => record.UpdatedAt ?? record.CreatedAt)
                .FirstOrDefault();
            return latest is null || latest.Status is CollaborationStatus.Cancelled or CollaborationStatus.Expired
                ? null
                : latest;
        });

    public void Dispose()
    {
        if (_disposed) return;
        _mutex.Dispose();
        _disposed = true;
    }

    private CollaborationRequest AuthorizeRemoteUnsafe(Dictionary<Guid, CollaborationRequest> records, Guid requestId,
        string accessCode, Guid connectionId, string? clientId, ProjectAuthorizationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (requestId == Guid.Empty || connectionId == Guid.Empty)
            throw Invalid("请求缺少有效的请求或连接身份。", "invalid_request");
        if (string.IsNullOrWhiteSpace(clientId))
            throw Invalid("当前网页客户端身份无效。", "client_identity_required");
        if (!records.TryGetValue(requestId, out var record))
            throw Invalid("协作请求不存在或不可访问。", "not_found");
        ExpireUnsafe(records, _clock());
        record = records[requestId];
        if (record.Status is CollaborationStatus.Cancelled or CollaborationStatus.Expired)
            throw Invalid("协作请求已取消或过期。", "not_available");
        // Always compare the supplied code once a record is located. Connection identity is
        // still required, but it must not skip the secret comparison through short-circuiting.
        var accessCodeMatches = AccessCodeMatches(record.AccessCode, accessCode);
        if (record.ConnectionId != connectionId || !accessCodeMatches)
            throw Invalid("协作请求不存在或不可访问。", "not_authorized");
        if (!registry.TryGetReadableProject(record.ProjectId, out var project, out _)
            || !project.AllowCodexAskWeb)
            throw Invalid("项目不存在，或未授权网页协作。", "project_not_authorized");
        string canonicalRoot;
        try { canonicalRoot = ProjectPathGuard.CanonicalizeProjectRoot(project.Path); }
        catch (Exception) { throw Invalid("项目路径不可用，协作请求已拒绝。", "project_not_authorized"); }
        if (!string.Equals(record.ProjectRoot, canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw Invalid("项目路径已变更，协作请求已拒绝。", "project_not_authorized");
        var normalizedClientId = clientId.Trim();
        if (record.ClientId is not null && !string.Equals(record.ClientId, normalizedClientId, StringComparison.Ordinal))
            throw Invalid("协作请求已绑定到其他网页客户端。", "client_mismatch");
        return record;
    }

    private static bool AccessCodeMatches(string expected, string? supplied)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(supplied?.Trim() ?? string.Empty));

    private static string RequiredText(string? value, int maxLength, string field, string code)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) throw Invalid($"{field}不能为空。", code);
        if (normalized.Length > maxLength) throw Invalid($"{field}过长。", code);
        return normalized;
    }

    private static string? NormalizeConversationUrl(string? url)
    {
        if (url is null || url.Length == 0) return null;
        if (!ConversationUrlPattern.IsMatch(url))
            throw Invalid("conversation_url 只能是指定的 ChatGPT 对话链接。", "invalid_conversation_url");
        return url;
    }

    private static CollaborationException Invalid(string message, string code) => new(code, message);

    private CollaborationRequest UpdateRequest(Guid requestId, Func<CollaborationRequest, CollaborationRequest> update)
    {
        if (requestId == Guid.Empty) throw Invalid("request_id 必须是有效 UUID。", "invalid_request");
        return WithLock(() =>
        {
            var records = LoadAllUnsafe();
            ExpireUnsafe(records, _clock());
            if (!records.TryGetValue(requestId, out var record))
                throw Invalid("未找到协作请求。", "not_found");
            var updated = update(record);
            SaveUnsafe(updated);
            return updated;
        });
    }

    private T WithLock<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var acquired = false;
        try
        {
            try { acquired = _mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("无法取得协作记录锁。");
            return action();
        }
        finally
        {
            if (acquired) _mutex.ReleaseMutex();
        }
    }

    private Dictionary<Guid, CollaborationRequest> LoadAllUnsafe()
    {
        var records = new Dictionary<Guid, CollaborationRequest>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(fileName, "N", out var id))
                throw Invalid("协作记录目录包含无效数据，已拒绝继续写入。", "store_corrupt");
            CollaborationRequest? record;
            try { record = JsonSerializer.Deserialize<CollaborationRequest>(File.ReadAllText(path), JsonOptions); }
            catch (Exception) { throw Invalid("协作记录损坏，已拒绝继续写入。", "store_corrupt"); }
            if (record is { TurnId.Length: 0 } && record.Kind == CollaborationKind.General)
            {
                record = record with
                {
                    TurnId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    UpdatedAt = record.UpdatedAt ?? record.CreatedAt
                };
                SaveUnsafe(record);
            }
            if (record is { Status: CollaborationStatus.Answered, ReplyOrigin: CollaborationReplyOrigin.None })
            {
                record = record with { ReplyOrigin = CollaborationReplyOrigin.McpTool };
                SaveUnsafe(record);
            }
            if (record is null || record.RequestId != id || !IsValidStoredRecord(record))
                throw Invalid("协作记录损坏，已拒绝继续写入。", "store_corrupt");
            if (!records.TryAdd(id, record)) throw Invalid("协作记录重复，已拒绝继续写入。", "store_corrupt");
        }
        return records;
    }

    private static bool IsValidStoredRecord(CollaborationRequest record)
        => record.RequestId != Guid.Empty && record.ProjectId != Guid.Empty && record.ConnectionId != Guid.Empty
           && !string.IsNullOrWhiteSpace(record.ProjectRoot) && !string.IsNullOrWhiteSpace(record.Source)
           && !string.IsNullOrWhiteSpace(record.Question) && record.Question.Length <= 16_000
           && record.TurnId is { Length: 32 } turnId
           && turnId.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')
           && record.Source.Length <= 300 && record.AccessCode is { Length: 64 } accessCode
           && accessCode.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')
           && record.ExpiresAt > record.CreatedAt
           && (record.ConversationUrl is null || ConversationUrlPattern.IsMatch(record.ConversationUrl))
           && (record.Reply is null || (!string.IsNullOrWhiteSpace(record.Reply) && record.Reply.Length <= 32_000))
           && record.ReplyOrigin is CollaborationReplyOrigin.None or CollaborationReplyOrigin.McpTool
               or CollaborationReplyOrigin.BrowserVisible
           && record.Status is CollaborationStatus.Pending or CollaborationStatus.Read or CollaborationStatus.Answered
               or CollaborationStatus.Cancelled or CollaborationStatus.Expired
           && (record.Status is not (CollaborationStatus.Read or CollaborationStatus.Answered)
               || !string.IsNullOrWhiteSpace(record.ClientId))
           && (record.Status != CollaborationStatus.Answered
               || !string.IsNullOrWhiteSpace(record.Reply) && record.ReplyOrigin != CollaborationReplyOrigin.None)
           && (record.Status == CollaborationStatus.Answered || record.ReplyOrigin == CollaborationReplyOrigin.None);

    private ConversationBinding? LoadBindingUnsafe(Guid connectionId, Guid projectId, string source)
    {
        var path = BindingPath(connectionId, projectId, source);
        if (!File.Exists(path)) return null;
        ConversationBinding? binding;
        try { binding = JsonSerializer.Deserialize<ConversationBinding>(File.ReadAllText(path), JsonOptions); }
        catch (Exception) { throw Invalid("对话绑定记录损坏。", "store_corrupt"); }
        if (binding is null || binding.ConnectionId != connectionId || binding.ProjectId != projectId
            || !string.Equals(binding.Source, source, StringComparison.Ordinal)
            || NormalizeConversationUrl(binding.ConversationUrl) is null)
            throw Invalid("对话绑定记录损坏。", "store_corrupt");
        return binding;
    }

    private void SaveBindingUnsafe(ConversationBinding binding)
    {
        var path = BindingPath(binding.ConnectionId, binding.ProjectId, binding.Source);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, binding, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private string BindingPath(Guid connectionId, Guid projectId, string source)
    {
        var identity = $"{connectionId:D}|{projectId:D}|{source}";
        var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json";
        return Path.Combine(_bindingsDirectory, fileName);
    }

    private void ExpireUnsafe(Dictionary<Guid, CollaborationRequest> records, DateTimeOffset now)
    {
        foreach (var record in records.Values.Where(record => record.ExpiresAt <= now
                     && record.Status is CollaborationStatus.Pending or CollaborationStatus.Read).ToArray())
        {
            var expired = record with { Status = CollaborationStatus.Expired, UpdatedAt = now };
            SaveUnsafe(expired);
            records[expired.RequestId] = expired;
        }
    }

    private void SaveUnsafe(CollaborationRequest record)
    {
        var path = Path.Combine(_directory, record.RequestId.ToString("N") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* 保留原始异常，清理失败不改变已有记录。 */ }
            throw;
        }
    }
}
