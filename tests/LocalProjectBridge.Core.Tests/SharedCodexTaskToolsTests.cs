using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Gateway;

namespace LocalProjectBridge.Core.Tests;

public sealed class SharedCodexTaskToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-shared-tasks").FullName;
    private readonly ProjectRecord _a;
    private readonly ProjectRecord _b;
    private readonly ProjectAuthorizationRegistry _authorizations = new();

    public SharedCodexTaskToolsTests()
    {
        _a = Project("A");
        _b = Project("B");
        _authorizations.ReplaceProjects([_a, _b]);
    }

    public void Dispose() => TestTree.Delete(_root);

    [Fact]
    public async Task TaskRoutingUsesProjectScopedBridgeAndOpaqueRouteId()
    {
        var runtime = new FakeRuntime();
        var tasks = new SharedCodexTaskTools(_authorizations, runtime).Create();
        var start = await Tool(tasks, "codex_task_start").Invoke(new JsonObject
        {
            ["project_id"] = _a.Id.ToString("D"), ["prompt"] = "检查 A"
        }, CancellationToken.None);

        var routeTaskId = RouteTaskId(start);
        var aBridge = runtime.Bridge(_a.Id);
        Assert.Equal("codex_task", aBridge.Calls.Single().Tool);
        Assert.Equal(Path.GetFullPath(_a.Path), aBridge.Calls.Single().Arguments["cwd"]!.GetValue<string>());
        Assert.Equal("read-only", aBridge.Calls.Single().Arguments["sandbox"]!.GetValue<string>());
        Assert.Equal("background", aBridge.Calls.Single().Arguments["executionMode"]!.GetValue<string>());
        Assert.Contains($"project_id: {_a.Id:D}", aBridge.Calls.Single().Arguments["prompt"]!.GetValue<string>());
        Assert.StartsWith("|from_chatgpt|:\n", aBridge.Calls.Single().Arguments["prompt"]!.GetValue<string>());

        var status = await Tool(tasks, "codex_task_status").Invoke(new JsonObject
        {
            ["project_id"] = _a.Id.ToString("D"), ["task_id"] = routeTaskId.ToString("D")
        }, CancellationToken.None);
        Assert.False(status["isError"]!.GetValue<bool>());
        Assert.Equal("codex_status", aBridge.Calls.Last().Tool);
        Assert.Equal("job-A", aBridge.Calls.Last().Arguments["jobId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATaskCannotBeReadOrStoppedThroughAnotherProject()
    {
        var runtime = new FakeRuntime();
        var tasks = new SharedCodexTaskTools(_authorizations, runtime).Create();
        var start = await Tool(tasks, "codex_task_start").Invoke(new JsonObject
        {
            ["project_id"] = _a.Id.ToString("D"), ["prompt"] = "检查 A"
        }, CancellationToken.None);
        var routeTaskId = RouteTaskId(start);

        var rejected = await Tool(tasks, "codex_task_stop").Invoke(new JsonObject
        {
            ["project_id"] = _b.Id.ToString("D"), ["task_id"] = routeTaskId.ToString("D")
        }, CancellationToken.None);
        Assert.True(rejected["isError"]!.GetValue<bool>());
        Assert.DoesNotContain(runtime.Bridge(_b.Id).Calls, call => call.Tool == "codex_cancel");
        Assert.DoesNotContain(runtime.Bridge(_a.Id).Calls, call => call.Tool == "codex_cancel");
    }

    [Fact]
    public async Task RevocationRejectsFollowUpsAndReclaimsProjectLease()
    {
        var runtime = new FakeRuntime();
        var surface = new SharedCodexTaskTools(_authorizations, runtime);
        var tasks = surface.Create();
        var start = await Tool(tasks, "codex_task_start").Invoke(new JsonObject
        {
            ["project_id"] = _a.Id.ToString("D"), ["prompt"] = "检查 A"
        }, CancellationToken.None);
        var routeTaskId = RouteTaskId(start);
        var owner = runtime.Owner(_a.Id);

        _a.AllowCodexTasks = false;
        _authorizations.ReplaceProjects([_a, _b]);
        await surface.ReconcileAsync([_a, _b]);
        Assert.True(owner.Disposed);

        var rejected = await Tool(tasks, "codex_task_status").Invoke(new JsonObject
        {
            ["project_id"] = _a.Id.ToString("D"), ["task_id"] = routeTaskId.ToString("D")
        }, CancellationToken.None);
        Assert.True(rejected["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TaskOnlyProjectAppearsInSharedProjectListWithDelegateCapability()
    {
        _a.AllowWebRead = false;
        _authorizations.ReplaceProjects([_a, _b]);
        var list = MultiProjectTools.Create(_authorizations, new CommandRunner(new RedactingLogger(_root)))
            .Single(tool => tool.Name == "list_projects");
        var response = await list.Invoke(new JsonObject(), CancellationToken.None);
        var payload = JsonNode.Parse(response["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
        var a = payload["projects"]!.AsArray().Single(project => project!["project_id"]!.GetValue<string>() == _a.Id.ToString("D"));
        Assert.Contains("WebDelegateCodex", a!["capabilities"]!.GetValue<string>());
    }

    [Fact]
    public async Task RegrantDoesNotRestoreOldTaskId_ChangesToOtherProjectsDoNotBreakIt()
    {
        var runtime = new FakeRuntime();
        var tasks = new SharedCodexTaskTools(_authorizations, runtime).Create();
        var started = await Tool(tasks, "codex_task_start").Invoke(new JsonObject { ["project_id"]=_a.Id.ToString(), ["prompt"]="test" }, CancellationToken.None);
        var arguments = new JsonObject { ["project_id"]=_a.Id.ToString(), ["task_id"]=RouteTaskId(started).ToString() };
        _b.AllowCodexTasks=false;
        _authorizations.ReplaceProjects([_a,_b]);
        Assert.False((await Tool(tasks,"codex_task_status").Invoke(arguments,CancellationToken.None))["isError"]!.GetValue<bool>());
        _a.AllowCodexTasks=false;
        _authorizations.ReplaceProjects([_a,_b]);
        _a.AllowCodexTasks=true;
        _authorizations.ReplaceProjects([_a,_b]);
        Assert.True((await Tool(tasks,"codex_task_status").Invoke(arguments,CancellationToken.None))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RevokeDuringLazyStartPreventsTaskExecutionAndCleansOwner()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeBridge("A");
        var owner = new FakeOwner();
        await using var runtime = new SharedCodexTaskRuntime(async (_, ct) => {
            ready.SetResult(); await release.Task.WaitAsync(ct); return new CodexTaskBridgeLease(bridge,owner);
        });
        var tasks = new SharedCodexTaskTools(_authorizations,runtime).Create();
        var request=Tool(tasks,"codex_task_start").Invoke(new JsonObject { ["project_id"]=_a.Id.ToString(), ["prompt"]="test" },CancellationToken.None);
        await ready.Task;
        _a.AllowCodexTasks=false;
        _authorizations.ReplaceProjects([_a,_b]);
        release.SetResult();
        Assert.True((await request)["isError"]!.GetValue<bool>());
        Assert.Empty(bridge.Calls);
        Assert.True(owner.Disposed);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
    }
    private ProjectRecord Project(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name,
        Path = Directory.CreateDirectory(Path.Combine(_root, name)).FullName,
        AllowWebRead = false, AllowCodexTasks = true
    };

    [Fact]
    public async Task WritableGrantIsServerSelectedAndDuplicateRequestStartsOnlyOnce()
    {
        _a.AllowCodexWrite = true;
        _authorizations.ReplaceProjects([_a, _b]);
        var runtime = new FakeRuntime();
        var tools = new SharedCodexTaskTools(_authorizations, runtime).Create();
        var args = new JsonObject { ["project_id"] = _a.Id.ToString(), ["prompt"] = "修改并检查", ["request_id"] = Guid.NewGuid().ToString() };
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Tool(tools, "codex_task_start").Invoke((JsonObject)args.DeepClone(), CancellationToken.None)));
        Assert.All(results, result => Assert.Equal(RouteTaskId(results[0]), RouteTaskId(result)));
        Assert.Single(runtime.Bridge(_a.Id).Calls);
        Assert.Equal("workspace-write", runtime.Bridge(_a.Id).Calls[0].Arguments["sandbox"]!.GetValue<string>());
        args["prompt"] = "不同任务";
        Assert.True((await Tool(tools, "codex_task_start").Invoke(args, CancellationToken.None))["isError"]!.GetValue<bool>());
        Assert.Single(runtime.Bridge(_a.Id).Calls);
        _a.AllowCodexWrite = false;
        _authorizations.ReplaceProjects([_a, _b]);
        Assert.True((await Tool(tools, "codex_task_status").Invoke(new JsonObject {
            ["project_id"] = _a.Id.ToString(), ["task_id"] = RouteTaskId(results[0]).ToString()
        }, CancellationToken.None))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DowngradeReclaimsWritableBridgeAndStartsReadOnlyBridge()
    {
        var started = new List<(bool Write, FakeOwner Owner)>();
        await using var runtime = new SharedCodexTaskRuntime((project, _) => {
            var owner = new FakeOwner(); started.Add((project.AllowCodexWrite, owner));
            return Task.FromResult(new CodexTaskBridgeLease(new FakeBridge(project.Name), owner));
        });
        _a.AllowCodexWrite = true;
        await runtime.GetBridgeAsync(_a, CancellationToken.None);
        _a.AllowCodexWrite = false;
        await runtime.ReconcileAsync([_a, _b]);
        Assert.True(started[0].Owner.Disposed);
        await runtime.GetBridgeAsync(_a, CancellationToken.None);
        Assert.Equal(2, started.Count);
        Assert.True(started[0].Write);
        Assert.False(started[1].Write);
    }

    private static GatewayTool Tool(IReadOnlyList<GatewayTool> tools, string name) => tools.Single(tool => tool.Name == name);

    private static Guid RouteTaskId(JsonObject result)
    {
        var text = result["content"]!.AsArray().Last()!["text"]!.GetValue<string>();
        Assert.True(Guid.TryParse(JsonNode.Parse(text)!["task_id"]!.GetValue<string>(), out var taskId));
        return taskId;
    }

    private sealed class FakeRuntime : ISharedCodexTaskRuntime
    {
        private readonly Dictionary<Guid, FakeBridge> _bridges = [];
        private readonly Dictionary<Guid, FakeOwner> _owners = [];

        public Task<ICodexTaskBridge> GetBridgeAsync(ProjectRecord project, CancellationToken cancellationToken)
        {
            if (!_bridges.TryGetValue(project.Id, out var bridge))
            {
                bridge = new FakeBridge(project.Name);
                _bridges.Add(project.Id, bridge);
                _owners.Add(project.Id, new FakeOwner());
            }
            return Task.FromResult<ICodexTaskBridge>(bridge);
        }

        public Task ReconcileAsync(IEnumerable<ProjectRecord> projects, CancellationToken cancellationToken = default)
        {
            var allowed = projects.Where(project => project.AllowCodexTasks).Select(project => project.Id).ToHashSet();
            foreach (var (projectId, owner) in _owners)
                if (!allowed.Contains(projectId)) owner.Disposed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public FakeBridge Bridge(Guid projectId) => _bridges.TryGetValue(projectId, out var bridge) ? bridge : new FakeBridge("unused");
        public FakeOwner Owner(Guid projectId) => _owners[projectId];
    }

    private sealed class FakeBridge(string name) : ICodexTaskBridge
    {
        public List<(string Tool, JsonObject Arguments)> Calls { get; } = [];

        public Task<JsonObject?> CallToolAsync(string bridgeTool, JsonObject arguments, CancellationToken cancellationToken)
        {
            Calls.Add((bridgeTool, (JsonObject)arguments.DeepClone()));
            return Task.FromResult<JsonObject?>(new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = new JsonObject
                    {
                        ["jobId"] = $"job-{name}", ["activityId"] = Guid.NewGuid().ToString("D"), ["threadId"] = $"thread-{name}"
                    }.ToJsonString()
                }),
                ["isError"] = false
            });
        }
    }

    private sealed class FakeOwner : IAsyncDisposable
    {
        public bool Disposed { get; set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}

