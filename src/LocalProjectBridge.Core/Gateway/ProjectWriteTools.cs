using System.Text.Json.Nodes;

namespace LocalProjectBridge.Core.Gateway;

public static class ProjectWriteTools
{
    public static IReadOnlyList<GatewayTool> Create(ProjectWriteService service, Guid connectionId)
        =>
        [
            new GatewayTool("prepare_change", "生成文本补丁、新建、重命名或删除预览，不写入项目。用 list_projects 查询项目写入模式。", PrepareSchema(),
                (args, token) => InvokeAsync(() => service.PrepareChangeAsync(
                    ProjectId(args), connectionId, GatewayRequestContext.ClientIdentity, RequestId(args),
                    args["operations"] as JsonArray ?? throw Invalid("operations 必须是数组。"), token), false, service),
                GatewayToolAnnotations.NonDestructiveClosed),
            new GatewayTool("apply_change", "提交修改。项目已开启 YOLO 时直接应用，否则等待本机确认；始终检查权限、路径和文件冲突。", ChangeRequestSchema(),
                (args, token) => InvokeAsync(() => service.ApplyChangeAsync(
                    ProjectId(args), connectionId, GatewayRequestContext.ClientIdentity, ChangeId(args), RequestId(args), token), true, service),
                GatewayToolAnnotations.MutationClosed),
            new GatewayTool("get_change", "查看修改预览、提交结果和逐文件状态。", GetSchema(),
                (args, _) => InvokeAsync(() => Task.FromResult(service.GetChange(
                    ProjectId(args), connectionId, GatewayRequestContext.ClientIdentity, ChangeId(args))), false, service),
                GatewayToolAnnotations.ReadOnlyClosed),
            new GatewayTool("restore_change", "恢复本次修改；项目已开启 YOLO 时可直接恢复，否则需本机操作。保留后续人工编辑。", ChangeRequestSchema(),
                (args, token) => InvokeAsync(() => service.RestoreRemoteAsync(
                    ProjectId(args), connectionId, GatewayRequestContext.ClientIdentity, ChangeId(args), RequestId(args), token), true, service),
                GatewayToolAnnotations.MutationClosed)
        ];

    private static async Task<JsonObject> InvokeAsync(Func<Task<ChangeRecord>> action, bool failForNonSuccess, ProjectWriteService service)
    {
        try
        {
            var record = await action().ConfigureAwait(false);
            var error = failForNonSuccess && record.Status is ChangeStatus.Partial or ChangeStatus.Interrupted
                or ChangeStatus.NeedsRecovery or ChangeStatus.RestorePartial;
            var lease = service.GetAutoApplyLease(record.ProjectId, record.ConnectionId, GatewayRequestContext.ClientIdentity);
            return Result(record, error, lease);
        }
        catch (WriteOperationException error)
        {
            return Error(error.Code, error.Message, error.Retryable, error.Action);
        }
        catch (InvalidOperationException error)
        {
            return Error("invalid_request", error.Message, false, "检查请求参数后重试。");
        }
    }

    private static JsonObject Result(ChangeRecord record, bool isError, WriteLease? lease)
    {
        var files = new JsonArray(record.Files.Select(file => (JsonNode)new JsonObject
        {
            ["operation"] = file.Operation.ToString().ToLowerInvariant(),
            ["path"] = file.Path,
            ["target_path"] = file.TargetPath,
            ["before_hash"] = file.BeforeHash,
            ["after_hash"] = file.AfterHash,
            ["status"] = file.Status.ToString(),
            ["error"] = file.Error
        }).ToArray());
        var payload = new JsonObject
        {
            ["change_id"] = record.ChangeId.ToString("D"),
            ["project_id"] = record.ProjectId.ToString("D"),
            ["policy_version"] = record.RootVersion,
            ["status"] = record.Status.ToString(),
            ["requires_deletion_confirmation"] = record.RequiresDeletionConfirmation && lease?.AllowDeletion != true,
            ["changes_require_local_confirmation"] = lease is null,
            ["auto_apply_lease_id"] = record.AutoApplyLeaseId?.ToString("D"),
            ["preview"] = record.Preview,
            ["files"] = files,
            ["error"] = record.Error
        };
        return new ToolOutcome(payload.ToJsonString(), isError).ToContent();
    }

    private static JsonObject Error(string code, string message, bool retryable, string action)
        => new ToolOutcome(new JsonObject
        {
            ["status"] = "error",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
                ["retryable"] = retryable,
                ["action"] = action
            }
        }.ToJsonString(), true).ToContent();

    private static Guid ProjectId(JsonObject args) => RequiredGuid(args, "project_id");
    private static Guid ChangeId(JsonObject args) => RequiredGuid(args, "change_id");
    private static Guid RequestId(JsonObject args) => RequiredGuid(args, "request_id");

    private static Guid RequiredGuid(JsonObject args, string name)
        => args[name]?.GetValue<string>() is { } raw && Guid.TryParse(raw, out var value) && value != Guid.Empty
            ? value : throw Invalid($"{name} 必须是有效 UUID。");

    private static WriteOperationException Invalid(string message)
        => new("invalid_arguments", message, false, "检查工具参数后重试。");

    private static JsonObject GetSchema() => Schema(false, false);
    private static JsonObject ChangeRequestSchema() => Schema(true, false);
    private static JsonObject Schema(bool requestId, bool operations)
    {
        var properties = new JsonObject
        {
            ["project_id"] = Uuid("项目 UUID。"),
            ["change_id"] = Uuid("prepare_change 返回的修改 UUID。")
        };
        var required = new JsonArray("project_id", "change_id");
        if (requestId)
        {
            properties["request_id"] = Uuid("本次逻辑请求的幂等 UUID；重试时必须复用。");
            required.Add("request_id");
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static JsonObject PrepareSchema()
        => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["project_id"] = Uuid("项目 UUID。"),
                ["request_id"] = Uuid("本次逻辑请求的幂等 UUID；重试时必须复用。"),
                ["operations"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 20,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "type 为 patch/create/rename/delete；分别使用 old_text+new_text、content、target_path。",
                        ["properties"] = new JsonObject
                        {
                            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("patch", "create", "rename", "delete") },
                            ["path"] = new JsonObject { ["type"] = "string" },
                            ["old_text"] = new JsonObject { ["type"] = "string" },
                            ["new_text"] = new JsonObject { ["type"] = "string" },
                            ["content"] = new JsonObject { ["type"] = "string" },
                            ["target_path"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray("type", "path")
                    }
                }
            },
            ["required"] = new JsonArray("project_id", "request_id", "operations")
        };

    private static JsonObject Uuid(string description) => new()
    {
        ["type"] = "string", ["format"] = "uuid", ["description"] = description
    };
}
