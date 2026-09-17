using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Processes;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

public sealed class GatewayHealthMonitorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-gateway-health-").FullName;
    private readonly string _projectPath;
    private readonly string _tunnelClient;

    public GatewayHealthMonitorTests()
    {
        _projectPath = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        _tunnelClient = Path.Combine(_root, "tunnel-client.exe");
        File.WriteAllBytes(_tunnelClient, []);
    }

    public static TheoryData<SecureTunnelHealth> RuntimeFaults => new()
    {
        new SecureTunnelHealth(false, true, true),
        new SecureTunnelHealth(true, false, true),
        new SecureTunnelHealth(true, true, false)
    };

    [Theory]
    [MemberData(nameof(RuntimeFaults))]
    public async Task RuntimeHealthFailure_DegradesControllerStopsGatewayAndPreservesCallAsHistory(
        SecureTunnelHealth failure)
    {
        var runtime = new QueueTunnelRuntime();
        runtime.Enqueue(SecureTunnelHealth.Healthy);
        var registry = new ProjectAuthorizationRegistry();
        var project = new ProjectRecord { Name = "fault-write", Path = _projectPath };
        registry.ReplaceProjects([project]);
        var service = new ProjectWriteService(registry, new WriteLeaseStore(), new ChangeJournal(Path.Combine(_root, "changes")));
        var profile = NewProfile();
        var adapter = CreateAdapter(runtime, service, registry, profile);
        await using var controller = new SessionController([adapter], new RedactingLogger(_root), _root);

        Assert.True(await controller.ConnectAsync(profile, _projectPath, CapabilityFlags.WebRead));
        await MakeVerifiedToolCallAsync(adapter.ListenUrl!);
        var verifiedAt = controller.Readiness.LastVerifiedCall;
        Assert.NotNull(verifiedAt);
        var clientIdentity = controller.Readiness.ClientIdentity!;
        service.GrantAutoApply(project.Id, profile.Id, clientIdentity, TimeSpan.FromHours(1));

        runtime.Enqueue(failure);
        await WaitUntilAsync(() => controller.State == SessionState.NeedsAttention);

        Assert.False(controller.Readiness.LocalReady);
        Assert.False(controller.Readiness.TransportReady);
        Assert.False(controller.Readiness.ClientAuthorized);
        Assert.Equal(verifiedAt, controller.Readiness.LastVerifiedCall);
        Assert.Null(adapter.ListenUrl);
        Assert.Null(service.GetAutoApplyLease(project.Id, profile.Id, clientIdentity));
        Assert.Equal(1, runtime.StopCount);
        var completedProbes = runtime.ProbeCount;
        await Task.Delay(80);
        Assert.Equal(completedProbes, runtime.ProbeCount);
    }

    [Fact]
    public async Task ReconnectSameConnection_DoesNotRestoreWebVerificationBeforeAnotherRealCall()
    {
        var runtime = new QueueTunnelRuntime();
        runtime.Enqueue(SecureTunnelHealth.Healthy);
        var adapter = CreateAdapter(runtime);
        await using var controller = new SessionController([adapter], new RedactingLogger(_root), _root);
        var profile = NewProfile();

        Assert.True(await controller.ConnectAsync(profile, _projectPath, CapabilityFlags.WebRead));
        await MakeVerifiedToolCallAsync(adapter.ListenUrl!);
        Assert.NotNull(controller.Readiness.LastVerifiedCall);
        runtime.Enqueue(new SecureTunnelHealth(false, true, true));
        await WaitUntilAsync(() => controller.State == SessionState.NeedsAttention);

        runtime.Enqueue(SecureTunnelHealth.Healthy);
        Assert.True(await controller.ConnectAsync(profile, _projectPath, CapabilityFlags.WebRead));

        Assert.True(controller.Readiness.LocalReady);
        Assert.True(controller.Readiness.TransportReady);
        Assert.False(controller.Readiness.ClientAuthorized);
        Assert.Null(controller.Readiness.LastVerifiedCall);
        Assert.NotNull(profile.LastVerifiedCall);
    }

    [Fact]
    public async Task OldMonitorResult_CannotOverwriteANewerConnection()
    {
        var runtime = new BlockingTunnelRuntime();
        var adapter = CreateAdapter(runtime);
        var policy = NewPolicy();
        var faults = 0;
        adapter.Faulted += (_, _) => faults++;

        await adapter.StartAsync(policy);
        await adapter.VerifyAsync(policy);
        await WaitUntilAsync(() => runtime.ProbeCount >= 2);
        await adapter.StopAsync(policy);

        await adapter.StartAsync(policy);
        await adapter.VerifyAsync(policy);
        runtime.CompleteOldProbe(new SecureTunnelHealth(false, false, false));
        await Task.Delay(80);

        Assert.True(adapter.Readiness.LocalReady);
        Assert.True(adapter.Readiness.TransportReady);
        Assert.Equal(0, faults);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task SecureTunnelEntry_PreparesRemotelyAndAppliesOnlyAfterLocalConfirmation()
    {
        var runtime = new QueueTunnelRuntime();
        runtime.Enqueue(SecureTunnelHealth.Healthy);
        var profile = NewProfile();
        var registry = new ProjectAuthorizationRegistry();
        var project = new ProjectRecord { Name = "fixed-write", Path = _projectPath };
        registry.ReplaceProjects([project]);
        await File.WriteAllTextAsync(Path.Combine(_projectPath, "a.txt"), "one");
        var leases = new WriteLeaseStore();
        var journal = new ChangeJournal(Path.Combine(_root, "fixed-changes"));
        var service = new ProjectWriteService(registry, leases, journal);
        using var collaboration = new LocalProjectBridge.Core.Collaboration.CollaborationStore(Path.Combine(_root, "collaboration"));
        await using var adapter = new GatewayAdapter(
            new RedactingLogger(_root), new CommandRunner(new RedactingLogger(_root)),
            connectionProfile: profile, authorizations: registry,
            stateDirectory: Path.Combine(_root, "fixed-tunnel-state"),
            runtimePaths: new RuntimePaths(null, null, null, null, _tunnelClient, null, null),
            tunnelRuntime: runtime, monitorInterval: TimeSpan.FromSeconds(30), writeService: service,
            collaborationStore: collaboration);
        var policy = SessionPolicyFactory.CreateConnection(profile, _projectPath, CapabilityFlags.WebRead);
        await adapter.StartAsync(policy);
        await adapter.VerifyAsync(policy);

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        async Task<JsonObject> Call(string name, object arguments)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(
                new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });
            using var response = await client.PostAsync(adapter.ListenUrl!,
                new StringContent(body, Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsObject();
        }

        var request = collaboration.Submit(project, profile.Id, "Fixed tunnel relay test.", "integration");
        Assert.Contains("Fixed tunnel relay test.", (await Call("get_collaboration_request",
            new { request_id = request.RequestId, access_code = request.AccessCode })).ToJsonString());
        Assert.False((await Call("reply_to_collaboration_request",
            new { request_id = request.RequestId, access_code = request.AccessCode, turn_id = request.TurnId, reply = "Fixed tunnel reply." }))["isError"]!.GetValue<bool>());
        Assert.Equal("Fixed tunnel reply.", collaboration.Get(request.RequestId)!.Reply);

        var prepared = JsonNode.Parse((await Call("prepare_change", new
        {
            project_id = project.Id, request_id = Guid.NewGuid(),
            operations = new[] { new { type = "patch", path = "a.txt", old_text = "one", new_text = "two" } }
        }))["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("Prepared", prepared["status"]!.GetValue<string>());
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(_projectPath, "a.txt")));

        var denied = JsonNode.Parse((await Call("apply_change", new
        {
            project_id = project.Id, change_id = prepared["change_id"]!.GetValue<string>(), request_id = Guid.NewGuid()
        }))["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        Assert.Equal("local_confirmation_required", denied["error"]!["code"]!.GetValue<string>());
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(_projectPath, "a.txt")));

        var applied = await service.ApplyLocallyAsync(Guid.Parse(prepared["change_id"]!.GetValue<string>()));
        Assert.Equal(ChangeStatus.Applied, applied.Status);
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(_projectPath, "a.txt")));

        service.GrantAutoApply(project.Id, profile.Id, adapter.Readiness.ClientIdentity!, TimeSpan.FromMinutes(15));
        async Task<JsonObject> Payload(string name, object args)
            => JsonNode.Parse((await Call(name, args))["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var listed = await Payload("list_projects", new { });
        Assert.False(listed["projects"]![0]!["changes_require_local_confirmation"]!.GetValue<bool>());
        Assert.Equal("auto_apply", listed["projects"]![0]!["write_mode"]!.GetValue<string>());
        var deletion = await Payload("prepare_change", new
        {
            project_id = project.Id, request_id = Guid.NewGuid(),
            operations = new[] { new { type = "delete", path = "a.txt" } }
        });
        Assert.False(deletion["requires_deletion_confirmation"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(_projectPath, "a.txt")));
        var autoApplied = await Payload("apply_change", new
        {
            project_id = project.Id, change_id = deletion["change_id"]!.GetValue<string>(), request_id = Guid.NewGuid()
        });
        Assert.Equal("Applied", autoApplied["status"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_projectPath, "a.txt")));
        var restored = await Payload("restore_change", new
        {
            project_id = project.Id, change_id = deletion["change_id"]!.GetValue<string>(), request_id = Guid.NewGuid()
        });
        Assert.Equal("Restored", restored["status"]!.GetValue<string>());
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(_projectPath, "a.txt")));
        var clientIdentity = adapter.Readiness.ClientIdentity;
        await adapter.StopAsync(policy);
        Assert.Null(service.GetAutoApplyLease(project.Id, profile.Id, clientIdentity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedTunnelRoutesWritableTasksAndReclaimsOnPermissionChangeAndDisconnect(bool probeThrows)
    {
        var registry = new ProjectAuthorizationRegistry();
        var project = new ProjectRecord { Name = "delegate", Path = _projectPath, AllowCodexTasks = true, AllowCodexWrite = true };
        registry.ReplaceProjects([project]);
        var leases = new List<TaskProbe>();
        var runtime = new QueueTunnelRuntime();
        await using var adapter = new GatewayAdapter(new RedactingLogger(_root), new CommandRunner(new RedactingLogger(_root)),
            connectionProfile: NewProfile(), authorizations: registry, stateDirectory: Path.Combine(_root, "task-tunnel"),
            runtimePaths: new RuntimePaths(null, null, null, null, _tunnelClient, null, null), tunnelRuntime: runtime,
            monitorInterval: TimeSpan.FromMilliseconds(10),
            taskBridgeFactory: (_, _) => { var probe = new TaskProbe(); leases.Add(probe); return Task.FromResult(new CodexTaskBridgeLease(probe, probe)); });
        var policy = NewPolicy();
        await adapter.StartAsync(policy);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        async Task<JsonNode> Start()
        {
            using var response = await client.PostAsync(adapter.ListenUrl, new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "codex_task_start", arguments = new { project_id = project.Id, prompt = "修改并检查" } }
            }), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        Assert.False((await Start())["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("workspace-write", leases[0].Sandbox);
        project.AllowCodexWrite = false;
        registry.ReplaceProjects([project]);
        await adapter.RefreshSharedPermissionsAsync();
        Assert.True(leases[0].Disposed);
        Assert.False((await Start())["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("read-only", leases[1].Sandbox);
        if (probeThrows)
        {
            runtime.Enqueue(SecureTunnelHealth.Healthy);
            await adapter.VerifyAsync(policy);
            runtime.FailProbe();
            await WaitUntilAsync(() => !adapter.Readiness.LocalReady);
            Assert.True(leases[1].Disposed);
        }
        await adapter.StopAsync(policy);
        Assert.True(leases[1].Disposed);
    }

    private sealed class TaskProbe : ICodexTaskBridge, IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public string? Sandbox { get; private set; }
        public Task<JsonObject?> CallToolAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
        {
            Sandbox = arguments["sandbox"]?.GetValue<string>();
            return Task.FromResult<JsonObject?>(new JsonObject { ["isError"] = false, ["content"] = new JsonArray(new JsonObject {
                ["type"] = "text", ["text"] = "{\"jobId\":\"test-job\"}"
            }) });
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private GatewayAdapter CreateAdapter(ISecureTunnelRuntime runtime, ProjectWriteService? service = null,
        ProjectAuthorizationRegistry? authorizations = null, ConnectionProfile? profile = null)
        => new(
            new RedactingLogger(_root),
            new CommandRunner(new RedactingLogger(_root)),
            connectionProfile: profile ?? NewProfile(),
            authorizations: authorizations ?? new ProjectAuthorizationRegistry(),
            stateDirectory: Path.Combine(_root, "tunnel-state"),
            runtimePaths: new RuntimePaths(null, null, null, null, _tunnelClient, null, null),
            tunnelRuntime: runtime,
            monitorInterval: TimeSpan.FromMilliseconds(10), writeService: service);

    private static ConnectionProfile NewProfile() => new()
    {
        Name = "health-test",
        Provider = TunnelProvider.OpenAiSecureTunnel,
        TunnelId = "tunnel_health_test",
        RuntimeCredentialReference = "env:NOT_USED_BY_FAKE"
    };

    private SessionPolicy NewPolicy()
        => SessionPolicyFactory.CreateConnection(NewProfile(), _projectPath, CapabilityFlags.WebRead);

    private static async Task MakeVerifiedToolCallAsync(string url)
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var response = await client.PostAsync(url, new StringContent(
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"list_projects\",\"arguments\":{}}}",
            Encoding.UTF8,
            "application/json"));
        response.EnsureSuccessStatusCode();
        await Task.Delay(20);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(10);
        Assert.True(condition(), "Expected asynchronous state change did not occur.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class QueueTunnelRuntime : ISecureTunnelRuntime
    {
        private readonly Channel<SecureTunnelHealth> _health = Channel.CreateUnbounded<SecureTunnelHealth>();
        public int ProbeCount { get; private set; }
        public int StopCount { get; private set; }
        public void Enqueue(SecureTunnelHealth health) => _health.Writer.TryWrite(health);
        public void FailProbe() => _health.Writer.TryComplete(new IOException("test probe failure"));
        public Task AssertRuntimeCredentialReadyAsync(ConnectionProfile profile) => Task.CompletedTask;
        public Task ConnectAsync(string executable, ConnectionProfile profile, string mcpServerUrl,
            string stateDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<SecureTunnelHealth> ProbeHealthAsync(string executable, ConnectionProfile profile,
            string mcpServerUrl, string stateDirectory, CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            return await _health.Reader.ReadAsync(cancellationToken);
        }
        public Task StopOwnedAsync(string executable, ConnectionProfile profile, string stateDirectory,
            CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingTunnelRuntime : ISecureTunnelRuntime
    {
        private readonly TaskCompletionSource<SecureTunnelHealth> _oldProbe =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProbeCount { get; private set; }
        public Task AssertRuntimeCredentialReadyAsync(ConnectionProfile profile) => Task.CompletedTask;
        public Task ConnectAsync(string executable, ConnectionProfile profile, string mcpServerUrl,
            string stateDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SecureTunnelHealth> ProbeHealthAsync(string executable, ConnectionProfile profile,
            string mcpServerUrl, string stateDirectory, CancellationToken cancellationToken = default)
        {
            ProbeCount++;
            return ProbeCount switch
            {
                1 or 3 => Task.FromResult(SecureTunnelHealth.Healthy),
                2 => _oldProbe.Task,
                _ => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ContinueWith(_ => SecureTunnelHealth.Healthy, CancellationToken.None)
            };
        }
        public void CompleteOldProbe(SecureTunnelHealth health) => _oldProbe.TrySetResult(health);
        public Task StopOwnedAsync(string executable, ConnectionProfile profile, string stateDirectory,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
