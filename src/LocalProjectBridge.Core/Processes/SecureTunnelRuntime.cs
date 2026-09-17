using System.Text.Json;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Processes;

public sealed record SecureTunnelHealth(bool Ready, bool ProcessRunning, bool TargetMatches)
{
    public static SecureTunnelHealth Healthy { get; } = new(true, true, true);
    public bool IsHealthy => Ready && ProcessRunning && TargetMatches;
}

/// <summary>可替换的固定隧道生命周期与状态探测边界。</summary>
public interface ISecureTunnelRuntime
{
    Task AssertRuntimeCredentialReadyAsync(ConnectionProfile profile);
    Task ConnectAsync(string executable, ConnectionProfile profile, string mcpServerUrl,
        string stateDirectory, CancellationToken cancellationToken);
    Task<SecureTunnelHealth> ProbeHealthAsync(string executable, ConnectionProfile profile,
        string mcpServerUrl, string stateDirectory, CancellationToken cancellationToken = default);
    Task StopOwnedAsync(string executable, ConnectionProfile profile, string stateDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Configures and supervises one tunnel-client managed runtime. The target URL is
/// supplied on every connect so an alias can never silently keep an obsolete port.
/// </summary>
public sealed class SecureTunnelRuntime : ISecureTunnelRuntime
{
    private const string ProtectedRuntimeKeyEnvironmentVariable = "PROJECTBRIDGE_TUNNEL_RUNTIME_KEY";
    private readonly CommandRunner _runner;
    private readonly IRuntimeCredentialStore _credentialStore;
    private readonly HashSet<string> _ownedAliases = new(StringComparer.OrdinalIgnoreCase);

    public SecureTunnelRuntime(CommandRunner runner, IRuntimeCredentialStore? credentialStore = null)
    {
        _runner = runner;
        _credentialStore = credentialStore ?? new WindowsRuntimeCredentialStore();
    }

    /// <summary>
    /// 检查日常运行所需的 Tunnel ID 和受限运行密钥引用。此流程不读取管理员 profile，
    /// 也不要求 OPENAI_ADMIN_KEY。
    /// </summary>
    public Task AssertRuntimeCredentialReadyAsync(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.TunnelId)
            || !profile.TunnelId.StartsWith("tunnel_", StringComparison.Ordinal))
            throw new MissingTunnelRuntimeCredentialException("请在 OpenAI 固定入口页面创建或查看入口，并粘贴 tunnel_ 开头的 ID。");
        if (!HasUsableRuntimeCredentialReference(profile.RuntimeCredentialReference, _credentialStore))
            throw new MissingTunnelRuntimeCredentialException("尚未保存可用的运行密钥。请在连接向导中创建或粘贴运行密钥。");
        return Task.CompletedTask;
    }

    public async Task ConnectAsync(
        string executable,
        ConnectionProfile profile,
        string mcpServerUrl,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        await AssertRuntimeCredentialReadyAsync(profile).ConfigureAwait(false);
        var alias = profile.RuntimeAlias;
        var environment = new Dictionary<string, string?>(BuildRuntimeEnvironment(stateDirectory));
        var runtimeCredentialReference = profile.RuntimeCredentialReference ?? string.Empty;
        if (TryGetWindowsCredentialTarget(runtimeCredentialReference, out var credentialTarget))
        {
            environment[ProtectedRuntimeKeyEnvironmentVariable] = _credentialStore.Read(credentialTarget);
            runtimeCredentialReference = "env:" + ProtectedRuntimeKeyEnvironmentVariable;
        }
        await StopRecordedOwnedAsync(executable, profile, stateDirectory, cancellationToken).ConfigureAwait(false);
        lock (_ownedAliases) _ownedAliases.Add(alias);
        WriteOwnershipMarker(profile, stateDirectory);
        var result = await _runner.RunAsync(
            executable,
            BuildConnectArguments(profile, mcpServerUrl, stateDirectory, runtimeCredentialReference),
            cancellationToken: cancellationToken,
            environment: environment).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
                ? "无法启动 OpenAI Secure Tunnel。"
                : _runner.Redact(result.StandardError.Trim()));

        var lastHealth = new SecureTunnelHealth(false, false, false);
        using var readinessTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readinessTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (var attempt = 0; attempt < 60; attempt++)
            {
                lastHealth = await ProbeHealthAsync(
                    executable, alias, mcpServerUrl, readinessTimeout.Token, environment).ConfigureAwait(false);
                if (lastHealth.IsHealthy) return;
                await Task.Delay(500, readinessTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && readinessTimeout.IsCancellationRequested)
        {
            // Convert the internal readiness deadline into an actionable connection error.
        }
        throw new TimeoutException(DescribeUnready(lastHealth));
    }

    public static IReadOnlyList<string> BuildConnectArguments(
        ConnectionProfile profile,
        string mcpServerUrl,
        string stateDirectory,
        string? runtimeCredentialReference = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return
        [
            "runtimes", "connect",
            "--alias", profile.RuntimeAlias,
            "--tunnel-id", profile.TunnelId ?? string.Empty,
            "--runtime-api-key", runtimeCredentialReference ?? profile.RuntimeCredentialReference ?? string.Empty,
            "--mcp-server-url", mcpServerUrl,
            "--profile-dir", Path.Combine(Path.GetFullPath(stateDirectory), "profiles"),
            "--json"
        ];
    }

    public static IReadOnlyDictionary<string, string?> BuildRuntimeEnvironment(string stateDirectory)
    {
        var root = Path.GetFullPath(stateDirectory);
        return new Dictionary<string, string?>
        {
            ["TUNNEL_CLIENT_STATE_DIR"] = Path.Combine(root, "state"),
            ["TUNNEL_CLIENT_PROFILE_DIR"] = Path.Combine(root, "profiles")
        };
    }

    private async Task<SecureTunnelHealth> ProbeHealthAsync(
        string executable,
        string alias,
        string mcpServerUrl,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?> environment)
    {
        var result = await _runner.RunAsync(
            executable,
            ["runtimes", "status", alias, "--json"],
            cancellationToken: cancellationToken,
            environment: environment).ConfigureAwait(false);
        return result.Success && !string.IsNullOrWhiteSpace(result.StandardOutput)
            ? ParseHealth(result.StandardOutput, mcpServerUrl)
            : new SecureTunnelHealth(false, false, false);
    }

    public Task<SecureTunnelHealth> ProbeHealthAsync(
        string executable,
        ConnectionProfile profile,
        string mcpServerUrl,
        string stateDirectory,
        CancellationToken cancellationToken = default)
        => ProbeHealthAsync(
            executable,
            profile.RuntimeAlias,
            mcpServerUrl,
            cancellationToken,
            BuildRuntimeEnvironment(stateDirectory));

    public async Task<bool> IsReadyForTargetAsync(
        string executable,
        ConnectionProfile profile,
        string mcpServerUrl,
        string stateDirectory,
        CancellationToken cancellationToken = default)
        => (await ProbeHealthAsync(
            executable, profile, mcpServerUrl, stateDirectory, cancellationToken).ConfigureAwait(false)).IsHealthy;

    public static bool StatusMatchesTarget(string jsonText, string mcpServerUrl)
        => ParseHealth(jsonText, mcpServerUrl).IsHealthy;

    public static SecureTunnelHealth ParseHealth(string jsonText, string mcpServerUrl)
    {
        try
        {
            using var json = JsonDocument.Parse(jsonText);
            var root = json.RootElement;
            var ready = root.TryGetProperty("ready", out var readyValue) && readyValue.ValueKind == JsonValueKind.True;
            var running = root.TryGetProperty("process_running", out var runningValue) && runningValue.ValueKind == JsonValueKind.True;
            var target = root.TryGetProperty("target_value", out var targetValue)
                ? targetValue.GetString()
                : root.TryGetProperty("process", out var process)
                  && process.TryGetProperty("target_value", out var nestedTarget)
                    ? nestedTarget.GetString()
                    : null;
            return new SecureTunnelHealth(ready, running, UriEquals(target, mcpServerUrl));
        }
        catch (JsonException) { return new SecureTunnelHealth(false, false, false); }
    }

    public static string DescribeUnready(SecureTunnelHealth health)
        => !health.ProcessRunning
            ? "OpenAI Secure Tunnel 未就绪：本地隧道进程未运行。"
            : !health.TargetMatches
                ? "OpenAI Secure Tunnel 未就绪：隧道仍指向旧的本地服务地址。"
                : !health.Ready
                    ? "OpenAI Secure Tunnel 未就绪：进程已启动且本地地址匹配，但固定入口未通过就绪检查。"
                    : "OpenAI Secure Tunnel 未进入就绪状态。";

    public async Task StopOwnedAsync(
        string executable,
        ConnectionProfile profile,
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        var alias = profile.RuntimeAlias;
        var marker = OwnershipMarkerPath(profile, stateDirectory);
        var recorded = IsRecordedOwner(profile, marker);
        lock (_ownedAliases)
        {
            if (!_ownedAliases.Remove(alias) && !recorded) return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        CommandResult result;
        try
        {
            result = await _runner.RunAsync(
                executable,
                ["runtimes", "stop", alias, "--json"],
                cancellationToken: timeout.Token,
                environment: BuildRuntimeEnvironment(stateDirectory)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_ownedAliases) _ownedAliases.Add(alias);
            throw new TimeoutException("停止本程序拥有的 OpenAI Secure Tunnel 超时。");
        }
        if (!result.Success)
        {
            lock (_ownedAliases) _ownedAliases.Add(alias);
            throw new InvalidOperationException("停止本程序拥有的 OpenAI Secure Tunnel 失败。" +
                (string.IsNullOrWhiteSpace(result.StandardError) ? string.Empty : " " + _runner.Redact(result.StandardError.Trim())));
        }
        try { File.Delete(marker); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private async Task StopRecordedOwnedAsync(
        string executable,
        ConnectionProfile profile,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var marker = OwnershipMarkerPath(profile, stateDirectory);
        if (!IsRecordedOwner(profile, marker)) return;
        lock (_ownedAliases) _ownedAliases.Add(profile.RuntimeAlias);
        await StopOwnedAsync(executable, profile, stateDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static string OwnershipMarkerPath(ConnectionProfile profile, string stateDirectory)
        => Path.Combine(Path.GetFullPath(stateDirectory), "ownership", profile.RuntimeAlias + ".json");

    private static void WriteOwnershipMarker(ConnectionProfile profile, string stateDirectory)
    {
        var marker = OwnershipMarkerPath(profile, stateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            connection_id = profile.Id,
            alias = profile.RuntimeAlias,
            owner_pid = Environment.ProcessId,
            started_at = DateTimeOffset.UtcNow
        }));
    }

    private static bool IsRecordedOwner(ConnectionProfile profile, string marker)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            return json.RootElement.TryGetProperty("connection_id", out var id)
                && id.GetGuid() == profile.Id
                && json.RootElement.TryGetProperty("alias", out var alias)
                && string.Equals(alias.GetString(), profile.RuntimeAlias, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return false;
        }
    }

    private static bool UriEquals(string? left, string right)
        => Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
           && Uri.TryCreate(right, UriKind.Absolute, out var rightUri)
           && Uri.Compare(leftUri, rightUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped,
               StringComparison.OrdinalIgnoreCase) == 0;

    public static bool HasUsableRuntimeCredentialReference(
        string? reference,
        IRuntimeCredentialStore? credentialStore = null)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var variableName = reference["env:".Length..].Trim();
            return variableName.Length > 0
                   && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName));
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = reference["file:".Length..].Trim();
            try { return path.Length > 0 && new FileInfo(path).Length > 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        if (TryGetWindowsCredentialTarget(reference, out var target))
        {
            try { return credentialStore is not null && credentialStore.Exists(target); }
            catch (Exception error) when (error is PlatformNotSupportedException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }

        return false;
    }

    public static bool TryGetWindowsCredentialTarget(string? reference, out string target)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(reference)
            || !reference.StartsWith("wincred:", StringComparison.OrdinalIgnoreCase)) return false;
        target = reference["wincred:".Length..].Trim();
        return target.Length > 0;
    }
}

public sealed class MissingTunnelRuntimeCredentialException : InvalidOperationException
{
    public MissingTunnelRuntimeCredentialException(string message) : base(message) { }
}
