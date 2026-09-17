using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core.Sessions;

/// <summary>
/// 面向用户的错误三要素（设计文档 9.2）：发生了什么、哪项能力受影响、用户现在可以做什么。
/// Detail 仅进入诊断日志，界面默认不展示。
/// </summary>
public sealed record BridgeError(
    string What,
    string AffectedCapability,
    string NextAction,
    string? Detail = null)
{
    public override string ToString()
    {
        // Detail may contain an exception stack or a backend message.  It is kept
        // for the diagnostic log/copy action, but never rendered in the main UI.
        return $"{What}\n受影响能力：{AffectedCapability}\n下一步：{NextAction}";
    }
}

public static class ErrorClassifier
{
    public static BridgeError Classify(Exception exception, CapabilityFlags capabilities, string phase)
    {
        var affected = capabilities == CapabilityFlags.None
            ? "当前连接"
            : string.Join("、", Enum.GetValues<CapabilityFlags>()
                .Where(flag => flag != CapabilityFlags.None && capabilities.Allows(flag))
                .Select(flag => flag.Describe()));

        return exception switch
        {
            MissingTunnelRuntimeCredentialException => new BridgeError(
                "OpenAI Secure Tunnel 尚未配置可用的运行凭据。",
                "网页端与本地项目的连接",
                "填写固定入口 ID，并在连接向导中创建或粘贴运行密钥。也可在高级选项中使用环境变量或密钥文件。",
                exception.Message),
            DirectoryNotFoundException => new BridgeError(
                "项目目录不存在或已被移动。",
                affected,
                "在项目管理中重新选择项目文件夹。",
                exception.Message),
            UnauthorizedAccessException => new BridgeError(
                "没有权限访问项目中的部分内容。",
                affected,
                "确认文件夹可读后重试，或检查杀毒软件的拦截记录。",
                exception.Message),
            TimeoutException timeout when phase.Contains("tunnel", StringComparison.OrdinalIgnoreCase) || timeout.Message.Contains("隧道", StringComparison.Ordinal) => new BridgeError(
                "公网隧道未能在预期时间内就绪。",
                affected,
                "检查网络连接后点击“连接”重试；若反复失败，请打开“首次设置”重新完成连接授权。",
                exception.Message),
            TimeoutException => new BridgeError(
                "本地组件未能在预期时间内启动。",
                affected,
                "点击“连接”重试一次；若仍然失败，请复制诊断信息查看日志。",
                exception.Message),
            InvalidOperationException invalid when invalid.Message.Contains("尚未就绪", StringComparison.Ordinal)
                || invalid.Message.Contains("未找到", StringComparison.Ordinal)
                || invalid.Message.Contains("尚未安装", StringComparison.Ordinal) => new BridgeError(
                "本地运行组件缺失或未通过健康检查。",
                affected,
                "打开“首次设置”重新准备运行组件。",
                exception.Message),
            InvalidOperationException invalid when invalid.Message.Contains("授权", StringComparison.Ordinal)
                || invalid.Message.Contains("配对", StringComparison.Ordinal)
                || invalid.Message.Contains("ChatGPT", StringComparison.Ordinal) => new BridgeError(
                "ChatGPT 侧的授权尚未完成或已经失效。",
                "网页端与本地项目的全部协作能力",
                "按“首次设置”向导在 ChatGPT 中重新添加或确认连接。",
                exception.Message),
            InvalidOperationException => new BridgeError(
                Summarize(exception.Message),
                affected,
                "点击“连接”重试一次；若仍然失败，请复制诊断信息。",
                exception.ToString()),
            OperationCanceledException => new BridgeError(
                IsDisconnectPhase(phase) ? "断开操作超时或被取消。" : "连接操作超时或被取消。",
                affected,
                IsDisconnectPhase(phase) ? "再次点击“断开”。" : "点击“连接”重试一次。",
                exception.Message),
            _ => new BridgeError(
                "发生意外错误。",
                affected,
                "点击“连接”重试；若反复出现，请复制诊断信息反馈。",
                exception.ToString())
        };
    }

    private static bool IsDisconnectPhase(string phase)
        => phase.Contains("disconnect", StringComparison.OrdinalIgnoreCase)
           || phase.Contains("stop", StringComparison.OrdinalIgnoreCase);

    private static string Summarize(string message)
    {
        var firstLine = message.Split('\n')[0].Trim();
        return firstLine.Length == 0 ? "本地组件返回了无法识别的状态。" : firstLine;
    }
}
