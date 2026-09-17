using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

public sealed class McpDispatcherTests
{
    private static McpDispatcher CreateDispatcher()
        => new("local-project-bridge", "0.3.0",
        [
            new GatewayTool(
                "echo",
                "回显文本",
                new JsonObject { ["type"] = "object" },
                (arguments, _) => Task.FromResult(new ToolOutcome(arguments["text"]?.GetValue<string>() ?? string.Empty).ToContent()))
        ]);

    [Fact]
    public async Task Initialize_ReturnsProtocolVersionAndServerInfo()
    {
        var dispatcher = CreateDispatcher();
        var response = await dispatcher.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.NotNull(response);
        using var json = JsonDocument.Parse(response!);
        Assert.Equal("2025-06-18", json.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("local-project-bridge", json.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Notification_ReturnsNoResponse()
    {
        var dispatcher = CreateDispatcher();
        var response = await dispatcher.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Null(response);
    }

    [Fact]
    public async Task ToolsList_DescribesAllTools()
    {
        var dispatcher = new McpDispatcher("local-project-bridge", "0.3.0",
        [
            new GatewayTool("read", "read", new JsonObject { ["type"] = "object" },
                (_, _) => Task.FromResult(new ToolOutcome("ok").ToContent()),
                GatewayToolAnnotations.ReadOnlyClosed),
            new GatewayTool("write", "write", new JsonObject { ["type"] = "object" },
                (_, _) => Task.FromResult(new ToolOutcome("ok").ToContent()),
                GatewayToolAnnotations.MutationClosed)
        ]);
        var response = await dispatcher.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        using var json = JsonDocument.Parse(response!);
        var tools = json.RootElement.GetProperty("result").GetProperty("tools");
        Assert.Equal(2, tools.GetArrayLength());
        var read = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "read").GetProperty("annotations");
        Assert.True(read.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(read.GetProperty("destructiveHint").GetBoolean());
        Assert.True(read.GetProperty("idempotentHint").GetBoolean());
        Assert.False(read.GetProperty("openWorldHint").GetBoolean());
        var write = tools.EnumerateArray().Single(tool => tool.GetProperty("name").GetString() == "write").GetProperty("annotations");
        Assert.False(write.GetProperty("readOnlyHint").GetBoolean());
        Assert.True(write.GetProperty("destructiveHint").GetBoolean());
    }

    [Fact]
    public async Task ToolCall_ReturnsTextContent()
    {
        var dispatcher = CreateDispatcher();
        var response = await dispatcher.HandleAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"你好"}}}""");
        using var json = JsonDocument.Parse(response!);
        var result = json.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Contains("你好", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task UnknownTool_ReturnsMethodNotFound()
    {
        var dispatcher = CreateDispatcher();
        var response = await dispatcher.HandleAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"nope","arguments":{}}}""");
        using var json = JsonDocument.Parse(response!);
        Assert.Equal(-32601, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task BrokenJson_ReturnsParseError()
    {
        var dispatcher = CreateDispatcher();
        var response = await dispatcher.HandleAsync("{not json");
        using var json = JsonDocument.Parse(response!);
        Assert.Equal(-32700, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task HandlerThrow_ReturnsInternalErrorWithGenericMessage()
    {
        var dispatcher = new McpDispatcher("t", "0", [
            new GatewayTool("boom", "", new JsonObject { ["type"] = "object" },
                (_, _) => throw new InvalidOperationException("secret token=abc"))
        ]);
        var response = await dispatcher.HandleAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"boom","arguments":{}}}""");
        using var json = JsonDocument.Parse(response!);
        var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("网关内部错误", message);
        Assert.DoesNotContain("abc", message); // 统一错误格式不外泄内部细节
    }
}

public sealed class GatewayVerificationTests
{
    [Fact]
    public async Task ProtectedResourceMetadata_Returns404ForNoOAuthPrivateMcpPath()
    {
        var root = Directory.CreateTempSubdirectory("lpb-metadata-").FullName;
        try
        {
            var dispatcher = new McpDispatcher("test", "1", []);
            await using var server = new GatewayServer(dispatcher, new RedactingLogger(root),
                requestPath: "/mcp/test-only", trustAllRequestsAsRemote: true);
            await server.StartAsync();
            var listenUri = new Uri(server.ListenUrl!);
            var metadataUri = new Uri(listenUri.GetLeftPart(UriPartial.Authority)
                                      + "/.well-known/oauth-protected-resource/mcp/test-only");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });

            using var response = await client.GetAsync(metadataUri);

            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReadinessDoesNotTreatInitializeAsVerifiedWebCall()
    {
        var root = Directory.CreateTempSubdirectory("lpb-verify-").FullName;
        try
        {
            var dispatcher = new McpDispatcher("test", "1", [
                new GatewayTool("ok", "", new JsonObject { ["type"] = "object" },
                    (_, _) => Task.FromResult(new ToolOutcome("ok").ToContent()))
            ]);
            await using var server = new GatewayServer(dispatcher, new RedactingLogger(root),
                requestPath: "/mcp/test-only", trustAllRequestsAsRemote: true);
            var calls = new List<VerifiedRemoteCallEventArgs>();
            server.VerifiedRemoteCall += (_, e) => calls.Add(e);
            await server.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var initialized = await client.PostAsync(server.ListenUrl,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", System.Text.Encoding.UTF8, "application/json"));
            initialized.EnsureSuccessStatusCode();
            Assert.Empty(calls);

            client.DefaultRequestHeaders.Add("X-ProjectBridge-Client", "untrusted-identity");
            using var called = await client.PostAsync(server.ListenUrl,
                new StringContent("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"ok\",\"arguments\":{}}}", System.Text.Encoding.UTF8, "application/json"));
            called.EnsureSuccessStatusCode();
            Assert.Single(calls);
            Assert.Equal("secure-tunnel-connection", calls[0].ClientIdentity);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

public sealed class GatewayToolCapabilityTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("lpb-gw").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_project, recursive: true); } catch (IOException) { }
    }

    private SessionPolicy Policy(CapabilityFlags flags) => new(
        Guid.NewGuid(), Guid.NewGuid(), "网关演示项目", Path.GetFullPath(_project), flags, DateTimeOffset.Now, null);

    [Fact]
    public void ReadTools_RequireWebReadCapability()
    {
        Assert.Throws<InvalidOperationException>(
            () => ProjectReadTools.Create(Policy(CapabilityFlags.WebDelegateCodex), new CommandRunner(new RedactingLogger(_project))));
    }

    [Fact]
    public void CodexTools_RequireDelegateCapability()
    {
        Assert.Throws<InvalidOperationException>(
            () => CodexTaskTools.Create(Policy(CapabilityFlags.WebRead), new FakeBridge()));
    }

    [Fact]
    public async Task CodexTools_MapToUpstreamSchemaAndInjectPolicy()
    {
        JsonObject? captured = null;
        string? capturedTool = null;
        var bridge = new FakeBridge { OnCall = (tool, arguments) => { capturedTool = tool; captured = arguments; } };
        var policy = Policy(CapabilityFlags.WebDelegateCodex);
        var tools = CodexTaskTools.Create(policy, bridge);
        var start = tools.Single(tool => tool.Name == "codex_task_start");
        await start.Invoke(new JsonObject { ["prompt"] = "审查代码" }, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("codex_task", capturedTool);
        Assert.Equal(policy.CanonicalProjectPath, captured!["cwd"]?.GetValue<string>());
        Assert.Equal("read-only", captured["sandbox"]?.GetValue<string>());
        Assert.True(Guid.TryParse(captured["requestId"]?.GetValue<string>(), out _));
        var prompt = captured["prompt"]?.GetValue<string>();
        Assert.Contains($"project_id: {policy.ProjectId:D}", prompt);
        Assert.Contains($"session_id: {policy.SessionId:D}", prompt);
        Assert.Contains("source: chatgpt-web", prompt);
        Assert.DoesNotContain("readonly", captured);
        Assert.DoesNotContain("network", captured);
    }

    [Fact]
    public async Task CodexStatusAndStop_MapSnakeCaseToUpstreamNames()
    {
        var calls = new List<(string Tool, JsonObject Arguments)>();
        var bridge = new FakeBridge { OnCall = (tool, arguments) => calls.Add((tool, (JsonObject)arguments.DeepClone())) };
        var tools = CodexTaskTools.Create(Policy(CapabilityFlags.WebDelegateCodex), bridge);

        await tools.Single(tool => tool.Name == "codex_task_status").Invoke(
            new JsonObject { ["job_id"] = "job-1", ["wait_for"] = "terminal", ["wait_ms"] = 500 }, CancellationToken.None);
        await tools.Single(tool => tool.Name == "codex_task_stop").Invoke(
            new JsonObject { ["job_id"] = "job-1", ["expected_version"] = 2 }, CancellationToken.None);

        Assert.Equal("codex_status", calls[0].Tool);
        Assert.Equal("job-1", calls[0].Arguments["jobId"]?.GetValue<string>());
        Assert.Equal("terminal", calls[0].Arguments["waitFor"]?.GetValue<string>());
        Assert.Equal("codex_cancel", calls[1].Tool);
        Assert.Equal(2, calls[1].Arguments["expectedVersion"]?.GetValue<int>());
    }

    [Fact]
    public void SsePayload_ExtractsJsonRpcData()
    {
        var payload = HttpCodexTaskBridge.ExtractJsonPayload(
            "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n",
            "text/event-stream");
        Assert.Contains("\"result\"", payload);
    }

    private sealed class FakeBridge : ICodexTaskBridge
    {
        public Action<string, JsonObject>? OnCall { get; init; }

        public Task<JsonObject?> CallToolAsync(string bridgeTool, JsonObject arguments, CancellationToken cancellationToken)
        {
            OnCall?.Invoke(bridgeTool, arguments);
            return Task.FromResult<JsonObject?>(new JsonObject { ["ok"] = true });
        }
    }
}
