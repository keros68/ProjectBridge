using System.Text.Json;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class ConnectionProfileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-profile-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void OldPathSelection_MigratesToProjectId()
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var projectId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_root, "projects.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Projects = new[] { new { Id = projectId, Name = "A", Path = path, AllowWebRead = true } },
            SelectedProjectPath = path
        }));

        var settings = new RegistryStore(_root).Load();

        Assert.Equal(projectId, settings.SelectedProjectId);
        Assert.Equal(2, settings.Version);
    }

    [Fact]
    public async Task ConnectionProfile_IsStoredSeparatelyFromProjectRegistry()
    {
        var store = new RegistryStore(_root);
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_test",
            RuntimeCredentialReference = "env:CONTROL_PLANE_API_KEY"
        };

        await store.SaveAsync(new AppSettings());
        await store.SaveConnectionAsync(profile);

        Assert.Equal(profile.Id, store.LoadConnection().Id);
        Assert.DoesNotContain("tunnel_test", await File.ReadAllTextAsync(Path.Combine(_root, "projects.json")));
        Assert.Contains("env:CONTROL_PLANE_API_KEY", await File.ReadAllTextAsync(Path.Combine(_root, "connections.json")));
    }

    [Fact]
    public async Task ReadinessAndConfigurationWrites_CannotRestoreTheOldRuntimeConfiguration()
    {
        var oldRuntime = new ConnectionProfile
        {
            Id = Guid.NewGuid(), Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_old", RuntimeCredentialReference = "env:OLD_KEY",
            LastVerifiedCall = DateTimeOffset.UtcNow, LastVerifiedClientId = "old-client"
        };
        var newRuntime = new ConnectionProfile
        {
            Id = oldRuntime.Id, Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_new", RuntimeCredentialReference = "env:NEW_KEY"
        };

        for (var iteration = 0; iteration < 12; iteration++)
        {
            var store = new RegistryStore(_root);
            await store.SaveConnectionAsync(oldRuntime);
            var readiness = ConnectionProfileConfiguration.Clone(oldRuntime);
            readiness.LastVerifiedCall = DateTimeOffset.UtcNow.AddMinutes(iteration + 1);
            readiness.LastVerifiedClientId = "active-old-client";

            await Task.WhenAll(
                Task.Run(() => store.TryUpdateConnectionReadinessAsync(readiness)),
                Task.Run(() => new RegistryStore(_root).SaveConnectionAsync(ConnectionProfileConfiguration.Clone(newRuntime))));

            var persisted = store.LoadConnection();
            Assert.Equal("tunnel_new", persisted.TunnelId);
            Assert.Equal("env:NEW_KEY", persisted.RuntimeCredentialReference);
            Assert.Null(persisted.LastVerifiedCall);
            Assert.Null(persisted.LastVerifiedClientId);
        }
    }
}
