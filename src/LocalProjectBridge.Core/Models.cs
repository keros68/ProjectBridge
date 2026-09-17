namespace LocalProjectBridge.Core;

public sealed class ProjectRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Path { get; set; }

    /// <summary>网页端读取项目（只读审查：读取、搜索、Git 状态与差异）。</summary>
    public bool AllowWebRead { get; set; } = true;

    /// <summary>网页端委派只读、禁网的 Codex 任务。</summary>
    public bool AllowCodexTasks { get; set; }

    /// <summary>本机单独授权 Codex 在项目工作区写入；不授予命令联网或提权。</summary>
    public bool AllowCodexWrite { get; set; }

    /// <summary>Codex 向绑定的 ChatGPT 对话请求规划或复核。</summary>
    public bool AllowCodexAskWeb { get; set; } = true;

    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public string? LastFault { get; set; }
    public override string ToString() => Name;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public List<ProjectRecord> Projects { get; set; } = [];

    /// <summary>仅用于界面选择；远程请求必须显式传 project_id。</summary>
    public Guid? SelectedProjectId { get; set; }

    /// <summary>旧版迁移字段。读取后会迁移到 SelectedProjectId。</summary>
    public string? SelectedProjectPath { get; set; }
    public bool StartWithWindows { get; set; }
    public bool RestoreReadOnlyConnection { get; set; }
    public bool SetupCompleted { get; set; }
    public int ConnectionVersion { get; set; }

    /// <summary>试验性：启用统一 MCP 网关（设计文档 5 / 阶段 B）。默认关闭时保持双上游过渡形态。</summary>
    public bool UseUnifiedGateway { get; set; }
}

public enum TunnelProvider
{
    CloudflareQuickTunnel,
    OpenAiSecureTunnel
}

/// <summary>
/// 当前 Windows 用户的一条机器级连接。项目不是连接身份，只是这条连接可访问的授权资源。
/// 凭据字段只保存 Windows 凭据名称、环境变量或密钥文件引用，不保存明文密钥。
/// </summary>
public sealed class ConnectionProfile
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "ProjectBridge";
    public TunnelProvider Provider { get; set; } = TunnelProvider.CloudflareQuickTunnel;
    public string? TunnelId { get; set; }
    public string? RuntimeCredentialReference { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastAuthorizedAt { get; set; }
    public DateTimeOffset? LastVerifiedCall { get; set; }
    public string? LastVerifiedClientId { get; set; }

    public string RuntimeAlias => $"projectbridge-{Id:N}";
}

public sealed record RuntimePaths(
    string? Node,
    string? CodexScript,
    string? CodexShim,
    string? ReverseBridge,
    string? TunnelClient,
    string? C2cCli,
    string? Cloudflared)
{
    public bool NodeReady => File.Exists(Node);
    public bool CodexReady => File.Exists(CodexScript) && File.Exists(CodexShim);
    public bool BridgeReady => File.Exists(ReverseBridge);
    public bool TunnelReady => File.Exists(TunnelClient);
    public bool C2cReady => File.Exists(C2cCli) && File.Exists(Cloudflared);
}

public sealed record BackendStatus(
    bool TransceiverCheckoutReady,
    bool C2cCheckoutReady,
    bool C2cBuilt,
    string? TransceiverCommit,
    string? C2cCommit);

public sealed record C2cTunnelChoice(bool NeedsChoice, string? UserPrompt, string? LoginPrompt);

public sealed record C2cSetupResult(string WorkspaceName, string ConnectorName, string McpUrl);
