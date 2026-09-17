namespace LocalProjectBridge;

/// <summary>
/// Keeps persisted project preferences separate from permissions that are effective
/// in the current connection session.
/// </summary>
public sealed class SessionProjectPermissions
{
    private readonly HashSet<Guid> _taskApprovals = [];
    private readonly HashSet<Guid> _writeApprovals = [];

    public bool IsReadOnlyRestore { get; private set; }

    public void BeginReadOnlyRestore()
    {
        IsReadOnlyRestore = true;
        _taskApprovals.Clear();
        _writeApprovals.Clear();
    }

    public void BeginInteractiveSession()
    {
        IsReadOnlyRestore = false;
        _taskApprovals.Clear();
        _writeApprovals.Clear();
    }

    public static bool SupportsCodexTasks(TunnelProvider provider)
        => provider is TunnelProvider.CloudflareQuickTunnel or TunnelProvider.OpenAiSecureTunnel;

    public bool IsCodexTaskEnabled(ProjectRecord project, TunnelProvider provider)
        => SupportsCodexTasks(provider)
           && project.AllowCodexTasks
           && (!IsReadOnlyRestore || _taskApprovals.Contains(project.Id));

    public bool SetCodexTaskEnabled(ProjectRecord project, TunnelProvider provider, bool enabled)
    {
        if (!SupportsCodexTasks(provider)) return false;
        project.AllowCodexTasks = enabled;
        if (enabled) _taskApprovals.Add(project.Id);
        else
        {
            _taskApprovals.Remove(project.Id);
            _writeApprovals.Remove(project.Id);
        }
        return true;
    }

    public bool IsCodexTaskWriteEnabled(ProjectRecord project, TunnelProvider provider)
        => IsCodexTaskEnabled(project, provider)
           && project.AllowCodexWrite
           && _writeApprovals.Contains(project.Id);

    public bool SetCodexTaskWriteEnabled(ProjectRecord project, TunnelProvider provider, bool enabled)
    {
        if (!SupportsCodexTasks(provider) || (enabled && !IsCodexTaskEnabled(project, provider))) return false;
        project.AllowCodexWrite = enabled;
        if (enabled) _writeApprovals.Add(project.Id);
        else _writeApprovals.Remove(project.Id);
        return true;
    }

    public bool RevokeCodexTaskWriteApprovals()
    {
        if (_writeApprovals.Count == 0) return false;
        _writeApprovals.Clear();
        return true;
    }

    public string DescribeCodexTasks(ProjectRecord project, TunnelProvider provider)
    {
        if (!SupportsCodexTasks(provider))
            return "当前连接不支持网页委派 Codex 任务。";
        if (!IsCodexTaskEnabled(project, provider))
            return IsReadOnlyRestore && project.AllowCodexTasks
                ? "只读恢复未重新授权 Codex 任务；勾选后可在本次连接中使用。"
                : "当前未允许网页委派 Codex 任务。";
        if (IsCodexTaskWriteEnabled(project, provider))
            return "本次连接允许 Codex 修改项目并运行本地检查。任务不使用网络或提升权限。";
        if (project.AllowCodexWrite)
            return "已保存可写偏好；本次连接仍需勾选“允许 Codex 修改项目并运行本地检查”。";
        return "本次连接仅允许只读 Codex 任务。勾选下方选项后可允许修改项目并运行本地检查。";
    }
}
