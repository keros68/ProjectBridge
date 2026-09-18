using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectBridge.CodexShim;

/// <summary>
/// Enforces the local ProjectBridge task boundary at the final hop before the
/// Codex app-server. The reverse bridge is not trusted to keep a caller-supplied
/// sandbox or working directory intact.
/// </summary>
public static class AppServerProtocolGuard
{
    public static string RewriteClientLine(string line, string projectRoot, bool allowWrite = false)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) throw new ArgumentException("项目根目录不能为空。", nameof(projectRoot));
        JsonObject? request;
        try { request = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return line; }
        if (request is null || request["method"] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method)) return line;
        if (request["params"] is not JsonObject parameters) return line;

        switch (method)
        {
            case "thread/start":
            case "thread/resume":
            case "thread/fork":
                // Current app-server schema accepts the legacy sandbox enum on
                // thread/start. Turn-start below supplies the richer policy.
                parameters["cwd"] = projectRoot;
                parameters["sandbox"] = allowWrite ? "workspace-write" : "read-only";
                parameters["approvalPolicy"] = "never";
                parameters.Remove("config");
                parameters.Remove("permissions");
                break;
            case "turn/start":
                parameters["cwd"] = projectRoot;
                parameters["approvalPolicy"] = "never";
                parameters["sandboxPolicy"] = SandboxPolicy(projectRoot, allowWrite);
                parameters.Remove("permissions");
                break;
            case "command/exec":
                parameters["cwd"] = projectRoot;
                parameters["sandboxPolicy"] = SandboxPolicy(projectRoot, allowWrite);
                parameters.Remove("env");
                parameters.Remove("permissions");
                break;
        }
        return request.ToJsonString();
    }

    private static JsonObject ReadOnlyNoNetworkPolicy() => new()
    {
        ["type"] = "readOnly",
        ["networkAccess"] = false
    };

    private static JsonObject SandboxPolicy(string root, bool allowWrite) => allowWrite ? new JsonObject
    {
        ["type"] = "workspaceWrite",
        ["writableRoots"] = new JsonArray(root),
        ["networkAccess"] = false,
        ["excludeTmpdirEnvVar"] = true,
        ["excludeSlashTmp"] = true
    } : ReadOnlyNoNetworkPolicy();
}
