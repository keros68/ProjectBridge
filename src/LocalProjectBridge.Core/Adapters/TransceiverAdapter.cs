using System.Net.Http;
using System.Text.Json;
using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Adapters;

/// <summary>
/// Transceiver 适配器（设计文档 6.7）：网页端到 Codex 的任务委派。
/// 会话配置完全由 SessionPolicy 生成，能力开关在这里成为后端强制策略。
/// </summary>
public sealed class TransceiverAdapter : ConnectableAdapterBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly RedactingLogger _logger;
    private readonly string _appDataDirectory;
    private readonly RuntimeDiscovery _discovery;
    private Process? _bridge;
    private JobObject? _job;
    private readonly SecureTunnelRuntime _tunnelRuntime;
    private readonly bool _manageTunnel;
    private CancellationTokenSource? _monitorCancellation;
    private SessionPolicy? _policy;
    private RuntimePaths? _paths;
    private ConnectionProfile? _tunnelProfile;
    private int _port;
    private int _restartAttempts;
    private int _tunnelFailures;
    private bool _stopped;
    private bool _disposed;
    private Task? _disposeTask;
    private readonly object _disposeGate = new();

    public TransceiverAdapter(
        string appDataDirectory,
        RedactingLogger logger,
        RuntimeDiscovery? discovery = null,
        bool manageTunnel = true)
    {
        _appDataDirectory = appDataDirectory;
        _logger = logger;
        _discovery = discovery ?? new RuntimeDiscovery();
        _tunnelRuntime = new SecureTunnelRuntime(new CommandRunner(logger));
        _manageTunnel = manageTunnel;
    }

    public override string Name => "transceiver";

    public override bool IsRequiredFor(CapabilityFlags capabilities) => capabilities.Allows(CapabilityFlags.WebDelegateCodex);

    public string SessionDirectory => _policy is null
        ? throw new InvalidOperationException("尚未建立会话。")
        : Path.Combine(_appDataDirectory, "sessions", _policy.SessionId.ToString("D"), "transceiver");

    public string? BridgeMcpUrl => _port == 0 ? null : $"http://127.0.0.1:{_port}/mcp";

    public override async Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        var paths = _discovery.Discover();
        var missing = new List<string>();
        if (!paths.NodeReady) missing.Add("Node.js");
        if (!paths.CodexReady) missing.Add("Codex CLI");
        if (!paths.BridgeReady) missing.Add("Transceiver 本地桥");
        if (_manageTunnel && !paths.TunnelReady) missing.Add("tunnel-client");
        if (missing.Count > 0)
            throw new InvalidOperationException($"委派 Codex 任务所需组件尚未就绪：{string.Join("、", missing)}。");
        if (_manageTunnel)
        {
            _tunnelProfile = RuntimeProfileFromEnvironment();
            await _tunnelRuntime.AssertRuntimeCredentialReadyAsync(_tunnelProfile).ConfigureAwait(false);
        }
    }

    public override async Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        await StopAsync(policy);
        var paths = _discovery.Discover();
        _policy = policy;
        _paths = paths;
        _stopped = false;
        _restartAttempts = 0;
        _tunnelFailures = 0;
        _port = TcpPortAllocator.GetFreePort();

        var sessionDirectory = SessionDirectory;
        Directory.CreateDirectory(sessionDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, "transceiver.json"),
            BuildSessionConfigJson(policy),
            cancellationToken);

        _job = JobObject.CreateKillOnClose();
        StartBridge(paths, sessionDirectory);
        await WaitForBridgeAsync(cancellationToken).ConfigureAwait(false);
        if (_manageTunnel)
            await _tunnelRuntime.ConnectAsync(paths.TunnelClient!, _tunnelProfile!, BridgeMcpUrl!, TunnelStateDirectory, cancellationToken).ConfigureAwait(false);

        _monitorCancellation?.Dispose();
        _monitorCancellation = new CancellationTokenSource();
        _ = MonitorAsync(_monitorCancellation.Token);
    }

    public override async Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        await WaitForBridgeAsync(cancellationToken).ConfigureAwait(false);
        if (_manageTunnel && _paths is not null
            && !await _tunnelRuntime.IsReadyForTargetAsync(_paths.TunnelClient!, _tunnelProfile!, BridgeMcpUrl!, TunnelStateDirectory, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("隧道未进入就绪状态，可能需要重新授权或检查网络。");
        return _manageTunnel ? "本地桥与隧道就绪。" : "本地任务桥就绪，统一网关将接管隧道。";
    }

    /// <summary>会话配置只由策略生成：能力开关在这里成为后端强制策略，而不是界面选项。</summary>
    public static string BuildSessionConfigJson(SessionPolicy policy)
    {
        var config = new
        {
            version = 1,
            unknownProject = "deny",
            sessionId = policy.SessionId,
            projects = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [policy.CanonicalProjectPath] = new
                {
                    name = policy.ProjectDisplayName,
                    sync = policy.Allows(CapabilityFlags.WebRead) || policy.Allows(CapabilityFlags.WebDelegateCodex),
                    allowCodexTasks = policy.Allows(CapabilityFlags.WebDelegateCodex)
                }
            }
        };
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private void StartBridge(RuntimePaths paths, string sessionDirectory)
    {
        var info = new ProcessStartInfo(paths.Node!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        info.ArgumentList.Add(paths.ReverseBridge!);
        info.Environment["TRANSCEIVER_CONFIG"] = Path.Combine(sessionDirectory, "transceiver.json");
        info.Environment["TRANSCEIVER_STATE_DIR"] = Path.Combine(sessionDirectory, "state");
        if (paths.TunnelClient is not null) info.Environment["TRANSCEIVER_TUNNEL_CLIENT"] = paths.TunnelClient;
        info.Environment["TRANSCEIVER_REVERSE_PORT"] = _port.ToString();
        info.Environment["CODEX"] = paths.CodexShim!;
        // 与启动器保持一致的运行时发现结果，CodexShim 优先使用这两个值
        info.Environment["LPB_NODE"] = paths.Node!;
        info.Environment["LPB_CODEX_SCRIPT"] = paths.CodexScript ?? string.Empty;
        info.Environment["LPB_PROJECT_ROOT"] = _policy!.CanonicalProjectPath;
        info.Environment["LPB_CODEX_ALLOW_WRITE"] = "0";
        info.Environment["CODEX_MCP_BRIDGE_DEFAULT_ACCESS_STRATEGY"] = "read-only";
        info.Environment["CODEX_MCP_BRIDGE_ALLOW_WRITE"] = "0";
        info.Environment["CODEX_MCP_BRIDGE_ALLOW_DANGER_FULL_ACCESS"] = "0";
        info.Environment.Remove("NODE_OPTIONS");
        _bridge = StartLoggedProcess(info, "bridge");
        _job?.Attach(_bridge);
    }

    private Process StartLoggedProcess(ProcessStartInfo info, string component)
    {
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _ = _logger.WriteAsync("info", $"{component}: {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _ = _logger.WriteAsync("error", $"{component}: {e.Data}"); };
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {component}。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WaitForBridgeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bridge?.HasExited == true) throw new InvalidOperationException("本地桥启动后立即退出，请查看诊断日志。");
            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/healthz", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("本地桥健康检查超时。");
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_stopped)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (_stopped) return;
                if (_bridge?.HasExited == true)
                {
                    var activePolicy = _policy;
                    var activePaths = _paths;
                    if (_restartAttempts >= 3 || activePolicy is null || activePaths is null)
                    {
                        if (activePolicy is not null) await StopAsync(activePolicy).ConfigureAwait(false);
                        RaiseFaulted("本地桥连续失败，已停止自动重试。");
                        return;
                    }
                    _restartAttempts++;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, _restartAttempts)), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var sessionDirectory = SessionDirectory;
                        StartBridge(activePaths, sessionDirectory);
                        await WaitForBridgeAsync(cancellationToken).ConfigureAwait(false);
                        await _logger.WriteAsync("info", $"bridge recovered after attempt {_restartAttempts}").ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        await _logger.WriteAsync("error", $"bridge recovery failed: {error.Message}").ConfigureAwait(false);
                    }
                }

                if (_manageTunnel && _paths is not null && BridgeMcpUrl is not null
                    && !await _tunnelRuntime.IsReadyForTargetAsync(_paths.TunnelClient!, _tunnelProfile!, BridgeMcpUrl, TunnelStateDirectory, cancellationToken).ConfigureAwait(false))
                {
                    _tunnelFailures++;
                    if (_tunnelFailures > 3)
                    {
                        if (_policy is not null) await StopAsync(_policy).ConfigureAwait(false);
                        RaiseFaulted("隧道连续失败，已停止自动重试。");
                        return;
                    }
                    await _tunnelRuntime.ConnectAsync(_paths.TunnelClient!, _tunnelProfile!, BridgeMcpUrl, TunnelStateDirectory, cancellationToken).ConfigureAwait(false);
                    await _logger.WriteAsync("info", $"tunnel restart requested after failed check {_tunnelFailures}").ConfigureAwait(false);
                }
                else
                {
                    _tunnelFailures = 0;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            await _logger.WriteAsync("error", $"monitor stopped: {error.Message}").ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(SessionPolicy policy)
    {
        if (_disposed) return;
        _stopped = true;
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        if (_manageTunnel) await StopTunnelClientAsync().ConfigureAwait(false);
        Kill(_bridge);
        _bridge = null;
        _job?.Dispose();
        _job = null;
        await WaitForPortClosedAsync().ConfigureAwait(false);
    }

    private async Task StopTunnelClientAsync()
    {
        var tunnel = _paths?.TunnelClient;
        if (tunnel is null || !File.Exists(tunnel)) return;
        if (_tunnelProfile is not null)
            await _tunnelRuntime.StopOwnedAsync(tunnel, _tunnelProfile, TunnelStateDirectory).ConfigureAwait(false);
    }

    private string TunnelStateDirectory => Path.Combine(_appDataDirectory, "secure-tunnel", "transceiver");

    private static ConnectionProfile RuntimeProfileFromEnvironment()
    {
        var tunnelId = Environment.GetEnvironmentVariable("CONTROL_PLANE_TUNNEL_ID");
        var keyName = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTROL_PLANE_API_KEY"))
            ? "CONTROL_PLANE_API_KEY"
            : "OPENAI_API_KEY";
        return new ConnectionProfile
        {
            Id = new Guid("8bbaec2d-186e-42e5-89f4-80b66c02850a"),
            Name = "ProjectBridge Transceiver",
            Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = tunnelId,
            RuntimeCredentialReference = "env:" + keyName
        };
    }

    private static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process?.Dispose(); }
    }

    private async Task WaitForPortClosedAsync()
    {
        if (_port == 0) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync($"http://127.0.0.1:{_port}/healthz").ConfigureAwait(false);
            }
            catch (HttpRequestException) { return; }
            catch (TaskCanceledException) { return; }
            await Task.Delay(150).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Codex 任务桥端口仍未关闭，任务清理未完成，请重试断开。");
    }

    public override ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is { IsFaulted: true } or { IsCanceled: true }) _disposeTask = null;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_policy is not null) await StopAsync(_policy).ConfigureAwait(false);
        _monitorCancellation?.Dispose();
        _disposed = true;
        _http.Dispose();
    }
}
