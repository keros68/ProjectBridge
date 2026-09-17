namespace LocalProjectBridge.Core.Gateway;

public sealed record WriteLease(
    Guid LeaseId,
    Guid ProjectId,
    long RootVersion,
    Guid ConnectionId,
    string ClientId,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    bool SingleChange,
    int AppliedChanges,
    bool AllowDeletion = false);

/// <summary>In-memory only: restarting the application invalidates every write lease.</summary>
public sealed class WriteLeaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WriteLease> _leases = [];
    private readonly Func<DateTimeOffset> _clock;

    public WriteLeaseStore(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.Now);

    public WriteLease Grant(Guid projectId, long rootVersion, Guid connectionId, string clientId,
        TimeSpan duration, bool singleChange = false, bool allowDeletion = false)
    {
        if (projectId == Guid.Empty || connectionId == Guid.Empty || rootVersion <= 0)
            throw new ArgumentException("写入授权缺少项目或连接身份。");
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("写入授权缺少客户端身份。", nameof(clientId));
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(2))
            throw new ArgumentOutOfRangeException(nameof(duration), "写入授权有效期必须在两小时以内。");
        var now = _clock();
        var lease = new WriteLease(Guid.NewGuid(), projectId, rootVersion, connectionId, clientId.Trim(),
            now, now.Add(duration), singleChange, 0, allowDeletion);
        lock (_gate) _leases[projectId] = lease;
        return lease;
    }

    public bool TryAuthorize(Guid projectId, long rootVersion, Guid connectionId, string? clientId,
        out WriteLease lease, out string reason)
        => TryValidate(projectId, rootVersion, connectionId, clientId, allowConsumedSingleChange: false,
            out lease, out reason);

    public bool TryValidateIdentity(Guid projectId, long rootVersion, Guid connectionId, string? clientId,
        out WriteLease lease, out string reason)
        => TryValidate(projectId, rootVersion, connectionId, clientId, allowConsumedSingleChange: true,
            out lease, out reason);

    private bool TryValidate(Guid projectId, long rootVersion, Guid connectionId, string? clientId,
        bool allowConsumedSingleChange, out WriteLease lease, out string reason)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(projectId, out lease!))
            {
                reason = "本机尚未为该项目授予写入权限。";
                return false;
            }
            if (lease.ExpiresAt <= _clock())
            {
                _leases.Remove(projectId);
                reason = "该项目的本机写入授权已过期。";
                return false;
            }
            if (lease.RootVersion != rootVersion || lease.ConnectionId != connectionId
                || !string.Equals(lease.ClientId, clientId, StringComparison.Ordinal))
            {
                reason = "本次请求与获准的项目、连接主体或项目版本不一致。";
                return false;
            }
            if (!allowConsumedSingleChange && lease.SingleChange && lease.AppliedChanges > 0)
            {
                reason = "本次修改授权已经使用。";
                return false;
            }
            reason = string.Empty;
            return true;
        }
    }

    public WriteLease? GetCurrent(Guid projectId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(projectId, out var lease)) return null;
            if (lease.ExpiresAt <= _clock())
            {
                _leases.Remove(projectId);
                return null;
            }
            return lease;
        }
    }

    public void MarkApplied(Guid leaseId)
    {
        lock (_gate)
        {
            var pair = _leases.FirstOrDefault(pair => pair.Value.LeaseId == leaseId);
            if (pair.Value is null) return;
            _leases[pair.Key] = pair.Value with { AppliedChanges = pair.Value.AppliedChanges + 1 };
        }
    }

    public void RevokeProject(Guid projectId)
    {
        lock (_gate) _leases.Remove(projectId);
    }

    public void RevokeMissingProjects(IEnumerable<Guid> activeProjectIds)
    {
        var active = activeProjectIds.ToHashSet();
        lock (_gate)
            foreach (var projectId in _leases.Keys.Where(projectId => !active.Contains(projectId)).ToArray())
                _leases.Remove(projectId);
    }

    public void RevokeAll()
    {
        lock (_gate) _leases.Clear();
    }
}
