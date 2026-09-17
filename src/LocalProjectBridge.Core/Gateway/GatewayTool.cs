using System.Text.Json.Nodes;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>统一网关对网页端暴露的工具定义（设计文档 5.1 / 6.7）。</summary>
public sealed record GatewayTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, CancellationToken, Task<JsonObject>> Invoke,
    GatewayToolAnnotations? Annotations = null);

/// <summary>MCP tool behavior hints. These describe actual side effects; they are not an authorization mechanism.</summary>
public sealed record GatewayToolAnnotations(
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    bool OpenWorldHint)
{
    public static GatewayToolAnnotations ReadOnlyClosed { get; } = new(true, false, true, false);
    public static GatewayToolAnnotations NonIdempotentClosed { get; } = new(false, false, false, false);
    public static GatewayToolAnnotations NonDestructiveClosed { get; } = new(false, false, true, false);
    public static GatewayToolAnnotations MutationClosed { get; } = new(false, true, true, false);
    public static GatewayToolAnnotations DestructiveClosed { get; } = new(false, true, false, false);

    public JsonObject ToJson() => new()
    {
        ["readOnlyHint"] = ReadOnlyHint,
        ["destructiveHint"] = DestructiveHint,
        ["idempotentHint"] = IdempotentHint,
        ["openWorldHint"] = OpenWorldHint
    };
}

/// <summary>工具执行结果：文本内容与错误标记（MCP tool result）。</summary>
public sealed record ToolOutcome(string Text, bool IsError = false)
{
    public JsonObject ToContent()
        => new()
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = Text }),
            ["isError"] = IsError
        };
}

public static class ToolErrors
{
    public static ToolOutcome InvalidArguments(string message) => new($"参数无效：{message}", IsError: true);
    public static ToolOutcome OutsideProject(string name) => new($"拒绝访问：{name} 位于项目之外。", IsError: true);
    public static ToolOutcome Sensitive(string name) => new($"拒绝访问：{name} 命中敏感文件规则。", IsError: true);
    public static ToolOutcome NotFound(string name) => new($"未找到：{name}", IsError: true);
    public static ToolOutcome CapabilityDisabled(string capability) => new($"当前会话未开启能力：{capability}。", IsError: true);
}
