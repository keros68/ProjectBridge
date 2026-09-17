namespace LocalProjectBridge.Core.Sessions;

/// <summary>网页能力开关（设计文档 8）。</summary>
[Flags]
public enum CapabilityFlags
{
    None = 0,

    /// <summary>网页端读取项目：读取、搜索和比较当前项目中的安全文本内容。</summary>
    WebRead = 1,

    /// <summary>网页端委派 Codex：默认只读；另行授权后可写。</summary>
    WebDelegateCodex = 2,

    /// <summary>Codex 请求网页协作：向已绑定的 ChatGPT 对话发送规划或复核请求。</summary>
    CodexAskWeb = 4,

    /// <summary>当前项目已获准网页写入；仅由本机限时授权产生。</summary>
    WebWrite = 8,

    WebDelegateCodexWrite = 16
}

public static class CapabilityExtensions
{
    public static bool Allows(this CapabilityFlags capabilities, CapabilityFlags capability)
        => (capabilities & capability) == capability;

    /// <summary>把项目登记的能力偏好转换为会话能力快照。</summary>
    public static CapabilityFlags FromProject(ProjectRecord project)
    {
        var flags = CapabilityFlags.None;
        if (project.AllowWebRead) flags |= CapabilityFlags.WebRead;
        if (project.AllowCodexTasks) flags |= CapabilityFlags.WebDelegateCodex;
        if (project.AllowCodexTasks && project.AllowCodexWrite) flags |= CapabilityFlags.WebDelegateCodexWrite;
        if (project.AllowCodexAskWeb) flags |= CapabilityFlags.CodexAskWeb;
        return flags;
    }

    public static string Describe(this CapabilityFlags capability) => capability switch
    {
        CapabilityFlags.WebRead => "网页读取项目",
        CapabilityFlags.WebDelegateCodex => "网页委派 Codex 任务",
        CapabilityFlags.CodexAskWeb => "Codex 请求网页协作",
        CapabilityFlags.WebWrite => "网页直接应用修改",
        CapabilityFlags.WebDelegateCodexWrite => "Codex 修改项目并运行本地检查",
        _ => capability.ToString()
    };
}
