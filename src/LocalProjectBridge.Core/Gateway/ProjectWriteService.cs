using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalProjectBridge.Core.Gateway;

public sealed class WriteOperationException(string code, string message, bool retryable = false, string? action = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public string Action { get; } = action ?? "检查本机授权和文件状态后重新生成修改预览。";
}

public sealed class ProjectWriteService
{
    private readonly ProjectAuthorizationRegistry _authorizations;
    private readonly WriteLeaseStore _leases;
    private readonly ChangeJournal _journal;
    private readonly BackupStore _backups;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _projectLocks = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _requestLocks = new();
    private readonly IWriteFaultInjector? _faultInjector;

    public ProjectWriteService(ProjectAuthorizationRegistry authorizations, WriteLeaseStore leases,
        ChangeJournal journal, IWriteFaultInjector? faultInjector = null)
    {
        _authorizations = authorizations;
        _leases = leases;
        _journal = journal;
        _backups = new BackupStore(journal.RootDirectory);
        _faultInjector = faultInjector;
    }

    public ChangeJournal Journal => _journal;

    // Local UI only; never expose granting authority as a remote tool.
    public WriteLease GrantAutoApply(Guid projectId, Guid connectionId, string clientId, TimeSpan duration)
    {
        var access = AuthorizeProposal(projectId, connectionId, clientId);
        return _leases.Grant(projectId, access.RootVersion, connectionId, clientId, duration,
            allowDeletion: true);
    }

    public WriteLease? GetAutoApplyLease(Guid projectId, Guid connectionId, string? clientId)
    {
        try { return Authorize(projectId, connectionId, clientId).Lease; }
        catch (WriteOperationException) { return null; }
    }

    public void RevokeAutoApply(Guid projectId) => _leases.RevokeProject(projectId);
    public void RevokeAllAutoApply() => _leases.RevokeAll();

    public bool HasWriteAccess(Guid projectId, Guid connectionId, string? clientId)
    {
        try { Authorize(projectId, connectionId, clientId); return true; }
        catch (WriteOperationException) { return false; }
    }

    public Task<ChangeRecord> PrepareChangeAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid requestId, JsonArray operations, CancellationToken cancellationToken = default)
    {
        var operationCopy = (JsonArray)operations.DeepClone();
        var payloadHash = PreparePayloadHash(operationCopy);
        return WithRequestLockAsync(requestId, cancellationToken,
            () => PrepareChangeCoreAsync(projectId, connectionId, clientId, requestId,
                operationCopy, payloadHash, cancellationToken));
    }

    private async Task<ChangeRecord> PrepareChangeCoreAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid requestId, JsonArray operations, string payloadHash, CancellationToken cancellationToken)
    {
        if (_journal.FindRequest(requestId) is { } duplicate)
        {
            var existingAccess = AuthorizeProposal(projectId, connectionId, clientId);
            return ValidateExisting(duplicate, projectId, connectionId, clientId, requestId,
                duplicate.PrepareRequestId, null, payloadHash, existingAccess.RootVersion);
        }
        var access = AuthorizeProposal(projectId, connectionId, clientId);
        if (operations.Count is < 1 or > 20)
            throw new WriteOperationException("invalid_operation_count", "一次修改必须包含 1–20 个文件操作。");

        var prepared = new List<PreparedFile>();
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < operations.Count; index++)
        {
            if (operations[index] is not JsonObject operation)
                throw new WriteOperationException("invalid_operation", $"第 {index + 1} 个操作格式无效。");
            prepared.Add(PrepareFile(access.Project, operation, occupied));
        }
        EnsureAuthorized(access, projectId, connectionId, clientId);

        var now = DateTimeOffset.Now;
        var record = new ChangeRecord
        {
            ProjectId = projectId,
            RootVersion = access.RootVersion,
            ConnectionId = connectionId,
            ClientId = clientId!,
            ProjectRoot = ProjectPathGuard.CanonicalizeProjectRoot(access.Project.Path),
            PrepareRequestId = requestId,
            PreparePayloadHash = payloadHash,
            CreatedAt = now,
            UpdatedAt = now,
            Preview = string.Join(Environment.NewLine + Environment.NewLine, prepared.Select(file => file.Preview))
        };
        var staged = new Dictionary<int, byte[]>();
        for (var index = 0; index < prepared.Count; index++)
        {
            var file = prepared[index];
            record.Files.Add(new ChangeFileRecord
            {
                Index = index,
                Operation = file.Operation,
                Path = file.Path,
                TargetPath = file.TargetPath,
                BeforeHash = file.BeforeHash,
                AfterHash = file.AfterHash,
                Encoding = file.Encoding,
                NewLine = file.NewLine,
                Status = ChangeFileStatus.Pending
            });
            if (file.NewBytes is not null && file.Operation is ChangeOperationType.Patch or ChangeOperationType.Create)
                staged[index] = file.NewBytes;
        }
        await _journal.AddPreparedAsync(record, staged, cancellationToken).ConfigureAwait(false);
        EnsureAuthorized(access, projectId, connectionId, clientId);
        return _journal.Get(record.ChangeId)!;
    }

    public Task<ChangeRecord> ApplyChangeAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid changeId, Guid requestId, CancellationToken cancellationToken = default)
        => WithRequestLockAsync(requestId, cancellationToken,
            () => ApplyChangeCoreAsync(projectId, connectionId, clientId, changeId, requestId, cancellationToken));

    public Task<ChangeRecord> ApplyLocallyAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var record = _journal.Get(changeId)
            ?? throw new WriteOperationException("change_not_found", "未找到修改记录。");
        return ApplyChangeCoreAsync(record.ProjectId, record.ConnectionId, record.ClientId,
            changeId, null, cancellationToken);
    }

    private async Task<ChangeRecord> ApplyChangeCoreAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid changeId, Guid? requestId, CancellationToken cancellationToken)
    {
        if (requestId is { } remoteRequest && _journal.FindRequest(remoteRequest) is { } duplicate)
        {
            var existingAccess = AuthorizeProposal(projectId, connectionId, clientId);
            return ValidateExisting(duplicate, projectId, connectionId, clientId, remoteRequest,
                duplicate.ApplyRequestId, changeId, null, existingAccess.RootVersion);
        }
        var access = requestId is null
            ? AuthorizeLocal(changeId)
            : AuthorizeProposal(projectId, connectionId, clientId);
        var projectLock = _projectLocks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            access = requestId is null
                ? AuthorizeLocal(changeId)
                : AuthorizeProposal(projectId, connectionId, clientId);
            var record = GetOwned(changeId, projectId, connectionId, clientId);
            if (record.RootVersion != access.RootVersion || !PathEquals(record.ProjectRoot, access.Project.Path))
                throw new WriteOperationException("project_changed", "项目根目录或授权版本已经变化，拒绝提交。");
            // A local application has no remote request ID yet. A subsequent web
            // submission acknowledges that result; it must not ask for approval again.
            if (requestId is not null && record.Status == ChangeStatus.Applied && record.ApplyRequestId is null)
            {
                record.ApplyRequestId = requestId;
                await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
                return _journal.Get(record.ChangeId)!;
            }
            if (record.Status == ChangeStatus.Applied)
                throw new WriteOperationException("change_already_applied", "该修改已由另一请求提交；请使用原 request_id 查询结果。", action: "在本机修改记录中查看原提交结果。");
            if (record.Status != ChangeStatus.Prepared)
                throw new WriteOperationException("change_not_applicable", "该修改不是可提交状态；请检查或恢复后重新准备。", action: "在本机查看修改记录和恢复状态。");
            if (requestId is not null) access = Authorize(projectId, connectionId, clientId);
            if (record.RootVersion != access.RootVersion || !PathEquals(record.ProjectRoot, access.Project.Path))
                throw new WriteOperationException("project_changed", "项目根目录或授权版本已经变化，拒绝提交。");
            if (record.RequiresDeletionConfirmation && access.Lease?.AllowDeletion != true)
                throw new WriteOperationException("deletion_confirmation_required", "删除操作尚未在本机单独确认。", action: "在 ProjectBridge 的编辑页确认删除后重试。");

            var verifiedStaged = Preflight(record, access.Project);
            foreach (var file in record.Files)
            {
                var source = ResolveSafePath(record.ProjectRoot, file.Path, mustExist: file.Operation != ChangeOperationType.Create);
                var original = file.Operation == ChangeOperationType.Create ? null : await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);
                file.BackupFile = await _backups.SaveAsync(record.ChangeId, file.Index, original,
                    file.BeforeHash, cancellationToken).ConfigureAwait(false);
            }
            record.Status = ChangeStatus.Applying;
            record.AutoApplyLeaseId = access.Lease?.LeaseId;
            if (access.Lease?.AllowDeletion == true) record.DeletionConfirmed = true;
            record.ApplyRequestId = requestId;
            record.Error = null;
            await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);

            var leaseConsumed = false;
            foreach (var file in record.Files)
            {
                try
                {
                    EnsureAuthorized(access, projectId, connectionId, clientId);
                    _faultInjector?.BeforeMutation(record.ChangeId, file.Index);
                    verifiedStaged.TryGetValue(file.Index, out var stagedBytes);
                    await ApplyFileAsync(record, file, stagedBytes, cancellationToken).ConfigureAwait(false);
                    if (!leaseConsumed && access.Lease is not null)
                    {
                        _leases.MarkApplied(access.Lease.LeaseId);
                        leaseConsumed = true;
                    }
                    _faultInjector?.AfterMutation(record.ChangeId, file.Index);
                    file.Status = ChangeFileStatus.Applied;
                    file.Error = null;
                    _faultInjector?.BeforeAppliedJournalUpdate(record.ChangeId, file.Index);
                    await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
                }
                catch (SimulatedWriteProcessCrashException) { throw; }
                catch (Exception error)
                {
                    var diskState = ChangeFileSystem.InspectMutation(record, file);
                    file.Status = diskState switch
                    {
                        MutationDiskState.Applied => ChangeFileStatus.Applied,
                        MutationDiskState.NotApplied => ChangeFileStatus.Failed,
                        _ => ChangeFileStatus.MutationUncertain
                    };
                    file.Error = SafeError(error);
                    var anyChangedOrUncertain = record.Files.Any(item => item.Status is
                        ChangeFileStatus.Applied or ChangeFileStatus.MutationUncertain);
                    if (anyChangedOrUncertain && !leaseConsumed && access.Lease is not null)
                    {
                        _leases.MarkApplied(access.Lease.LeaseId);
                        leaseConsumed = true;
                    }
                    record.Status = diskState is MutationDiskState.Applied or MutationDiskState.Uncertain
                        ? ChangeStatus.NeedsRecovery
                        : record.Files.Any(item => item.Status == ChangeFileStatus.Applied)
                            ? ChangeStatus.Partial : ChangeStatus.Interrupted;
                    record.Error = record.Status == ChangeStatus.NeedsRecovery
                        ? "写入后发生异常；已按磁盘状态保留恢复或人工检查入口。"
                        : "修改未全部完成；未继续处理后续文件。";
                    await _journal.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
                    return _journal.Get(record.ChangeId)!;
                }
            }
            record.Status = ChangeStatus.Applied;
            await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            EnsureAuthorized(access, projectId, connectionId, clientId);
            return _journal.Get(record.ChangeId)!;
        }
        finally { projectLock.Release(); }
    }

    public ChangeRecord GetChange(Guid projectId, Guid connectionId, string? clientId, Guid changeId)
    {
        AuthorizeProposal(projectId, connectionId, clientId);
        return GetOwned(changeId, projectId, connectionId, clientId);
    }

    public async Task<ChangeRecord> RejectLocallyAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var record = _journal.Get(changeId) ?? throw new WriteOperationException("change_not_found", "未找到修改记录。");
        var projectLock = _projectLocks.GetOrAdd(record.ProjectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            record = _journal.Get(changeId)!;
            if (record.Status == ChangeStatus.Rejected) return record;
            if (record.Status != ChangeStatus.Prepared)
                throw new WriteOperationException("change_not_rejectable", "仅可拒绝尚未应用的修改；已应用的修改请使用安全恢复。");
            record.Status = ChangeStatus.Rejected;
            await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            return _journal.Get(changeId)!;
        }
        finally { projectLock.Release(); }
    }

    public Task<ChangeRecord> RestoreRemoteAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid changeId, Guid requestId, CancellationToken cancellationToken = default)
        => WithRequestLockAsync(requestId, cancellationToken,
            () => RestoreRemoteCoreAsync(projectId, connectionId, clientId, changeId, requestId, cancellationToken));

    private async Task<ChangeRecord> RestoreRemoteCoreAsync(Guid projectId, Guid connectionId, string? clientId,
        Guid changeId, Guid requestId, CancellationToken cancellationToken)
    {
        if (_journal.FindRequest(requestId) is { } duplicate)
        {
            var existingAccess = AuthorizeProposal(projectId, connectionId, clientId);
            var matchingRestoreRequest = duplicate.HasRestoreRequest(requestId) ? requestId : (Guid?)null;
            return ValidateExisting(duplicate, projectId, connectionId, clientId, requestId,
                matchingRestoreRequest, changeId, null, existingAccess.RootVersion);
        }
        var access = Authorize(projectId, connectionId, clientId);
        var record = GetOwned(changeId, projectId, connectionId, clientId);
        WriteAccess AuthorizeRestore()
        {
            EnsureAuthorized(access, projectId, connectionId, clientId);
            if (record.RootVersion != access.RootVersion || !PathEquals(record.ProjectRoot, access.Project.Path))
                throw new WriteOperationException("project_changed", "项目根目录或授权版本已经变化，拒绝远程恢复。");
            return access;
        }
        return await RestoreInternalAsync(record, AuthorizeRestore,
            requestId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChangeRecord> RestoreLocallyAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var record = _journal.Get(changeId) ?? throw new WriteOperationException("change_not_found", "未找到修改记录。");
        return await RestoreInternalAsync(record, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChangeRecord> RestoreInternalAsync(ChangeRecord record, Func<object>? authorize,
        Guid? requestId, CancellationToken cancellationToken)
    {
        var projectLock = _projectLocks.GetOrAdd(record.ProjectId, _ => new SemaphoreSlim(1, 1));
        await projectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            authorize?.Invoke();
            record = _journal.Get(record.ChangeId)!;
            if (record.Status == ChangeStatus.Restored)
            {
                if (requestId is null) return record;
                throw new WriteOperationException("change_already_restored", "该修改已由另一请求恢复；请使用原 request_id 查询结果。", action: "在本机修改记录中查看原恢复结果。");
            }
            if (record.Status is not (ChangeStatus.Applied or ChangeStatus.Partial or ChangeStatus.NeedsRecovery or ChangeStatus.RestorePartial))
                throw new WriteOperationException("change_not_restorable", "该修改没有可恢复的已写入文件。");
            if (requestId is not null)
            {
                record.RestoreRequestIds ??= [];
                if (record.RestoreRequestId is { } previousRequest
                    && !record.RestoreRequestIds.Contains(previousRequest))
                    record.RestoreRequestIds.Add(previousRequest);
                if (!record.RestoreRequestIds.Contains(requestId.Value))
                    record.RestoreRequestIds.Add(requestId.Value);
                record.RestoreRequestId = requestId;
                await _journal.UpdateAsync(record, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in record.Files.OrderByDescending(file => file.Index))
            {
                if (file.Status is not (ChangeFileStatus.Applied or ChangeFileStatus.MutationUncertain
                    or ChangeFileStatus.RestoreConflict)) continue;
                authorize?.Invoke();
                try
                {
                    await RestoreFileAsync(record, file, cancellationToken).ConfigureAwait(false);
                    file.Status = ChangeFileStatus.Restored;
                    file.Error = null;
                }
                catch (Exception error)
                {
                    file.Status = ChangeFileStatus.RestoreConflict;
                    file.Error = SafeError(error);
                }
                await _journal.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            }
            record.Status = record.Files.Any(file => file.Status == ChangeFileStatus.RestoreConflict)
                ? ChangeStatus.RestorePartial : ChangeStatus.Restored;
            record.Error = record.Status == ChangeStatus.RestorePartial
                ? "部分文件已有后续修改，未覆盖这些文件。" : null;
            await _journal.UpdateAsync(record, CancellationToken.None).ConfigureAwait(false);
            return _journal.Get(record.ChangeId)!;
        }
        finally { projectLock.Release(); }
    }

    private sealed record WriteAccess(ProjectRecord Project, long RootVersion, WriteLease? Lease);

    private WriteAccess Authorize(Guid projectId,
        Guid connectionId, string? clientId)
    {
        var access = AuthorizeProposal(projectId, connectionId, clientId);
        if (!_leases.TryAuthorize(projectId, access.RootVersion, connectionId, clientId, out var lease, out _))
            throw new WriteOperationException("local_confirmation_required",
                "修改已准备，等待在 ProjectBridge 中确认应用。",
                action: "在 ProjectBridge 的编辑页选择该修改并点击“应用修改”。");
        return access with { Lease = lease };
    }

    private WriteAccess AuthorizeProposal(Guid projectId,
        Guid connectionId, string? clientId)
    {
        if (!_journal.IsHealthy)
            throw new WriteOperationException("change_journal_unavailable",
                "本机修改记录无法完整读取，已停止新的网页编辑。", action: "在 ProjectBridge 中检查修改记录与诊断信息；不要删除恢复资料。");
        if (string.IsNullOrWhiteSpace(clientId))
            throw new WriteOperationException("unverified_client", "修改请求没有经过可验证的连接主体。", action: "先从网页完成一次真实项目调用。");
        if (!_authorizations.TryGetWriteProject(projectId, out var project, out var rootVersion))
            throw new WriteOperationException("project_not_authorized", "项目不存在或未授权访问。");
        return new WriteAccess(project, rootVersion, null);
    }

    private WriteAccess AuthorizeLocal(Guid changeId)
    {
        if (!_journal.IsHealthy)
            throw new WriteOperationException("change_journal_unavailable",
                "本机修改记录无法完整读取，已停止新的网页编辑。", action: "在 ProjectBridge 中检查修改记录与诊断信息；不要删除恢复资料。");
        var record = _journal.Get(changeId)
            ?? throw new WriteOperationException("change_not_found", "未找到修改记录。");
        if (!_authorizations.TryGetWriteProject(record.ProjectId, out var project, out var rootVersion))
            throw new WriteOperationException("project_not_authorized", "项目不存在或未授权访问。");
        if (record.RootVersion != rootVersion || !PathEquals(record.ProjectRoot, project.Path))
            throw new WriteOperationException("project_changed", "项目根目录或授权版本已经变化，拒绝应用。");
        return new WriteAccess(project, rootVersion, null);
    }

    private void EnsureAuthorized(WriteAccess access,
        Guid projectId, Guid connectionId, string? clientId)
    {
        if (!_authorizations.IsStillWriteProject(projectId, access.RootVersion))
            throw new WriteOperationException("project_changed", "项目授权已经变化。");
        if (access.Lease is null) return;
        if (!_leases.TryValidateIdentity(projectId, access.RootVersion, connectionId, clientId, out var current, out var reason))
            throw new WriteOperationException("write_lease_changed", reason);
        if (current.LeaseId != access.Lease.LeaseId)
            throw new WriteOperationException("write_lease_changed", "写入授权已经被替换。");
    }

    private ChangeRecord GetOwned(Guid changeId, Guid projectId, Guid connectionId, string? clientId)
    {
        var record = _journal.Get(changeId) ?? throw new WriteOperationException("change_not_found", "未找到修改记录。");
        if (record.ProjectId != projectId || record.ConnectionId != connectionId
            || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
            throw new WriteOperationException("change_not_found", "未找到修改记录。");
        return record;
    }

    private static ChangeRecord ValidateExisting(ChangeRecord record, Guid projectId, Guid connectionId,
        string? clientId, Guid requestId, Guid? expectedRequest, Guid? expectedChangeId,
        string? expectedPreparePayloadHash, long expectedRootVersion)
    {
        if (record.ProjectId != projectId || record.ConnectionId != connectionId
            || !string.Equals(record.ClientId, clientId, StringComparison.Ordinal)
            || expectedRequest != requestId
            || record.RootVersion != expectedRootVersion
            || expectedChangeId is not null && record.ChangeId != expectedChangeId
            || expectedPreparePayloadHash is not null
                && !string.Equals(record.PreparePayloadHash, expectedPreparePayloadHash, StringComparison.Ordinal))
            throw new WriteOperationException("request_id_conflict", "request_id 已被其他请求使用。");
        return record;
    }

    private async Task<ChangeRecord> WithRequestLockAsync(Guid requestId, CancellationToken cancellationToken,
        Func<Task<ChangeRecord>> action)
    {
        var requestLock = _requestLocks.GetOrAdd(requestId, _ => new SemaphoreSlim(1, 1));
        await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { requestLock.Release(); }
    }

    private static string PreparePayloadHash(JsonArray operations)
    {
        var canonical = CanonicalizeJson(operations);
        return ChangeFileSystem.Hash(Encoding.UTF8.GetBytes(canonical!.ToJsonString()));
    }

    private static JsonNode? CanonicalizeJson(JsonNode? node)
        => node switch
        {
            JsonObject source => new JsonObject(source.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, CanonicalizeJson(pair.Value))).ToArray()),
            JsonArray source => new JsonArray(source.Select(CanonicalizeJson).ToArray()),
            null => null,
            _ => node.DeepClone()
        };

    private static PreparedFile PrepareFile(ProjectRecord project, JsonObject operation, HashSet<string> occupied)
    {
        var root = ProjectPathGuard.CanonicalizeProjectRoot(project.Path);
        var kind = GetRequiredString(operation, "type").ToLowerInvariant() switch
        {
            "patch" => ChangeOperationType.Patch,
            "create" => ChangeOperationType.Create,
            "rename" => ChangeOperationType.Rename,
            "delete" => ChangeOperationType.Delete,
            _ => throw new WriteOperationException("unsupported_operation", "仅支持 patch、create、rename 和 delete。")
        };
        var relative = NormalizeRelative(GetRequiredString(operation, "path"));
        if (!occupied.Add(relative)) throw new WriteOperationException("duplicate_path", $"同一计划重复使用路径：{relative}");
        var source = ResolveSafePath(root, relative, mustExist: kind != ChangeOperationType.Create);
        if (kind != ChangeOperationType.Create && !File.Exists(source))
            throw new WriteOperationException("file_not_found", $"文件不存在：{relative}");
        if (Directory.Exists(source)) throw new WriteOperationException("directory_not_supported", "不支持目录写入或递归删除。");

        byte[]? original = null;
        string text = string.Empty, encoding = "utf-8", newLine = "None";
        string? beforeHash = null;
        if (kind != ChangeOperationType.Create)
        {
            original = File.ReadAllBytes(source);
            (text, encoding, newLine) = ChangeFileSystem.DecodeText(original);
            beforeHash = ChangeFileSystem.Hash(original);
        }

        byte[]? changed = null;
        string? targetRelative = null;
        string preview;
        switch (kind)
        {
            case ChangeOperationType.Patch:
                var oldText = GetRequiredString(operation, "old_text");
                var newText = operation["new_text"]?.GetValue<string>() ?? string.Empty;
                var first = text.IndexOf(oldText, StringComparison.Ordinal);
                if (first < 0 || text.IndexOf(oldText, first + oldText.Length, StringComparison.Ordinal) >= 0)
                    throw new WriteOperationException("patch_not_unique", "old_text 必须在目标文件中精确出现一次。", action: "重新读取文件并提供更精确的上下文。");
                var patched = text[..first] + newText + text[(first + oldText.Length)..];
                changed = ChangeFileSystem.EncodeText(patched, encoding);
                EnsureSize(changed);
                var contextStart = Math.Max(0, first - 180);
                var contextEnd = Math.Min(text.Length, first + oldText.Length + 180);
                var prefix = text[contextStart..first];
                var suffix = text[(first + oldText.Length)..contextEnd];
                preview = TextPreview(relative, prefix + oldText + suffix, prefix + newText + suffix);
                break;
            case ChangeOperationType.Create:
                if (File.Exists(source) || Directory.Exists(source))
                    throw new WriteOperationException("target_exists", $"新建目标已存在：{relative}");
                var content = operation["content"]?.GetValue<string>() ?? string.Empty;
                changed = ChangeFileSystem.EncodeText(content, "utf-8");
                EnsureSize(changed);
                newLine = content.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : content.Contains('\n') ? "LF" : "None";
                preview = TextPreview(relative, string.Empty, content);
                break;
            case ChangeOperationType.Rename:
                targetRelative = NormalizeRelative(GetRequiredString(operation, "target_path"));
                if (!occupied.Add(targetRelative)) throw new WriteOperationException("duplicate_path", $"同一计划重复使用路径：{targetRelative}");
                var target = ResolveSafePath(root, targetRelative, mustExist: false);
                if (File.Exists(target) || Directory.Exists(target))
                    throw new WriteOperationException("target_exists", $"重命名目标已存在：{targetRelative}");
                preview = $"RENAME {relative} -> {targetRelative}";
                break;
            default:
                preview = $"DELETE {relative}{Environment.NewLine}" + TextPreview(relative, text, string.Empty);
                break;
        }
        return new PreparedFile(kind, relative, targetRelative, original, changed, beforeHash,
            kind == ChangeOperationType.Delete ? null : changed is null ? beforeHash : ChangeFileSystem.Hash(changed), encoding, newLine, preview);
    }

    private Dictionary<int, byte[]> Preflight(ChangeRecord record, ProjectRecord project)
    {
        if (!PathEquals(record.ProjectRoot, project.Path))
            throw new WriteOperationException("project_changed", "项目根目录已经变化。");
        var staged = new Dictionary<int, byte[]>();
        foreach (var file in record.Files)
        {
            var source = ResolveSafePath(record.ProjectRoot, file.Path, mustExist: file.Operation != ChangeOperationType.Create);
            if (file.Operation == ChangeOperationType.Create)
            {
                if (File.Exists(source) || Directory.Exists(source)) throw Conflict(file.Path);
                staged[file.Index] = _journal.ReadStagedVerified(record, file);
                continue;
            }
            if (ChangeFileSystem.HashFile(source) != file.BeforeHash) throw Conflict(file.Path);
            if (file.Operation == ChangeOperationType.Patch)
                staged[file.Index] = _journal.ReadStagedVerified(record, file);
            if (file.Operation == ChangeOperationType.Rename)
            {
                var target = ResolveSafePath(record.ProjectRoot, file.TargetPath!, mustExist: false);
                if (File.Exists(target) || Directory.Exists(target)) throw Conflict(file.TargetPath!);
            }
        }
        return staged;
    }

    private async Task ApplyFileAsync(ChangeRecord record, ChangeFileRecord file, byte[]? stagedBytes,
        CancellationToken cancellationToken)
    {
        var source = ResolveSafePath(record.ProjectRoot, file.Path, mustExist: file.Operation != ChangeOperationType.Create);
        switch (file.Operation)
        {
            case ChangeOperationType.Patch:
                if (ChangeFileSystem.HashFile(source) != file.BeforeHash) throw Conflict(file.Path);
                await ChangeFileSystem.WriteAtomicallyAsync(source, stagedBytes
                    ?? throw new WriteOperationException("staged_content_invalid", "修改计划缺少已验证的暂存内容。"), true, cancellationToken).ConfigureAwait(false);
                break;
            case ChangeOperationType.Create:
                if (File.Exists(source) || Directory.Exists(source)) throw Conflict(file.Path);
                await ChangeFileSystem.WriteAtomicallyAsync(source, stagedBytes
                    ?? throw new WriteOperationException("staged_content_invalid", "修改计划缺少已验证的暂存内容。"), false, cancellationToken).ConfigureAwait(false);
                break;
            case ChangeOperationType.Rename:
                var target = ResolveSafePath(record.ProjectRoot, file.TargetPath!, mustExist: false);
                if (ChangeFileSystem.HashFile(source) != file.BeforeHash || File.Exists(target)) throw Conflict(file.Path);
                File.Move(source, target, false);
                break;
            case ChangeOperationType.Delete:
                if (ChangeFileSystem.HashFile(source) != file.BeforeHash) throw Conflict(file.Path);
                File.Delete(source);
                break;
        }
        var appliedPath = file.Operation == ChangeOperationType.Rename
            ? Path.Combine(record.ProjectRoot, file.TargetPath!) : source;
        if (file.Operation == ChangeOperationType.Delete)
        {
            if (File.Exists(source)) throw new IOException("删除后文件仍然存在。");
        }
        else if (ChangeFileSystem.HashFile(appliedPath) != file.AfterHash)
            throw new IOException("写入后的文件哈希与预览不一致。");
    }

    private async Task RestoreFileAsync(ChangeRecord record, ChangeFileRecord file, CancellationToken cancellationToken)
    {
        var source = ResolveSafePath(record.ProjectRoot, file.Path, mustExist: false);
        byte[]? backup = null;
        if (file.Operation != ChangeOperationType.Create)
        {
            if (file.BackupFile is null)
                throw new WriteOperationException("backup_content_invalid", "修改记录缺少恢复资料，未修改项目文件。");
            backup = _backups.ReadVerified(record.ChangeId, file.BackupFile, file.BeforeHash);
        }
        switch (file.Operation)
        {
            case ChangeOperationType.Patch:
                if (ChangeFileSystem.HashFile(source) == file.BeforeHash) return;
                if (ChangeFileSystem.HashFile(source) != file.AfterHash) throw Conflict(file.Path);
                await ChangeFileSystem.WriteAtomicallyAsync(source, backup!, true, cancellationToken).ConfigureAwait(false);
                break;
            case ChangeOperationType.Create:
                if (!File.Exists(source)) return;
                if (ChangeFileSystem.HashFile(source) != file.AfterHash) throw Conflict(file.Path);
                File.Delete(source);
                break;
            case ChangeOperationType.Rename:
                var target = ResolveSafePath(record.ProjectRoot, file.TargetPath!, mustExist: false);
                if (ChangeFileSystem.HashFile(source) == file.BeforeHash && !File.Exists(target)) return;
                if (File.Exists(source) || ChangeFileSystem.HashFile(target) != file.AfterHash) throw Conflict(file.TargetPath!);
                await ChangeFileSystem.WriteAtomicallyAsync(source, backup!, false, cancellationToken).ConfigureAwait(false);
                File.Delete(target);
                break;
            case ChangeOperationType.Delete:
                if (ChangeFileSystem.HashFile(source) == file.BeforeHash) return;
                if (File.Exists(source)) throw Conflict(file.Path);
                await ChangeFileSystem.WriteAtomicallyAsync(source, backup!, false, cancellationToken).ConfigureAwait(false);
                break;
        }
        if (ChangeFileSystem.InspectMutation(record, file) != MutationDiskState.NotApplied)
            throw new IOException("恢复后的文件状态与原始基线不一致。");
    }

    private static string ResolveSafePath(string root, string relative, bool mustExist)
    {
        relative = NormalizeRelative(relative);
        var canonicalRoot = ProjectPathGuard.CanonicalizeProjectRoot(root);
        var full = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        ProjectPathGuard.EnsureInsideProject(canonicalRoot, full);
        if (SensitiveFilePolicy.IsSensitiveRelative(relative, SensitiveFilePolicy.LoadIgnoreRules(canonicalRoot)))
            throw new WriteOperationException("sensitive_path", $"路径命中敏感文件规则：{relative}");
        var parent = Path.GetDirectoryName(full)!;
        ProjectPathGuard.EnsureInsideProject(canonicalRoot, parent);
        if (!Directory.Exists(parent)) throw new WriteOperationException("parent_not_found", $"父目录不存在：{relative}");
        if (mustExist && !File.Exists(full)) throw new WriteOperationException("file_not_found", $"文件不存在：{relative}");
        return full;
    }

    private static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new WriteOperationException("invalid_path", "文件路径必须是项目内的普通相对路径。");
        var segments = relative.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new WriteOperationException("invalid_path", "文件路径包含不允许的片段。");
        var reserved = new HashSet<string>(["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"], StringComparer.OrdinalIgnoreCase);
        if (segments.Any(segment => reserved.Contains(Path.GetFileNameWithoutExtension(segment))))
            throw new WriteOperationException("invalid_path", "文件路径包含 Windows 保留名称。");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(ProjectPathGuard.CanonicalizeProjectRoot(left), ProjectPathGuard.CanonicalizeProjectRoot(right), StringComparison.OrdinalIgnoreCase);

    private static string GetRequiredString(JsonObject source, string name)
        => source[name]?.GetValue<string>() is { Length: > 0 } value ? value
            : throw new WriteOperationException("invalid_arguments", $"缺少参数：{name}");

    private static void EnsureSize(byte[] bytes)
    {
        if (bytes.Length > ChangeFileSystem.MaxFileBytes)
            throw new WriteOperationException("file_too_large", "单个文件超过 2 MiB 限制。");
    }

    private static WriteOperationException Conflict(string path)
        => new("hash_conflict", $"文件已在预览后发生变化，拒绝覆盖：{path}", true, "重新读取文件并生成新的修改预览。");

    private static string TextPreview(string path, string before, string after)
    {
        return $"--- {path}{Environment.NewLine}+++ {path}{Environment.NewLine}@@{Environment.NewLine}- {before}{Environment.NewLine}+ {after}";
    }

    private static string SafeError(Exception error)
        => error is WriteOperationException write ? $"{write.Code}: {write.Message}" : $"{error.GetType().Name}: {error.Message}";
}
