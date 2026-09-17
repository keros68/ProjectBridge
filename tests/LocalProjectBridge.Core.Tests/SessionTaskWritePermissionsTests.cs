using LocalProjectBridge;

namespace LocalProjectBridge.Core.Tests;

public sealed class SessionTaskWritePermissionsTests
{
    [Theory]
    [InlineData(TunnelProvider.OpenAiSecureTunnel)]
    [InlineData(TunnelProvider.CloudflareQuickTunnel)]
    public void WritesRequireSeparateSessionGrantAndDoNotRestoreFromPreferences(TunnelProvider provider)
    {
        var project = new ProjectRecord { Name = "test", Path = "D:\\test", AllowCodexTasks = true, AllowCodexWrite = true };
        var permissions = new SessionProjectPermissions();
        permissions.BeginInteractiveSession();
        Assert.True(permissions.IsCodexTaskEnabled(project, provider));
        Assert.False(permissions.IsCodexTaskWriteEnabled(project, provider));
        Assert.True(permissions.SetCodexTaskWriteEnabled(project, provider, true));
        Assert.True(permissions.IsCodexTaskWriteEnabled(project, provider));
        permissions.BeginReadOnlyRestore();
        Assert.False(permissions.IsCodexTaskEnabled(project, provider));
        Assert.False(permissions.SetCodexTaskWriteEnabled(project, provider, true));
        permissions.SetCodexTaskEnabled(project, provider, true);
        Assert.False(permissions.IsCodexTaskWriteEnabled(project, provider));
        permissions.SetCodexTaskWriteEnabled(project, provider, true);
        Assert.True(permissions.IsCodexTaskWriteEnabled(project, provider));
        permissions.SetCodexTaskEnabled(project, provider, false);
        permissions.SetCodexTaskEnabled(project, provider, true);
        Assert.False(permissions.IsCodexTaskWriteEnabled(project, provider));
        permissions.SetCodexTaskWriteEnabled(project, provider, true);
        Assert.True(permissions.RevokeCodexTaskWriteApprovals());
        Assert.False(permissions.IsCodexTaskWriteEnabled(project, provider));
        Assert.True(project.AllowCodexWrite);
    }
}
