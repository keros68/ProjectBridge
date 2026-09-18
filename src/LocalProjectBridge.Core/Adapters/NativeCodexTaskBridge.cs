using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Adapters;

/// <summary>Runs delegated writable tasks through Codex app-server and the local protocol guard.</summary>
public sealed class NativeCodexTaskBridge : ICodexTaskBridge, IAsyncDisposable
{
    private sealed class TaskState(string id)
    {
        public string Id { get; } = id;
        public string? ThreadId;
        public string? TurnId;
        public string Status = "running";
        public string Text = "";
        public string? Error;
        public List<JsonObject> ToolFailures { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Process _process;
    private readonly JobObject _job;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskState> _tasks = new();
    private readonly Dictionary<string, string> _requests = [];
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly Task _output;
    private readonly Task _errors;
    private long _nextId;
    private bool _disposed;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private readonly string _projectRoot;

    private NativeCodexTaskBridge(Process process, JobObject job, RedactingLogger logger, string projectRoot)
    {
        _process = process; _job = job; _logger = logger;
        _projectRoot = projectRoot;
        _output = ReadOutputAsync();
        _errors = ReadErrorsAsync();
    }

    public static async Task<CodexTaskBridgeLease> StartAsync(ProjectRecord project, RuntimeDiscovery discovery,
        string stateDirectory, CancellationToken cancellationToken, RuntimePaths? runtimePaths = null)
    {
        if (!project.AllowCodexTasks || !project.AllowCodexWrite) throw new InvalidOperationException("项目尚未授权可写 Codex 委派。");
        var paths = runtimePaths ?? discovery.Discover();
        if (!paths.NodeReady || !paths.CodexReady || !File.Exists(paths.CodexShim))
            throw new InvalidOperationException("可写任务需要 Node.js、Codex CLI 和本机任务启动组件。");
        var info = new ProcessStartInfo(paths.CodexShim!) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = ProjectPathGuard.CanonicalizeProjectRoot(project.Path)
        };
        info.ArgumentList.Add("app-server");
        info.Environment["LPB_NODE"] = paths.Node;
        info.Environment["LPB_CODEX_SCRIPT"] = paths.CodexScript;
        info.Environment["LPB_PROJECT_ROOT"] = info.WorkingDirectory;
        info.Environment["LPB_CODEX_ALLOW_WRITE"] = "1";
        info.Environment.Remove("NODE_OPTIONS");
        var job = JobObject.CreateKillOnClose();
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 Codex 任务组件。"); }
        catch { job.Dispose(); throw; }
        try { job.Attach(process); }
        catch { process.Kill(true); process.Dispose(); job.Dispose(); throw; }
        var bridge = new NativeCodexTaskBridge(process, job, new RedactingLogger(stateDirectory), info.WorkingDirectory);
        try
        {
            await bridge.RequestAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject {
                ["name"] = "projectbridge-delegated-task", ["version"] = "1"
            } }, cancellationToken).ConfigureAwait(false);
            await bridge.WriteAsync(new JsonObject { ["method"] = "initialized" }, cancellationToken).ConfigureAwait(false);
            return new CodexTaskBridgeLease(bridge, bridge);
        }
        catch { await bridge.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<JsonObject?> CallToolAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        if (tool == "codex_status")
        {
            var state = FindTask(arguments);
            if (arguments["waitFor"]?.GetValue<string>() is "terminal" or "change" && !state.Completed.Task.IsCompleted)
            {
                try { await state.Completed.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(arguments["waitMs"]?.GetValue<int>() ?? 1000, 1, 10000)), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            return Snapshot(state);
        }
        await _commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || _process.HasExited) throw new CodexTaskBridgeException("Codex 任务组件已停止，请重新授权或重新连接。");
            if (tool == "codex_cancel")
            {
                var state = FindTask(arguments);
                if (!state.Completed.Task.IsCompleted && state.TurnId is not null)
                {
                    try
                    {
                        await RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = state.ThreadId, ["turnId"] = state.TurnId }, cancellationToken).ConfigureAwait(false);
                        await state.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    }
                    catch { await DisposeAsync().ConfigureAwait(false); }
                }
                return Snapshot(state);
            }
            if (tool != "codex_task") throw new CodexTaskBridgeException("不支持此任务操作。");
            var requestId = arguments["requestId"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
            if (_requests.TryGetValue(requestId, out var existing)) return Snapshot(_tasks[existing]);
            if (_tasks.Values.Any(state => !state.Completed.Task.IsCompleted))
                throw new CodexTaskBridgeException("该项目已有运行中的 Codex 任务，请等待完成或先停止。");
            // Test the actual sandbox before accepting each new write task. A
            // policy label or Windows setup success does not prove ACL access.
            await VerifyWorkspaceWriteAsync(cancellationToken).ConfigureAwait(false);
            var task = new TaskState(Guid.NewGuid().ToString());
            _tasks[task.Id] = task;
            _requests[requestId] = task.Id;
            try
            {
                var thread = await RequestAsync("thread/start", new JsonObject {
                    ["sandbox"] = "workspace-write", ["approvalPolicy"] = "never",
                    ["ephemeral"] = false, ["persistExtendedHistory"] = true
                }, cancellationToken).ConfigureAwait(false);
                task.ThreadId = thread["thread"]?["id"]?.GetValue<string>() ?? throw new CodexTaskBridgeException("Codex 没有返回任务会话编号。");
                var title = arguments["activityTitle"]?.GetValue<string>()?.Trim();
                title = string.IsNullOrWhiteSpace(title) ? "ChatGPT 委派任务" : "ChatGPT · " + title;
                await RequestAsync("thread/name/set", new JsonObject {
                    ["threadId"] = task.ThreadId, ["name"] = title[..Math.Min(title.Length, 120)]
                }, cancellationToken).ConfigureAwait(false);
                var prompt = arguments["prompt"]?.GetValue<string>()?.Trim() ?? "";
                if (!prompt.StartsWith("|from_chatgpt|:", StringComparison.Ordinal)) prompt = "|from_chatgpt|:\n" + prompt;
                var turn = await RequestAsync("turn/start", new JsonObject {
                    ["threadId"] = task.ThreadId,
                    ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt })
                }, cancellationToken).ConfigureAwait(false);
                task.TurnId = turn["turn"]?["id"]?.GetValue<string>() ?? throw new CodexTaskBridgeException("Codex 没有返回任务轮次编号。");
                return Snapshot(task);
            }
            catch (Exception error)
            {
                Finish(task, "failed", error.Message);
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _commands.Release(); }
    }

    private TaskState FindTask(JsonObject args)
        => args["jobId"]?.GetValue<string>() is { } id && _tasks.TryGetValue(id, out var state)
            ? state : throw new CodexTaskBridgeException("未找到该项目中的任务。");

    private async Task VerifyWorkspaceWriteAsync(CancellationToken cancellationToken)
    {
        var probe = Path.Combine(_projectRoot, $".projectbridge-write-probe-{Guid.NewGuid():N}.tmp");
        var quoted = probe.Replace("'", "''", StringComparison.Ordinal);
        const string marker = "projectbridge-write-probe-ok";
        try
        {
            var response = await RequestAsync("command/exec", new JsonObject {
                ["command"] = new JsonArray("powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                    $"$ErrorActionPreference='Stop'; try {{ New-Item -ItemType File -Path '{quoted}' -Value '{marker}' -ErrorAction Stop | Out-Null; "
                    + $"Get-Content -LiteralPath '{quoted}' -Raw }} finally {{ if (Test-Path -LiteralPath '{quoted}') {{ Remove-Item -LiteralPath '{quoted}' -Force }} }}"),
                ["timeoutMs"] = 10000
            }, cancellationToken).ConfigureAwait(false);
            if (response["exitCode"]?.GetValue<int>() != 0 || response["stdout"]?.GetValue<string>().Trim() != marker)
                throw new CodexTaskBridgeException($"workspace_write_unavailable：项目已授权，但 Codex 沙盒无法写入 {_projectRoot}，任务未启动。"
                    + "请在 Codex 中为该目录完成 Windows 沙盒设置，或选择当前用户拥有的项目目录。"
                    + $" 原因：{response["stderr"]?.GetValue<string>() ?? "写入探测未通过"}");
        }
        finally
        {
            // Only the uniquely named probe owned by this invocation is removed.
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private static JsonObject Snapshot(TaskState state)
    {
        lock (state) return new ToolOutcome(new JsonObject {
            ["jobId"] = state.Id, ["threadId"] = state.ThreadId, ["status"] = state.Status,
            ["threadUrl"] = state.ThreadId is null ? null : $"codex://threads/{state.ThreadId}",
            ["result"] = state.Text, ["error"] = state.Error, ["sandbox"] = "workspace-write",
            ["writeAccessVerified"] = true,
            ["toolFailures"] = new JsonArray(state.ToolFailures.Select(item => (JsonNode)item.DeepClone()).ToArray()),
            ["completionMeaning"] = "Codex turn ended; task success must be checked against the result and toolFailures."
        }.ToJsonString()).ToContent();
    }

    private static void Finish(TaskState state, string status, string? error = null)
    {
        lock (state)
        {
            if (state.Completed.Task.IsCompleted) return;
            state.Status = status; state.Error = error; state.Completed.TrySetResult();
        }
    }

    private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (response["error"] is { } error) throw new CodexTaskBridgeException(error["message"]?.GetValue<string>() ?? "Codex 请求失败。");
            return response["result"]?.AsObject() ?? new JsonObject();
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _input.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _process.StandardInput.WriteLineAsync(message.ToJsonString()); await _process.StandardInput.FlushAsync(cancellationToken); }
        finally { _input.Release(); }
    }

    private async Task ReadOutputAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject message) continue;
                if (message["method"] is not null && message.ContainsKey("id"))
                {
                    await WriteAsync(new JsonObject { ["id"] = message["id"]!.DeepClone(), ["error"] = new JsonObject {
                        ["code"] = -32601, ["message"] = "Delegated tasks cannot request additional approvals or interactive input."
                    } }, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
                if (message["id"] is JsonValue value && value.TryGetValue<long>(out var id))
                {
                    if (_pending.TryGetValue(id, out var completion)) completion.TrySetResult(message);
                    continue;
                }
                var parameters = message["params"];
                var threadId = parameters?["threadId"]?.GetValue<string>();
                var task = _tasks.Values.FirstOrDefault(state => state.ThreadId == threadId && threadId is not null && !state.Completed.Task.IsCompleted);
                if (task is null) continue;
                switch (message["method"]?.GetValue<string>())
                {
                    case "item/agentMessage/delta":
                        lock (task) task.Text += parameters?["delta"]?.GetValue<string>() ?? "";
                        break;
                    case "item/completed" when (parameters?["item"]?["type"]?.GetValue<string>() == "agentMessage"):
                        lock (task) task.Text = parameters?["item"]?["text"]?.GetValue<string>() ?? task.Text;
                        break;
                    case "item/completed":
                        if (parameters?["item"] is JsonObject item
                            && (item["type"]?.GetValue<string>() == "commandExecution" && item["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var code) && code != 0
                                || item["type"]?.GetValue<string>() == "fileChange" && item["status"]?.GetValue<string>() is "failed" or "declined"))
                            lock (task) task.ToolFailures.Add((JsonObject)item.DeepClone());
                        break;
                    case "turn/completed":
                        Finish(task, parameters?["turn"]?["status"]?.GetValue<string>() ?? "failed", parameters?["turn"]?["error"]?.ToJsonString());
                        break;
                }
            }
        }
        catch (Exception error) { await _logger.WriteAsync("error", "Codex task output: " + error.Message).ConfigureAwait(false); }
        finally
        {
            lock (_job) _job.Dispose();
            foreach (var pending in _pending.Values) pending.TrySetException(new CodexTaskBridgeException("Codex 任务进程已退出。"));
            foreach (var state in _tasks.Values) Finish(state, "failed", "Codex 任务进程已退出。");
        }
    }

    private async Task ReadErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            await _logger.WriteAsync("info", "Codex task: " + line).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is { IsFaulted: true } or { IsCanceled: true }) _disposeTask = null;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_disposed) return;
        lock (_job) _job.Dispose();
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.WhenAll(_output, _errors).ConfigureAwait(false);
        _disposed = true;
        _process.Dispose();
    }
}
