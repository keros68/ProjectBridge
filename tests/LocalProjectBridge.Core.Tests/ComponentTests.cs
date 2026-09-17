using System.Text.Json;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Sessions;
using LocalProjectBridge.Core.Processes;
using System.Diagnostics;

namespace LocalProjectBridge.Core.Tests;

/// <summary>能力开关必须成为后端强制策略（设计文档 13.3）。</summary>
public sealed class TransceiverConfigTests
{
    private static SessionPolicy Policy(CapabilityFlags flags) => new(
        Guid.NewGuid(), Guid.NewGuid(), "演示项目",
        @"C:\work\demo", flags, DateTimeOffset.Now, null);

    [Fact]
    public void DelegateCapability_EnablesCodexTasks()
    {
        using var json = JsonDocument.Parse(TransceiverAdapter.BuildSessionConfigJson(Policy(CapabilityFlags.WebDelegateCodex)));
        var project = json.RootElement.GetProperty("projects").EnumerateObject().First().Value;
        Assert.True(project.GetProperty("allowCodexTasks").GetBoolean());
        Assert.Equal("deny", json.RootElement.GetProperty("unknownProject").GetString());
    }

    [Fact]
    public void ReadCapability_DisablesCodexTasks()
    {
        using var json = JsonDocument.Parse(TransceiverAdapter.BuildSessionConfigJson(Policy(CapabilityFlags.WebRead)));
        var project = json.RootElement.GetProperty("projects").EnumerateObject().First().Value;
        Assert.False(project.GetProperty("allowCodexTasks").GetBoolean());
    }

    [Fact]
    public void UnknownProjects_AreAlwaysDenied()
    {
        using var json = JsonDocument.Parse(TransceiverAdapter.BuildSessionConfigJson(Policy(CapabilityFlags.None)));
        Assert.Equal("deny", json.RootElement.GetProperty("unknownProject").GetString());
        Assert.Single(json.RootElement.GetProperty("projects").EnumerateObject());
    }

    [Fact]
    public void Config_PinsCanonicalProjectPath()
    {
        var policy = new SessionPolicy(Guid.NewGuid(), Guid.NewGuid(), "演示项目", @"C:\work\demo",
            CapabilityFlags.WebDelegateCodex, DateTimeOffset.Now, null);
        using var json = JsonDocument.Parse(TransceiverAdapter.BuildSessionConfigJson(policy));
        Assert.True(json.RootElement.GetProperty("projects").TryGetProperty(policy.CanonicalProjectPath, out _));
    }
}

public sealed class ProcessSupervisionTests
{
    [Fact]
    public async Task JobObject_DisposeTerminatesAttachedProcess()
    {
        using var process = Process.Start(new ProcessStartInfo("ping.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-t", "127.0.0.1" }
        }) ?? throw new InvalidOperationException("无法启动测试进程。");
        using (var job = JobObject.CreateKillOnClose())
        {
            job.Attach(process);
        }

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(process.HasExited);
    }

    [Fact]
    public void TunnelStatus_RequiresReadyRunningAndExactTarget()
    {
        const string json = """{"ready":true,"process_running":true,"target_value":"http://127.0.0.1:43123/mcp"}""";
        Assert.True(SecureTunnelRuntime.StatusMatchesTarget(json, "http://127.0.0.1:43123/mcp"));
        Assert.False(SecureTunnelRuntime.StatusMatchesTarget(json, "http://127.0.0.1:8876/mcp"));
        Assert.False(SecureTunnelRuntime.StatusMatchesTarget(
            """{"ready":false,"process_running":true,"target_value":"http://127.0.0.1:43123/mcp"}""",
            "http://127.0.0.1:43123/mcp"));
        Assert.True(SecureTunnelRuntime.StatusMatchesTarget(
            """{"ready":true,"process_running":true,"process":{"target_value":"http://127.0.0.1:43123/mcp"}}""",
            "http://127.0.0.1:43123/mcp"));
    }

}

public sealed class C2cAdapterCapabilityTests : IDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("lpb-c2c").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void UnifiedGatewayMode_DoesNotStartC2cForWebReadAlone()
    {
        var runner = new CommandRunner(new RedactingLogger(_appData));
        var adapter = new C2cAdapter(runner, serveWebRead: false);

        Assert.False(adapter.IsRequiredFor(CapabilityFlags.WebRead));
        Assert.True(adapter.IsRequiredFor(CapabilityFlags.CodexAskWeb));
    }

    [Fact]
    public void JsonOutputParser_AcceptsAnsiAndDiagnosticPrefix()
    {
        using var parsed = C2cAdapter.ParseJsonOutput(
            "\u001b[36mdiagnostic\u001b[0m\r\n\u001b[32m{\"ok\":true,\"needsChoice\":false}\u001b[0m\r\n");

        Assert.True(parsed.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(parsed.RootElement.GetProperty("needsChoice").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void JsonCommandFailure_PreservesBackendReasonFromStdout(int exitCode)
    {
        var error = Assert.Throws<InvalidOperationException>(() => C2cAdapter.ParseCommandResult(
            new CommandResult(exitCode, """{"ok":false,"error":"Tunnel start timed out"}""", "")));
        Assert.Contains("Tunnel start timed out", error.Message);
    }

    [Fact]
    public void NonJsonCommandFailure_PreservesStderr()
    {
        var error = Assert.Throws<InvalidOperationException>(() => C2cAdapter.ParseCommandResult(
            new CommandResult(1, "", "组件启动失败。")));
        Assert.Contains("组件启动失败。", error.Message);
    }

    [Fact]
    public void MalformedSuccessfulOutput_IsNotAcceptedAsHealthy()
    {
        Assert.Throws<JsonException>(() => C2cAdapter.ParseCommandResult(new CommandResult(0, "", "")));
    }
}

public sealed class RegistryStoreTests : IDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("lpb-store").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsProjects()
    {
        var store = new RegistryStore(_appData);
        var settings = new AppSettings();
        RegistryStore.UpsertProject(settings, @"C:\work\demo", "演示项目");
        RegistryStore.UpsertProject(settings, @"C:\work\other");
        await store.SaveAsync(settings);

        var loaded = await new RegistryStore(_appData).LoadAsync();
        Assert.Equal(2, loaded.Projects.Count);
        Assert.Equal("演示项目", loaded.Projects[0].Name);
        Assert.NotEqual(Guid.Empty, loaded.Projects[0].Id);
    }

    [Fact]
    public async Task StartupAndReadOnlyRestore_AreIndependentAndDefaultOff()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.StartWithWindows);
        Assert.False(defaults.RestoreReadOnlyConnection);

        var store = new RegistryStore(_appData);
        defaults.StartWithWindows = true;
        await store.SaveAsync(defaults);
        var loaded = await store.LoadAsync();

        Assert.True(loaded.StartWithWindows);
        Assert.False(loaded.RestoreReadOnlyConnection);

        loaded.RestoreReadOnlyConnection = true;
        await store.SaveAsync(loaded);
        Assert.True((await store.LoadAsync()).RestoreReadOnlyConnection);
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCollideOnTemporaryFile()
    {
        var settings = new AppSettings();
        RegistryStore.UpsertProject(settings, @"C:\work\concurrent", "concurrent");

        var stores = Enumerable.Range(0, 8).Select(_ => new RegistryStore(_appData)).ToArray();
        await Task.WhenAll(stores.Select(store => store.SaveAsync(settings)));

        var loaded = await stores[0].LoadAsync();
        Assert.Single(loaded.Projects);
        Assert.Equal("concurrent", loaded.Projects[0].Name);
        Assert.Empty(Directory.EnumerateFiles(_appData, "*.tmp"));
    }

    [Fact]
    public async Task Load_RecoversNewerValidLegacyTemporaryFile()
    {
        var store = new RegistryStore(_appData);
        var oldSettings = new AppSettings();
        RegistryStore.UpsertProject(oldSettings, @"C:\work\old", "old");
        await store.SaveAsync(oldSettings);
        File.SetLastWriteTimeUtc(Path.Combine(_appData, "projects.json"), DateTime.UtcNow.AddMinutes(-2));

        var recovered = new AppSettings();
        RegistryStore.UpsertProject(recovered, @"C:\work\current", "current");
        await File.WriteAllTextAsync(Path.Combine(_appData, "projects.json.tmp"),
            System.Text.Json.JsonSerializer.Serialize(recovered));

        var loaded = store.Load();

        Assert.Equal("current", Assert.Single(loaded.Projects).Name);
        Assert.False(File.Exists(Path.Combine(_appData, "projects.json.tmp")));
    }

    [Fact]
    public void Upsert_IgnoresPathCase()
    {
        var settings = new AppSettings();
        var first = RegistryStore.UpsertProject(settings, @"C:\work\Demo", "A");
        var second = RegistryStore.UpsertProject(settings, @"C:\WORK\demo", "B");
        Assert.Same(first, second);
        Assert.Single(settings.Projects);
        Assert.Equal("B", second.Name);
    }    [Fact]
    public void Upsert_NormalizesTrailingSeparator()
    {
        var settings = new AppSettings();
        RegistryStore.UpsertProject(settings, @"C:\work\demo\");
        Assert.Equal(@"C:\work\demo", settings.Projects[0].Path);
    }

    [Fact]
    public async Task Load_AssignsMissingIds()
    {
        var path = Path.Combine(_appData, "projects.json");
        await File.WriteAllTextAsync(path,
            """{"Version":1,"Projects":[{"Name":"legacy","Path":"C:\\w"}]}""");
        var loaded = await new RegistryStore(_appData).LoadAsync();
        Assert.NotEqual(Guid.Empty, loaded.Projects[0].Id);
    }
}

public sealed class ErrorClassifierTests
{
    private static BridgeError Classify(Exception error, string phase = "start")
        => ErrorClassifier.Classify(error, CapabilityFlags.WebRead | CapabilityFlags.WebDelegateCodex, phase);

    [Fact]
    public void MovedProject_GivesRedirectionAdvice()
    {
        var error = Classify(new DirectoryNotFoundException("missing"));
        Assert.Contains("重新选择", error.NextAction);
        Assert.Contains("网页读取项目", error.AffectedCapability);
    }

    [Fact]
    public void TunnelTimeout_PointsAtNetworkOrSetup()
    {
        var error = Classify(new TimeoutException("隧道未进入就绪状态，可能需要重新授权或检查网络。"), "start-tunnel");
        Assert.Contains("网络", error.NextAction);
        Assert.Contains("隧道", error.What);
    }

    [Fact]
    public void MissingComponent_PointsAtSetup()
    {
        var error = Classify(new InvalidOperationException("委派 Codex 任务所需组件尚未就绪：Node.js。"));
        Assert.Contains("首次设置", error.NextAction);
    }

    [Fact]
    public void AuthorizationError_PointsAtChatGptConfirmation()
    {
        var error = Classify(new InvalidOperationException("ChatGPT 侧连接需要确认：配对码已过期。"));
        Assert.Contains("ChatGPT", error.NextAction);
        Assert.Equal("网页端与本地项目的全部协作能力", error.AffectedCapability);
    }

    [Fact]
    public void UnknownError_StaysGenericWithoutLeakingDetail()
    {
        var error = Classify(new Exception("boom with token=abc123"));
        Assert.Equal("发生意外错误。", error.What);
        Assert.NotNull(error.Detail);
        Assert.DoesNotContain("abc123", error.What); // What 只包含摘要，原始细节留在 Detail（仅日志）
        Assert.DoesNotContain("abc123", error.ToString());
    }

    [Fact]
    public void MissingRuntimeCredential_PointsToGuidedSetup()
    {
        var error = Classify(new MissingTunnelRuntimeCredentialException("missing runtime key"));
        Assert.Equal("网页端与本地项目的连接", error.AffectedCapability);
        Assert.Contains("连接向导中创建或粘贴运行密钥", error.NextAction);
        Assert.DoesNotContain("详情：", error.ToString());
    }

    [Theory]
    [InlineData("check")]
    [InlineData("start")]
    [InlineData("authorize")]
    public void ConnectionCancellation_IsNotReportedAsDisconnectFailure(string phase)
    {
        var error = Classify(new OperationCanceledException(), phase);
        Assert.Contains("连接操作", error.What);
        Assert.Contains("连接", error.NextAction);
        Assert.DoesNotContain("断开", error.What);
    }

    [Fact]
    public void DisconnectCancellation_KeepsDisconnectAdvice()
    {
        var error = Classify(new OperationCanceledException(), "stop");
        Assert.Contains("断开操作", error.What);
        Assert.Contains("断开", error.NextAction);
    }
}
