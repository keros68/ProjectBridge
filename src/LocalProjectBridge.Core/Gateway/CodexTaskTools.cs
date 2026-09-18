using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>
/// Codex 任务桥：统一网关把任务工具转发给 Transceiver reverse-bridge（MCP）。
/// 上游工具名以映射表维护，待上游协议固定后在配置中调整（设计文档 7.1）。
/// </summary>
public interface ICodexTaskBridge
{
    Task<JsonObject?> CallToolAsync(string bridgeTool, JsonObject arguments, CancellationToken cancellationToken);
}

public sealed class HttpCodexTaskBridge : ICodexTaskBridge
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _bridgeUrl;

    public HttpCodexTaskBridge(string bridgeUrl) => _bridgeUrl = bridgeUrl.TrimEnd('/');

    public async Task<JsonObject?> CallToolAsync(string bridgeTool, JsonObject arguments, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Random.Shared.Next(1, int.MaxValue),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = bridgeTool,
                ["arguments"] = arguments.DeepClone()
            }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _bridgeUrl)
        {
            Content = new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var response = await Http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var payload = ExtractJsonPayload(body, response.Content.Headers.ContentType?.MediaType);
        var root = JsonNode.Parse(payload) as JsonObject
            ?? throw new CodexTaskBridgeException("Codex 任务组件返回了无效响应。");
        if (root["error"] is JsonObject error)
            throw new CodexTaskBridgeException(error["message"]?.GetValue<string>() ?? "Codex 任务组件拒绝了请求。");
        return root["result"] as JsonObject;
    }

    public static string ExtractJsonPayload(string body, string? mediaType)
    {
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)) return body;
        var data = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line[5..].Trim())
            .LastOrDefault(line => line.Length > 0 && line != "[DONE]");
        return data ?? throw new CodexTaskBridgeException("Codex 任务组件返回了空事件流。");
    }
}

public sealed class CodexTaskBridgeException(string message) : Exception(message);

/// <summary>网关侧任务工具（设计 7.1：任务只能读取当前项目；只读、禁网由会话策略固定）。</summary>
public static class CodexTaskTools
{
    /// <summary>网关工具名 → Transceiver bridge 工具名。</summary>
    public static readonly Dictionary<string, string> BridgeToolMap = new(StringComparer.Ordinal)
    {
        ["codex_task_start"] = "codex_task",
        ["codex_task_status"] = "codex_status",
        ["codex_task_stop"] = "codex_cancel"
    };

    public static IReadOnlyList<GatewayTool> Create(SessionPolicy policy, ICodexTaskBridge bridge)
    {
        if (!policy.Allows(CapabilityFlags.WebDelegateCodex))
            throw new InvalidOperationException("会话未开启“网页委派 Codex”能力。");
        return [CreateStart(policy, bridge), CreateStatus(policy, bridge), CreateStop(policy, bridge)];
    }

    private static GatewayTool CreateStart(SessionPolicy policy, ICodexTaskBridge bridge) => new(
        "codex_task_start",
        "创建或继续一个只读、禁网的本地 Codex 任务。",
        ObjectSchema(
            required: ["prompt"],
            ("prompt", StringSchema("交给 Codex 的任务说明。")),
            ("request_id", UuidSchema("同一逻辑请求重试时复用；省略则由网关生成。")),
            ("activity_id", UuidSchema("继续已有 Activity 时使用。")),
            ("activity_title", StringSchema("新 Activity 的标题。")),
            ("execution_mode", EnumSchema("auto", "foreground", "background")),
            ("session_mode", EnumSchema("auto", "new", "continue")),
            ("thread_id", StringSchema("继续指定 Codex 任务时的准确 thread id。"))),
        async (arguments, cancellationToken) =>
        {
            if (arguments["prompt"] is not JsonValue promptValue
                || !promptValue.TryGetValue<string>(out var prompt)
                || string.IsNullOrWhiteSpace(prompt))
                return ToolErrors.InvalidArguments("prompt 不能为空。").ToContent();
            var upstream = new JsonObject
            {
                ["requestId"] = GetString(arguments, "request_id") ?? Guid.NewGuid().ToString("D"),
                ["prompt"] = BuildProvenanceEnvelope(policy, prompt),
                ["cwd"] = policy.CanonicalProjectPath,
                ["sandbox"] = "read-only"
            };
            Copy(arguments, upstream, "activity_id", "activityId");
            Copy(arguments, upstream, "activity_title", "activityTitle");
            Copy(arguments, upstream, "execution_mode", "executionMode");
            Copy(arguments, upstream, "session_mode", "sessionMode");
            Copy(arguments, upstream, "thread_id", "threadId");
            return await CallAsync(bridge, BridgeToolMap["codex_task_start"], upstream, cancellationToken).ConfigureAwait(false);
        }, GatewayToolAnnotations.NonIdempotentClosed);

    private static GatewayTool CreateStatus(SessionPolicy policy, ICodexTaskBridge bridge) => new(
        "codex_task_status",
        "查询当前会话中的 Codex 任务状态或等待终态。",
        ObjectSchema(
            required: [],
            ("job_id", StringSchema("codex_task_start 返回的 job id。")),
            ("activity_id", UuidSchema("准确的 Activity id。")),
            ("thread_id", StringSchema("准确的 Codex thread id。")),
            ("wait_for", EnumSchema("change", "terminal")),
            ("wait_ms", IntegerSchema(1, 60_000))),
        async (arguments, cancellationToken) =>
        {
            var upstream = new JsonObject();
            Copy(arguments, upstream, "job_id", "jobId");
            Copy(arguments, upstream, "activity_id", "activityId");
            Copy(arguments, upstream, "thread_id", "threadId");
            Copy(arguments, upstream, "wait_for", "waitFor");
            Copy(arguments, upstream, "wait_ms", "waitMs");
            return await CallAsync(bridge, BridgeToolMap["codex_task_status"], upstream, cancellationToken).ConfigureAwait(false);
        }, GatewayToolAnnotations.ReadOnlyClosed);

    private static GatewayTool CreateStop(SessionPolicy policy, ICodexTaskBridge bridge) => new(
        "codex_task_stop",
        "强制停止一个活动中的 Codex 任务；可能保留部分文件系统变化。",
        ObjectSchema(
            required: ["job_id"],
            ("job_id", StringSchema("要停止的准确 job id。")),
            ("expected_version", IntegerSchema(1, int.MaxValue))),
        async (arguments, cancellationToken) =>
        {
            var jobId = GetString(arguments, "job_id");
            if (string.IsNullOrWhiteSpace(jobId)) return ToolErrors.InvalidArguments("job_id 不能为空。").ToContent();
            var upstream = new JsonObject { ["jobId"] = jobId };
            Copy(arguments, upstream, "expected_version", "expectedVersion");
            return await CallAsync(bridge, BridgeToolMap["codex_task_stop"], upstream, cancellationToken).ConfigureAwait(false);
        }, GatewayToolAnnotations.DestructiveClosed);

    private static async Task<JsonObject> CallAsync(
        ICodexTaskBridge bridge, string tool, JsonObject arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bridge.CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            if (result is null) return new ToolOutcome("Codex 任务组件返回了空结果。", IsError: true).ToContent();
            if (result["content"] is JsonArray) return (JsonObject)result.DeepClone();
            return new ToolOutcome(result.ToJsonString()).ToContent();
        }
        catch (HttpRequestException error)
        {
            return new ToolOutcome($"Codex 任务组件不可达：{error.Message}", IsError: true).ToContent();
        }
        catch (TaskCanceledException)
        {
            return new ToolOutcome("Codex 任务组件响应超时。", IsError: true).ToContent();
        }
        catch (CodexTaskBridgeException error)
        {
            return new ToolOutcome($"Codex 任务组件拒绝请求：{error.Message}", IsError: true).ToContent();
        }
    }

    private static string BuildProvenanceEnvelope(SessionPolicy policy, string prompt)
        => $"|from_chatgpt|:\nproject_id: {policy.ProjectId:D}\nsession_id: {policy.SessionId:D}\nsource: chatgpt-web\n\n{prompt.Trim()}";

    private static void Copy(JsonObject source, JsonObject target, string sourceName, string targetName)
    {
        if (source[sourceName] is { } value) target[targetName] = value.DeepClone();
    }

    private static string? GetString(JsonObject source, string name)
        => source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject ObjectSchema(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var propertyObject = new JsonObject();
        foreach (var property in properties) propertyObject[property.Name] = property.Schema;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = propertyObject, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        return schema;
    }

    private static JsonObject StringSchema(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject UuidSchema(string description) => new()
    {
        ["type"] = "string", ["description"] = description,
        ["pattern"] = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
    };
    private static JsonObject EnumSchema(params string[] values)
        => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
    private static JsonObject IntegerSchema(int minimum, int maximum)
        => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
}
