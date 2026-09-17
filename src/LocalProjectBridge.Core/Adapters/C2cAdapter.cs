using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Security.Cryptography;
using LocalProjectBridge.Core.Processes;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Collaboration;

namespace LocalProjectBridge.Core.Adapters;

/// <summary>
/// C2C（codex-with-chatgpt）适配器（设计文档 6.7）：ProjectReader（只读审查）
/// 与 ConversationRelay（Codex 请求网页协作）都由该连接承载。
/// </summary>
public sealed partial class C2cAdapter : ConnectableAdapterBase, IConnectionReadinessSource
{
    private readonly CommandRunner _runner;
    private readonly RuntimeDiscovery _discovery;
    private readonly bool _serveWebRead;
    private readonly RuntimePaths? _runtimePaths;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _disposal = new();
    private Task? _disposeTask;
    private readonly HttpClient _localHttp = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _bridge;
    private JobObject? _job;
    private Task? _stdout;
    private Task? _stderr;
    private CancellationTokenSource? _monitorCancellation;
    private string? _ownedWorkspace;
    private string? _lastTunnelError;
    private int _port;
    private string? _observedWorkspaceName;
    private string? _observedConnectorName;
    private string? _observedMcpUrl;
    private readonly ProjectAuthorizationRegistry? _sharedProjects;
    private GatewayServer? _sharedGateway;
    private SharedCodexTaskRuntime? _taskRuntime;
    private SharedCodexTaskTools? _taskTools;
    private readonly Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>>? _taskBridgeFactory;
    private readonly ProjectWriteService? _writeService;
    private readonly CollaborationStore? _collaborationStore;
    private readonly Guid? _connectionId;
    private ConnectionReadiness _readiness = ConnectionReadiness.Stopped;
    public bool IsSharedConnection => _sharedProjects is not null;
    public string ConnectionWorkspace => Directory.CreateDirectory(Path.Combine(_stateDirectory, "ProjectBridge")).FullName;

    public C2cAdapter(CommandRunner runner, RuntimeDiscovery? discovery = null, bool serveWebRead = true,
        string? stateDirectory = null, RuntimePaths? runtimePaths = null, ProjectAuthorizationRegistry? sharedProjects = null,
        Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>>? taskBridgeFactory = null,
        ProjectWriteService? writeService = null, Guid? connectionId = null, CollaborationStore? collaborationStore = null)
    {
        _runner = runner;
        _discovery = discovery ?? new RuntimeDiscovery();
        _serveWebRead = serveWebRead;
        _runtimePaths = runtimePaths;
        _stateDirectory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalProjectBridge", "c2c");
        _sharedProjects = sharedProjects;
        _taskBridgeFactory = taskBridgeFactory;
        _writeService = writeService;
        _connectionId = connectionId;
        _collaborationStore = collaborationStore;
    }

    public override string Name => "codex-with-chatgpt";

    public override bool IsRequiredFor(CapabilityFlags capabilities)
        => (_serveWebRead && capabilities.Allows(CapabilityFlags.WebRead))
            || capabilities.Allows(CapabilityFlags.CodexAskWeb);

    /// <summary>最近一次启动观察到的工作区名称（用于连接状态展示与身份核对）。</summary>
    public string? ObservedWorkspaceName => _observedWorkspaceName;
    public string? ObservedConnectorName => _observedConnectorName;
    public string? ObservedMcpUrl => _observedMcpUrl;
    public ConnectionReadiness Readiness => _readiness;
    public event EventHandler<ConnectionReadinessChangedEventArgs>? ReadinessChanged;

    public override Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        var paths = Discover();
        var missing = new List<string>();
        if (!paths.NodeReady) missing.Add("Node.js");
        if (!paths.C2cReady) missing.Add("只读审查组件（c2c 与 cloudflared）");
        if (missing.Count > 0)
            throw new InvalidOperationException($"只读审查所需组件尚未就绪：{string.Join("、", missing)}。");
        return Task.CompletedTask;
    }

    public override async Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        if (IsSharedConnection) policy = policy with { CanonicalProjectPath = ConnectionWorkspace, ProjectDisplayName = "ProjectBridge" };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
        await _lifecycleGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _ownedWorkspace = policy.CanonicalProjectPath;
            await StartManagedBridgeAsync(policy, lifetime.Token).ConfigureAwait(false);
            await StartOnceAsync(policy, lifetime.Token).ConfigureAwait(false);
            _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            _ = MonitorAsync(policy, _monitorCancellation.Token);
        }
        catch (Exception error)
        {
            var tunnelError = _lastTunnelError;
            await StopCoreAsync().ConfigureAwait(false);
            if (error is not OperationCanceledException && !string.IsNullOrWhiteSpace(tunnelError))
                throw new InvalidOperationException($"连接失败：{_runner.Redact(tunnelError)}", error);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartOnceAsync(SessionPolicy policy, CancellationToken cancellationToken)
    {
        using var json = await RunJsonAsync(["start", "-w", policy.CanonicalProjectPath, "--tunnel", "--json"], cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;
        _observedMcpUrl = RequiredString(root, "mcpUrl");
        _observedConnectorName = root.TryGetProperty("connectorName", out var connector) && !string.IsNullOrWhiteSpace(connector.GetString())
            ? connector.GetString()!
            : $"Codex with ChatGPT · {policy.ProjectDisplayName}";
        _observedWorkspaceName = root.TryGetProperty("workspaceName", out var workspace) && !string.IsNullOrWhiteSpace(workspace.GetString())
            ? workspace.GetString()!
            : policy.ProjectDisplayName;
        SetReadiness(_readiness with { TransportReady = true });
    }

    /// <summary>核对本程序拥有的共享工作区、本地服务和公网隧道。</summary>
    public override async Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_observedWorkspaceName))
            throw new InvalidOperationException("只读审查组件没有返回工作区名称。");
        if (string.IsNullOrWhiteSpace(_observedMcpUrl)
            || !_observedMcpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只读审查组件没有返回有效的公网安全隧道地址。");
        using var status = await RunJsonAsync(["status", "-w", policy.CanonicalProjectPath, "--json"], cancellationToken).ConfigureAwait(false);
        VerifyOwnedStatus(status.RootElement, requireTunnel: true);
        var authorized = status.RootElement.TryGetProperty("tokenCount", out var count)
            && count.TryGetInt32(out var value) && value > 0;
        SetReadiness(_readiness with { LocalReady = true, TransportReady = true, ClientAuthorized = authorized });
        return authorized
            ? $"工作区“{_observedWorkspaceName}”的本地服务、隧道和 OAuth 授权已就绪。"
            : $"工作区“{_observedWorkspaceName}”的本地服务与隧道已就绪，等待网页授权。";
    }

    public override async Task StopAsync(SessionPolicy policy)
    {
        if (_disposeTask is not null) { await _disposeTask.ConfigureAwait(false); return; }
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    // ---- 以下命令供首次设置向导使用（设计 4.2），不属于日常连接流程 ----

    /// <summary>设置流程的临时连接清理（向导关闭时调用）。</summary>
    public Task DisconnectAsync(string workspace) => StopAsync(MakeWorkspaceOnlyPolicy(workspace));

    /// <summary>Observe real OAuth grants on this owned bridge, without exposing tokens to the UI.</summary>
    public async Task<bool> HasAuthorizationAsync(string workspace, CancellationToken cancellationToken = default)
    {
        using var status = await RunJsonAsync(["status", "-w", workspace, "--json"], cancellationToken).ConfigureAwait(false);
        VerifyOwnedStatus(status.RootElement, requireTunnel: false);
        var authorized = status.RootElement.TryGetProperty("tokenCount", out var count)
            && count.TryGetInt32(out var value) && value > 0;
        SetReadiness(_readiness with { ClientAuthorized = authorized });
        return authorized;
    }

    public Task RefreshSharedPermissionsAsync(CancellationToken cancellationToken = default)
        => _taskTools is { } tasks && _sharedProjects is { } projects
            ? tasks.ReconcileAsync(projects.DelegableProjects(), cancellationToken) : Task.CompletedTask;

    private Task<CodexTaskBridgeLease> StartTaskBridgeAsync(ProjectRecord project, CancellationToken cancellationToken)
        => ProjectTaskBridgeFactory.StartAsync(Path.GetDirectoryName(_stateDirectory)!, _discovery, project, cancellationToken);

    public async Task<C2cTunnelChoice> GetTunnelChoiceAsync(string workspace, CancellationToken cancellationToken = default)
    {
        using var json = await RunJsonAsync(["tunnel", "status", "-w", workspace, "--json"], cancellationToken);
        var root = json.RootElement;
        return new C2cTunnelChoice(
            root.TryGetProperty("needsChoice", out var needsChoice) && needsChoice.GetBoolean(),
            root.TryGetProperty("userPrompt", out var prompt) ? prompt.GetString() : null,
            root.TryGetProperty("loginPrompt", out var login) ? login.GetString() : null);
    }

    public async Task ChooseConnectionAsync(string workspace, bool fixedDomain, string? domain, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "tunnel", "choose", "-w", workspace, "--mode", fixedDomain ? "named" : "quick" };
        if (fixedDomain)
        {
            if (string.IsNullOrWhiteSpace(domain)) throw new InvalidOperationException("使用固定域名时需要填写已经接入 Cloudflare 的域名。");
            arguments.AddRange(["--zone", domain.Trim()]);
        }
        arguments.Add("--json");
        using var _ = await RunJsonAsync(arguments, cancellationToken);
    }

    public async Task<C2cSetupResult> StartForSetupAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        var policy = SessionPolicyFactory.Create(
            project,
            CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb);
        await StartAsync(policy, cancellationToken);
        if (_observedWorkspaceName is null || _observedConnectorName is null || _observedMcpUrl is null)
            throw new InvalidOperationException("只读审查组件没有返回连接信息。");
        return new C2cSetupResult(_observedWorkspaceName, _observedConnectorName, _observedMcpUrl);
    }

    public async Task<C2cSetupResult> StartForSetupAsync(
        ConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        var policy = SessionPolicyFactory.CreateConnection(
            profile,
            ConnectionWorkspace,
            CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb);
        await StartAsync(policy, cancellationToken).ConfigureAwait(false);
        if (_observedWorkspaceName is null || _observedConnectorName is null || _observedMcpUrl is null)
            throw new InvalidOperationException("只读审查组件没有返回连接信息。");
        return new C2cSetupResult(_observedWorkspaceName, _observedConnectorName, _observedMcpUrl);
    }

    /// <summary>显式解除本适配器工作区的 OAuth 配对；普通断开不会调用。</summary>
    public async Task ForgetAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        var workspace = _ownedWorkspace ?? ConnectionWorkspace;
        await RunStrictAsync(["unpair", "-w", workspace], cancellationToken).ConfigureAwait(false);
        SetReadiness(_readiness with { ClientAuthorized = false, LastVerifiedCall = null, ClientIdentity = null });
    }

    public async Task<string> CreatePairingCodeAsync(string workspace, CancellationToken cancellationToken = default)
    {
        if (_bridge is null || _bridge.HasExited) throw new InvalidOperationException("本地连接已停止，请重新准备连接。");
        using var json = await RunJsonAsync(["pair", "-w", workspace, "--json"], cancellationToken);
        return RequiredString(json.RootElement, "pairingCode");
    }

    public async Task<string?> DoctorAsync(string workspace, CancellationToken cancellationToken = default)
    {
        using var json = await RunJsonAsync(["doctor", "-w", workspace, "--no-fix", "--json"], cancellationToken);
        var root = json.RootElement;
        if (root.TryGetProperty("chatgptRepair", out var repair)
            && repair.TryGetProperty("needed", out var needed) && needed.GetBoolean())
            return repair.TryGetProperty("userMessage", out var message) ? message.GetString() : "需要重新连接 ChatGPT。";
        if (root.TryGetProperty("namedRepair", out var named)
            && named.TryGetProperty("needed", out var namedNeeded) && namedNeeded.GetBoolean())
            return named.TryGetProperty("userMessage", out var message) ? message.GetString() : "固定连接需要重新登录。";
        return null;
    }

    private async Task<JsonDocument> RunJsonAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var requestedArguments = arguments.ToArray();
        var phase = requestedArguments.Length > 1 && requestedArguments[0] == "tunnel"
            ? $"{requestedArguments[0]} {requestedArguments[1]}"
            : requestedArguments.FirstOrDefault() ?? "未知步骤";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { return await RunJsonOnceAsync(requestedArguments, cancellationToken).ConfigureAwait(false); }
            catch (JsonException) when (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException($"只读审查组件在“{phase}”阶段返回了无法识别的状态。", error);
            }
        }
        throw new InvalidOperationException("只读审查组件返回了无法识别的状态。");
    }

    private async Task<JsonDocument> RunJsonOnceAsync(string[] arguments, CancellationToken cancellationToken)
    {
        arguments = ConnectionArguments(arguments);
        var paths = Discover();
        if (!paths.NodeReady || !File.Exists(paths.C2cCli)) throw new InvalidOperationException("只读审查组件尚未安装。");
        var allArguments = new List<string> { paths.C2cCli! };
        allArguments.AddRange(arguments);
        var environment = BuildEnvironment(paths, _stateDirectory);
        var result = await _runner.RunAsync(paths.Node!, allArguments, cancellationToken: cancellationToken, environment: environment, job: _job).ConfigureAwait(false);
        try { return ParseCommandResult(result); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(_runner.Redact(error.Message));
        }
    }

    public static JsonDocument ParseCommandResult(CommandResult result)
    {
        JsonDocument? json = null;
        try { json = ParseJsonOutput(result.StandardOutput); }
        catch (JsonException) when (!result.Success) { }

        var reportedFailure = json is not null && json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;
        if (result.Success && !reportedFailure) return json!;

        // C2C writes --json errors to stdout, even when stderr is empty.
        string? detail = null;
        if (json is not null)
        {
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                detail = error.GetString();
            json.Dispose();
        }
        if (string.IsNullOrWhiteSpace(detail)) detail = result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"只读审查组件执行失败（退出码 {result.ExitCode}）。"
            : $"只读审查组件执行失败：{detail}");
    }

    public static JsonDocument ParseJsonOutput(string standardOutput)
    {
        var cleaned = AnsiSequence().Replace(standardOutput, string.Empty).Trim().TrimStart('\uFEFF');
        try { return JsonDocument.Parse(cleaned); }
        catch (JsonException) { }

        var jsonLine = cleaned
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var start = line.IndexOf('{');
                var end = line.LastIndexOf('}');
                return start >= 0 && end > start ? line[start..(end + 1)] : line;
            })
            .LastOrDefault(line => line.StartsWith('{') && line.EndsWith('}'));
        if (jsonLine is not null) return JsonDocument.Parse(jsonLine);

        for (var start = cleaned.IndexOf('{'); start >= 0; start = cleaned.IndexOf('{', start + 1))
        {
            for (var end = cleaned.LastIndexOf('}'); end > start; end = cleaned.LastIndexOf('}', end - 1))
            {
                try { return JsonDocument.Parse(cleaned[start..(end + 1)]); }
                catch (JsonException) { }
            }
        }
        throw new JsonException("C2C 没有返回可解析的 JSON 对象。");
    }

    private async Task RunStrictAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var paths = Discover();
        if (!paths.NodeReady || !File.Exists(paths.C2cCli))
            throw new InvalidOperationException("只读审查组件尚未安装，无法确认配对已解除。");
        var allArguments = new List<string> { paths.C2cCli! };
        allArguments.AddRange(ConnectionArguments(arguments.ToArray()));
        var environment = BuildEnvironment(paths, _stateDirectory);
        var result = await _runner.RunAsync(
            paths.Node!, allArguments, cancellationToken: cancellationToken, environment: environment, job: _job).ConfigureAwait(false);
        if (result.Success) return;
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"解除配对失败（退出码 {result.ExitCode}），原授权状态保持不变。"
            : $"解除配对失败：{_runner.Redact(detail)}");
    }

    /// <summary>设置流程只需要一个共享工作区策略，用于显式解除配对或停止。</summary>
    private static SessionPolicy MakeWorkspaceOnlyPolicy(string workspace)
        => new(
            Guid.Empty,
            Guid.Empty,
            Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar)),
            ProjectPathGuard.CanonicalizeProjectRoot(workspace),
            CapabilityFlags.None,
            DateTimeOffset.Now,
            null);

    private static string RequiredString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
        throw new InvalidOperationException($"只读审查组件没有返回 {property}。");
    }

    [GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiSequence();

    private RuntimePaths Discover() => _runtimePaths ?? _discovery.Discover();

    private string[] ConnectionArguments(string[] arguments)
    {
        if (!IsSharedConnection) return arguments;
        var result = arguments.ToArray();
        for (var i = 0; i < result.Length - 1; i++)
            if (result[i] is "-w" or "--workspace") result[++i] = ConnectionWorkspace;
        return result;
    }

    public static IReadOnlyDictionary<string, string?> BuildEnvironment(RuntimePaths paths, string stateDirectory)
    {
        var environment = new Dictionary<string, string?>
        {
            ["C2C_STATE_DIR"] = Path.GetFullPath(stateDirectory),
            ["C2C_CLOUDFLARED_PATH"] = paths.Cloudflared,
            ["C2C_LOG_LEVEL"] = "debug",
            ["NO_COLOR"] = "1"
        };
        var runtimeDirectory = Path.GetDirectoryName(paths.Cloudflared);
        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
            environment["PATH"] = runtimeDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        return environment;
    }

    private async Task StartManagedBridgeAsync(SessionPolicy policy, CancellationToken cancellationToken)
    {
        await AssertReadyAsync(policy, cancellationToken).ConfigureAwait(false);
        var paths = Discover();
        Directory.CreateDirectory(_stateDirectory);
        _port = TcpPortAllocator.GetFreePort();
        _lastTunnelError = null;
        var info = new ProcessStartInfo(paths.Node!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(paths.C2cCli!)!
        };
        var arguments = new[] { paths.C2cCli!, "serve", "--workspace", policy.CanonicalProjectPath, "--port", _port.ToString() };
        if (_sharedProjects is not null)
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _taskRuntime = new SharedCodexTaskRuntime(_taskBridgeFactory ?? StartTaskBridgeAsync);
            _taskTools = new SharedCodexTaskTools(_sharedProjects, _taskRuntime);
            var sharedTools = new List<GatewayTool>();
            sharedTools.AddRange(MultiProjectTools.Create(_sharedProjects, _runner, _writeService, _connectionId));
            if (_writeService is not null && _connectionId is { } connectionId)
                sharedTools.AddRange(ProjectWriteTools.Create(_writeService, connectionId));
            if (_collaborationStore is not null && _connectionId is { } relayConnectionId)
                sharedTools.AddRange(CollaborationTools.Create(_collaborationStore, _sharedProjects, relayConnectionId));
            sharedTools.AddRange(_taskTools.Create());
            var dispatcher = new McpDispatcher("ProjectBridge", "1.2", sharedTools);
            _sharedGateway = new GatewayServer(dispatcher, new RedactingLogger(Path.GetDirectoryName(_stateDirectory)!), key);
            _sharedGateway.VerifiedRemoteCall += SharedGateway_VerifiedRemoteCall;
            await _sharedGateway.StartAsync(cancellationToken).ConfigureAwait(false);
            var bootstrap = Path.Combine(_stateDirectory, "shared-bridge.mjs");
            using var resource = typeof(C2cAdapter).Assembly.GetManifestResourceStream("LocalProjectBridge.Core.Backend.shared-bridge.mjs")
                ?? throw new InvalidOperationException("共享连接组件缺失。");
            using var reader = new StreamReader(resource);
            await File.WriteAllTextAsync(bootstrap, await reader.ReadToEndAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            arguments = [bootstrap, Path.GetDirectoryName(Path.GetDirectoryName(paths.C2cCli!)!)!, policy.CanonicalProjectPath, _port.ToString(), _sharedGateway.ListenUrl!];
            info.Environment["LPB_SHARED_KEY"] = key;
        }
        foreach (var arg in arguments)
            info.ArgumentList.Add(arg);
        foreach (var variable in BuildEnvironment(paths, _stateDirectory)) info.Environment[variable.Key] = variable.Value;
        // Recent cloudflared versions run UDP prechecks even with HTTP/2 selected.
        // The shared bridge performs its own authenticated service/health checks.
        if (IsSharedConnection)
        {
            info.Environment["TUNNEL_NO_PRECHECKS"] = "true";
            if (!info.Environment.TryGetValue("C2C_TUNNEL_PROTOCOL", out var protocol) || string.IsNullOrWhiteSpace(protocol))
                info.Environment["C2C_TUNNEL_PROTOCOL"] = "http2";
        }
        _job = JobObject.CreateKillOnClose();
        _bridge = Process.Start(info) ?? throw new InvalidOperationException("无法启动本地项目服务。");
        _job.Attach(_bridge);
        _stdout = DrainOutputAsync(_bridge.StandardOutput);
        _stderr = DrainOutputAsync(_bridge.StandardError);
        var expectedId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policy.CanonicalProjectPath.ToLowerInvariant())))[..12].ToLowerInvariant();
        var ready = false;
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bridge.HasExited) throw new InvalidOperationException($"本地项目服务已退出（退出码 {_bridge.ExitCode}）。");
            try
            {
                using var health = JsonDocument.Parse(await _localHttp.GetStringAsync($"http://127.0.0.1:{_port}/health", cancellationToken).ConfigureAwait(false));
                ready = health.RootElement.TryGetProperty("workspaceId", out var id) && id.GetString() == expectedId;
                if (ready) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        if (!ready) throw new TimeoutException("本地项目服务未能及时就绪。");
        SetReadiness(_readiness with { LocalReady = true });
        using var status = await RunJsonAsync(["status", "-w", policy.CanonicalProjectPath, "--json"], cancellationToken).ConfigureAwait(false);
        VerifyOwnedStatus(status.RootElement, requireTunnel: false);
    }

    private void VerifyOwnedStatus(JsonElement status, bool requireTunnel)
    {
        if (_bridge is null || _bridge.HasExited || !status.TryGetProperty("pid", out var pid) || pid.GetInt32() != _bridge.Id
            || !status.TryGetProperty("workspaceRoot", out var root) || !string.Equals(root.GetString(), _ownedWorkspace, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本地服务与当前连接工作区不一致，连接已停止。");
        if (requireTunnel && (!status.TryGetProperty("tunnel", out var tunnel)
            || !tunnel.TryGetProperty("running", out var running) || running.ValueKind != JsonValueKind.True))
            throw new InvalidOperationException("公网隧道已停止，连接需要重新建立。");
    }

    private async Task DrainOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Contains("cloudflared:", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(line, @"\b(ERR|fatal|failed)\b", RegexOptions.IgnoreCase))
                _lastTunnelError = _runner.Redact(line);
            await _runner.LogAsync("info", $"c2c: {line}").ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(SessionPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                await VerifyAsync(policy, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!cancellationToken.IsCancellationRequested) RaiseFaulted(_runner.Redact(error.Message));
        }
    }

    private async Task StopCoreAsync()
    {
        if (_sharedGateway is not null) _writeService?.RevokeAllAutoApply();
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        var bridge = _bridge;
        _job?.Dispose();
            _job = null;
            if (bridge is not null)
            {
                try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (bridge.HasExited) { }
                await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                await Task.WhenAll(_stdout ?? Task.CompletedTask, _stderr ?? Task.CompletedTask).ConfigureAwait(false);
                bridge.Dispose();
            }
            _bridge = null;
            _stdout = _stderr = null;
            _ownedWorkspace = null;
            _port = 0;
            _observedWorkspaceName = _observedConnectorName = _observedMcpUrl = null;
            if (_sharedGateway is not null)
            {
                _sharedGateway.VerifiedRemoteCall -= SharedGateway_VerifiedRemoteCall;
                await _sharedGateway.DisposeAsync().ConfigureAwait(false);
            }
            _sharedGateway = null;
            if (_taskRuntime is not null) await _taskRuntime.DisposeAsync().ConfigureAwait(false);
            _taskRuntime = null;
            _taskTools = null;
            SetReadiness(ConnectionReadiness.Stopped);
    }

    private void SharedGateway_VerifiedRemoteCall(object? sender, VerifiedRemoteCallEventArgs e)
        => SetReadiness(new ConnectionReadiness(true, true, true, e.At, e.ClientIdentity));

    private void SetReadiness(ConnectionReadiness readiness)
    {
        if (_readiness == readiness) return;
        _readiness = readiness;
        ReadinessChanged?.Invoke(this, new ConnectionReadinessChangedEventArgs(readiness));
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposal) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposal.Cancel();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
        _localHttp.Dispose();
    }
}
