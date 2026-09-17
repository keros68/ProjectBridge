using LocalProjectBridge.Core.Processes;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class TunnelCredentialTests
{
    [Fact]
    public async Task RuntimeProfile_UsesRestrictedRuntimeReferenceWithoutAdminProfile()
    {
        var name = "LPB_TEST_RUNTIME_KEY_" + Guid.NewGuid().ToString("N");
        var profile = new ConnectionProfile
        {
            TunnelId = "tunnel_0123456789abcdef",
            RuntimeCredentialReference = "env:" + name
        };
        var runtime = new SecureTunnelRuntime(new CommandRunner(new RedactingLogger(Path.GetTempPath())));
        await Assert.ThrowsAsync<MissingTunnelRuntimeCredentialException>(() => runtime.AssertRuntimeCredentialReadyAsync(profile));
        try
        {
            Environment.SetEnvironmentVariable(name, "test-only");
            await runtime.AssertRuntimeCredentialReadyAsync(profile);
            var arguments = SecureTunnelRuntime.BuildConnectArguments(profile, "http://127.0.0.1:43123/mcp", Path.GetTempPath());
            Assert.Contains("--tunnel-id", arguments);
            Assert.Contains("--runtime-api-key", arguments);
            Assert.Contains("env:" + name, arguments);
            Assert.DoesNotContain("--admin-key", arguments);
            Assert.DoesNotContain("OPENAI_ADMIN_KEY", arguments);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret-inline")]
    [InlineData("env:")]
    [InlineData("file:")]
    public void MissingOrLiteralRuntimeSecret_IsRejected(string? reference)
        => Assert.False(SecureTunnelRuntime.HasUsableRuntimeCredentialReference(reference));

    [Fact]
    public void FileReference_RequiresAnExistingNonEmptyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "lpb-test-key-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(SecureTunnelRuntime.HasUsableRuntimeCredentialReference("file:" + path));
            File.WriteAllText(path, "");
            Assert.False(SecureTunnelRuntime.HasUsableRuntimeCredentialReference("file:" + path));
            File.WriteAllText(path, "test-only");
            Assert.True(SecureTunnelRuntime.HasUsableRuntimeCredentialReference("file:" + path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WindowsCredentialReference_IsAcceptedWithoutPuttingSecretInArguments()
    {
        var store = new FakeRuntimeCredentialStore();
        const string target = "ProjectBridge/Test/runtime-key";
        store.Save(target, "test-runtime-secret");
        var profile = new ConnectionProfile
        {
            TunnelId = "tunnel_0123456789abcdef",
            RuntimeCredentialReference = "wincred:" + target
        };
        var runtime = new SecureTunnelRuntime(
            new CommandRunner(new RedactingLogger(Path.GetTempPath())), store);

        await runtime.AssertRuntimeCredentialReadyAsync(profile);

        Assert.True(SecureTunnelRuntime.HasUsableRuntimeCredentialReference(profile.RuntimeCredentialReference, store));
        Assert.True(SecureTunnelRuntime.TryGetWindowsCredentialTarget(profile.RuntimeCredentialReference, out var parsed));
        Assert.Equal(target, parsed);
        var arguments = SecureTunnelRuntime.BuildConnectArguments(
            profile,
            "http://127.0.0.1:43123/mcp",
            Path.GetTempPath(),
            "env:PROJECTBRIDGE_TUNNEL_RUNTIME_KEY");
        Assert.Contains("env:PROJECTBRIDGE_TUNNEL_RUNTIME_KEY", arguments);
        Assert.DoesNotContain("test-runtime-secret", arguments);
    }

    [Theory]
    [InlineData("wincred:")]
    [InlineData("wincred:missing")]
    public void MissingWindowsCredential_IsRejected(string reference)
        => Assert.False(SecureTunnelRuntime.HasUsableRuntimeCredentialReference(
            reference, new FakeRuntimeCredentialStore()));

    [Fact]
    public void WindowsCredentialStore_RoundTripsAndDeletesCurrentUserSecret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new WindowsRuntimeCredentialStore();
        var target = "ProjectBridge/Test/" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(store.Exists(target));
            store.Save(target, "test-secret-中文");
            Assert.True(store.Exists(target));
            Assert.Equal("test-secret-中文", store.Read(target));
        }
        finally { store.Delete(target); }
        Assert.False(store.Exists(target));
    }

    [Fact]
    public void RuntimeAlias_IsStableForOneConnectionAndIndependentOfSelectedProject()
    {
        var id = Guid.NewGuid();
        var profile = new ConnectionProfile { Id = id };
        var before = profile.RuntimeAlias;
        var settings = new AppSettings { SelectedProjectId = Guid.NewGuid() };
        settings.SelectedProjectId = Guid.NewGuid();
        Assert.Equal(before, profile.RuntimeAlias);
        Assert.Contains(id.ToString("N"), before);
    }

    [Fact]
    public async Task SaveConfiguration_BlankKeyReusesSavedCredentialAcrossReload()
    {
        var credentialStore = new FakeRuntimeCredentialStore();
        var registry = new RegistryStore(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lpb-config-" + Guid.NewGuid().ToString("N"))).FullName);
        try
        {
            var current = new ConnectionProfile();
            var first = await ConnectionProfileConfiguration.SaveAsync(
                current, TunnelProvider.OpenAiSecureTunnel, "tunnel_test", "fictional-test-key", null,
                credentialStore, registry.SaveConnectionAsync);
            var second = await ConnectionProfileConfiguration.SaveAsync(
                first, TunnelProvider.OpenAiSecureTunnel, "tunnel_test", "", "",
                credentialStore, registry.SaveConnectionAsync);

            var reloaded = registry.LoadConnection();
            Assert.Equal(first.RuntimeCredentialReference, second.RuntimeCredentialReference);
            Assert.Equal(second.RuntimeCredentialReference, reloaded.RuntimeCredentialReference);
            Assert.True(SecureTunnelRuntime.HasUsableRuntimeCredentialReference(reloaded.RuntimeCredentialReference, credentialStore));
            Assert.DoesNotContain("fictional-test-key", await File.ReadAllTextAsync(Path.Combine(registry.AppDataDirectory, "connections.json")));
        }
        finally { Directory.Delete(registry.AppDataDirectory, true); }
    }

    [Fact]
    public async Task SaveConfiguration_FailedProfileWriteKeepsPreviouslySavedCredential()
    {
        var credentialStore = new FakeRuntimeCredentialStore();
        var current = new ConnectionProfile { Provider = TunnelProvider.OpenAiSecureTunnel, TunnelId = "tunnel_old" };
        var oldTarget = "ProjectBridge/OpenAITunnel/" + current.Id.ToString("N");
        credentialStore.Save(oldTarget, "old-fictional-key");
        current.RuntimeCredentialReference = "wincred:" + oldTarget;

        await Assert.ThrowsAsync<IOException>(() => ConnectionProfileConfiguration.SaveAsync(
            current, TunnelProvider.OpenAiSecureTunnel, "tunnel_new", "new-fictional-key", null,
            credentialStore, _ => throw new IOException("simulated save failure")));

        Assert.Equal("tunnel_old", current.TunnelId);
        Assert.Equal("old-fictional-key", credentialStore.Read(oldTarget));
        Assert.Single(credentialStore.Targets);
    }

    [Fact]
    public async Task SaveConfiguration_RejectsInvalidTunnelIdAndDoesNotDeleteForeignCredential()
    {
        var credentialStore = new FakeRuntimeCredentialStore();
        const string foreignTarget = "AnotherProduct/runtime-key";
        credentialStore.Save(foreignTarget, "foreign-fictional-key");
        var current = new ConnectionProfile
        {
            Provider = TunnelProvider.OpenAiSecureTunnel,
            TunnelId = "tunnel_old",
            RuntimeCredentialReference = "wincred:" + foreignTarget,
            LastConnectedAt = DateTimeOffset.UtcNow,
            LastAuthorizedAt = DateTimeOffset.UtcNow,
            LastVerifiedCall = DateTimeOffset.UtcNow,
            LastVerifiedClientId = "old-client"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectionProfileConfiguration.SaveAsync(
            current, TunnelProvider.OpenAiSecureTunnel, "key_not_a_tunnel", "new-fictional-key", null,
            credentialStore, _ => Task.CompletedTask));
        Assert.Single(credentialStore.Targets);

        var saved = await ConnectionProfileConfiguration.SaveAsync(
            current, TunnelProvider.OpenAiSecureTunnel, "tunnel_new", "new-fictional-key", null,
            credentialStore, _ => Task.CompletedTask);
        Assert.Equal("foreign-fictional-key", credentialStore.Read(foreignTarget));
        Assert.Null(saved.LastConnectedAt);
        Assert.Null(saved.LastAuthorizedAt);
        Assert.Null(saved.LastVerifiedCall);
        Assert.Null(saved.LastVerifiedClientId);
        Assert.Equal(2, credentialStore.Targets.Count);
    }

    [Theory]
    [InlineData(false, false, false, "进程未运行")]
    [InlineData(false, true, false, "旧的本地服务地址")]
    [InlineData(false, true, true, "固定入口未通过就绪检查")]
    public void UnreadyDescription_ReportsObservedFailedCheck(
        bool ready, bool running, bool targetMatches, string expected)
        => Assert.Contains(expected, SecureTunnelRuntime.DescribeUnready(
            new SecureTunnelHealth(ready, running, targetMatches)));

    [Fact]
    public async Task StopOwned_IgnoresMarkerForAnotherConnection()
    {
        var root = Directory.CreateTempSubdirectory("lpb-owned-").FullName;
        try
        {
            var profile = new ConnectionProfile { Id = Guid.NewGuid() };
            var marker = Path.Combine(root, "ownership", profile.RuntimeAlias + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, "{\"connection_id\":\"" + Guid.NewGuid() + "\",\"alias\":\"" + profile.RuntimeAlias + "\"}");
            var runtime = new SecureTunnelRuntime(new CommandRunner(new RedactingLogger(root)));

            await runtime.StopOwnedAsync(Path.Combine(root, "does-not-exist.exe"), profile, root);

            Assert.True(File.Exists(marker));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeRuntimeCredentialStore : IRuntimeCredentialStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public IReadOnlyCollection<string> Targets => _values.Keys;
        public void Save(string target, string secret) => _values[target] = secret;
        public bool Exists(string target) => _values.ContainsKey(target);
        public string Read(string target) => _values.TryGetValue(target, out var value)
            ? value
            : throw new InvalidOperationException("missing test credential");
        public void Delete(string target) => _values.Remove(target);
    }
}
