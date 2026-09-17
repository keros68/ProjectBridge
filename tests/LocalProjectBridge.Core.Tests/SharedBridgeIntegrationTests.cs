using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalProjectBridge.Core.Adapters;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Sessions;
using LocalProjectBridge.Core.Collaboration;

namespace LocalProjectBridge.Core.Tests;

// Real upstream OAuth + real Node process + native gateway, entirely over loopback.
public sealed class SharedBridgeIntegrationTests
{
    [Fact]
    public async Task OneOAuthConnection_ReadsTwoProjectsAndHonorsLiveRevocation()
    {
        var root = Directory.CreateTempSubdirectory("lpb-shared-test-").FullName;
        var paths = new RuntimeDiscovery().Discover();
        var registry = new ProjectAuthorizationRegistry();
        var a = new ProjectRecord { Name = "A", Path = Directory.CreateDirectory(Path.Combine(root, "A")).FullName };
        var b = new ProjectRecord { Name = "B", Path = Directory.CreateDirectory(Path.Combine(root, "B")).FullName };
        await File.WriteAllTextAsync(Path.Combine(a.Path,"sample.txt"), "from A");
        await File.WriteAllTextAsync(Path.Combine(b.Path,"sample.txt"), "from B");
        registry.ReplaceProjects([a]);
        var logger = new RedactingLogger(root);
        var taskBridge = new TestTaskBridge();
        var connectionId = Guid.NewGuid();
        var leases = new WriteLeaseStore();
        var changes = new ChangeJournal(Path.Combine(root, "changes"));
        var writeService = new ProjectWriteService(registry, leases, changes);
        using var collaboration = new CollaborationStore(Path.Combine(root, "collaboration"));
        await using var adapter = new C2cAdapter(new CommandRunner(logger), stateDirectory:Path.Combine(root,"state"), runtimePaths:paths, sharedProjects:registry,
            taskBridgeFactory: (project, _) => { taskBridge.Project = project; return Task.FromResult(new CodexTaskBridgeLease(taskBridge, taskBridge)); },
            writeService: writeService, connectionId: connectionId, collaborationStore: collaboration);
        try
        {
            var liveTunnel = Environment.GetEnvironmentVariable("LPB_TEST_LIVE_TUNNEL") == "1";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(liveTunnel ? 130 : 35));
            var type = typeof(C2cAdapter);
            var policy = SessionPolicyFactory.Create(a, CapabilityFlags.WebRead) with { CanonicalProjectPath = adapter.ConnectionWorkspace };
            if (liveTunnel) await adapter.StartForSetupAsync(a, timeout.Token);
            else
            {
                type.GetField("_ownedWorkspace", BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(adapter, policy.CanonicalProjectPath);
                await (Task)type.GetMethod("StartManagedBridgeAsync", BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(adapter,[policy,timeout.Token])!;
            }
            var connectionUrl = adapter.ObservedMcpUrl;
            var port = (int)type.GetField("_port",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(adapter)!;
            var process = (System.Diagnostics.Process)type.GetField("_bridge",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(adapter)!;
            var pid = process.Id;
            using var client = new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout=TimeSpan.FromSeconds(8) };
            using var unauthorized = await client.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=1,method="tools/list"});
            Assert.Equal(HttpStatusCode.Unauthorized,unauthorized.StatusCode);
            var gateway = (GatewayServer)type.GetField("_sharedGateway",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(adapter)!;
            using var bypass = await client.PostAsync(gateway.ListenUrl,new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",Encoding.UTF8,"application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized,bypass.StatusCode);

            Assert.False(await adapter.HasAuthorizationAsync(a.Path, timeout.Token));
            var (token, clientId) = await PairAsync(client, adapter, a.Path);
            Assert.True(await adapter.HasAuthorizationAsync(a.Path, timeout.Token));
            // The authorization belongs to the shared bridge, not the selected project.
            Assert.True(await adapter.HasAuthorizationAsync(b.Path, timeout.Token));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token);
            async Task<JsonObject> Call(string name, object args)
            {
                using var response = await client.PostAsJsonAsync("/mcp",new {jsonrpc="2.0",id=2,method="tools/call",@params=new {name,arguments=args}});
                Assert.Equal(HttpStatusCode.OK,response.StatusCode);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsObject();
            }
            Assert.Contains("from A", (await Call("read_text_file",new {project_id=a.Id,path="sample.txt"}))["content"]![0]!["text"]!.GetValue<string>());
            Assert.True((await Call("read_text_file",new {project_id=b.Id,path="sample.txt"}))["isError"]!.GetValue<bool>());
            registry.ReplaceProjects([a,b]);
            b.AllowCodexAskWeb = true;
            var request = collaboration.Submit(b, connectionId, "Compare two options.", "integration");
            Assert.Contains("Compare two options.", (await Call("get_collaboration_request",
                new { request_id = request.RequestId, access_code = request.AccessCode })).ToJsonString());
            Assert.False((await Call("reply_to_collaboration_request",
                new { request_id = request.RequestId, access_code = request.AccessCode, turn_id = request.TurnId, reply = "Use option A." }))["isError"]!.GetValue<bool>());
            Assert.Equal("Use option A.", collaboration.Get(request.RequestId)!.Reply);
            Assert.Contains(b.Id.ToString(),(await Call("list_projects",new {}))["content"]![0]!["text"]!.GetValue<string>());
            Assert.Contains("from B",(await Call("read_text_file",new {project_id=b.Id,path="sample.txt"}))["content"]![0]!["text"]!.GetValue<string>());
            var preparedWrite = Payload(await Call("prepare_change", new
            {
                project_id = b.Id,
                request_id = Guid.NewGuid(),
                operations = new[] { new { type = "patch", path = "sample.txt", old_text = "from B", new_text = "edited B" } }
            }));
            var changeId = preparedWrite["change_id"]!.GetValue<string>();
            Assert.Equal("Prepared", preparedWrite["status"]!.GetValue<string>());
            var deniedWrite = Payload(await Call("apply_change", new
            {
                project_id = b.Id, change_id = changeId, request_id = Guid.NewGuid()
            }));
            Assert.Equal("local_confirmation_required", deniedWrite["error"]!["code"]!.GetValue<string>());
            Assert.Equal("from B", await File.ReadAllTextAsync(Path.Combine(b.Path, "sample.txt")));
            Assert.Equal(ChangeStatus.Applied, (await writeService.ApplyLocallyAsync(Guid.Parse(changeId))).Status);
            Assert.Equal("edited B", await File.ReadAllTextAsync(Path.Combine(b.Path, "sample.txt")));
            async Task CheckAutoApply(Func<string, object, Task<JsonObject>> invoke)
            {
                writeService.GrantAutoApply(b.Id, connectionId, adapter.Readiness.ClientIdentity!, TimeSpan.FromMinutes(15));
                var list = Payload(await invoke("list_projects", new { }))["projects"]!.AsArray();
                Assert.True(list.Single(item => item!["project_id"]!.GetValue<string>() == a.Id.ToString())!["changes_require_local_confirmation"]!.GetValue<bool>());
                Assert.False(list.Single(item => item!["project_id"]!.GetValue<string>() == b.Id.ToString())!["changes_require_local_confirmation"]!.GetValue<bool>());
                var preview = Payload(await invoke("prepare_change", new
                {
                    project_id = b.Id, request_id = Guid.NewGuid(),
                    operations = new[] { new { type = "create", path = "auto-write.txt", content = "automatic" } }
                }));
                var autoId = preview["change_id"]!.GetValue<string>();
                Assert.False(File.Exists(Path.Combine(b.Path, "auto-write.txt")));
                var submission = new { project_id = b.Id, change_id = autoId, request_id = Guid.NewGuid() };
                Assert.Equal("Applied", Payload(await invoke("apply_change", submission))["status"]!.GetValue<string>());
                Assert.Equal("automatic", await File.ReadAllTextAsync(Path.Combine(b.Path, "auto-write.txt")));
                Assert.Equal("Applied", Payload(await invoke("apply_change", submission))["status"]!.GetValue<string>());
                Assert.Equal("Restored", Payload(await invoke("restore_change", new
                {
                    project_id = b.Id, change_id = autoId, request_id = Guid.NewGuid()
                }))["status"]!.GetValue<string>());
                Assert.False(File.Exists(Path.Combine(b.Path, "auto-write.txt")));
                writeService.RevokeAutoApply(b.Id);
                var deniedPreview = Payload(await invoke("prepare_change", new
                {
                    project_id = b.Id, request_id = Guid.NewGuid(),
                    operations = new[] { new { type = "create", path = "revoked-write.txt", content = "no" } }
                }));
                Assert.Equal("local_confirmation_required", Payload(await invoke("apply_change", new
                {
                    project_id = b.Id, change_id = deniedPreview["change_id"]!.GetValue<string>(), request_id = Guid.NewGuid()
                }))["error"]!["code"]!.GetValue<string>());
                Assert.False(File.Exists(Path.Combine(b.Path, "revoked-write.txt")));
            }
            if (liveTunnel)
            {
                using var remoteWrite = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                remoteWrite.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                async Task<JsonObject> RemoteCall(string name, object args)
                {
                    using var response = await remoteWrite.PostAsJsonAsync(connectionUrl,
                        new { jsonrpc = "2.0", id = 5, method = "tools/call", @params = new { name, arguments = args } });
                    response.EnsureSuccessStatusCode();
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsObject();
                }
                var publicRequest = collaboration.Submit(b, connectionId, "Public round trip.", "integration");
                Assert.Contains("Public round trip.", (await RemoteCall("get_collaboration_request",
                    new { request_id = publicRequest.RequestId, access_code = publicRequest.AccessCode })).ToJsonString());
                Assert.False((await RemoteCall("reply_to_collaboration_request",
                    new { request_id = publicRequest.RequestId, access_code = publicRequest.AccessCode, turn_id = publicRequest.TurnId, reply = "Public reply." }))["isError"]!.GetValue<bool>());
                Assert.Equal("Public reply.", collaboration.Get(publicRequest.RequestId)!.Reply);
                var publicPrepared = Payload(await RemoteCall("prepare_change", new
                {
                    project_id = b.Id, request_id = Guid.NewGuid(),
                    operations = new[] { new { type = "create", path = "public-write.txt", content = "through public tunnel" } }
                }));
                var publicDenied = Payload(await RemoteCall("apply_change", new
                {
                    project_id = b.Id,
                    change_id = publicPrepared["change_id"]!.GetValue<string>(),
                    request_id = Guid.NewGuid()
                }));
                Assert.Equal("local_confirmation_required", publicDenied["error"]!["code"]!.GetValue<string>());
                Assert.False(File.Exists(Path.Combine(b.Path, "public-write.txt")));
                Assert.Equal(ChangeStatus.Applied, (await writeService.ApplyLocallyAsync(
                    Guid.Parse(publicPrepared["change_id"]!.GetValue<string>()))).Status);
                Assert.Equal("through public tunnel", await File.ReadAllTextAsync(Path.Combine(b.Path, "public-write.txt")));
                await CheckAutoApply(RemoteCall);
            }
            else await CheckAutoApply(Call);
            var nextProposal = Payload(await Call("prepare_change", new
            {
                project_id = b.Id,
                request_id = Guid.NewGuid(),
                operations = new[] { new { type = "create", path = "proposal.txt", content = "proposal" } }
            }));
            Assert.Equal("Prepared", nextProposal["status"]!.GetValue<string>());
            Assert.False(File.Exists(Path.Combine(b.Path, "proposal.txt")));
            Assert.True((await Call("codex_task_start",new {project_id=b.Id,prompt="test"}))["isError"]!.GetValue<bool>());
            b.AllowCodexTasks = true;
            registry.ReplaceProjects([a,b]);
            Assert.False((await Call("codex_task_start",new {project_id=b.Id,prompt="test"}))["isError"]!.GetValue<bool>());
            Assert.Equal(b.Id, taskBridge.Project!.Id);
            Assert.Equal("read-only", taskBridge.Arguments!["sandbox"]!.GetValue<string>());
            Assert.Equal(b.Path, taskBridge.Arguments["cwd"]!.GetValue<string>());
            b.AllowCodexTasks = false;
            registry.ReplaceProjects([a,b]);
            await adapter.RefreshSharedPermissionsAsync();
            Assert.True(taskBridge.Disposed);
            Assert.True((await Call("codex_task_start",new {project_id=b.Id,prompt="test"}))["isError"]!.GetValue<bool>());
            Assert.True((await Call("read_text_file",new {path="sample.txt"}))["isError"]!.GetValue<bool>());
            Assert.True((await Call("read_text_file",new {project_id=a.Id,path="../B/sample.txt"}))["isError"]!.GetValue<bool>());
            a.AllowWebRead=false;
            registry.ReplaceProjects([a,b]);
            Assert.True((await Call("read_text_file",new {project_id=a.Id,path="sample.txt"}))["isError"]!.GetValue<bool>());
            Assert.Contains("edited B",(await Call("read_text_file",new {project_id=b.Id,path="sample.txt"}))["content"]![0]!["text"]!.GetValue<string>());
            Assert.Equal(pid,process.Id);
            Assert.False(process.HasExited);
            Assert.Equal(connectionUrl, adapter.ObservedMcpUrl);
            if (liveTunnel)
            {
                using var remote = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var denied = await remote.PostAsJsonAsync(connectionUrl,new {jsonrpc="2.0",id=3,method="tools/list"});
                Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
                remote.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",token);
                using var read = await remote.PostAsJsonAsync(connectionUrl,new {jsonrpc="2.0",id=4,method="tools/call",@params=new {name="read_text_file",arguments=new {project_id=b.Id,path="sample.txt"}}});
                read.EnsureSuccessStatusCode();
                Assert.Contains("edited B", await read.Content.ReadAsStringAsync());
            }
            var writeClient = adapter.Readiness.ClientIdentity!;
            writeService.GrantAutoApply(b.Id, connectionId, writeClient, TimeSpan.FromMinutes(15));
            await adapter.StopAsync(policy);
            Assert.Null(writeService.GetAutoApplyLease(b.Id, connectionId, writeClient));
            using var closed = new System.Net.Sockets.TcpClient();
            await Assert.ThrowsAnyAsync<System.Net.Sockets.SocketException>(() => closed.ConnectAsync("127.0.0.1",port));
        }
        finally
        {
            await adapter.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static JsonObject Payload(JsonObject result)
        => JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

    private static async Task<(string Token, string ClientId)> PairAsync(HttpClient client,C2cAdapter adapter,string workspace)
    {
        const string redirect="http://localhost/callback";
        using var registration = await client.PostAsJsonAsync("/oauth/register",new {client_name="ProjectBridge integration test",redirect_uris=new[]{redirect}});
        registration.EnsureSuccessStatusCode();
        var clientId=JsonNode.Parse(await registration.Content.ReadAsStringAsync())!["client_id"]!.GetValue<string>();
        var verifier=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');
        var page=await client.GetStringAsync($"/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirect)}&scope=workspace.read&code_challenge_method=S256&code_challenge={challenge}");
        var requestId=Regex.Match(page,"name=\"request_id\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(requestId);
        var pairing=await adapter.CreatePairingCodeAsync(workspace);
        using var authorized=await client.PostAsync("/oauth/authorize",new FormUrlEncodedContent(new Dictionary<string,string>{{"request_id",requestId},{"pairing_code",pairing}}));
        Assert.Equal(HttpStatusCode.Redirect,authorized.StatusCode);
        var code=System.Web.HttpUtility.ParseQueryString(authorized.Headers.Location!.Query)["code"]!;
        using var exchange=await client.PostAsync("/oauth/token",new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","authorization_code"},{"client_id",clientId},{"redirect_uri",redirect},{"code",code},{"code_verifier",verifier}}));
        exchange.EnsureSuccessStatusCode();
        return (JsonNode.Parse(await exchange.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>(), clientId);
    }

    private sealed class TestTaskBridge : ICodexTaskBridge, IAsyncDisposable
    {
        public ProjectRecord? Project;
        public JsonObject? Arguments;
        public bool Disposed;
        public Task<JsonObject?> CallToolAsync(string tool, JsonObject arguments, CancellationToken cancellationToken)
        {
            Arguments = (JsonObject)arguments.DeepClone();
            return Task.FromResult<JsonObject?>(new ToolOutcome("{\"jobId\":\"integration-job\"}").ToContent());
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
