namespace LocalProjectBridge.Core.Sessions;

public sealed record SessionStateChangedEventArgs(SessionState State, SessionState Previous, string? Detail);

/// <summary>
/// 单一会话控制器（设计文档 6.4）：连接与断开只能由它执行，界面不得分别启动后端。
/// 连接任一步失败都会按相反顺序回滚，不留下半连接状态。
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private readonly TimeSpan _connectTimeout;
    private readonly IReadOnlyList<IConnectableAdapter> _adapters;
    private readonly HashSet<IConnectableAdapter> _ownedAdapters;
    private readonly RedactingLogger _logger;
    private readonly string _appDataDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _verificationDetails = [];
    private ConnectionReadiness? _faultHistory;

    public SessionController(
        IReadOnlyList<IConnectableAdapter> adapters,
        RedactingLogger logger,
        string appDataDirectory,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? connectTimeout = null)
    {
        _adapters = adapters;
        _ownedAdapters = [.. adapters];
        _logger = logger;
        _appDataDirectory = appDataDirectory;
        _clock = clock ?? DefaultClock;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(150);
        foreach (var adapter in adapters)
        {
            adapter.Faulted += Adapter_Faulted;
            if (adapter is IConnectionReadinessSource source)
                source.ReadinessChanged += Adapter_ReadinessChanged;
        }
    }

    /// <summary>设计文档 9.1：状态只能由真实探测结果驱动，禁止跳变。</summary>
    public SessionState State { get; private set; } = SessionState.Disconnected;
    public SessionPolicy? CurrentSession { get; private set; }
    public ConnectionProfile? CurrentConnection { get; private set; }
    public ConnectionReadiness Readiness { get; private set; } = ConnectionReadiness.Stopped;
    public BridgeError? LastError { get; private set; }
    public IReadOnlyList<string> VerificationDetails => _verificationDetails;

    private List<IConnectableAdapter> _activeAdapters = [];

    public bool IsBusy => State is not (SessionState.Disconnected or SessionState.NeedsAttention);
    public bool HasPendingCleanup => _activeAdapters.Count > 0 && CurrentSession is not null;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<ConnectionReadinessChangedEventArgs>? ReadinessChanged;

    /// <summary>Take ownership of a verified setup connection without restarting its URL or revoking OAuth tokens.
    /// Until this returns true, the setup window remains responsible for cleanup.</summary>
    public async Task<bool> AdoptSetupConnectionAsync(ProjectRecord project, IConnectableAdapter preparedAdapter,
        CancellationToken cancellationToken = default)
        => await AdoptSetupConnectionAsync(
            new ConnectionProfile { Id = project.Id, Name = "ProjectBridge" },
            preparedAdapter is C2cAdapter { IsSharedConnection: true } shared ? shared.ConnectionWorkspace : project.Path,
            preparedAdapter,
            cancellationToken).ConfigureAwait(false);

    /// <summary>接管属于机器连接的已启动适配器；所选项目不参与连接身份。</summary>
    public async Task<bool> AdoptSetupConnectionAsync(
        ConnectionProfile profile,
        string connectionWorkspace,
        IConnectableAdapter preparedAdapter,
        CancellationToken cancellationToken = default)
    {
        var capabilities = preparedAdapter is C2cAdapter { IsSharedConnection: true }
            ? CapabilityFlags.WebRead : CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBusy || HasPendingCleanup) throw new InvalidOperationException("请先断开当前连接。");
            if (_ownedAdapters.Add(preparedAdapter))
            {
                preparedAdapter.Faulted += Adapter_Faulted;
                if (preparedAdapter is IConnectionReadinessSource addedSource)
                    addedSource.ReadinessChanged += Adapter_ReadinessChanged;
            }
            LastError = null;
            Transition(SessionState.Checking, "正在检查已配对的项目连接…");
            SessionPolicy policy;
            try
            {
                policy = SessionPolicyFactory.CreateConnection(profile, connectionWorkspace, capabilities, _clock);
                Transition(SessionState.Starting, "正在接管设置窗口中的连接…");
                Transition(SessionState.Authorizing, "正在核对现有连接…");
                await preparedAdapter.VerifyAsync(policy, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                await FailAsync(null, error, capabilities, "authorize").ConfigureAwait(false);
                return false;
            }
            CurrentSession = policy;
            CurrentConnection = profile;
            _activeAdapters = [preparedAdapter];
            profile.LastConnectedAt = _clock();
            UpdateReadinessFrom(preparedAdapter);
            ApplyReadinessToProfile(profile, Readiness);
            Transition(SessionState.Connected, preparedAdapter is C2cAdapter { IsSharedConnection: true }
                ? "共享连接已建立。添加或切换项目时会复用此连接。"
                : "网页读取项目、Codex 请求网页协作已连接。网页委派任务未启动。");
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>兼容旧调用方：把项目包装成一次连接。新界面使用 ConnectionProfile 重载。</summary>
    public async Task<bool> ConnectAsync(ProjectRecord project, CapabilityFlags capabilities, CancellationToken cancellationToken = default)
        => await ConnectAsync(
            new ConnectionProfile { Id = project.Id, Name = "ProjectBridge" },
            project.Path,
            capabilities,
            project,
            cancellationToken).ConfigureAwait(false);

    /// <summary>按机器连接配置启动；项目列表与界面选择不参与此生命周期。</summary>
    public Task<bool> ConnectAsync(
        ConnectionProfile profile,
        string connectionWorkspace,
        CapabilityFlags capabilities,
        CancellationToken cancellationToken = default)
        => ConnectAsync(profile, connectionWorkspace, capabilities, legacyProject: null, cancellationToken);

    private async Task<bool> ConnectAsync(
        ConnectionProfile profile,
        string connectionWorkspace,
        CapabilityFlags capabilities,
        ProjectRecord? legacyProject,
        CancellationToken cancellationToken)
    {
        if (capabilities == CapabilityFlags.None)
            throw new InvalidOperationException("请至少开启一种网页能力。");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not (SessionState.Disconnected or SessionState.NeedsAttention))
                throw new InvalidOperationException("已有项目处于连接或断开流程中，请先完成当前操作。");
            if (HasPendingCleanup)
                throw new InvalidOperationException("上一次连接仍有进程等待清理，请先再次点击“断开”。");
            LastError = null;
            _verificationDetails.Clear();

            Transition(SessionState.Checking, $"正在检查连接“{profile.Name}”与运行组件…");
            SessionPolicy policy;
            try
            {
                policy = SessionPolicyFactory.CreateConnection(profile, connectionWorkspace, capabilities, _clock);
            }
            catch (Exception error)
            {
                await FailAsync(legacyProject, error, capabilities, "check").ConfigureAwait(false);
                return false;
            }

            var required = _adapters.Where(adapter => adapter.IsRequiredFor(capabilities)).ToArray();
            if (required.Length == 0)
            {
                await FailAsync(legacyProject, new InvalidOperationException("所选能力没有对应的本地组件。"), capabilities, "check").ConfigureAwait(false);
                return false;
            }

            try
            {
                foreach (var adapter in required)
                    await adapter.AssertReadyAsync(policy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await FailAsync(legacyProject, error, capabilities, "check").ConfigureAwait(false);
                return false;
            }

            Transition(SessionState.Starting, "正在启动本地组件与安全隧道…");
            var started = new List<IConnectableAdapter>();
            try
            {
                foreach (var adapter in required)
                {
                    // Add before StartAsync: an adapter may create a process and then fail or be
                    // cancelled before returning. It must still participate in rollback.
                    started.Add(adapter);
                    await adapter.StartAsync(policy, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                var cleanup = await RollbackAsync(policy, started).ConfigureAwait(false);
                RetainFailedCleanup(policy, profile, cleanup.FailedAdapters);
                await FailAsync(legacyProject, error, capabilities, "start").ConfigureAwait(false);
                ApplyCleanupFailure(cleanup.Errors);
                return false;
            }

            Transition(SessionState.Authorizing, "正在核对授权、隧道与项目身份…");
            try
            {
                foreach (var adapter in required)
                {
                    var detail = await adapter.VerifyAsync(policy, cancellationToken).ConfigureAwait(false);
                    if (adapter is C2cAdapter c2c)
                    {
                        var repair = await c2c.DoctorAsync(policy.CanonicalProjectPath, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(repair))
                            throw new InvalidOperationException($"ChatGPT 侧连接需要确认：{repair}");
                    }
                    _verificationDetails.Add($"{adapter.Name}: {detail}");
                }
            }
            catch (Exception error)
            {
                var cleanup = await RollbackAsync(policy, started).ConfigureAwait(false);
                RetainFailedCleanup(policy, profile, cleanup.FailedAdapters);
                await FailAsync(legacyProject, error, capabilities, "authorize").ConfigureAwait(false);
                ApplyCleanupFailure(cleanup.Errors);
                return false;
            }

            CurrentSession = policy;
            CurrentConnection = profile;
            _activeAdapters = started;
            profile.LastConnectedAt = _clock();
            if (legacyProject is not null)
            {
                legacyProject.LastConnectedAt = profile.LastConnectedAt;
                legacyProject.LastFault = null;
            }
            foreach (var adapter in started) UpdateReadinessFrom(adapter);
            ApplyReadinessToProfile(profile, Readiness);
            Transition(SessionState.Connected, string.Join("；", _verificationDetails));
            _verificationDetails.Clear();
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>连接超时保护：设计 9.1 要求连接不能无限悬在“正在连接”。</summary>
    public async Task<bool> ConnectAsyncWithTimeout(ProjectRecord project, CapabilityFlags capabilities, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        return await ConnectAsync(project, capabilities, timeout.Token).ConfigureAwait(false);
    }

    public async Task<bool> ConnectAsyncWithTimeout(
        ConnectionProfile profile,
        string connectionWorkspace,
        CapabilityFlags capabilities,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        return await ConnectAsync(profile, connectionWorkspace, capabilities, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>断开当前会话（设计文档 6.4 断开顺序）。幂等。</summary>
    public async Task DisconnectAsync(string? reason = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is SessionState.Disconnected or SessionState.Disconnecting) return;
            var policy = CurrentSession;
            var active = _activeAdapters.ToList();
            Transition(SessionState.Disconnecting, reason ?? "正在断开…");
            var cleanup = policy is null
                ? CleanupResult.Empty
                : await StopAdaptersAsync(policy, active, "stop").ConfigureAwait(false);
            _activeAdapters = cleanup.FailedAdapters;
            if (cleanup.Errors.Count == 0)
            {
                if (policy is not null) CleanupSessionDirectory(policy);
                SetReadiness(ConnectionReadiness.Stopped);
                CurrentSession = null;
                CurrentConnection = null;
                LastError = null;
                Transition(SessionState.Disconnected, "已断开，本地组件与隧道已停止。");
            }
            else
            {
                SetReadiness(AsHistoricalFailure(Readiness));
                LastError = new BridgeError(
                    "连接已禁用，但部分进程清理失败。",
                    "本地连接进程",
                    "再次点击“断开”；若仍失败，请复制诊断信息后退出程序。",
                    string.Join(" | ", cleanup.Errors));
                Transition(SessionState.NeedsAttention, "连接已禁用，进程清理未完成。");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>启动时清理崩溃残留的会话目录（设计文档 11.2：退出或断开时会话目录不保留）。</summary>
    public Task CleanupStaleSessionsAsync()
    {
        var sessionsRoot = Path.Combine(_appDataDirectory, "sessions");
        try
        {
            if (!Directory.Exists(sessionsRoot)) return Task.CompletedTask;
            foreach (var directory in Directory.EnumerateDirectories(sessionsRoot))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    private static DateTimeOffset DefaultClock() => DateTimeOffset.Now;

    private async Task<CleanupResult> RollbackAsync(SessionPolicy policy, List<IConnectableAdapter> started)
    {
        var cleanup = await StopAdaptersAsync(policy, started, "rollback").ConfigureAwait(false);
        if (cleanup.Errors.Count == 0) CleanupSessionDirectory(policy);
        return cleanup;
    }

    private async Task<CleanupResult> StopAdaptersAsync(
        SessionPolicy policy,
        IReadOnlyList<IConnectableAdapter> adapters,
        string operation)
    {
        var failed = new HashSet<IConnectableAdapter>();
        var errors = new List<string>();
        foreach (var adapter in adapters.Reverse())
        {
            try { await adapter.StopAsync(policy).ConfigureAwait(false); }
            catch (Exception error)
            {
                failed.Add(adapter);
                errors.Add($"{adapter.Name}: {error.Message}");
                await _logger.WriteAsync("error", $"{operation} {adapter.Name} failed: {error.Message}").ConfigureAwait(false);
            }
        }
        return new CleanupResult(adapters.Where(failed.Contains).ToList(), errors);
    }

    private void RetainFailedCleanup(
        SessionPolicy policy,
        ConnectionProfile profile,
        List<IConnectableAdapter> failedAdapters)
    {
        _activeAdapters = failedAdapters;
        if (failedAdapters.Count == 0) return;
        CurrentSession = policy;
        CurrentConnection = profile;
    }

    private void ApplyCleanupFailure(IReadOnlyList<string> cleanupErrors)
    {
        if (cleanupErrors.Count == 0) return;
        var primary = LastError;
        LastError = new BridgeError(
            (primary?.What ?? "连接失败。") + " 部分进程清理失败。",
            "本地连接进程",
            "再次点击“断开”完成清理；清理完成前不能启动新连接。",
            string.Join(" | ", cleanupErrors));
    }

    private async Task FailAsync(ProjectRecord? project, Exception error, CapabilityFlags capabilities, string phase)
    {
        LastError = ErrorClassifier.Classify(error, capabilities, phase);
        if (project is not null) project.LastFault = LastError.What;
        await _logger.WriteAsync("error", $"connect failed at {phase}: {LastError.Detail ?? LastError.What}").ConfigureAwait(false);
        Transition(SessionState.NeedsAttention, LastError.What);
    }

    private void Adapter_Faulted(object? sender, string message)
        => _ = HandleAdapterFaultAsync(message);

    private async Task HandleAdapterFaultAsync(string message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is not (SessionState.Connected or SessionState.Recovering)) return;

            LastError = new BridgeError(
                message,
                "网页端与本地项目的连接",
                "点击“连接”重新建立；若反复失败，请打开“首次设置”检查组件。",
                message);
            _faultHistory = Readiness;
            Transition(SessionState.NeedsAttention, message);

            var policy = CurrentSession;
            var active = _activeAdapters.ToList();
            var historicalReadiness = Readiness;
            var cleanup = policy is null
                ? CleanupResult.Empty
                : await StopAdaptersAsync(policy, active, "fault cleanup").ConfigureAwait(false);
            _activeAdapters = cleanup.FailedAdapters;
            if (cleanup.Errors.Count == 0)
            {
                if (policy is not null) CleanupSessionDirectory(policy);
                CurrentSession = null;
                CurrentConnection = null;
            }
            else
            {
                ApplyCleanupFailure(cleanup.Errors);
            }
            SetReadiness(AsHistoricalFailure(historicalReadiness));
            _faultHistory = null;
        }
        finally { _gate.Release(); }
    }

    private void CleanupSessionDirectory(SessionPolicy policy)
    {
        var directory = Path.Combine(_appDataDirectory, "sessions", policy.SessionId.ToString("D"));
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Transition(SessionState next, string? detail)
    {
        if (!SessionStateTransitions.CanTransition(State, next))
            throw new InvalidOperationException($"不允许的状态迁移：{State} → {next}");
        var previous = State;
        State = next;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(next, previous, detail));
    }

    private void UpdateReadinessFrom(IConnectableAdapter adapter)
    {
        if (adapter is not IConnectionReadinessSource source) return;
        SetReadiness(source.Readiness);
    }

    private void Adapter_ReadinessChanged(object? sender, ConnectionReadinessChangedEventArgs e)
    {
        SetReadiness(_faultHistory is { } history ? AsHistoricalFailure(history) : e.Readiness);
        if (CurrentConnection is { } profile)
            ApplyReadinessToProfile(profile, e.Readiness);
    }

    private void ApplyReadinessToProfile(ConnectionProfile profile, ConnectionReadiness readiness)
    {
        if (readiness.ClientAuthorized && profile.LastAuthorizedAt is null)
            profile.LastAuthorizedAt = _clock();
        if (readiness.LastVerifiedCall is { } verified)
        {
            profile.LastVerifiedCall = verified;
            profile.LastVerifiedClientId = readiness.ClientIdentity;
        }
    }

    private void SetReadiness(ConnectionReadiness readiness)
    {
        if (Readiness == readiness) return;
        Readiness = readiness;
        ReadinessChanged?.Invoke(this, new ConnectionReadinessChangedEventArgs(readiness));
    }

    private static ConnectionReadiness AsHistoricalFailure(ConnectionReadiness readiness)
        => new(false, false, false, readiness.LastVerifiedCall, readiness.ClientIdentity);

    private sealed record CleanupResult(List<IConnectableAdapter> FailedAdapters, List<string> Errors)
    {
        public static CleanupResult Empty { get; } = new([], []);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _ownedAdapters)
        {
            adapter.Faulted -= Adapter_Faulted;
            if (adapter is IConnectionReadinessSource source)
                source.ReadinessChanged -= Adapter_ReadinessChanged;
        }
        await DisconnectAsync("正在退出…").ConfigureAwait(false);
        foreach (var adapter in _ownedAdapters)
            await adapter.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
