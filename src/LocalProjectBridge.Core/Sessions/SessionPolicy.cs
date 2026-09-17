namespace LocalProjectBridge.Core.Sessions;

/// <summary>
/// 每次连接生成的不可变会话策略（设计文档 6.3）。
/// 所有适配器只读取该策略，不能各自使用不同项目。
/// </summary>
public sealed record SessionPolicy(
    Guid SessionId,
    Guid ProjectId,
    string ProjectDisplayName,
    string CanonicalProjectPath,
    CapabilityFlags EnabledCapabilities,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    /// <summary>机器连接身份；项目策略中为空，连接策略中与 ProjectId 相同。</summary>
    public Guid? ConnectionId { get; init; }
    public bool IsConnectionPolicy { get; init; }

    public bool Allows(CapabilityFlags capability) => EnabledCapabilities.Allows(capability);
}

public static class SessionPolicyFactory
{
    public static SessionPolicy Create(
        ProjectRecord project,
        CapabilityFlags capabilities,
        Func<DateTimeOffset>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(project.Name))
            throw new InvalidOperationException("项目缺少显示名称，请在项目管理中补充。");
        var canonicalPath = ProjectPathGuard.CanonicalizeProjectRoot(project.Path);
        var now = clock?.Invoke() ?? DateTimeOffset.Now;
        return new SessionPolicy(
            Guid.NewGuid(),
            project.Id,
            project.Name.Trim(),
            canonicalPath,
            capabilities,
            now,
            ExpiresAt: null);
    }

    public static SessionPolicy CreateConnection(
        ConnectionProfile profile,
        string connectionWorkspace,
        CapabilityFlags capabilities,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty)
            throw new InvalidOperationException("连接配置缺少身份，请重新保存连接设置。");
        var canonicalPath = ProjectPathGuard.CanonicalizeProjectRoot(connectionWorkspace);
        var now = clock?.Invoke() ?? DateTimeOffset.Now;
        return new SessionPolicy(
            Guid.NewGuid(),
            profile.Id,
            string.IsNullOrWhiteSpace(profile.Name) ? "ProjectBridge" : profile.Name.Trim(),
            canonicalPath,
            capabilities,
            now,
            ExpiresAt: null)
        {
            ConnectionId = profile.Id,
            IsConnectionPolicy = true
        };
    }
}
