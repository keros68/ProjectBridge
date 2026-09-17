using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class ProjectPathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-guard").FullName;

    public void Dispose() => TestTree.Delete(_root);

    private string CreateProject()
    {
        var project = Path.Combine(_root, "proj");
        Directory.CreateDirectory(project);
        return project;
    }

    [Fact]
    public void Canonicalize_Throws_WhenDirectoryMissing()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => ProjectPathGuard.CanonicalizeProjectRoot(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Canonicalize_ResolvesJunctionedRoot()
    {
        var project = CreateProject();
        var link = Path.Combine(_root, "proj-link");
        Assert.True(CreateJunction(link, project), "mklink /J 失败");
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(link);
        Assert.Equal(Normalize(project), Normalize(canonical), ignoreCase: true);
    }

    [Fact]
    public void IsInside_AcceptsRootItselfCaseInsensitively()
    {
        var project = CreateProject();
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, project));
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, project.ToUpperInvariant()));
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, project.ToLowerInvariant()));
    }

    [Fact]
    public void IsInside_RejectsPathOutsideProject()
    {
        var project = CreateProject();
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.False(ProjectPathGuard.IsInsideProject(canonical, Path.Combine(_root, "outside", "secret.txt")));
        Assert.False(ProjectPathGuard.IsInsideProject(canonical, _root));
    }

    [Fact]
    public void IsInside_RejectsSiblingWithSimilarName()
    {
        var project = CreateProject();
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        var sibling = project + "-backup";
        Assert.False(ProjectPathGuard.IsInsideProject(canonical, sibling));
    }

    [Fact]
    public async Task IsInside_RejectsJunctionEscape()
    {
        var project = CreateProject();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "token");
        var junction = Path.Combine(project, "vendor-link");
        Assert.True(CreateJunction(junction, outside), "mklink /J 失败");

        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        var escaped = Path.Combine(junction, "secret.txt");
        Assert.False(ProjectPathGuard.IsInsideProject(canonical, escaped));
        Assert.Throws<UnauthorizedAccessException>(() => ProjectPathGuard.EnsureInsideProject(canonical, escaped));
    }

    [Fact]
    public void IsInside_AcceptsJunctionIntoProject()
    {
        var project = CreateProject();
        var inner = Path.Combine(project, "docs");
        Directory.CreateDirectory(inner);
        var junction = Path.Combine(project, "docs-link");
        Assert.True(CreateJunction(junction, inner), "mklink /J 失败");

        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, Path.Combine(junction, "readme.md")));
    }

    [Fact]
    public void EnsureInside_RejectsDotDotSegments()
    {
        var project = CreateProject();
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.Throws<UnauthorizedAccessException>(
            () => ProjectPathGuard.EnsureInsideProject(canonical, Path.Combine(project, "..", "outside")));
    }

    [Fact]
    public void IsInside_RejectsMissingChildBelowJunctionEscape()
    {
        var project = Path.Combine(_root, "project-missing-child");
        var outside = Path.Combine(_root, "outside-missing-child");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        var junction = Path.Combine(project, "linked");
        Assert.True(CreateJunction(junction, outside), "mklink /J 失败");

        Assert.False(ProjectPathGuard.IsInsideProject(project, Path.Combine(junction, "not-created-yet.txt")));
    }

    [Fact]
    public void RelativePath_UsesFinalPath()
    {
        var project = CreateProject();
        Directory.CreateDirectory(Path.Combine(project, "src"));
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        var relative = ProjectPathGuard.GetRelativePathInsideProject(canonical, Path.Combine(project, "src", "a.cs"));
        Assert.Equal(@"src\a.cs", relative);
    }

    [Fact]
    public void Canonicalize_HandlesChinesePath()
    {
        // 设计 14 阶段 A：中文路径必须完整可用
        var project = Path.Combine(_root, "我的项目", "上海分部");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "说明.md"), "内容");
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, Path.Combine(project, "说明.md")));
        Assert.Equal(@"说明.md", ProjectPathGuard.GetRelativePathInsideProject(canonical, Path.Combine(project, "说明.md")));
    }

    [Fact]
    public void Canonicalize_HandlesOneDrivePath()
    {
        // OneDrive 目录同样允许：只要真实目录存在，最终路径解析后仍在自身范围内
        var project = Path.Combine(_root, "OneDrive", "项目A");
        Directory.CreateDirectory(project);
        var canonical = ProjectPathGuard.CanonicalizeProjectRoot(project);
        Assert.True(ProjectPathGuard.IsInsideProject(canonical, Path.Combine(project, "a.txt")));
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

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
