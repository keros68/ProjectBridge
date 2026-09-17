using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

internal sealed class FakeAdapter : IConnectableAdapter
{
    private EventHandler<string>? _faulted;
    private readonly Action<SessionPolicy>? _onAssertReady;
    private readonly Action<SessionPolicy>? _onStart;
    private readonly Action<SessionPolicy>? _onVerify;

    public FakeAdapter(
        string name,
        Func<CapabilityFlags, bool> requiredFor,
        Action<SessionPolicy>? onAssertReady = null,
        Action<SessionPolicy>? onStart = null,
        Action<SessionPolicy>? onVerify = null)
    {
        Name = name;
        RequiredFor = requiredFor;
        _onAssertReady = onAssertReady;
        _onStart = onStart;
        _onVerify = onVerify;
    }

    public string Name { get; }
    public Func<CapabilityFlags, bool> RequiredFor { get; }
    public List<string> Events { get; } = [];
    public List<Guid> StartedSessions { get; } = [];
    public SessionPolicy? LastStartedPolicy { get; private set; }
    public int StopCount { get; private set; }

    public bool IsRequiredFor(CapabilityFlags capabilities) => RequiredFor(capabilities);

    public Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        _onAssertReady?.Invoke(policy);
        Events.Add($"assert:{Name}");
        return Task.CompletedTask;
    }

    public Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        _onStart?.Invoke(policy);
        Events.Add($"start:{Name}");
        StartedSessions.Add(policy.SessionId);
        LastStartedPolicy = policy;
        return Task.CompletedTask;
    }

    public Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
    {
        _onVerify?.Invoke(policy);
        Events.Add($"verify:{Name}");
        return Task.FromResult($"{Name} verified");
    }

    public Task StopAsync(SessionPolicy policy)
    {
        Events.Add($"stop:{Name}");
        StopCount++;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public event EventHandler<string>? Faulted
    {
        add => _faulted += value;
        remove => _faulted -= value;
    }

    public void RaiseFaulted(string message) => _faulted?.Invoke(this, message);
}

public sealed class SessionControllerTests : IDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("lpb-ctrl").FullName;
    private readonly string _projectPath;

    public SessionControllerTests()
    {
        _projectPath = Path.Combine(_appData, "demo-project");
        Directory.CreateDirectory(_projectPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
    }

    private ProjectRecord NewProject(bool webRead = true, bool codexTasks = false, bool askWeb = true)
        => new()
        {
            Name = "演示项目",
            Path = _projectPath,
            AllowWebRead = webRead,
            AllowCodexTasks = codexTasks,
            AllowCodexAskWeb = askWeb
        };

    private static RedactingLogger NewLogger(string appData) => new(appData);

    [Fact]
    public async Task MissingRuntimeCredential_FailsBeforeAnyAdapterStarts()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        var advanced = new FakeAdapter("advanced", _ => true, onAssertReady: _ =>
            throw new LocalProjectBridge.Core.Processes.MissingTunnelRuntimeCredentialException("missing runtime key"));
        await using var controller = new SessionController([c2c, advanced], NewLogger(_appData), _appData);
        Assert.False(await controller.ConnectAsync(NewProject(codexTasks: true), CapabilityFlags.WebRead | CapabilityFlags.WebDelegateCodex));
        Assert.Empty(c2c.StartedSessions);
        Assert.Empty(advanced.StartedSessions);
        Assert.Equal(0, c2c.StopCount);
        Assert.Equal("网页端与本地项目的连接", controller.LastError!.AffectedCapability);
    }

    [Fact]
    public async Task AdoptSetup_PreservesConnectionAndSkipsAdvancedAdapter()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        var advanced = new FakeAdapter("advanced", _ => true,
            onAssertReady: _ => throw new InvalidOperationException("unrelated adapter should not start"));
        await using var controller = new SessionController([c2c, advanced], NewLogger(_appData), _appData);
        var project = NewProject(codexTasks: true);

        Assert.True(await controller.AdoptSetupConnectionAsync(project, c2c));
        Assert.Equal(["verify:c2c"], c2c.Events);
        Assert.Empty(advanced.Events);
        Assert.True(project.AllowCodexTasks); // Saved preferences are not silently rewritten.
        Assert.Equal(CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb, controller.CurrentSession!.EnabledCapabilities);
        Assert.Equal(SessionState.Connected, controller.State);

        await controller.DisconnectAsync();
        Assert.Equal(1, c2c.StopCount);
        Assert.Equal(0, advanced.StopCount);
    }

    [Fact]
    public async Task AdoptSetup_VerificationFailureLeavesOwnershipWithWizard()
    {
        var c2c = new FakeAdapter("c2c", _ => true, onVerify: _ => throw new InvalidOperationException("connection ended"));
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        Assert.False(await controller.AdoptSetupConnectionAsync(NewProject(), c2c));
        Assert.Null(controller.CurrentSession);
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.Equal(0, c2c.StopCount);
    }

    [Fact]
    public async Task AdoptSetup_CancellationDuringVerificationDoesNotTransferOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var c2c = new FakeAdapter("c2c", _ => true, onVerify: _ => cancellation.Cancel());
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        Assert.False(await controller.AdoptSetupConnectionAsync(NewProject(), c2c, cancellation.Token));
        Assert.Null(controller.CurrentSession);
        Assert.Equal(0, c2c.StopCount);
    }

    [Fact]
    public async Task Connect_Success_ReachestConnected()
    {
        var c2c = new FakeAdapter("c2c", caps => caps.Allows(CapabilityFlags.WebRead));
        var transceiver = new FakeAdapter("transceiver", caps => caps.Allows(CapabilityFlags.WebDelegateCodex));
        await using var controller = new SessionController([c2c, transceiver], NewLogger(_appData), _appData);
        var states = new List<SessionState>();
        controller.StateChanged += (_, e) => states.Add(e.State);

        var connected = await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead);

        Assert.True(connected);
        Assert.Equal(SessionState.Connected, controller.State);
        Assert.NotNull(controller.CurrentSession);
        Assert.Contains(SessionState.Checking, states);
        Assert.Contains(SessionState.Starting, states);
        Assert.Contains(SessionState.Authorizing, states);
        Assert.Contains(SessionState.Connected, states);
        Assert.Equal(["assert:c2c", "start:c2c", "verify:c2c"], c2c.Events);
        Assert.Empty(transceiver.Events); // 未开启委派时绝不启动 Transceiver
    }

    [Fact]
    public async Task Connect_DelegateOnly_StartsOnlyTransceiver()
    {
        var c2c = new FakeAdapter("c2c", caps => caps.Allows(CapabilityFlags.WebRead) || caps.Allows(CapabilityFlags.CodexAskWeb));
        var transceiver = new FakeAdapter("transceiver", caps => caps.Allows(CapabilityFlags.WebDelegateCodex));
        await using var controller = new SessionController([c2c, transceiver], NewLogger(_appData), _appData);

        var connected = await controller.ConnectAsync(
            NewProject(webRead: false, codexTasks: true, askWeb: false),
            CapabilityFlags.WebDelegateCodex);

        Assert.True(connected);
        Assert.Equal(["assert:transceiver", "start:transceiver", "verify:transceiver"], transceiver.Events);
        Assert.Empty(c2c.Events);
    }

    [Fact]
    public async Task Connect_SecondAdapterFails_RollsBackFirstAndNeedsAttention()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        var transceiver = new FakeAdapter("transceiver", _ => true, onStart: _ => throw new TimeoutException("隧道未进入就绪状态"));
        await using var controller = new SessionController([c2c, transceiver], NewLogger(_appData), _appData);

        var connected = await controller.ConnectAsync(
            NewProject(codexTasks: true),
            CapabilityFlags.WebRead | CapabilityFlags.WebDelegateCodex);

        Assert.False(connected);
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.NotNull(controller.LastError);
        // 两个适配器都必须参与回滚；第二个可能已经创建进程后才报告启动失败。
        Assert.Equal(1, c2c.StopCount);
        Assert.Equal(1, transceiver.StopCount);
        Assert.Null(controller.CurrentSession);
        Assert.Contains("stop:c2c", c2c.Events);
        Assert.Contains("stop:transceiver", transceiver.Events);
    }

    [Fact]
    public async Task Connect_WhileConnected_Throws()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));
    }

    [Fact]
    public async Task ConnectionIdentity_IsIndependentOfAtoBtoAProjectSelection()
    {
        var adapter = new FakeAdapter("shared", _ => true);
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);
        var connectionWorkspace = Directory.CreateDirectory(Path.Combine(_appData, "connection-workspace")).FullName;
        var profile = new ConnectionProfile { Id = Guid.NewGuid(), Name = "machine connection" };
        var a = NewProject();
        var b = new ProjectRecord { Name = "B", Path = Directory.CreateDirectory(Path.Combine(_appData, "B")).FullName };
        var view = new AppSettings { Projects = [a, b], SelectedProjectId = a.Id };

        Assert.True(await controller.ConnectAsync(profile, connectionWorkspace, CapabilityFlags.WebRead));
        view.SelectedProjectId = b.Id;
        view.SelectedProjectId = a.Id;

        Assert.Equal(SessionState.Connected, controller.State);
        Assert.Equal(profile.Id, controller.CurrentConnection!.Id);
        Assert.Equal(profile.Id, controller.CurrentSession!.ConnectionId);
        Assert.True(controller.CurrentSession.IsConnectionPolicy);
        Assert.Equal(Path.GetFullPath(connectionWorkspace), adapter.LastStartedPolicy!.CanonicalProjectPath);
        Assert.Single(adapter.StartedSessions);
        Assert.Equal(0, adapter.StopCount);
    }

    [Fact]
    public async Task AddingProjectAndRefreshingPermissions_DoesNotRestartConnection()
    {
        var adapter = new FakeAdapter("shared", _ => true);
        var registry = new LocalProjectBridge.Core.Gateway.ProjectAuthorizationRegistry();
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);
        var profile = new ConnectionProfile { Name = "machine connection" };
        var projects = new List<ProjectRecord> { NewProject() };
        registry.ReplaceProjects(projects);
        Assert.True(await controller.ConnectAsync(profile, _projectPath, CapabilityFlags.WebRead));

        projects.Add(new ProjectRecord
        {
            Name = "new project",
            Path = Directory.CreateDirectory(Path.Combine(_appData, "new-project")).FullName,
            AllowWebRead = true
        });
        registry.ReplaceProjects(projects);

        Assert.Equal(SessionState.Connected, controller.State);
        Assert.Single(adapter.StartedSessions);
        Assert.Equal(0, adapter.StopCount);
    }

    [Fact]
    public async Task Connect_NoneCapability_Throws()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(NewProject(), CapabilityFlags.None));
    }

    [Fact]
    public async Task Connect_MovedProject_FailsWithClassifiedError()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        var missing = new ProjectRecord { Name = "gone", Path = Path.Combine(_appData, "no-such-dir") };

        var connected = await controller.ConnectAsync(missing, CapabilityFlags.WebRead);

        Assert.False(connected);
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.NotNull(controller.LastError);
        Assert.Contains("重新选择", controller.LastError.NextAction);
        Assert.Equal(0, c2c.StopCount); // 未启动任何组件，无需回滚
    }

    [Fact]
    public async Task Connect_AuthorizationRepair_RollsBackAndExplainsNextAction()
    {
        var c2c = new FakeAdapter("c2c", _ => true, onVerify: _ => { });
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        // 真实 C2cAdapter 的 doctor 检查在控制器内联执行；此处通过 Verify 抛出授权异常模拟
        var failing = new FakeAdapter("c2c", _ => true, onVerify: _ =>
            throw new InvalidOperationException("ChatGPT 侧连接需要确认：配对码已过期。"));
        await using var failingController = new SessionController([failing], NewLogger(_appData), _appData);

        var connected = await failingController.ConnectAsync(NewProject(), CapabilityFlags.WebRead);

        Assert.False(connected);
        Assert.Equal(SessionState.NeedsAttention, failingController.State);
        Assert.NotNull(failingController.LastError);
        Assert.Contains("ChatGPT", failingController.LastError.What);
    }

    [Fact]
    public async Task Disconnect_StopsAdaptersInReverseAndRemovesSessionDirectory()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        var transceiver = new FakeAdapter("transceiver", _ => true);
        await using var controller = new SessionController([c2c, transceiver], NewLogger(_appData), _appData);
        await controller.ConnectAsync(NewProject(codexTasks: true),
            CapabilityFlags.WebRead | CapabilityFlags.WebDelegateCodex);
        var sessionId = controller.CurrentSession!.SessionId;
        var sessionDirectory = Path.Combine(_appData, "sessions", sessionId.ToString("D"));
        Directory.CreateDirectory(sessionDirectory); // 模拟适配器写入的会话配置

        await controller.DisconnectAsync();

        Assert.Equal(SessionState.Disconnected, controller.State);
        Assert.Null(controller.CurrentSession);
        Assert.False(Directory.Exists(sessionDirectory), "断开后必须删除会话目录");
        // 断开顺序与启动相反：transceiver 后启动，先停止
        Assert.True(c2c.Events.IndexOf("stop:transceiver") < c2c.Events.IndexOf("stop:c2c"));
    }

    [Fact]
    public async Task Disconnect_WhenDisconnected_IsNoOp()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        await controller.DisconnectAsync();
        Assert.Equal(0, c2c.StopCount);
    }

    [Fact]
    public async Task Disconnect_CleanupFailureIsReportedAndCanBeRetried()
    {
        var adapter = new FlakyStopAdapter();
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);
        var profile = new ConnectionProfile { Name = "machine" };
        Assert.True(await controller.ConnectAsync(profile, _projectPath, CapabilityFlags.WebRead));

        await controller.DisconnectAsync();

        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.NotNull(controller.CurrentSession);
        Assert.Contains("清理失败", controller.LastError!.What);
        Assert.Equal(1, adapter.StopCount);

        await controller.DisconnectAsync();
        Assert.Equal(SessionState.Disconnected, controller.State);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(2, adapter.StopCount);
    }

    [Fact]
    public async Task Connect_StartFailureAndCleanupFailure_RetainsRetryableCleanup()
    {
        var adapter = new FailingLifecycleAdapter(failStart: true);
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);

        Assert.False(await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.True(controller.HasPendingCleanup);
        Assert.NotNull(controller.CurrentSession);
        Assert.Equal(1, adapter.StopCount);

        await controller.DisconnectAsync();

        Assert.Equal(SessionState.Disconnected, controller.State);
        Assert.False(controller.HasPendingCleanup);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(2, adapter.StopCount);
    }

    [Fact]
    public async Task Connect_VerificationFailureAndCleanupFailure_RetainsRetryableCleanup()
    {
        var adapter = new FailingLifecycleAdapter(failVerify: true);
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);

        Assert.False(await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.True(controller.HasPendingCleanup);
        Assert.NotNull(controller.CurrentSession);
        Assert.Equal(1, adapter.StopCount);

        await controller.DisconnectAsync();

        Assert.Equal(SessionState.Disconnected, controller.State);
        Assert.False(controller.HasPendingCleanup);
        Assert.Equal(2, adapter.StopCount);
    }

    [Fact]
    public async Task AdapterFault_CleanupFailure_RetainsRetryableCleanup()
    {
        var adapter = new FailingLifecycleAdapter();
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);
        Assert.True(await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));

        adapter.RaiseFaulted("simulated runtime failure");
        for (var attempt = 0; attempt < 50 && adapter.StopCount == 0; attempt++)
            await Task.Delay(10);

        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.True(controller.HasPendingCleanup);
        Assert.NotNull(controller.CurrentSession);
        Assert.Equal(1, adapter.StopCount);

        await controller.DisconnectAsync();

        Assert.Equal(SessionState.Disconnected, controller.State);
        Assert.False(controller.HasPendingCleanup);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(2, adapter.StopCount);
    }

    [Fact]
    public async Task Connect_WithPendingCleanup_IsRejectedWithoutLosingRetryObject()
    {
        var adapter = new FailingLifecycleAdapter(failStart: true);
        await using var controller = new SessionController([adapter], NewLogger(_appData), _appData);
        Assert.False(await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead));

        Assert.Contains("断开", error.Message);
        Assert.True(controller.HasPendingCleanup);
        Assert.Equal(1, adapter.StopCount);
    }

    [Fact]
    public async Task AdapterFaulted_WhileConnected_TransitionsToNeedsAttention()
    {
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);
        await controller.ConnectAsync(NewProject(), CapabilityFlags.WebRead);

        c2c.RaiseFaulted("本地桥连续失败，已停止自动重试。");

        for (var attempt = 0; attempt < 20 && controller.CurrentSession is not null; attempt++)
            await Task.Delay(10);

        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.NotNull(controller.LastError);
        Assert.Contains("连续失败", controller.LastError.What);
        Assert.Null(controller.CurrentSession);
        Assert.Equal(1, c2c.StopCount);
    }

    [Fact]
    public async Task ConnectWithTimeout_CancelsInFlightStartAndRollsBack()
    {
        var adapter = new CancellableStartAdapter();
        await using var controller = new SessionController(
            [adapter], NewLogger(_appData), _appData, connectTimeout: TimeSpan.FromMilliseconds(50));

        var connected = await controller.ConnectAsyncWithTimeout(NewProject(), CapabilityFlags.WebRead);

        Assert.False(connected);
        Assert.Equal(SessionState.NeedsAttention, controller.State);
        Assert.True(adapter.CancellationObserved);
        Assert.Equal(1, adapter.StopCount);
    }

    [Fact]
    public async Task CleanupStaleSessions_RemovesLeftovers()
    {
        var stale = Path.Combine(_appData, "sessions", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "transceiver.json"), "{}");
        var c2c = new FakeAdapter("c2c", _ => true);
        await using var controller = new SessionController([c2c], NewLogger(_appData), _appData);

        await controller.CleanupStaleSessionsAsync();

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void StateTransition_RejectsIllegalJump()
    {
        Assert.False(SessionStateTransitions.CanTransition(SessionState.Disconnected, SessionState.Connected));
        Assert.True(SessionStateTransitions.CanTransition(SessionState.Checking, SessionState.Starting));
        Assert.True(SessionStateTransitions.CanTransition(SessionState.Connected, SessionState.Disconnecting));
        Assert.True(SessionStateTransitions.CanTransition(SessionState.NeedsAttention, SessionState.Checking));
    }

    private sealed class CancellableStartAdapter : IConnectableAdapter
    {
        public string Name => "blocking";
        public bool CancellationObserved { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRequiredFor(CapabilityFlags capabilities) => true;
        public Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        public Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
            => Task.FromResult("ready");
        public Task StopAsync(SessionPolicy policy) { StopCount++; return Task.CompletedTask; }
        public event EventHandler<string>? Faulted { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FlakyStopAdapter : IConnectableAdapter
    {
        public string Name => "flaky-stop";
        public int StopCount { get; private set; }
        public bool IsRequiredFor(CapabilityFlags capabilities) => true;
        public Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default) => Task.FromResult("ready");
        public Task StopAsync(SessionPolicy policy)
        {
            StopCount++;
            return StopCount == 1
                ? Task.FromException(new InvalidOperationException("simulated cleanup failure"))
                : Task.CompletedTask;
        }
        public event EventHandler<string>? Faulted { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingLifecycleAdapter : IConnectableAdapter
    {
        private readonly bool _failStart;
        private readonly bool _failVerify;
        private EventHandler<string>? _faulted;

        public FailingLifecycleAdapter(bool failStart = false, bool failVerify = false)
        {
            _failStart = failStart;
            _failVerify = failVerify;
        }

        public string Name => "failing-lifecycle";
        public int StopCount { get; private set; }
        public bool IsRequiredFor(CapabilityFlags capabilities) => true;
        public Task AssertReadyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task StartAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
            => _failStart
                ? Task.FromException(new InvalidOperationException("simulated start failure"))
                : Task.CompletedTask;
        public Task<string> VerifyAsync(SessionPolicy policy, CancellationToken cancellationToken = default)
            => _failVerify
                ? Task.FromException<string>(new InvalidOperationException("simulated verification failure"))
                : Task.FromResult("ready");
        public Task StopAsync(SessionPolicy policy)
        {
            StopCount++;
            return StopCount == 1
                ? Task.FromException(new InvalidOperationException("simulated cleanup failure"))
                : Task.CompletedTask;
        }
        public event EventHandler<string>? Faulted
        {
            add => _faulted += value;
            remove => _faulted -= value;
        }
        public void RaiseFaulted(string message) => _faulted?.Invoke(this, message);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
