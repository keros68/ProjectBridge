using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Gateway;
using Xunit.Abstractions;

namespace LocalProjectBridge.Core.Tests;

// Explicit release acceptance; ordinary test runs never submit model work.
public sealed class LiveNativeTaskFactAttribute : FactAttribute
{
    public LiveNativeTaskFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LPB_TEST_LIVE_CODEX") != "1")
            Skip = "Opt in with LPB_TEST_LIVE_CODEX=1 and LPB_TEST_PROJECT_ROOT.";
    }
}

public sealed class LiveNativeTaskTests(ITestOutputHelper output)
{
    [LiveNativeTaskFact]
    public async Task NativeWriteTaskHasVerifiedFileAndPersistentThread()
    {
        var root = Environment.GetEnvironmentVariable("LPB_TEST_PROJECT_ROOT")!;
        Assert.True(Directory.Exists(root));
        var marker = Path.Combine(root, $".projectbridge-live-{Guid.NewGuid():N}.txt");
        var state = Directory.CreateTempSubdirectory("lpb-live-state-").FullName;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var project = new ProjectRecord { Name = "release acceptance", Path = root, AllowCodexTasks = true, AllowCodexWrite = true };
        var lease = await NativeCodexTaskBridge.StartAsync(project, new RuntimeDiscovery(), state, timeout.Token);
        try
        {
            static JsonObject Payload(JsonObject value) => JsonNode.Parse(value["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
            var result = Payload((await lease.Bridge.CallToolAsync("codex_task", new JsonObject {
                ["activityTitle"] = "ProjectBridge release acceptance",
                ["prompt"] = $"Create ONLY {marker} with one UTF-8 line NATIVE_WRITE_OK using your file edit tool, then read it back. A trailing newline is allowed. Do not edit any other file or inspect unrelated files. Report actual failures."
            }, timeout.Token))!);
            output.WriteLine("threadId=" + result["threadId"]);
            while (result["status"]!.GetValue<string>() == "running")
                result = Payload((await lease.Bridge.CallToolAsync("codex_status", new JsonObject {
                    ["jobId"] = result["jobId"]!.DeepClone(), ["waitFor"] = "terminal", ["waitMs"] = 10000
                }, timeout.Token))!);
            output.WriteLine(result.ToJsonString());
            Assert.Equal("completed", result["status"]!.GetValue<string>());
            Assert.True(result["writeAccessVerified"]!.GetValue<bool>());
            Assert.Empty(result["toolFailures"]!.AsArray());
            Assert.Matches("^NATIVE_WRITE_OK(?:\\r?\\n)?$", await File.ReadAllTextAsync(marker, timeout.Token));
            Assert.StartsWith("codex://threads/", result["threadUrl"]!.GetValue<string>());
        }
        finally
        {
            await lease.Owner.DisposeAsync();
            if (File.Exists(marker)) File.Delete(marker);
            TestTree.Delete(state);
        }
    }

    [LiveNativeTaskFact]
    public async Task NativeWriteTaskFailsBeforeModelOnKnownDeniedRoot()
    {
        var root = Environment.GetEnvironmentVariable("LPB_TEST_DENIED_ROOT")!;
        Assert.True(Directory.Exists(root));
        var state = Directory.CreateTempSubdirectory("lpb-live-denied-").FullName;
        var project = new ProjectRecord { Name = "denied acceptance", Path = root, AllowCodexTasks = true, AllowCodexWrite = true };
        var lease = await NativeCodexTaskBridge.StartAsync(project, new RuntimeDiscovery(), state, CancellationToken.None);
        try
        {
            var error = await Assert.ThrowsAsync<CodexTaskBridgeException>(() => lease.Bridge.CallToolAsync("codex_task",
                new JsonObject { ["prompt"] = "This task must be rejected before model execution." }, CancellationToken.None));
            Assert.Contains("workspace_write_unavailable", error.Message);
            output.WriteLine(error.Message);
        }
        finally { await lease.Owner.DisposeAsync(); TestTree.Delete(state); }
    }
}
