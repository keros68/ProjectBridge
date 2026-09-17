using System.Text.Json.Nodes;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Gateway;

namespace LocalProjectBridge.Core.Tests;

public sealed class MultiProjectToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-multi").FullName;
    private readonly string _projectA;
    private readonly string _projectB;
    private readonly ProjectRecord _a;
    private readonly ProjectRecord _b;
    private readonly ProjectAuthorizationRegistry _authorizations = new();
    private readonly IReadOnlyList<GatewayTool> _tools;

    public MultiProjectToolsTests()
    {
        _projectA = Directory.CreateDirectory(Path.Combine(_root, "A")).FullName;
        _projectB = Directory.CreateDirectory(Path.Combine(_root, "B")).FullName;
        File.WriteAllText(Path.Combine(_projectA, "note.txt"), "only A");
        File.WriteAllText(Path.Combine(_projectB, "note.txt"), "only B");
        _a = Project("A", _projectA);
        _b = Project("B", _projectB);
        _authorizations.ReplaceProjects([_a, _b]);
        _tools = MultiProjectTools.Create(_authorizations, new CommandRunner(new RedactingLogger(_root)));
    }

    public void Dispose() => TestTree.Delete(_root);

    [Fact]
    public async Task ReadsAreRoutedByExplicitProjectIdentity()
    {
        var a = Payload(await Tool("read_text_file").Invoke(Args(_a, "note.txt"), CancellationToken.None));
        var b = Payload(await Tool("read_text_file").Invoke(Args(_b, "note.txt"), CancellationToken.None));

        Assert.Contains("only A", a["content"]?.GetValue<string>());
        Assert.Contains("only B", b["content"]?.GetValue<string>());

        var listed = Payload(await Tool("list_projects").Invoke(new JsonObject(), CancellationToken.None));
        var ids = listed["projects"]!.AsArray().Select(item => item!["project_id"]!.GetValue<string>()).ToArray();
        Assert.Contains(_a.Id.ToString("D"), ids);
        Assert.Contains(_b.Id.ToString("D"), ids);
    }

    [Fact]
    public async Task SuccessfulProjectCallRecordsOnlyThatProjectsRecentAccess()
    {
        var accessed = new List<ProjectAccessedEventArgs>();
        _authorizations.ProjectAccessed += (_, e) => accessed.Add(e);

        await Tool("read_text_file").Invoke(Args(_b, "note.txt"), CancellationToken.None);

        var access = Assert.Single(accessed);
        Assert.Equal(_b.Id, access.ProjectId);
        Assert.InRange(access.At, DateTimeOffset.Now.AddSeconds(-5), DateTimeOffset.Now.AddSeconds(5));
    }

    [Fact]
    public async Task FailedProjectCallDoesNotClaimRecentAccess()
    {
        var accessed = new List<ProjectAccessedEventArgs>();
        _authorizations.ProjectAccessed += (_, e) => accessed.Add(e);

        var result = await Tool("read_text_file").Invoke(Args(_a, "missing.txt"), CancellationToken.None);

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Empty(accessed);
    }

    [Fact]
    public async Task TenProjectsShareOneRegistryAndRevokingOneLeavesTheOthersAvailable()
    {
        var projects = Enumerable.Range(0, 10).Select(index => Project(
            $"P{index}", Directory.CreateDirectory(Path.Combine(_root, $"P{index}")).FullName)).ToList();
        _authorizations.ReplaceProjects(projects);

        projects[4].AllowWebRead = false;
        _authorizations.ReplaceProjects(projects);
        var listed = Payload(await Tool("list_projects").Invoke(new JsonObject(), CancellationToken.None));
        var ids = listed["projects"]!.AsArray().Select(item => item!["project_id"]!.GetValue<string>()).ToArray();

        Assert.Equal(9, ids.Length);
        Assert.DoesNotContain(projects[4].Id.ToString("D"), ids);
        Assert.Contains(projects[8].Id.ToString("D"), ids);
    }

    [Fact]
    public async Task MissingOrInvalidProjectIdIsRejectedBeforeFileAccess()
    {
        var missing = await Tool("read_text_file").Invoke(new JsonObject { ["path"] = "note.txt" }, CancellationToken.None);
        var invalid = await Tool("read_text_file").Invoke(new JsonObject { ["project_id"] = "not-a-guid", ["path"] = "note.txt" }, CancellationToken.None);

        Assert.True(missing["isError"]!.GetValue<bool>());
        Assert.True(invalid["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DisabledOrRemovedProjectIsRejectedImmediately()
    {
        _a.AllowWebRead = false;
        _authorizations.ReplaceProjects([_a, _b]);
        var disabled = await Tool("read_text_file").Invoke(Args(_a, "note.txt"), CancellationToken.None);
        Assert.True(disabled["isError"]!.GetValue<bool>());

        _authorizations.ReplaceProjects([_b]);
        var removed = await Tool("read_text_file").Invoke(Args(_a, "note.txt"), CancellationToken.None);
        Assert.True(removed["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RoutingKeepsProjectReadPathGuard()
    {
        var outside = Path.Combine(_root, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");

        var result = await Tool("read_text_file").Invoke(Args(_a, "../outside.txt"), CancellationToken.None);
        Assert.True(result["isError"]!.GetValue<bool>());
    }

    [Fact]
    public void ProjectSpecificSchemasRequireProjectId()
    {
        foreach (var tool in _tools.Where(tool => tool.Name != "list_projects"))
        {
            var required = tool.InputSchema["required"]!.AsArray().Select(item => item!.GetValue<string>());
            Assert.Contains("project_id", required);
            Assert.Equal("string", tool.InputSchema["properties"]!["project_id"]!["type"]!.GetValue<string>());
        }
        Assert.Null(Tool("list_projects").InputSchema["required"]);
    }

    private GatewayTool Tool(string name) => _tools.Single(tool => tool.Name == name);

    private static ProjectRecord Project(string name, string path) => new()
    {
        Id = Guid.NewGuid(), Name = name, Path = path, AllowWebRead = true, AllowCodexAskWeb = true
    };

    private static JsonObject Args(ProjectRecord project, string path) => new()
    {
        ["project_id"] = project.Id.ToString("D"), ["path"] = path
    };

    private static JsonObject Payload(JsonObject result)
        => JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
}
