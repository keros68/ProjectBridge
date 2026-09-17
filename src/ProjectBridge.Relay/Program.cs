using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Collaboration;
using LocalProjectBridge.Core.Gateway;

Console.OutputEncoding = Encoding.UTF8;
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
try
{
    if (args.Length == 0 || args[0] is "help" or "--help")
    {
        Console.WriteLine("""
        ProjectBridge Codex 协作助手
          projects
          begin --project <ID或完整目录> --goal-file <UTF-8文件> --source <codex://threads/...> --stage <plan|review> [--parent <规划UUID>] [--iteration <0-11>] [--max-iterations <1-12>] [--id <UUID>]
          current --project <ID或完整目录> --source <codex://threads/...>
          intent --id <UUID> --conversation <精确ChatGPT对话URL>
          sent --id <UUID>
          bind --id <UUID> --confirmed-project-id <UUID>
          accept-reply --id <UUID> --turn-id <轮次> --project-id <UUID> --conversation <精确ChatGPT对话URL> --reply-file <UTF-8文件>
          checkpoint --id <UUID> --state <Init|PlanReceived|Executing|ExecutedLocal|ExecutedSent|Done|Blocked> [--note <短说明>]
          get --id <UUID> [--wait-seconds <0-60>]
          cancel --id <UUID>
          ask --project <ID或完整目录> --question-file <UTF-8文件> [--source <来源>] [--conversation <URL>] [--id <UUID>]
        begin/current/intent/sent/accept-reply/bind/checkpoint 供随包 Codex Skill 驱动自动规划与复核。
        记录发送意图后必须先核对原对话；不要因超时重复发送。
        """);
        return 0;
    }

    var options = ParseOptions(args);
    ValidateOptions(args[0], options.Keys);
    string Required(string key) => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"缺少 {key}。");

    var registry = new RegistryStore();
    var settings = registry.Load();
    var connection = registry.LoadConnection();
    var authorizations = new ProjectAuthorizationRegistry();
    authorizations.ReplaceProjects(settings.Projects);
    using var store = new CollaborationStore(Path.Combine(registry.AppDataDirectory, "collaboration"));
    ProjectRecord Project(string value)
    {
        var project = Guid.TryParse(value, out var projectId)
            ? settings.Projects.SingleOrDefault(item => item.Id == projectId)
            : settings.Projects.SingleOrDefault(item => string.Equals(
                Path.GetFullPath(item.Path).TrimEnd('\\', '/'),
                Path.GetFullPath(value).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        return project is { AllowWebRead: true, AllowCodexAskWeb: true }
            ? project
            : throw new InvalidOperationException("项目未启用协作。请在 ProjectBridge 中添加项目并开启协作。");
    }

    object output;
    switch (args[0])
    {
        case "projects":
            output = settings.Projects.Where(project => project.AllowWebRead && project.AllowCodexAskWeb)
                .Select(project => new { project_id = project.Id, name = project.Name, path = project.Path }).ToArray();
            break;
        case "ask":
        case "begin":
        {
            var project = Project(Required("--project"));
            var inputPath = Required(args[0] == "ask" ? "--question-file" : "--goal-file");
            var question = ReadInput(inputPath, 16_000);
            var id = options.TryGetValue("--id", out var rawId) ? Guid.Parse(rawId) : (Guid?)null;
            var source = options.GetValueOrDefault("--source", "ProjectBridge CLI");
            var kind = CollaborationKind.General;
            Guid? parent = null;
            var iteration = 0;
            var maxIterations = 4;
            if (args[0] == "begin")
            {
                kind = Required("--stage").ToLowerInvariant() switch
                {
                    "plan" => CollaborationKind.Plan,
                    "review" => CollaborationKind.Review,
                    _ => throw new ArgumentException("--stage 只支持 plan 或 review。")
                };
                if (options.TryGetValue("--parent", out var rawParent)) parent = Guid.Parse(rawParent);
                if (options.TryGetValue("--iteration", out var rawIteration)) iteration = int.Parse(rawIteration);
                if (options.TryGetValue("--max-iterations", out var rawMax)) maxIterations = int.Parse(rawMax);
            }
            var request = store.Submit(project, connection.Id, question, source,
                options.GetValueOrDefault("--conversation"), id, kind, parent, iteration, maxIterations);
            output = RequestOutput(request, includePrompt: true);
            break;
        }
        case "current":
        {
            var project = Project(Required("--project"));
            var current = store.FindCurrent(project.Id, connection.Id, Required("--source"));
            object? currentOutput = current is null ? null : RequestOutput(current, includePrompt: true);
            output = new { current = currentOutput };
            break;
        }
        case "intent":
            output = RequestOutput(store.RecordSendIntent(Guid.Parse(Required("--id")), Required("--conversation")), true);
            break;
        case "sent":
            output = RequestOutput(store.ConfirmMessageSent(Guid.Parse(Required("--id"))), false);
            break;
        case "bind":
            output = store.ConfirmConversationBinding(
                Guid.Parse(Required("--id")), Guid.Parse(Required("--confirmed-project-id")));
            break;
        case "accept-reply":
            output = RequestOutput(store.AcceptBrowserVisibleReply(
                Guid.Parse(Required("--id")), Required("--turn-id"), Guid.Parse(Required("--project-id")),
                connection.Id, Required("--conversation"), authorizations,
                ReadInput(Required("--reply-file"), 32_000)), false);
            break;
        case "checkpoint":
            output = RequestOutput(store.SetCheckpoint(
                Guid.Parse(Required("--id")),
                Enum.Parse<CollaborationCheckpoint>(Required("--state"), ignoreCase: true),
                options.GetValueOrDefault("--note")), false);
            break;
        case "get":
        {
            var id = Guid.Parse(Required("--id"));
            var wait = options.TryGetValue("--wait-seconds", out var rawWait) ? int.Parse(rawWait) : 0;
            if (wait is < 0 or > 60) throw new ArgumentException("等待时间须为 0–60 秒。");
            var until = DateTimeOffset.UtcNow.AddSeconds(wait);
            CollaborationRequest current;
            do
            {
                current = store.Get(id) ?? throw new InvalidOperationException("未找到请求。");
                if (current.Status is not (CollaborationStatus.Pending or CollaborationStatus.Read)
                    || DateTimeOffset.UtcNow >= until) break;
                await Task.Delay(500);
            } while (true);
            output = RequestOutput(current, false);
            break;
        }
        default:
        {
            var id = Guid.Parse(Required("--id"));
            output = RequestOutput(store.Cancel(id), false);
            break;
        }
    }

    Console.WriteLine(JsonSerializer.Serialize(output, json));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = error.Message }, json));
    return 1;
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 1; index < arguments.Length; index += 2)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length
            || !options.TryAdd(arguments[index], arguments[index + 1]))
            throw new ArgumentException("选项格式无效；运行 --help 查看用法。");
    }
    return options;
}

static void ValidateOptions(string command, IEnumerable<string> keys)
{
    string[] allowed = command switch
    {
        "projects" => [],
        "ask" => ["--project", "--question-file", "--source", "--conversation", "--id"],
        "begin" => ["--project", "--goal-file", "--source", "--stage", "--parent", "--iteration", "--max-iterations", "--id"],
        "current" => ["--project", "--source"],
        "intent" => ["--id", "--conversation"],
        "sent" => ["--id"],
        "bind" => ["--id", "--confirmed-project-id"],
        "accept-reply" => ["--id", "--turn-id", "--project-id", "--conversation", "--reply-file"],
        "checkpoint" => ["--id", "--state", "--note"],
        "get" => ["--id", "--wait-seconds"],
        "cancel" => ["--id"],
        _ => throw new ArgumentException("未知操作；运行 --help 查看用法。")
    };
    if (keys.Any(key => !allowed.Contains(key)))
        throw new ArgumentException("包含当前操作不支持的选项。");
}

static string ReadInput(string path, int maxCharacters)
{
    var file = new FileInfo(path);
    if (!file.Exists) throw new FileNotFoundException("输入文件不存在。", path);
    if (file.Length > maxCharacters * 4L + 3) throw new ArgumentException($"输入文件超过 {maxCharacters} 字符上限。");
    var value = File.ReadAllText(path, new UTF8Encoding(false, true));
    if (value.Trim().Length > maxCharacters) throw new ArgumentException($"输入正文超过 {maxCharacters} 字符上限。");
    return value;
}

static object RequestOutput(CollaborationRequest request, bool includePrompt) => new
{
    request_id = request.RequestId,
    project_id = request.ProjectId,
    source = request.Source,
    stage = request.Kind,
    status = request.Status,
    delivery_state = request.DeliveryState,
    checkpoint = request.Checkpoint,
    turn_id = request.TurnId,
    parent_request_id = request.ParentRequestId,
    iteration = request.Iteration,
    max_iterations = request.MaxIterations,
    conversation_url = request.ConversationUrl,
    conversation_verified = request.ConversationVerifiedAt is not null,
    reply = request.Reply,
    reply_origin = request.ReplyOrigin,
    expires_at = request.ExpiresAt,
    prompt = includePrompt ? CollaborationStore.BuildPrompt(request) : null,
    resume_action = ResumeAction(request)
};

static string ResumeAction(CollaborationRequest request) => request switch
{
    { Checkpoint: CollaborationCheckpoint.Done } => "completed_summarize_without_restarting",
    { Checkpoint: CollaborationCheckpoint.Blocked } => "blocked_report_reason_and_wait_for_explicit_resume",
    { Checkpoint: CollaborationCheckpoint.PlanReceived } => "execute_reviewed_plan",
    { Checkpoint: CollaborationCheckpoint.Executing } => "inspect_files_and_tests_then_resume_execution",
    { Checkpoint: CollaborationCheckpoint.ExecutedLocal } => "create_or_recover_review_only",
    { Checkpoint: CollaborationCheckpoint.ExecutedSent } => "wait_for_review_without_resending",
    { Status: CollaborationStatus.Answered } => "consume_reply",
    { DeliveryState: CollaborationDeliveryState.NotStarted } => "open_or_create_exact_chat_then_record_intent",
    { DeliveryState: CollaborationDeliveryState.IntentRecorded } => "inspect_original_chat_before_sending_or_confirming",
    { DeliveryState: CollaborationDeliveryState.MessageConfirmed } => "wait_for_reply_without_resending",
    _ => "stop"
};
