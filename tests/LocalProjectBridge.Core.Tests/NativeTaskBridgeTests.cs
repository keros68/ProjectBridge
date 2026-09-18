using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Adapters;

namespace LocalProjectBridge.Core.Tests;

public sealed class NativeTaskBridgeTests
{
    [Theory]
    [InlineData("complete", "completed")]
    [InlineData("cancel", "interrupted")]
    [InlineData("crash", "failed")]
    [InlineData("tool-failure", "completed")]
    public async Task NativeBridgeReturnsResultStopsAndHandlesProcessFailure(string scenario, string expectedStatus)
    {
        var root = Directory.CreateTempSubdirectory("lpb-native-task-").FullName;
        try
        {
            var script = Path.Combine(root, "fake-codex.cjs");
            await File.WriteAllTextAsync(script, FakeServer);
            var project = new ProjectRecord { Name = "native", Path = root, AllowCodexTasks = true, AllowCodexWrite = true };
            var paths = new RuntimeDiscovery().Discover() with { CodexScript = script };
            var lease = await NativeCodexTaskBridge.StartAsync(project, new RuntimeDiscovery(), root, CancellationToken.None, paths);
            try
            {
                var start = Payload((await lease.Bridge.CallToolAsync("codex_task", new JsonObject { ["requestId"] = "r1", ["prompt"] = scenario }, CancellationToken.None))!);
                var job = start["jobId"]!.GetValue<string>();
                var args = new JsonObject { ["jobId"] = job, ["waitFor"] = "terminal", ["waitMs"] = 10000 };
                var result = Payload((await lease.Bridge.CallToolAsync(scenario == "cancel" ? "codex_cancel" : "codex_status", args, CancellationToken.None))!);
                Assert.Equal(expectedStatus, result["status"]!.GetValue<string>());
                if (scenario == "complete") Assert.Equal("checked result", result["result"]!.GetValue<string>());
                var received = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "received.json")))!;
                Assert.Equal(root, received["cwd"]!.GetValue<string>());
                Assert.Equal("never", received["approvalPolicy"]!.GetValue<string>());
                Assert.Equal("workspaceWrite", received["sandboxPolicy"]!["type"]!.GetValue<string>());
                Assert.False(received["sandboxPolicy"]!["networkAccess"]!.GetValue<bool>());
                Assert.Equal(root, received["sandboxPolicy"]!["writableRoots"]![0]!.GetValue<string>());
                var thread = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "thread.json")))!;
                Assert.False(thread["ephemeral"]!.GetValue<bool>());
                Assert.True(thread["persistExtendedHistory"]!.GetValue<bool>());
                Assert.StartsWith("|from_chatgpt|:\n", received["input"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("codex://threads/thread-native", result["threadUrl"]!.GetValue<string>());
                Assert.True(result["writeAccessVerified"]!.GetValue<bool>());
                Assert.Equal(scenario == "tool-failure" ? 2 : 0, result["toolFailures"]!.AsArray().Count);
            }
            finally { await Task.WhenAll(lease.Owner.DisposeAsync().AsTask(), lease.Owner.DisposeAsync().AsTask()); }
        }
        finally { TestTree.Delete(root); }
    }

    [Fact]
    public async Task NativeBridgeRejectsMissingLocalWriteGrantBeforeStarting()
    {
        var project = new ProjectRecord { Name = "no grant", Path = "D:\\unused", AllowCodexTasks = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeCodexTaskBridge.StartAsync(project, new RuntimeDiscovery(), "D:\\unused", CancellationToken.None));
    }

    private static JsonObject Payload(JsonObject value) => JsonNode.Parse(value["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    [Fact]
    public async Task DeniedRealWriteProbeDoesNotStartModelOrClaimTaskAccepted()
    {
        var root = Directory.CreateTempSubdirectory("lpb-native-denied-").FullName;
        try
        {
            var script = Path.Combine(root, "fake-codex.cjs");
            await File.WriteAllTextAsync(script, FakeServer.Replace("exitCode:0,stdout:'projectbridge-write-probe-ok'", "exitCode:1,stdout:'',stderr:'Access denied'", StringComparison.Ordinal));
            var paths = new RuntimeDiscovery().Discover() with { CodexScript = script };
            var project = new ProjectRecord { Name = "denied", Path = root, AllowCodexTasks = true, AllowCodexWrite = true };
            var lease = await NativeCodexTaskBridge.StartAsync(project, new RuntimeDiscovery(), root, CancellationToken.None, paths);
            try
            {
                var error = await Assert.ThrowsAsync<LocalProjectBridge.Core.Gateway.CodexTaskBridgeException>(() =>
                    lease.Bridge.CallToolAsync("codex_task", new JsonObject { ["prompt"] = "complete" }, CancellationToken.None));
                Assert.Contains("workspace_write_unavailable", error.Message);
                Assert.Contains("Access denied", error.Message);
                Assert.False(File.Exists(Path.Combine(root, "thread.json")));
            }
            finally { await lease.Owner.DisposeAsync(); }
        }
        finally { TestTree.Delete(root); }
    }

    private const string FakeServer = """
        const fs=require('fs'),path=require('path'),rl=require('readline').createInterface({input:process.stdin});
        if(process.argv.includes('list')) { console.log('[]'); process.exit(0); }
        const send=x=>console.log(JSON.stringify(x));
        const done=status=>send({method:'turn/completed',params:{threadId:'thread-native',turn:{id:'turn-native',status}}});
        rl.on('line',line=>{
          const q=JSON.parse(line); if(!q.method)return;
          if(q.method==='initialize')send({id:q.id,result:{}});
          if(q.method==='command/exec')send({id:q.id,result:{exitCode:0,stdout:'projectbridge-write-probe-ok'}});
          if(q.method==='thread/start'){
            fs.writeFileSync(path.join(process.env.LPB_PROJECT_ROOT,'thread.json'),JSON.stringify(q.params));
            send({id:q.id,result:{thread:{id:'thread-native'}}});
          }
          if(q.method==='thread/name/set')send({id:q.id,result:{}});
          if(q.method==='turn/start'){
            fs.writeFileSync(path.join(process.env.LPB_PROJECT_ROOT,'received.json'),JSON.stringify(q.params));
            send({id:q.id,result:{turn:{id:'turn-native'}}});
            const scenario=q.params.input[0].text.trim().split('\n').at(-1);
            if(scenario==='crash')setTimeout(()=>process.exit(17),30);
            if(scenario==='tool-failure')setTimeout(()=>{
              send({method:'item/completed',params:{threadId:'thread-native',item:{type:'commandExecution',exitCode:1,aggregatedOutput:'Access denied'}}});
              send({method:'item/completed',params:{threadId:'thread-native',item:{type:'fileChange',status:'failed'}}});
              done('completed');
            },30);
            if(scenario==='complete')setTimeout(()=>{
              send({method:'item/completed',params:{threadId:'thread-native',item:{type:'agentMessage',text:'checked result'}}}); done('completed');
            },30);
          }
          if(q.method==='turn/interrupt'){send({id:q.id,result:{}});done('interrupted');}
        });
        rl.on('close',()=>process.exit(0));
        """;
}
