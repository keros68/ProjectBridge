using System.Text.Json.Nodes;
using System.Diagnostics;
using ProjectBridge.CodexShim;

namespace LocalProjectBridge.Core.Tests;

public sealed class CodexShimProtocolTests
{
    private const string Root = "D:\\work\\isolated-project";

    [Theory]
    [InlineData("turn/start")]
    [InlineData("command/exec")]
    public void WritablePolicyCannotExpandBeyondProject(string method)
    {
        var input = new JsonObject { ["id"] = 1, ["method"] = method, ["params"] = new JsonObject {
            ["cwd"] = "D:\\outside", ["sandboxPolicy"] = new JsonObject { ["type"] = "dangerFullAccess", ["networkAccess"] = true },
            ["env"] = new JsonObject { ["EVIL"] = "1" }, ["approvalPolicy"] = "on-request"
        }};
        var parameters = Parse(AppServerProtocolGuard.RewriteClientLine(input.ToJsonString(), Root, true))["params"]!;
        var policy = parameters["sandboxPolicy"]!;
        Assert.Equal("workspaceWrite", policy["type"]!.GetValue<string>());
        Assert.Equal(Root, policy["writableRoots"]![0]!.GetValue<string>());
        Assert.Single(policy["writableRoots"]!.AsArray());
        Assert.False(policy["networkAccess"]!.GetValue<bool>());
        Assert.True(policy["excludeTmpdirEnvVar"]!.GetValue<bool>());
        Assert.True(policy["excludeSlashTmp"]!.GetValue<bool>());
        Assert.Equal(Root, parameters["cwd"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("thread/start")]
    [InlineData("thread/resume")]
    [InlineData("thread/fork")]
    public void ThreadPoliciesAreBoundToLocalWriteGrant(string method)
    {
        var line = new JsonObject { ["method"] = method, ["params"] = new JsonObject { ["sandbox"] = "danger-full-access", ["permissions"] = ":danger-full-access", ["config"] = new JsonObject() } }.ToJsonString();
        foreach (var write in new[] { false, true })
        {
            var parameters = Parse(AppServerProtocolGuard.RewriteClientLine(line, Root, write))["params"]!;
            Assert.Equal(write ? "workspace-write" : "read-only", parameters["sandbox"]!.GetValue<string>());
            Assert.Equal("never", parameters["approvalPolicy"]!.GetValue<string>());
            Assert.Null(parameters["config"]);
            Assert.Null(parameters["permissions"]);
        }
    }

    [Fact]
    public void ThreadStartCannotOverrideRootSandboxOrApproval()
    {
        var rewritten = Parse(AppServerProtocolGuard.RewriteClientLine(
            """{"jsonrpc":"2.0","id":1,"method":"thread/start","params":{"cwd":"D:\\other","sandbox":"danger-full-access","approvalPolicy":"on-request","config":{"sandbox_permissions":["disk-full-read-access"]}}}""", Root));
        var parameters = rewritten["params"]!.AsObject();
        Assert.Equal(Root, parameters["cwd"]!.GetValue<string>());
        Assert.Equal("read-only", parameters["sandbox"]!.GetValue<string>());
        Assert.Equal("never", parameters["approvalPolicy"]!.GetValue<string>());
        Assert.Null(parameters["config"]);
    }

    [Fact]
    public void TurnAndCommandGetRestrictedReadOnlyNoNetworkPolicy()
    {
        foreach (var method in new[] { "turn/start", "command/exec" })
        {
            var input = """{"jsonrpc":"2.0","id":1,"method":"METHOD","params":{"cwd":"D:\\other","sandboxPolicy":{"type":"dangerFullAccess","networkAccess":true},"env":{"X":"1"}}}"""
                .Replace("METHOD", method, StringComparison.Ordinal);
            var rewritten = Parse(AppServerProtocolGuard.RewriteClientLine(input, Root));
            var parameters = rewritten["params"]!.AsObject();
            Assert.Equal(Root, parameters["cwd"]!.GetValue<string>());
            var sandbox = parameters["sandboxPolicy"]!.AsObject();
            Assert.Equal("readOnly", sandbox["type"]!.GetValue<string>());
            Assert.False(sandbox["networkAccess"]!.GetValue<bool>());
            if (method == "turn/start") Assert.Equal("never", parameters["approvalPolicy"]!.GetValue<string>());
            else Assert.Null(parameters["env"]);
        }
    }

    [Fact]
    public void OtherOrMalformedMessagesPassThrough()
    {
        const string other = """{"jsonrpc":"2.0","method":"thread/read","params":{"threadId":"x"}}""";
        Assert.Equal(other, AppServerProtocolGuard.RewriteClientLine(other, Root));
        Assert.Equal("not json", AppServerProtocolGuard.RewriteClientLine("not json", Root));
    }

    [Fact]
    public async Task ShimForwardsARewrittenAppServerRequestToChildProcess()
    {
        var directory = Directory.CreateTempSubdirectory("lpb-shim-child").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
            var fakeCli = Path.Combine(directory, "echo-json.cjs");
            await File.WriteAllTextAsync(fakeCli, """
                if (process.argv.includes('list')) { console.log('[{"name":"test-mcp"}]'); process.exit(0); }
                if (!process.argv.includes('mcp_servers.test-mcp.enabled=false')) process.exit(6);
                let buffered = '';
                process.stdin.setEncoding('utf8');
                process.stdin.on('data', chunk => {
                  buffered += chunk;
                  let newline;
                  while ((newline = buffered.indexOf('\n')) >= 0) {
                    const line = buffered.slice(0, newline).replace(/\r$/, '');
                    buffered = buffered.slice(newline + 1);
                    if (line) { JSON.parse(line); process.stdout.write(line + '\n'); }
                  }
                });
                """);
            var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (!File.Exists(node)) node = Environment.GetEnvironmentVariable("LPB_NODE")!;
            Assert.True(File.Exists(node));
            var shim = Path.Combine(AppContext.BaseDirectory, "ProjectBridge.CodexShim.exe");
            Assert.True(File.Exists(shim));
            var start = new ProcessStartInfo(shim)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("--stdio");
            start.Environment["LPB_NODE"] = node;
            start.Environment["LPB_CODEX_SCRIPT"] = fakeCli;
            start.Environment["LPB_PROJECT_ROOT"] = root;
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":7,"method":"turn/start","params":{"cwd":"D:\\other","approvalPolicy":"on-request","sandboxPolicy":{"type":"dangerFullAccess"}}}""");
            await process.StandardInput.FlushAsync();
            var output = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var rewritten = Parse(output!);
            Assert.Equal(root, rewritten["params"]!["cwd"]!.GetValue<string>());
            Assert.Equal("never", rewritten["params"]!["approvalPolicy"]!.GetValue<string>());
            Assert.Equal("readOnly", rewritten["params"]!["sandboxPolicy"]!["type"]!.GetValue<string>());
            Assert.False(rewritten["params"]!["sandboxPolicy"]!["networkAccess"]!.GetValue<bool>());
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, process.ExitCode);
        }
        finally { TestTree.Delete(directory); }
    }

    [Fact]
    public async Task RealAppServer_InitializesWithoutBomAndDisablesInheritedMcpAndWebSearch()
    {
        var root=Directory.CreateTempSubdirectory("lpb-codex-handshake-").FullName;
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"ProjectBridge.CodexShim.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new System.Text.UTF8Encoding(false), StandardOutputEncoding=System.Text.Encoding.UTF8, StandardErrorEncoding=System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        start.Environment["LPB_PROJECT_ROOT"]=root;
        using var process=Process.Start(start)!;
        var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            async Task<JsonObject> Request(int id,string method,JsonObject parameters)
            {
                await process.StandardInput.WriteLineAsync(new JsonObject { ["id"]=id,["method"]=method,["params"]=parameters }.ToJsonString());
                await process.StandardInput.FlushAsync();
                while(true)
                {
                    var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
                    Assert.NotNull(line);
                    var response=JsonNode.Parse(line!)!.AsObject();
                    if(response["id"]?.ToString()==id.ToString()) return response;
                }
            }
            var initialized=await Request(1,"initialize",new JsonObject { ["clientInfo"]=new JsonObject { ["name"]="projectbridge-regression",["version"]="1" } });
            Assert.NotNull(initialized["result"]);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            var reply=await Request(2,"config/read",new JsonObject { ["includeLayers"]=false });
            Assert.Null(reply["error"]);
            var config=reply["result"]!["config"]!;
            Assert.Equal("disabled",config["web_search"]!.GetValue<string>());
            Assert.Empty(config["sandbox_workspace_write"]!["writable_roots"]!.AsArray());
            if(config["mcp_servers"] is JsonObject servers)
                Assert.All(servers,server=>Assert.False(server.Value!["enabled"]!.GetValue<bool>()));
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch(TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            await stderr;
            Directory.Delete(root,true);
        }
    }
    private static JsonObject Parse(string line) => JsonNode.Parse(line)!.AsObject();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealAppServer_EnforcesWriteGrantAndRejectsOutsideWrite(bool allowWrite)
    {
        var temp = Directory.CreateTempSubdirectory("lpb-task-write-").FullName;
        var root = Directory.CreateDirectory(Path.Combine(temp, "project")).FullName;
        var outside = Path.Combine(temp, "outside.txt");
        var inside = Path.Combine(root, "result.txt");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ProjectBridge.CodexShim.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add("app-server");
        start.Environment["LPB_PROJECT_ROOT"] = root;
        start.Environment["LPB_CODEX_ALLOW_WRITE"] = allowWrite ? "1" : "0";
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            async Task<JsonObject> Request(int id, string method, JsonObject parameters)
            {
                await process.StandardInput.WriteLineAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString());
                await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                    Assert.NotNull(line);
                    var reply = Parse(line);
                    if (reply["id"]?.ToString() == id.ToString()) return reply;
                }
            }
            Assert.NotNull((await Request(1, "initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "write-regression", ["version"] = "1" } }))["result"]);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            async Task<JsonObject> Write(int id, string path) => await Request(id, "command/exec", new JsonObject {
                ["command"] = new JsonArray("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Set-Content -LiteralPath '" + path.Replace("'", "''") + "' -Value 'test' -NoNewline"),
                ["cwd"] = temp,
                ["sandboxPolicy"] = new JsonObject { ["type"] = "dangerFullAccess" }, ["timeoutMs"] = 10000
            });
            var thread = await Request(4, "thread/start", new JsonObject { ["cwd"] = temp, ["sandbox"] = "danger-full-access", ["ephemeral"] = true });
            Assert.True(thread["error"] is null, thread.ToJsonString());
            var result = await Write(2, inside);
            Assert.True(File.Exists(inside) == allowWrite, result.ToJsonString());
            if (allowWrite) Assert.Equal("test", await File.ReadAllTextAsync(inside));
            await Write(3, outside);
            Assert.False(File.Exists(outside));
        }
        finally
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            await stderr;
            TestTree.Delete(temp);
        }
    }
}

