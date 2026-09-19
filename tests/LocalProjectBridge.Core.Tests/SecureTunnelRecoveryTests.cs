using System.Text.Json;
using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Tests;

public sealed class SecureTunnelRecoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-tunnel-recovery-").FullName;
    private readonly ConnectionProfile _profile = new() { TunnelId = "tunnel_test" };
    private const string Target = "http://127.0.0.1:43123/mcp";
    private string Marker => Path.Combine(_root, "ownership", _profile.RuntimeAlias + ".json");

    [Fact]
    public async Task FailedAliasSave_RollbackAndRetryCanRecover()
    {
        var calls = new List<string>();
        var connects = 0;
        var runtime = CreateRuntime(args =>
        {
            calls.Add(args[1]);
            return args[1] switch
            {
                "connect" when ++connects == 1 => new(1, "", "replace state file: disk error"),
                "connect" => new(0, "{}", ""),
                "stop" => MissingAlias(),
                "status" => Healthy(),
                _ => throw new InvalidOperationException()
            };
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(runtime));
        Assert.Contains("disk error", error.Message);
        await runtime.StopOwnedAsync("fake.exe", _profile, _root);
        Assert.False(File.Exists(Marker));
        await Connect(runtime);
        Assert.Equal(new[] { "connect", "stop", "connect", "status" }, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: ")]
    public async Task StaleMarkerFromPreviousRun_DoesNotBlockConnect(string errorPrefix)
    {
        WriteMarker();
        var calls = new List<string>();
        var runtime = CreateRuntime(args =>
        {
            calls.Add(args[1]);
            return args[1] switch
            {
                "stop" => new(1, "", $"{errorPrefix}alias {_profile.RuntimeAlias} is not known; run create or connect first\n"),
                "connect" => new(0, "{}", ""),
                "status" => Healthy(),
                _ => throw new InvalidOperationException()
            };
        });

        await Connect(runtime);
        Assert.Equal(new[] { "stop", "connect", "status" }, calls);
        Assert.True(File.Exists(Marker));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("invalid-json")]
    [InlineData("null")]
    public async Task MissingAlias_WithUncertainProcessState_KeepsOwnership(string state)
    {
        WriteMarker();
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        var content = state == "process"
            ? JsonSerializer.Serialize(new Dictionary<string, object> { [_profile.RuntimeAlias] = new { pid = 123 } })
            : state;
        File.WriteAllText(Path.Combine(_root, "state", "processes.yaml"), content);
        var runtime = CreateRuntime(_ => MissingAlias());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StopOwnedAsync("fake.exe", _profile, _root));
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task MissingAlias_WithOnlyAnotherProcess_DoesNotChangeItsRecord()
    {
        WriteMarker();
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        const string content = "{\"another-alias\":{\"pid\":123}}";
        var path = Path.Combine(_root, "state", "processes.yaml");
        File.WriteAllText(path, content);
        var runtime = CreateRuntime(_ => MissingAlias());

        await runtime.StopOwnedAsync("fake.exe", _profile, _root);
        Assert.False(File.Exists(Marker));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("permission denied")]
    [InlineData("read state file: invalid JSON")]
    [InlineData("Error: alias another-alias is not known; run create or connect first")]
    public async Task OtherStopErrors_AreNotSuppressed(string error)
    {
        WriteMarker();
        var runtime = CreateRuntime(_ => new(1, "", error));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StopOwnedAsync("fake.exe", _profile, _root));
        Assert.Contains(error, failure.Message);
        Assert.True(File.Exists(Marker));
    }

    [Fact]
    public async Task PartiallyStartedConnectFailure_StillStopsOwnedRuntime()
    {
        var calls = new List<string>();
        var runtime = CreateRuntime(args =>
        {
            calls.Add(args[1]);
            return args[1] == "connect" ? new(1, "", "failed after starting process") : new(0, "{}", "");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Connect(runtime));
        Assert.True(File.Exists(Marker));
        await runtime.StopOwnedAsync("fake.exe", _profile, _root);
        Assert.False(File.Exists(Marker));
        Assert.Equal(new[] { "connect", "stop" }, calls);
    }

    private SecureTunnelRuntime CreateRuntime(Func<IReadOnlyList<string>, CommandResult> run)
    {
        var key = Path.Combine(_root, "test-key");
        File.WriteAllText(key, "test-only");
        _profile.RuntimeCredentialReference = "file:" + key;
        return new SecureTunnelRuntime(new CommandRunner(new RedactingLogger(_root)), null,
            (_, args, _, _) => Task.FromResult(run(args)));
    }

    private Task Connect(SecureTunnelRuntime runtime)
        => runtime.ConnectAsync("fake.exe", _profile, Target, _root, CancellationToken.None);

    private CommandResult MissingAlias() => new(1, "",
        $"Error: alias {_profile.RuntimeAlias} is not known; run create or connect first\n");

    private static CommandResult Healthy() => new(0,
        JsonSerializer.Serialize(new { ready = true, process_running = true, target_value = Target }), "");

    private void WriteMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, JsonSerializer.Serialize(new { connection_id = _profile.Id, alias = _profile.RuntimeAlias }));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
