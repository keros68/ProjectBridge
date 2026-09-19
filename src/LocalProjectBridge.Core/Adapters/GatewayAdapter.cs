using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Collaboration;
using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Adapters;

/// <summary>
/// 统一网关适配器（设计文档 5 / 13.2 阶段 B）：把网关作为会话的一部分启动与停止。
/// 网关接管 Transceiver 的安全隧道；启用 Codex→网页协作时，C2C 仍作为独立控制面运行。
/// </summary>
public sealed class GatewayAdapter : ConnectableAdapterBase, IConnectionReadinessSource
{
    private readonly RedactingLogger _logger;
    private readonly CommandRunner _runner;
    private readonly RuntimeDiscovery _discovery;
    private readonly ISecureTunnelRuntime _tunnelRuntime;
    private readonly ConnectionProfile _connectionProfile;
    private readonly ProjectAuthorizationRegistry _authorizations;
    private readonly string _stateDirectory;
    private readonly RuntimePaths? _runtimePaths;
    private readonly TimeSpan _monitorInterval;
    private readonly ProjectWriteService? _writeService;
    private readonly CollaborationStore? _collaborationStore;
    private readonly Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>>? _taskBridgeFactory;
    private SharedCodexTaskRuntime? _taskRuntime;
    private SharedCodexTaskTools? _taskTools;
    private GatewayServer? _server;
    private RuntimePaths? _paths;
    private string? _tunnelTarget;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private long _monitorGeneration;
    private ConnectionReadiness _readiness = ConnectionReadiness.Stopped;

    public GatewayAdapter(
        RedactingLogger logger,
        CommandRunner runner,
        RuntimeDiscovery? discovery = null,
        ConnectionProfile? connectionProfile = null,
        ProjectAuthorizationRegistry? authorizations = null,
        string? stateDirectory = null,
        RuntimePaths? runtimePaths = null,
        ISecureTunnelRuntime? tunnelRuntime = null,
        TimeSpan? monitorInterval = null,
        ProjectWriteService? writeService = null,
        CollaborationStore? collaborationStore = null,
        Func<ProjectRecord, CancellationToken, Task<CodexTaskBridgeLease>>? taskBridgeFactory = null)
    {
        _logger = logger;
        _runner = runner;
        _discovery = discovery ?? new RuntimeDiscovery();
        _tunnelRuntime = tunnelRuntime ?? new SecureTunnelRuntime(runner);
        _connectionProfile = connectionProfile ?? ProfileFromEnvironment();
        _authorizations = authorizations ?? new ProjectAuthorizationRegistry();
        _stateDirectory = stateDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalProjectBridge", "secure-tunnel");
        _runtimePaths = runtimePaths;
        _monitorInterval = monitorInterval ?? TimeSpan.FromSeconds(5);
        _writeService = writeService;
        _collaborationStore = collaborationStore;
        _taskBridgeFactory = taskBridgeFactory;
    }

    public override string Name => "unified-gateway";
    public ConnectionReadiness Readiness => _readiness;
    internal string? ListenUrl => _server?.ListenUrl;
    public event EventHandler<ConnectionReadinessChangedEventArgs>? ReadinessChanged;

    public override bool IsRequiredFor(CapabilityFlags capabilities) => capabilities != CapabilityFlags.None;

    public override async Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        var paths = Discover();
        if (!paths.TunnelReady) throw new InvalidOperationException(
            "缺少内置隧道组件 tunnel-client.exe，请重新安装 ProjectBridge，或完整解压免安装包并保留 runtime 文件夹。");
        await _tunnelRuntime.AssertRuntimeCredentialReadyAsync(_connectionProfile).ConfigureAwait(false);
    }

    public override async Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        await StopAsync(policy).ConfigureAwait(false);
        var tools = new List<GatewayTool>();
        if (policy.Allows(CapabilityFlags.WebRead))
            tools.AddRange(MultiProjectTools.Create(_authorizations, _runner, _writeService, _connectionProfile.Id));
        if (_writeService is not null)
            tools.AddRange(ProjectWriteTools.Create(_writeService, _connectionProfile.Id));
        if (_collaborationStore is not null)
            tools.AddRange(CollaborationTools.Create(_collaborationStore, _authorizations, _connectionProfile.Id));
        if (tools.Count == 0)
            throw new InvalidOperationException("OpenAI Secure Tunnel 的 P0 连接至少需要网页读取能力。");
        _taskRuntime = new SharedCodexTaskRuntime(_taskBridgeFactory ?? ((project, token) =>
            ProjectTaskBridgeFactory.StartAsync(_stateDirectory, _discovery, project, token)));
        _taskTools = new SharedCodexTaskTools(_authorizations, _taskRuntime);
        tools.AddRange(_taskTools.Create());
        var dispatcher = new McpDispatcher("local-project-bridge", "0.4.0", tools);
        var privatePath = "/mcp/" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _server = new GatewayServer(dispatcher, _logger, requestPath: privatePath, trustAllRequestsAsRemote: true,
            trustedRemoteIdentity: $"secure-tunnel:{_connectionProfile.TunnelId}");
        _server.VerifiedRemoteCall += Server_VerifiedRemoteCall;
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        SetReadiness(_readiness with { LocalReady = true });
        _paths = Discover();
        _tunnelTarget = _server.ListenUrl ?? throw new InvalidOperationException("统一网关没有提供监听地址。");
        await _tunnelRuntime.ConnectAsync(
            _paths.TunnelClient!, _connectionProfile, _tunnelTarget, _stateDirectory, cancellationToken).ConfigureAwait(false);
        SetReadiness(_readiness with { TransportReady = true });
    }

    public override async Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        var url = _server?.ListenUrl ?? throw new InvalidOperationException("统一网关没有启动。");
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var request = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""";
        using var content = new System.Net.Http.StringContent(request, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TimeoutException("统一网关初始化探测失败。");
        if (_paths is null || _tunnelTarget is null
            || !(await _tunnelRuntime.ProbeHealthAsync(
                _paths.TunnelClient!, _connectionProfile, _tunnelTarget, _stateDirectory, cancellationToken).ConfigureAwait(false)).IsHealthy)
            throw new TimeoutException("统一网关隧道没有指向本次会话地址。");
        SetReadiness(new ConnectionReadiness(true, true, false, null));
        await StartMonitorAsync().ConfigureAwait(false);
        return "本地网关与 OpenAI Secure Tunnel 已就绪，等待网页真实调用。";
    }

    public override async Task StopAsync(SessionPolicy policy)
    {
        if (_server is not null) _writeService?.RevokeAllAutoApply();
        await StopMonitorAsync().ConfigureAwait(false);
        var server = _server;
        if (server is not null) server.VerifiedRemoteCall -= Server_VerifiedRemoteCall;
        _server = null;
        _tunnelTarget = null;
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
        await StopTasksAsync().ConfigureAwait(false);
        SetReadiness(_readiness with { LocalReady = false, TransportReady = false, ClientAuthorized = false });
        if (_paths?.TunnelClient is { } tunnelClient)
            await _tunnelRuntime.StopOwnedAsync(tunnelClient, _connectionProfile, _stateDirectory).ConfigureAwait(false);
        _paths = null;
        SetReadiness(ConnectionReadiness.Stopped);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_server is not null) _writeService?.RevokeAllAutoApply();
        await StopMonitorAsync().ConfigureAwait(false);
        if (_server is not null)
        {
            _server.VerifiedRemoteCall -= Server_VerifiedRemoteCall;
            await _server.DisposeAsync().ConfigureAwait(false);
        }
        _server = null;
        _tunnelTarget = null;
        await StopTasksAsync().ConfigureAwait(false);
        if (_paths?.TunnelClient is { } tunnelClient)
            await _tunnelRuntime.StopOwnedAsync(tunnelClient, _connectionProfile, _stateDirectory).ConfigureAwait(false);
        _paths = null;
        SetReadiness(ConnectionReadiness.Stopped);
    }

    private void Server_VerifiedRemoteCall(object? sender, VerifiedRemoteCallEventArgs e)
    {
        if (!_readiness.LocalReady || !_readiness.TransportReady) return;
        SetReadiness(new ConnectionReadiness(true, true, true, e.At,
            $"secure-tunnel:{_connectionProfile.TunnelId}"));
    }

    public Task RefreshSharedPermissionsAsync(CancellationToken cancellationToken = default)
        => _taskTools?.ReconcileAsync(_authorizations.DelegableProjects(), cancellationToken) ?? Task.CompletedTask;

    private async Task StopTasksAsync()
    {
        if (_taskRuntime is not null) await _taskRuntime.DisposeAsync().ConfigureAwait(false);
        _taskRuntime = null;
        _taskTools = null;
    }

    private async Task StartMonitorAsync()
    {
        await StopMonitorAsync().ConfigureAwait(false);
        var cancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _monitorGeneration);
        _monitorCancellation = cancellation;
        _monitorTask = MonitorAsync(generation, cancellation.Token);
    }

    private async Task StopMonitorAsync()
    {
        Interlocked.Increment(ref _monitorGeneration);
        var cancellation = _monitorCancellation;
        var monitor = _monitorTask;
        _monitorCancellation = null;
        _monitorTask = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        try
        {
            if (monitor is not null) await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally { cancellation.Dispose(); }
    }

    private async Task MonitorAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_monitorInterval, cancellationToken).ConfigureAwait(false);
                var paths = _paths;
                var target = _tunnelTarget;
                if (paths?.TunnelClient is null || target is null) return;
                var probe = _tunnelRuntime.ProbeHealthAsync(
                    paths.TunnelClient, _connectionProfile, target, _stateDirectory, cancellationToken);
                var health = await probe.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (health.IsHealthy) continue;
                if (generation != Volatile.Read(ref _monitorGeneration) || cancellationToken.IsCancellationRequested) return;

                _writeService?.RevokeAllAutoApply();
                var server = _server;
                if (server is not null) server.VerifiedRemoteCall -= Server_VerifiedRemoteCall;
                _server = null;
                if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
                await StopTasksAsync().ConfigureAwait(false);
                if (generation != Volatile.Read(ref _monitorGeneration) || cancellationToken.IsCancellationRequested) return;
                SetReadiness(_readiness with { LocalReady = false, TransportReady = false, ClientAuthorized = false });
                RaiseFaulted(HealthFailureMessage(health));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != Volatile.Read(ref _monitorGeneration) || cancellationToken.IsCancellationRequested) return;
            _writeService?.RevokeAllAutoApply();
            var server = _server;
            if (server is not null) server.VerifiedRemoteCall -= Server_VerifiedRemoteCall;
            _server = null;
            if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
            try { await StopTasksAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                error = new AggregateException(error, cleanupError);
            }
            SetReadiness(_readiness with { LocalReady = false, TransportReady = false, ClientAuthorized = false });
            RaiseFaulted("OpenAI Secure Tunnel 状态探测失败：" + _runner.Redact(error.Message));
        }
    }

    private static string HealthFailureMessage(SecureTunnelHealth health)
        => !health.ProcessRunning
            ? "OpenAI Secure Tunnel 进程已退出。"
            : !health.TargetMatches
                ? "OpenAI Secure Tunnel 已不再指向本次本地服务。"
                : "OpenAI Secure Tunnel 运行状态已降级。";

    private void SetReadiness(ConnectionReadiness readiness)
    {
        if (_readiness == readiness) return;
        _readiness = readiness;
        ReadinessChanged?.Invoke(this, new ConnectionReadinessChangedEventArgs(readiness));
    }

    private RuntimePaths Discover() => _runtimePaths ?? _discovery.Discover();

    private static ConnectionProfile ProfileFromEnvironment() => new()
    {
        Name = "ProjectBridge",
        Provider = TunnelProvider.OpenAiSecureTunnel,
        TunnelId = Environment.GetEnvironmentVariable("CONTROL_PLANE_TUNNEL_ID"),
        RuntimeCredentialReference = "env:" +
            (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTROL_PLANE_API_KEY"))
                ? "CONTROL_PLANE_API_KEY" : "OPENAI_API_KEY")
    };
}
