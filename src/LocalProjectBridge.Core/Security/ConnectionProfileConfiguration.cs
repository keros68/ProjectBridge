using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Security;

/// <summary>
/// Saves connection settings without exposing a runtime key in JSON. A replacement key uses a
/// new credential target so a failed JSON save cannot silently replace the last saved key.
/// </summary>
public static class ConnectionProfileConfiguration
{
    private const string CredentialTargetPrefix = "ProjectBridge/OpenAITunnel/";

    public static async Task<ConnectionProfile> SaveAsync(
        ConnectionProfile current,
        TunnelProvider provider,
        string? tunnelId,
        string? pastedRuntimeKey,
        string? advancedCredentialReference,
        IRuntimeCredentialStore credentialStore,
        Func<ConnectionProfile, Task> persist)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(persist);

        var normalizedTunnelId = string.IsNullOrWhiteSpace(tunnelId) ? null : tunnelId.Trim();
        var normalizedKey = pastedRuntimeKey?.Trim() ?? string.Empty;
        var normalizedReference = advancedCredentialReference?.Trim() ?? string.Empty;
        var previousReference = current.RuntimeCredentialReference;
        string? stagedCredentialTarget = null;

        var candidate = Clone(current);
        candidate.Provider = provider;
        try
        {
            if (provider == TunnelProvider.OpenAiSecureTunnel)
            {
                if (normalizedTunnelId?.StartsWith("tunnel_", StringComparison.Ordinal) != true
                    || normalizedTunnelId.Length <= "tunnel_".Length)
                    throw new InvalidOperationException("固定入口 ID 必须以 tunnel_ 开头。");
                candidate.TunnelId = normalizedTunnelId;

                if (normalizedKey.Length > 0)
                {
                    stagedCredentialTarget = CredentialTargetPrefix + candidate.Id.ToString("N") + "/" + Guid.NewGuid().ToString("N");
                    credentialStore.Save(stagedCredentialTarget, normalizedKey);
                    candidate.RuntimeCredentialReference = "wincred:" + stagedCredentialTarget;
                }
                else if (normalizedReference.Length > 0)
                {
                    if (!IsAdvancedCredentialReference(normalizedReference))
                        throw new InvalidOperationException("密钥引用只支持 env:变量名 或 file:完整路径。");
                    candidate.RuntimeCredentialReference = normalizedReference;
                }

                if (!SecureTunnelRuntime.HasUsableRuntimeCredentialReference(candidate.RuntimeCredentialReference, credentialStore))
                    throw new MissingTunnelRuntimeCredentialException("请先创建运行密钥，并粘贴到“运行密钥”输入框。");
            }
            if (!RuntimeConfigurationEquals(current, candidate))
            {
                candidate.LastConnectedAt = null;
                candidate.LastAuthorizedAt = null;
                candidate.LastVerifiedCall = null;
                candidate.LastVerifiedClientId = null;
            }
            await persist(candidate).ConfigureAwait(false);
        }
        catch
        {
            if (stagedCredentialTarget is not null) TryDelete(credentialStore, stagedCredentialTarget);
            throw;
        }

        if (!string.Equals(previousReference, candidate.RuntimeCredentialReference, StringComparison.Ordinal)
            && SecureTunnelRuntime.TryGetWindowsCredentialTarget(previousReference, out var previousTarget)
            && IsOwnedCredentialTarget(candidate.Id, previousTarget))
            TryDelete(credentialStore, previousTarget);

        return candidate;
    }

    public static ConnectionProfile Clone(ConnectionProfile source) => new()
    {
        Version = source.Version,
        Id = source.Id,
        Name = source.Name,
        Provider = source.Provider,
        TunnelId = source.TunnelId,
        RuntimeCredentialReference = source.RuntimeCredentialReference,
        LastConnectedAt = source.LastConnectedAt,
        LastAuthorizedAt = source.LastAuthorizedAt,
        LastVerifiedCall = source.LastVerifiedCall,
        LastVerifiedClientId = source.LastVerifiedClientId
    };

    public static void CopyTo(ConnectionProfile source, ConnectionProfile target)
    {
        target.Version = source.Version;
        target.Id = source.Id;
        target.Name = source.Name;
        target.Provider = source.Provider;
        target.TunnelId = source.TunnelId;
        target.RuntimeCredentialReference = source.RuntimeCredentialReference;
        target.LastConnectedAt = source.LastConnectedAt;
        target.LastAuthorizedAt = source.LastAuthorizedAt;
        target.LastVerifiedCall = source.LastVerifiedCall;
        target.LastVerifiedClientId = source.LastVerifiedClientId;
    }

    public static bool RuntimeConfigurationEquals(ConnectionProfile left, ConnectionProfile right)
        => left.Id == right.Id && left.Provider == right.Provider
           && string.Equals(left.TunnelId, right.TunnelId, StringComparison.Ordinal)
           && string.Equals(left.RuntimeCredentialReference, right.RuntimeCredentialReference, StringComparison.Ordinal);

    public static bool IsAdvancedCredentialReference(string? reference)
        => reference?.StartsWith("env:", StringComparison.OrdinalIgnoreCase) == true
           || reference?.StartsWith("file:", StringComparison.OrdinalIgnoreCase) == true;

    private static void TryDelete(IRuntimeCredentialStore store, string target)
    {
        try { store.Delete(target); }
        catch { /* The saved profile remains usable; stale credentials are safer than losing it. */ }
    }

    private static bool IsOwnedCredentialTarget(Guid profileId, string target)
    {
        var root = CredentialTargetPrefix + profileId.ToString("N");
        return string.Equals(target, root, StringComparison.Ordinal)
               || target.StartsWith(root + "/", StringComparison.Ordinal);
    }
}
