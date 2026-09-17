using System.Text.Json;
using System.Text.Json.Nodes;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Security;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

/// <summary>网关只读工具的安全行为（设计 6.3 / 15 安全验收）。</summary>
public sealed class ProjectReadToolsTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("lpb-read").FullName;
    private readonly SessionPolicy _policy;
    private readonly IReadOnlyList<GatewayTool> _tools;

    public ProjectReadToolsTests()
    {
        _policy = new SessionPolicy(
            Guid.NewGuid(), Guid.NewGuid(), "读取演示", Path.GetFullPath(_project),
            CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb, DateTimeOffset.Now, null);
        _tools = ProjectReadTools.Create(_policy, new CommandRunner(new RedactingLogger(_project)));
    }

    public void Dispose() => TestTree.Delete(_project);

    private GatewayTool Tool(string name) => _tools.Single(tool => tool.Name == name);

    private static JsonObject Payload(JsonObject content)
    {
        var text = content["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<string> WriteAsync(string relative, string content)
    {
        var fullPath = Path.Combine(_project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        return relative;
    }

    [Fact]
    public async Task ProjectInfo_ReturnsNameAndCapabilities()
    {
        var result = Payload(await Tool("project_info").Invoke(new JsonObject(), CancellationToken.None));
        Assert.Equal("读取演示", result["name"]?.GetValue<string>());
        Assert.Contains("WebRead", result["capabilities"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListDirectory_HidesSensitiveEntries()
    {
        await WriteAsync(".env", "SECRET=1");
        await WriteAsync("src/main.rs", "fn main() {}");
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_project, ".git", "config"), "[remote]\n");

        var result = Payload(await Tool("list_directory").Invoke(new JsonObject { ["path"] = "" }, CancellationToken.None));
        var names = result["entries"]!.AsArray().Select(entry => entry!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("src", names);
        Assert.DoesNotContain(".env", names);
        Assert.DoesNotContain(".git", names);
    }

    [Fact]
    public async Task ReadTextFile_ReturnsContentAndRejectsSensitive()
    {
        var relative = await WriteAsync("docs/说明.md", "# 标题\n正文");
        var ok = Payload(await Tool("read_text_file").Invoke(new JsonObject { ["path"] = relative }, CancellationToken.None));
        Assert.Contains("正文", ok["content"]?.GetValue<string>());

        await WriteAsync(".env.production", "KEY=x");
        var denied = await Tool("read_text_file").Invoke(new JsonObject { ["path"] = ".env.production" }, CancellationToken.None);
        Assert.True(denied["isError"]?.GetValue<bool>());
        Assert.Contains("敏感文件", denied["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadTextFile_RejectsTraversalAndRootedPaths()
    {
        foreach (var path in new[] { "../outside.txt", "/etc/passwd", "a/../../b.txt" })
        {
            var result = await Tool("read_text_file").Invoke(new JsonObject { ["path"] = path }, CancellationToken.None);
            Assert.True(result["isError"]?.GetValue<bool>(), path);
        }
    }

    [Fact]
    public async Task ReadTextFile_RejectsJunctionEscape()
    {
        var outside = Directory.CreateTempSubdirectory("lpb-outside");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside.FullName, "secret.txt"), "top secret");
            var junction = Path.Combine(_project, "vendor-link");
            Assert.True(CreateJunction(junction, outside.FullName), "mklink /J 失败");

            var result = await Tool("read_text_file").Invoke(
                new JsonObject { ["path"] = "vendor-link/secret.txt" }, CancellationToken.None);
            Assert.True(result["isError"]?.GetValue<bool>(), "联接逃逸必须被拒绝");
        }
        finally { outside.Delete(recursive: true); }
    }

    [Fact]
    public async Task SearchFiles_FindsTextAndSkipsSensitive()
    {
        await WriteAsync("src/lib.rs", "let api_token = fetch();");
        await WriteAsync(".env", "api_token=leaked");
        var result = Payload(await Tool("search_files").Invoke(
            new JsonObject { ["query"] = "api_token" }, CancellationToken.None));
        var paths = result["matches"]!.AsArray().Select(match => match!["path"]!.GetValue<string>()).ToList();
        Assert.Contains("src/lib.rs", paths);
        Assert.DoesNotContain(".env", paths);
    }

    [Fact]
    public async Task GitTools_ReportStatusAndDiff()
    {
        RunGit(["init"]);
        RunGit(["-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"]);
        RunGit(["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "init", "--allow-empty"]);
        await WriteAsync("app.txt", "hello");
        RunGit(["add", "app.txt"]);
        await File.WriteAllTextAsync(Path.Combine(_project, "app.txt"), "hello changed");

        var status = Payload(await Tool("git_status").Invoke(new JsonObject(), CancellationToken.None));
        Assert.Contains("app.txt", status["output"]?.GetValue<string>());

        var diff = Payload(await Tool("git_diff").Invoke(new JsonObject { ["staged"] = true }, CancellationToken.None));
        Assert.Contains("hello", diff["output"]?.GetValue<string>());
    }

    [Fact]
    public async Task GitTools_HideTrackedSensitiveFileNamesAndDiffContent()
    {
        RunGit(["init"]);
        await WriteAsync(".env", "TOKEN=before");
        await WriteAsync("app.txt", "public before");
        CommitAll("init");
        await WriteAsync(".env", "TOKEN=super-secret-after");
        await WriteAsync("app.txt", "public after");

        var status = Payload(await Tool("git_status").Invoke(new JsonObject(), CancellationToken.None));
        var statusOutput = status["output"]?.GetValue<string>() ?? string.Empty;
        Assert.DoesNotContain(".env", statusOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("app.txt", statusOutput);

        var diff = Payload(await Tool("git_diff").Invoke(new JsonObject(), CancellationToken.None));
        var diffOutput = diff["output"]?.GetValue<string>() ?? string.Empty;
        Assert.DoesNotContain(".env", diffOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-after", diffOutput);
        Assert.Contains("public after", diffOutput);
    }

    [Fact]
    public async Task GitTools_HideDiffsMatchingC2cAndBridgeIgnoreRules()
    {
        RunGit(["init"]);
        await WriteAsync("c2c-private/note.txt", "c2c before");
        await WriteAsync("bridge-private/note.txt", "bridge before");
        await WriteAsync("app.txt", "public before");
        CommitAll("init");
        await WriteAsync("c2c-private/note.txt", "c2c secret after");
        await WriteAsync("bridge-private/note.txt", "bridge secret after");
        await WriteAsync("app.txt", "public after");
        await File.WriteAllLinesAsync(Path.Combine(_project, ".c2cignore"), ["c2c-private/"]);
        await File.WriteAllLinesAsync(Path.Combine(_project, ".bridgeignore"), ["bridge-private/"]);
        var tools = ProjectReadTools.Create(_policy, new CommandRunner(new RedactingLogger(_project)));

        var status = Payload(await tools.Single(tool => tool.Name == "git_status").Invoke(new JsonObject(), CancellationToken.None));
        var statusOutput = status["output"]?.GetValue<string>() ?? string.Empty;
        Assert.DoesNotContain("c2c-private", statusOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bridge-private", statusOutput, StringComparison.OrdinalIgnoreCase);

        var diff = Payload(await tools.Single(tool => tool.Name == "git_diff").Invoke(new JsonObject(), CancellationToken.None));
        var diffOutput = diff["output"]?.GetValue<string>() ?? string.Empty;
        Assert.DoesNotContain("c2c-private", diffOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bridge-private", diffOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("c2c secret after", diffOutput);
        Assert.DoesNotContain("bridge secret after", diffOutput);
        Assert.Contains("public after", diffOutput);
    }

    private void RunGit(string[] arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "git", arguments.Select(argument => argument.Contains(' ') ? $"\"{argument}\"" : argument))
        {
            WorkingDirectory = _project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit(15_000);
        Assert.Equal(0, process.ExitCode);
    }

    private void CommitAll(string message)
    {
        RunGit(["-c", "user.email=t@t", "-c", "user.name=t", "add", "-A"]);
        RunGit(["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", message]);
    }

    private static bool CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        });
        process!.WaitForExit(10_000);
        return process.ExitCode == 0;
    }
}

/// <summary>网关 HTTP 服务器端到端（TcpListener 实现）。</summary>
public sealed class GatewayServerTests : IAsyncDisposable
{
    private readonly string _appData = Directory.CreateTempSubdirectory("lpb-gwsrv").FullName;
    private readonly GatewayServer _server;

    public GatewayServerTests()
    {
        var dispatcher = new McpDispatcher("local-project-bridge", "0.3.0",
        [
            new GatewayTool("ping_tool", "ping", new JsonObject { ["type"] = "object" },
                (_, _) => Task.FromResult(new ToolOutcome("pong").ToContent()))
        ]);
        _server = new GatewayServer(dispatcher, new RedactingLogger(_appData));
    }

    [Fact]
    public async Task Post_Mcp_Request_RoundTripsOverHttp()
    {
        await _server.StartAsync();
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new System.Net.Http.StringContent(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"ping_tool","arguments":{}}}""",
            System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(_server.ListenUrl, content);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Contains("pong", json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task NonPost_Returns405()
    {
        await _server.StartAsync();
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync(_server.ListenUrl);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task ProtectedResourceMetadata_Returns404WhenOAuthIsNotSupported(string metadataPath)
    {
        await _server.StartAsync();
        var listenUri = new Uri(_server.ListenUrl!);
        var metadataUri = new Uri(listenUri.GetLeftPart(UriPartial.Authority) + metadataPath);
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await client.GetAsync(metadataUri);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        try { Directory.Delete(_appData, recursive: true); } catch (IOException) { }
    }
}
