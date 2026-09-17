using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Collaboration;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>网页端仅能通过访问码取得一条已明确交付给当前连接的协作请求。</summary>
public static class CollaborationTools
{
    public static IReadOnlyList<GatewayTool> Create(CollaborationStore store,
        ProjectAuthorizationRegistry registry, Guid connectionId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        if (connectionId == Guid.Empty) throw new ArgumentException("连接身份无效。", nameof(connectionId));
        return
        [
            new GatewayTool("get_collaboration_request",
                "使用本机交付的 request_id 和 access_code 读取一条协作请求；不会列出其他请求。",
                RequestSchema(includeReply: false),
                (args, _) => Task.FromResult(Invoke(() => store.ReadRemote(RequestId(args), AccessCode(args), connectionId,
                    GatewayRequestContext.ClientIdentity, registry))),
                GatewayToolAnnotations.ReadOnlyClosed),
            new GatewayTool("reply_to_collaboration_request",
                "回复一条已读取的协作请求。回复只作为建议保存，不会自动执行。",
                RequestSchema(includeReply: true),
                (args, _) => Task.FromResult(Invoke(() => store.ReplyRemote(RequestId(args), AccessCode(args), connectionId,
                    GatewayRequestContext.ClientIdentity, registry, Reply(args), TurnId(args)))),
                GatewayToolAnnotations.NonDestructiveClosed)
        ];
    }

    private static JsonObject Invoke(Func<CollaborationRequest> action)
    {
        try { return Success(action()); }
        catch (CollaborationException error) { return Error(error.Code, error.Message); }
        catch (InvalidOperationException error) { return Error("invalid_request", error.Message); }
    }

    private static JsonObject Success(CollaborationRequest request)
    {
        // Deliberately omit AccessCode, ProjectRoot, ClientId and ConversationUrl from remote responses.
        var payload = new JsonObject
        {
            ["request_id"] = request.RequestId.ToString("D"),
            ["project_id"] = request.ProjectId.ToString("D"),
            ["status"] = request.Status.ToString().ToLowerInvariant(),
            ["stage"] = request.Kind.ToString().ToLowerInvariant(),
            ["turn_id"] = request.TurnId,
            ["iteration"] = request.Iteration,
            ["source"] = request.Source,
            ["question"] = request.Question
        };
        if (request.Reply is not null)
        {
            payload["reply"] = request.Reply;
            payload["reply_origin"] = request.ReplyOrigin.ToString();
        }
        return new ToolOutcome(payload.ToJsonString()).ToContent();
    }

    private static JsonObject Error(string code, string message)
        => new ToolOutcome(new JsonObject
        {
            ["status"] = "error",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }.ToJsonString(), true).ToContent();

    private static Guid RequestId(JsonObject args)
        => args["request_id"] is JsonValue value && value.TryGetValue<string>(out var raw)
           && Guid.TryParse(raw, out var id) && id != Guid.Empty
            ? id : throw new CollaborationException("invalid_arguments", "request_id 必须是有效 UUID。");

    private static string AccessCode(JsonObject args)
        => args["access_code"] is JsonValue value && value.TryGetValue<string>(out var code)
           && !string.IsNullOrWhiteSpace(code)
            ? code : throw new CollaborationException("invalid_arguments", "access_code 不能为空。");

    private static string Reply(JsonObject args)
        => args["reply"] is JsonValue value && value.TryGetValue<string>(out var reply)
            ? reply : throw new CollaborationException("invalid_arguments", "reply 不能为空。");

    private static string TurnId(JsonObject args)
        => args["turn_id"] is JsonValue value && value.TryGetValue<string>(out var turnId)
           && !string.IsNullOrWhiteSpace(turnId)
            ? turnId : throw new CollaborationException("invalid_arguments", "turn_id 不能为空。");

    private static JsonObject RequestSchema(bool includeReply)
    {
        var properties = new JsonObject
        {
            ["request_id"] = new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = "本机交付的协作请求 UUID。" },
            ["access_code"] = new JsonObject { ["type"] = "string", ["description"] = "本机交付的访问码。" }
        };
        var required = new JsonArray("request_id", "access_code");
        if (includeReply)
        {
            properties["reply"] = new JsonObject { ["type"] = "string", ["maxLength"] = 32000, ["description"] = "给本机 Codex 的建议文本；不会自动执行。" };
            properties["turn_id"] = new JsonObject { ["type"] = "string", ["description"] = "读取请求时返回的当前轮次标识。" };
            required.Add("reply");
            required.Add("turn_id");
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }
}
