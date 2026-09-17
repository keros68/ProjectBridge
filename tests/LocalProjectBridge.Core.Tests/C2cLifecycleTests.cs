using System.Diagnostics;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

public sealed class C2cLifecycleTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-lifecycle-").FullName;
    private C2cAdapter _adapter = null!;
    private ProjectRecord _project = null!;
    private string State => Path.Combine(_root, "state");

    public async Task InitializeAsync()
    {
        var node = new RuntimeDiscovery().Discover().Node ?? throw new InvalidOperationException("C2C process tests require Node.js, as does ProjectBridge.");
        Directory.CreateDirectory(State);
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var cli = Path.Combine(_root, "fake-c2c.cjs");
        await File.WriteAllTextAsync(cli, """
            const fs = require('fs'), path = require('path'), http = require('http');
            const {spawn} = require('child_process');
            const args = process.argv.slice(2), command = args[0], state = process.env.C2C_STATE_DIR;
            const runtimeFile = path.join(state, 'test-runtime.json');
            const mode = () => fs.readFileSync(path.join(state, 'mode.txt'), 'utf8');
            const value = key => args[args.indexOf(key) + 1];
            if (command === 'serve') {
              const workspaceRoot = path.resolve(value('--workspace'));
              const workspaceId = require('crypto').createHash('sha256').update(workspaceRoot.toLowerCase()).digest('hex').slice(0,12);
              const server = http.createServer((req,res) => { res.setHeader('Content-Type','application/json'); res.end(JSON.stringify({service:'c2c-bridge',status:'ok',workspaceId})); });
              server.listen(Number(value('--port')), '127.0.0.1', () => {
                const child = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {detached:true, stdio:'ignore'});
                child.unref();
                fs.writeFileSync(runtimeFile, JSON.stringify({pid:process.pid,childPid:child.pid,port:server.address().port,workspaceRoot}));
                console.log('test bridge ready');
              });
            } else if (command === 'status') {
              console.log(JSON.stringify({ok:true,running:true,tokenCount:fs.existsSync(path.join(state,'revoked.txt'))?0:1,...JSON.parse(fs.readFileSync(runtimeFile,'utf8')),tunnel:{running:mode()==='success'}}));
            } else if (command === 'start') {
              const child = spawn(process.execPath, ['-e', 'setInterval(()=>{},1000)'], {detached:true, stdio:'ignore'});
              child.unref();
              fs.writeFileSync(path.join(state,'command-child.pid'),String(child.pid));
              if (mode()==='wait') setInterval(()=>{},1000);
              else if (mode()==='fail') { console.log(JSON.stringify({ok:false,error:'simulated tunnel failure'})); process.exitCode=1; }
              else console.log(JSON.stringify({ok:true,mcpUrl:'https://example.invalid/mcp',workspaceName:'test',connectorName:'test'}));
            } else if (command === 'unpair') {
              if (mode()==='unpair-fail') process.exitCode=1;
              else if (mode()==='unpair-wait') setInterval(()=>{},1000);
              else fs.writeFileSync(path.join(state,'revoked.txt'),'true');
            }
            """);
        var paths = new RuntimePaths(node, null, null, null, null, cli, node);
        _adapter = new C2cAdapter(new CommandRunner(new RedactingLogger(_root)), stateDirectory: State, runtimePaths: paths);
        _project = new ProjectRecord { Name = "test", Path = workspace };
    }

    [Fact]
    public async Task SetupHandoff_PreservesProcessUrlAndAuthorizationUntilMainDisconnects()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "success");
        var setup = await _adapter.StartForSetupAsync(_project);
        var before = await File.ReadAllTextAsync(Path.Combine(State, "test-runtime.json"));
        await using var controller = new SessionController([_adapter], new RedactingLogger(_root), _root);

        Assert.True(await controller.AdoptSetupConnectionAsync(_project, _adapter));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(State, "test-runtime.json")));
        Assert.Equal(setup.McpUrl, _adapter.ObservedMcpUrl);
        Assert.False(File.Exists(Path.Combine(State, "revoked.txt")));
        await _adapter.VerifyAsync(controller.CurrentSession!);

        await controller.DisconnectAsync();
        AssertStopped();
        Assert.False(File.Exists(Path.Combine(State, "revoked.txt")));
    }

    [Fact]
    public async Task FailedTunnelStart_ReclaimsBridgeAndDetachedChild()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "fail");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.StartForSetupAsync(_project));
        Assert.Contains("simulated tunnel failure", error.Message);
        Assert.True(File.Exists(Path.Combine(State, "command-child.pid")));
        AssertStopped();
        Assert.Null(_adapter.ObservedMcpUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrDisposeDuringStart_ReclaimsOwnedProcesses(bool dispose)
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "wait");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = _adapter.StartForSetupAsync(_project, cancellation.Token);
        while (!File.Exists(Path.Combine(State, "test-runtime.json")))
            await Task.Delay(50, cancellation.Token);
        if (dispose) await _adapter.DisposeAsync();
        else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        AssertStopped();
    }

    [Fact]
    public async Task ConnectedSession_DisconnectKeepsPairingAndClosesPort()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "success");
        await _adapter.StartForSetupAsync(_project);
        var policy = SessionPolicyFactory.Create(_project, CapabilityFlags.WebRead);
        await _adapter.VerifyAsync(policy);
        await _adapter.StopAsync(policy);
        AssertStopped();
        Assert.False(File.Exists(Path.Combine(State, "revoked.txt")));
        using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(State, "test-runtime.json")));
        using var client = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => client.ConnectAsync("127.0.0.1", json.RootElement.GetProperty("port").GetInt32()));
    }

    [Fact]
    public async Task ExplicitForget_RevokesPairing()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "success");
        await _adapter.StartForSetupAsync(_project);
        Assert.True(await _adapter.HasAuthorizationAsync(_project.Path));
        await _adapter.ForgetAuthorizationAsync();
        Assert.True(File.Exists(Path.Combine(State, "revoked.txt")));
        await _adapter.StopAsync(SessionPolicyFactory.Create(_project, CapabilityFlags.WebRead));
        await _adapter.StartForSetupAsync(_project);
        Assert.False(await _adapter.HasAuthorizationAsync(_project.Path));
    }

    [Fact]
    public async Task ExplicitForget_NonZeroExitIsReportedAndAuthorizationFactIsRetained()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "unpair-fail");
        await _adapter.StartForSetupAsync(_project);
        Assert.True(await _adapter.HasAuthorizationAsync(_project.Path));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.ForgetAuthorizationAsync());

        Assert.True(_adapter.Readiness.ClientAuthorized);
        Assert.False(File.Exists(Path.Combine(State, "revoked.txt")));
    }

    [Fact]
    public async Task ExplicitForget_MissingComponentIsReported()
    {
        var node = new RuntimeDiscovery().Discover().Node!;
        var missing = Path.Combine(_root, "missing-c2c.js");
        await using var adapter = new C2cAdapter(
            new CommandRunner(new RedactingLogger(_root)),
            stateDirectory: Path.Combine(_root, "missing-state"),
            runtimePaths: new RuntimePaths(node, null, null, null, null, missing, node));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ForgetAuthorizationAsync());
        Assert.Contains("尚未安装", error.Message);
    }

    [Fact]
    public async Task ExplicitForget_CancellationIsReportedAndAuthorizationFactIsRetained()
    {
        await File.WriteAllTextAsync(Path.Combine(State, "mode.txt"), "unpair-wait");
        await _adapter.StartForSetupAsync(_project);
        Assert.True(await _adapter.HasAuthorizationAsync(_project.Path));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _adapter.ForgetAuthorizationAsync(cancellation.Token));

        Assert.True(_adapter.Readiness.ClientAuthorized);
        Assert.False(File.Exists(Path.Combine(State, "revoked.txt")));
    }

    private void AssertStopped()
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(State, "test-runtime.json")));
        var pids = new List<int> { json.RootElement.GetProperty("pid").GetInt32(), json.RootElement.GetProperty("childPid").GetInt32() };
        var commandChild = Path.Combine(State, "command-child.pid");
        if (File.Exists(commandChild)) pids.Add(int.Parse(File.ReadAllText(commandChild)));
        foreach (var pid in pids)
        {
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"Process {pid} survived cleanup"); }
            catch (ArgumentException) { }
        }
    }

    public async Task DisposeAsync()
    {
        if (_adapter is not null) await _adapter.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
