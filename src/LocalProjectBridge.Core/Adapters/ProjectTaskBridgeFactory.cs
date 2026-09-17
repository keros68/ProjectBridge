using LocalProjectBridge.Core.Gateway;

namespace LocalProjectBridge.Core.Adapters;

internal static class ProjectTaskBridgeFactory
{
    public static async Task<CodexTaskBridgeLease> StartAsync(string directory, RuntimeDiscovery discovery,
        ProjectRecord project, CancellationToken cancellationToken)
    {
        if (project.AllowCodexWrite)
            return await NativeCodexTaskBridge.StartAsync(project, discovery, directory, cancellationToken).ConfigureAwait(false);
        var adapter = new TransceiverAdapter(directory, new RedactingLogger(directory), discovery, manageTunnel: false);
        var capabilities = CapabilityFlags.WebDelegateCodex | (project.AllowCodexWrite ? CapabilityFlags.WebDelegateCodexWrite : CapabilityFlags.None);
        var policy = SessionPolicyFactory.Create(project, capabilities);
        try
        {
            await adapter.AssertReadyAsync(policy, cancellationToken).ConfigureAwait(false);
            await adapter.StartAsync(policy, cancellationToken).ConfigureAwait(false);
            await adapter.VerifyAsync(policy, cancellationToken).ConfigureAwait(false);
            return new CodexTaskBridgeLease(new HttpCodexTaskBridge(adapter.BridgeMcpUrl!), adapter);
        }
        catch { await adapter.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
