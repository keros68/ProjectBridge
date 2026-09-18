using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>
/// Owns local reverse-bridge leases for the shared public connection.  A lease is
/// started only when its project first receives a delegated task; it never owns
/// a public tunnel or OAuth credential.
/// </summary>
public interface ISharedCodexTaskRuntime : IAsyncDisposable
{
    Task<ICodexTaskBridge> GetBridgeAsync(ProjectRecord project, CancellationToken cancellationToken);
    Task ReconcileAsync(IEnumerable<ProjectRecord> projects, CancellationToken cancellationToken = default);
}

public sealed record CodexTaskBridgeLease(ICodexTaskBridge Bridge, IAsyncDisposable Owner);

/// <summary>
/// Project-scoped lifetime manager for reverse bridges.  The supplied factory
/// creates a loopback-only bridge, normally a TransceiverAdapter with
/// <c>manageTunnel:false</c>.  Revoking a project's task permission disposes its
/// whole process tree through the lease owner.
/// </summary>
public sealed class SharedCodexTaskRuntime : ISharedCodexTaskRuntime
{
    private sealed record Entry(string CanonicalPath, bool AllowWrite, CodexTaskBridgeLease Lease);

    private readonly Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>> _start;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SharedCodexTaskRuntime(Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>> start)
        => _start = start ?? throw new ArgumentNullException(nameof(start));

    public async Task<ICodexTaskBridge> GetBridgeAsync(ProjectRecord project, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        var canonicalPath = ProjectPathGuard.CanonicalizeProjectRoot(project.Path);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(project.Id, out var entry))
            {
                if (entry.AllowWrite == project.AllowCodexWrite && string.Equals(entry.CanonicalPath, canonicalPath, StringComparison.OrdinalIgnoreCase))
                    return entry.Lease.Bridge;

                await entry.Lease.Owner.DisposeAsync().ConfigureAwait(false);
                _entries.Remove(project.Id);
            }

            var lease = await _start(project, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("本地 Codex 任务桥没有返回连接。");
            _entries.Add(project.Id, new Entry(canonicalPath, project.AllowCodexWrite, lease));
            return lease.Bridge;
        }
        finally { _gate.Release(); }
    }

    public async Task ReconcileAsync(IEnumerable<ProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projects);
        // 目录被移走或删除的项目按“未授权”处理，不能让它阻断其他项目的权限刷新。
        var allowed = projects
            .Where(project => project.AllowCodexTasks && Directory.Exists(project.Path))
            .ToDictionary(project => project.Id, project => (Path: ProjectPathGuard.CanonicalizeProjectRoot(project.Path), project.AllowCodexWrite));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (projectId, entry) in _entries.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (allowed.TryGetValue(projectId, out var permission) && entry.AllowWrite == permission.AllowCodexWrite
                    && string.Equals(entry.CanonicalPath, permission.Path, StringComparison.OrdinalIgnoreCase)) continue;
                await entry.Lease.Owner.DisposeAsync().ConfigureAwait(false);
                _entries.Remove(projectId);
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            foreach (var (id, entry) in _entries.ToArray())
            {
                await entry.Lease.Owner.DisposeAsync().ConfigureAwait(false);
                _entries.Remove(id);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedCodexTaskRuntime));
    }
}

/// <summary>
/// Fixed shared-MCP task surface.  A browser must use the router-issued
/// <c>task_id</c> for follow-up operations.  Upstream job ids are never accepted
/// from the browser, which prevents a task from one project being inspected or
/// cancelled through another project's route.
/// </summary>
public sealed class SharedCodexTaskTools
{
    private const string ProjectId = "project_id";
    private const string TaskId = "task_id";
    private sealed record TaskBinding(Guid ProjectId, long AuthorizationVersion, string CanonicalProjectPath, ICodexTaskBridge Bridge,
        string? JobId, string? ActivityId, string? ThreadId);

    private readonly ProjectAuthorizationRegistry _authorizations;
    private readonly ISharedCodexTaskRuntime _runtime;
    private readonly ConcurrentDictionary<Guid, TaskBinding> _tasks = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<Guid, (string Fingerprint, JsonObject Result)> _requests = [];

    public SharedCodexTaskTools(ProjectAuthorizationRegistry authorizations, ISharedCodexTaskRuntime runtime)
    {
        _authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public IReadOnlyList<GatewayTool> Create()
        => [CreateStart(), CreateStatus(), CreateStop()];

    /// <summary>Stop local task bridges that lost permission or changed project roots.</summary>
    public async Task ReconcileAsync(IEnumerable<ProjectRecord> projects, CancellationToken cancellationToken = default)
    {
        var snapshot = projects.ToArray();
        await _runtime.ReconcileAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var allowed = snapshot.Where(project => project.AllowCodexTasks).Select(project => project.Id).ToHashSet();
        foreach (var task in _tasks)
            if (!allowed.Contains(task.Value.ProjectId)) _tasks.TryRemove(task.Key, out _);
    }

    private GatewayTool CreateStart() => new(
        "codex_task_start",
        "委派本机 Codex 任务。可写授权在启动前验证实际沙盒写权限；验证失败不启动任务。返回 task_id 后用 status 获取结果，用 stop 停止。completed 只代表轮次结束，须检查 result、error 和 toolFailures。禁止命令联网和提权。",
        Schema([ProjectId, "prompt"],
            (ProjectId, UuidSchema("已授权委派 Codex 任务的项目 UUID。")),
            ("prompt", StringSchema("交给 Codex 的任务说明。")),
            ("request_id", UuidSchema("同一逻辑请求重试时复用；省略则由网关生成。")),
            ("activity_title", StringSchema("新 Activity 的标题。"))),
        StartAsync,
        GatewayToolAnnotations.DestructiveClosed);

    private GatewayTool CreateStatus() => new(
        "codex_task_status",
        "查询指定项目中由本连接创建的 Codex 任务状态。",
        Schema([ProjectId, TaskId],
            (ProjectId, UuidSchema("任务所属项目 UUID。")),
            (TaskId, UuidSchema("codex_task_start 返回的 task_id。")),
            ("wait_for", EnumSchema("change", "terminal")),
            ("wait_ms", IntegerSchema(1, 10_000))),
        StatusAsync,
        GatewayToolAnnotations.ReadOnlyClosed);

    private GatewayTool CreateStop() => new(
        "codex_task_stop",
        "停止指定项目中由本连接创建的 Codex 任务。已写入内容保留，不自动回滚。",
        Schema([ProjectId, TaskId],
            (ProjectId, UuidSchema("任务所属项目 UUID。")),
            (TaskId, UuidSchema("codex_task_start 返回的 task_id。")),
            ("expected_version", IntegerSchema(1, int.MaxValue))),
        StopAsync,
        GatewayToolAnnotations.DestructiveClosed);

    private async Task<JsonObject> StartAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StartCoreAsync(arguments, cancellationToken).ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    private async Task<JsonObject> StartCoreAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!TryProject(arguments, out var projectId, out var error)) return error!;
        if (GetString(arguments, "prompt") is not { Length: > 0 } prompt)
            return ToolErrors.InvalidArguments("prompt 不能为空。").ToContent();
        if (!_authorizations.TryGetDelegableProject(projectId, out var project, out var authorizationVersion)) return Denied();
        var requestId = Guid.NewGuid();
        if (arguments.ContainsKey("request_id") && (!TryGuid(arguments, "request_id", out requestId) || requestId == Guid.Empty))
            return ToolErrors.InvalidArguments("request_id 必须是有效的 UUID。").ToContent();
        var fingerprint = new JsonArray(projectId.ToString(), authorizationVersion, project.AllowCodexWrite, prompt,
            GetString(arguments, "activity_title")).ToJsonString();
        if (_requests.TryGetValue(requestId, out var previous))
            return previous.Fingerprint == fingerprint ? (JsonObject)previous.Result.DeepClone()
                : new ToolOutcome("request_id_conflict：同一编号不能用于不同项目、权限或任务内容。", true).ToContent();

        SessionPolicy policy;
        try { policy = SessionPolicyFactory.Create(project, CapabilityFlags.WebDelegateCodex | (project.AllowCodexWrite ? CapabilityFlags.WebDelegateCodexWrite : CapabilityFlags.None)); }
        catch (Exception exception) { return new ToolOutcome($"项目配置无效，无法委派 Codex：{exception.Message}", true).ToContent(); }

        ICodexTaskBridge bridge;
        try { bridge = await _runtime.GetBridgeAsync(project, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new ToolOutcome($"本地 Codex 任务桥未就绪：{exception.Message}", true).ToContent(); }

        // A project can be revoked while a lazy bridge is starting.  Never send
        // the task to Codex after that point, and reclaim the newly-created
        // bridge from the current registry snapshot.
        if (!IsCurrentProject(projectId, authorizationVersion, policy.CanonicalProjectPath))
        {
            await _runtime.ReconcileAsync(_authorizations.DelegableProjects(), cancellationToken).ConfigureAwait(false);
            return Denied();
        }

        var upstream = new JsonObject
        {
            ["requestId"] = requestId.ToString("D"),
            ["prompt"] = BuildProvenanceEnvelope(policy, prompt),
            ["cwd"] = policy.CanonicalProjectPath,
            ["sandbox"] = project.AllowCodexWrite ? "workspace-write" : "read-only",
            // The local reverse bridge responds through an HTTP proxy with a
            // bounded request timeout.  Never let a foreground task hold that
            // connection open; status polling is separately bounded to 10 s.
            ["executionMode"] = "background"
        };
        Copy(arguments, upstream, "activity_title", "activityTitle");
        var result = await CallAsync(bridge, "codex_task", upstream, cancellationToken).ConfigureAwait(false);
        if (result["isError"]?.GetValue<bool>() == true) return result;
        if (!IsCurrentProject(projectId, authorizationVersion, policy.CanonicalProjectPath)) return Denied();

        var routeTaskId = Guid.NewGuid();
        var identifiers = ExtractIdentifiers(result);
        _tasks[routeTaskId] = new TaskBinding(projectId, authorizationVersion, policy.CanonicalProjectPath, bridge,
            identifiers.JobId, identifiers.ActivityId, identifiers.ThreadId);
        var routed = WithRouteTask(result, routeTaskId, projectId);
        _requests.Add(requestId, (fingerprint, (JsonObject)routed.DeepClone()));
        return routed;
    }

    private async Task<JsonObject> StatusAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!TryBoundTask(arguments, out var binding, out var error)) return error!;
        var upstream = new JsonObject();
        if (binding!.JobId is not null) upstream["jobId"] = binding.JobId;
        if (binding.ActivityId is not null) upstream["activityId"] = binding.ActivityId;
        if (binding.ThreadId is not null) upstream["threadId"] = binding.ThreadId;
        Copy(arguments, upstream, "wait_for", "waitFor");
        if (arguments["wait_ms"] is JsonValue wait && wait.TryGetValue<int>(out var milliseconds))
            upstream["waitMs"] = Math.Clamp(milliseconds, 1, 10_000);
        if (upstream.Count == 0) return new ToolOutcome("任务组件没有返回可安全路由的任务标识。", true).ToContent();
        var result = await CallAsync(binding.Bridge, "codex_status", upstream, cancellationToken).ConfigureAwait(false);
        return IsCurrentProject(binding.ProjectId, binding.AuthorizationVersion, binding.CanonicalProjectPath) ? result : Denied();
    }

    private async Task<JsonObject> StopAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        if (!TryBoundTask(arguments, out var binding, out var error)) return error!;
        if (binding!.JobId is null) return new ToolOutcome("任务组件没有返回可停止的 job id。", true).ToContent();
        var upstream = new JsonObject { ["jobId"] = binding.JobId };
        Copy(arguments, upstream, "expected_version", "expectedVersion");
        var result = await CallAsync(binding.Bridge, "codex_cancel", upstream, cancellationToken).ConfigureAwait(false);
        return IsCurrentProject(binding.ProjectId, binding.AuthorizationVersion, binding.CanonicalProjectPath) ? result : Denied();
    }

    private bool TryBoundTask(JsonObject arguments, out TaskBinding? binding, out JsonObject? error)
    {
        binding = null;
        if (!TryProject(arguments, out var projectId, out error)) return false;
        if (!_authorizations.TryGetDelegableProject(projectId, out _, out _)) { error = Denied(); return false; }
        if (!TryGuid(arguments, TaskId, out var taskId))
        {
            error = ToolErrors.InvalidArguments("task_id 必须是 codex_task_start 返回的 UUID。").ToContent();
            return false;
        }
        if (!_tasks.TryGetValue(taskId, out binding) || binding.ProjectId != projectId
            || !IsCurrentProject(projectId, binding.AuthorizationVersion, binding.CanonicalProjectPath))
        {
            // Do not reveal whether a task exists under another project.
            error = new ToolOutcome("未找到该项目中的任务。", true).ToContent();
            return false;
        }
        error = null;
        return true;
    }

    private bool IsCurrentProject(Guid projectId, long version, string canonicalProjectPath)
        => _authorizations.TryGetDelegableProject(projectId, out var project, out var currentVersion) && version == currentVersion
           && string.Equals(ProjectPathGuard.CanonicalizeProjectRoot(project.Path), canonicalProjectPath, StringComparison.OrdinalIgnoreCase);

    private static bool TryProject(JsonObject arguments, out Guid projectId, out JsonObject? error)
    {
        if (TryGuid(arguments, ProjectId, out projectId)) { error = null; return true; }
        error = ToolErrors.InvalidArguments("project_id 必须是有效的项目 UUID。").ToContent();
        return false;
    }

    private static bool TryGuid(JsonObject arguments, string name, out Guid value)
    {
        value = Guid.Empty;
        return arguments[name] is JsonValue node && node.TryGetValue<string>(out var raw)
            && Guid.TryParse(raw, out value) && value != Guid.Empty;
    }

    private static JsonObject Denied() => new ToolOutcome("项目不存在，或未授权网页委派 Codex 任务。", true).ToContent();

    private static async Task<JsonObject> CallAsync(ICodexTaskBridge bridge, string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bridge.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            if (result is null) return new ToolOutcome("Codex 任务组件返回了空结果。", true).ToContent();
            return result["content"] is JsonArray ? (JsonObject)result.DeepClone() : new ToolOutcome(result.ToJsonString()).ToContent();
        }
        catch (HttpRequestException exception) { return new ToolOutcome($"Codex 任务组件不可达：{exception.Message}", true).ToContent(); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return new ToolOutcome("Codex 任务组件响应超时。", true).ToContent(); }
        catch (CodexTaskBridgeException exception) { return new ToolOutcome($"Codex 任务组件拒绝请求：{exception.Message}", true).ToContent(); }
    }

    private static JsonObject WithRouteTask(JsonObject result, Guid taskId, Guid projectId)
    {
        var routed = (JsonObject)result.DeepClone();
        var content = routed["content"] as JsonArray ?? new JsonArray();
        routed["content"] = content;
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = new JsonObject { [TaskId] = taskId.ToString("D"), [ProjectId] = projectId.ToString("D") }.ToJsonString()
        });
        return routed;
    }

    private static (string? JobId, string? ActivityId, string? ThreadId) ExtractIdentifiers(JsonNode node)
    {
        string? job = null, activity = null, thread = null;
        void Visit(JsonNode? value)
        {
            switch (value)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        if (child is JsonValue scalar && scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                        {
                            if (key is "jobId" or "job_id") job ??= text;
                            if (key is "activityId" or "activity_id") activity ??= text;
                            if (key is "threadId" or "thread_id") thread ??= text;
                        }
                        Visit(child);
                    }
                    break;
                case JsonArray array:
                    foreach (var child in array) Visit(child);
                    break;
                case JsonValue textNode when textNode.TryGetValue<string>(out var text):
                    try { Visit(JsonNode.Parse(text)); } catch (System.Text.Json.JsonException) { }
                    break;
            }
        }
        Visit(node);
        return (job, activity, thread);
    }

    private static string BuildProvenanceEnvelope(SessionPolicy policy, string prompt)
        => $"|from_chatgpt|:\nproject_id: {policy.ProjectId:D}\nsession_id: {policy.SessionId:D}\nsource: chatgpt-web\n"
           + $"task_mode: {(policy.Allows(CapabilityFlags.WebDelegateCodexWrite) ? "workspace-write" : "read-only")}\n"
           + "Work only on the authorized project. Do not request elevation, change accounts, install global tools, or publish/deploy. "
           + "Return a summary of changed files, checks run and any failures.\n\n" + prompt.Trim();

    private static void Copy(JsonObject source, JsonObject target, string sourceName, string targetName)
    {
        if (source[sourceName] is { } value) target[targetName] = value.DeepClone();
    }

    private static string? GetString(JsonObject source, string name)
        => source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : null;

    private static JsonObject Schema(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var propertyObject = new JsonObject();
        foreach (var property in properties) propertyObject[property.Name] = property.Schema;
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = propertyObject,
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject StringSchema(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject UuidSchema(string description) => new()
    {
        ["type"] = "string", ["description"] = description,
        ["pattern"] = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
    };
    private static JsonObject EnumSchema(params string[] values)
        => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject IntegerSchema(int minimum, int maximum)
        => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
}
