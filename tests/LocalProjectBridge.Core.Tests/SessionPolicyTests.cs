using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Tests;

public sealed class SessionPolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-policy").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Create_NormalizesPathAndSnapshotsCapabilities()
    {
        var project = Path.Combine(_root, "proj");
        Directory.CreateDirectory(project);
        var record = new ProjectRecord
        {
            Name = "我的项目",
            Path = project.ToUpperInvariant(),
            AllowWebRead = true,
            AllowCodexTasks = false
        };
        var policy = SessionPolicyFactory.Create(record, CapabilityExtensions.FromProject(record));

        Assert.Equal(Normalize(project), Normalize(policy.CanonicalProjectPath), ignoreCase: true);
        Assert.True(policy.Allows(CapabilityFlags.WebRead));
        Assert.False(policy.Allows(CapabilityFlags.WebDelegateCodex));
        Assert.NotEqual(Guid.Empty, policy.SessionId);
        Assert.Null(policy.ExpiresAt);
    }

    [Fact]
    public void Create_Throws_WhenDirectoryMoved()
    {
        var record = new ProjectRecord { Name = "gone", Path = Path.Combine(_root, "missing") };
        Assert.Throws<DirectoryNotFoundException>(() => SessionPolicyFactory.Create(record, CapabilityFlags.WebRead));
    }

    [Fact]
    public void Allows_CombinesFlags()
    {
        var flags = CapabilityFlags.WebRead | CapabilityFlags.CodexAskWeb;
        Assert.True(flags.Allows(CapabilityFlags.WebRead));
        Assert.True(flags.Allows(CapabilityFlags.CodexAskWeb));
        Assert.False(flags.Allows(CapabilityFlags.WebDelegateCodex));
        Assert.False(flags.Allows(CapabilityFlags.WebRead | CapabilityFlags.WebDelegateCodex));
    }

    [Fact]
    public void FromProject_MapsAllSwitches()
    {
        var record = new ProjectRecord
        {
            Name = "p",
            Path = _root,
            AllowWebRead = false,
            AllowCodexTasks = true,
            AllowCodexAskWeb = false
        };
        var flags = CapabilityExtensions.FromProject(record);
        Assert.Equal(CapabilityFlags.WebDelegateCodex, flags);
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}
