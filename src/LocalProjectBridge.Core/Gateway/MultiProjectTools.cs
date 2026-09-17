using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Gateway;

public sealed record ProjectAccessedEventArgs(Guid ProjectId, DateTimeOffset At);

/// <summary>
/// Thread-safe snapshot of the projects that the web connection may read.
/// The registry deliberately has no "current project": every read is routed by a
/// caller-supplied project id and is checked again immediately before it runs.
/// </summary>
public sealed class ProjectAuthorizationRegistry
{
    private sealed record Snapshot(long Version, IReadOnlyDictionary<Guid, ProjectRecord> Projects,
        IReadOnlyDictionary<Guid, long> TaskVersions, IReadOnlyDictionary<Guid, long> WriteVersions);

    private Snapshot _snapshot = new(0, new Dictionary<Guid, ProjectRecord>(), new Dictionary<Guid, long>(), new Dictionary<Guid, long>());
    public event EventHandler<ProjectAccessedEventArgs>? ProjectAccessed;

    /// <summary>Replace the complete authorization set with a cloned snapshot.</summary>
    public void ReplaceProjects(IEnumerable<ProjectRecord> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var copied = new Dictionary<Guid, ProjectRecord>();
        foreach (var project in projects)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (!copied.TryAdd(project.Id, Clone(project)))
                throw new InvalidOperationException($"项目 id 重复：{project.Id:D}");
        }

        lock (this)
        {
            var previous = Volatile.Read(ref _snapshot);
            if (copied.Count == previous.Projects.Count && copied.All(pair => previous.Projects.TryGetValue(pair.Key, out var old)
                && old.Path == pair.Value.Path && old.Name == pair.Value.Name
                && old.AllowWebRead == pair.Value.AllowWebRead
                && old.AllowCodexTasks == pair.Value.AllowCodexTasks
                && old.AllowCodexWrite == pair.Value.AllowCodexWrite
                && old.AllowCodexAskWeb == pair.Value.AllowCodexAskWeb)) return;
            var taskVersions = copied.ToDictionary(pair => pair.Key, pair =>
                previous.Projects.TryGetValue(pair.Key, out var old) && old.Path == pair.Value.Path
                    && old.AllowCodexTasks == pair.Value.AllowCodexTasks
                    && old.AllowCodexWrite == pair.Value.AllowCodexWrite
                    ? previous.TaskVersions[pair.Key] : previous.Version + 1);
            var writeVersions = copied.ToDictionary(pair => pair.Key, pair =>
                previous.Projects.TryGetValue(pair.Key, out var old) && old.Path == pair.Value.Path
                    && old.AllowWebRead == pair.Value.AllowWebRead
                    ? previous.WriteVersions.GetValueOrDefault(pair.Key, previous.Version) : previous.Version + 1);
            Volatile.Write(ref _snapshot, new Snapshot(previous.Version + 1, copied, taskVersions, writeVersions));
        }
    }

    internal bool TryGetReadableProject(Guid projectId, out ProjectRecord project, out long version)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        version = snapshot.Version;
        if (snapshot.Projects.TryGetValue(projectId, out var found) && found.AllowWebRead)
        {
            project = Clone(found);
            return true;
        }

        project = null!;
        return false;
    }

    internal bool TryGetDelegableProject(Guid projectId, out ProjectRecord project, out long version)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        version = snapshot.TaskVersions.GetValueOrDefault(projectId);
        if (snapshot.Projects.TryGetValue(projectId, out var found) && found.AllowCodexTasks)
        {
            project = Clone(found);
            return true;
        }

        project = null!;
        return false;
    }

    public bool TryGetWriteProject(Guid projectId, out ProjectRecord project, out long rootVersion)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        rootVersion = snapshot.WriteVersions.GetValueOrDefault(projectId);
        if (snapshot.Projects.TryGetValue(projectId, out var found) && found.AllowWebRead)
        {
            project = Clone(found);
            return true;
        }
        project = null!;
        return false;
    }

    /// <summary>
    /// Reject a result if any registry replacement happened while the tool ran.
    /// This prevents a read that started before a revoke from being returned after it.
    /// </summary>
    internal bool IsStillReadable(Guid projectId, long version)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Version == version
            && snapshot.Projects.TryGetValue(projectId, out var project)
            && project.AllowWebRead;
    }

    internal bool IsStillDelegable(Guid projectId, long version)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TaskVersions.GetValueOrDefault(projectId) == version
            && snapshot.Projects.TryGetValue(projectId, out var project)
            && project.AllowCodexTasks;
    }

    public bool IsStillWriteProject(Guid projectId, long rootVersion)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.WriteVersions.GetValueOrDefault(projectId) == rootVersion
            && snapshot.Projects.TryGetValue(projectId, out var project)
            && project.AllowWebRead;
    }

    internal IReadOnlyList<ProjectRecord> ReadableProjects()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Projects.Values
            .Where(project => project.AllowWebRead)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
    }

    internal IReadOnlyList<ProjectRecord> AuthorizedProjects()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Projects.Values
            .Where(project => project.AllowWebRead || project.AllowCodexTasks)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
    }

    internal IReadOnlyList<ProjectRecord> DelegableProjects()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Projects.Values
            .Where(project => project.AllowCodexTasks)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToArray();
    }

    internal void RecordSuccessfulAccess(Guid projectId)
        => ProjectAccessed?.Invoke(this, new ProjectAccessedEventArgs(projectId, DateTimeOffset.Now));

    private static ProjectRecord Clone(ProjectRecord source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Path = source.Path,
        AllowWebRead = source.AllowWebRead,
        AllowCodexTasks = source.AllowCodexTasks,
        AllowCodexWrite = source.AllowCodexWrite,
        AllowCodexAskWeb = source.AllowCodexAskWeb,
        LastConnectedAt = source.LastConnectedAt,
        LastAccessedAt = source.LastAccessedAt,
        LastFault = source.LastFault
    };
}

/// <summary>
/// A fixed, multi-project MCP read-tool surface. Unlike <see cref="ProjectReadTools"/>,
/// each project-specific call must include an explicit <c>project_id</c>.
/// </summary>
public static class MultiProjectTools
{
    private const string ProjectId = "project_id";

    public static IReadOnlyList<GatewayTool> Create(ProjectAuthorizationRegistry authorizations, CommandRunner runner,
        ProjectWriteService? writeService = null, Guid? connectionId = null)
    {
        ArgumentNullException.ThrowIfNull(authorizations);
        ArgumentNullException.ThrowIfNull(runner);

        return
        [
            new GatewayTool("list_projects", "列出已授权网页读取的项目及其 project_id。", EmptySchema(),
                (_, _) => Task.FromResult(ListProjects(authorizations, writeService, connectionId)),
                GatewayToolAnnotations.ReadOnlyClosed),
            RoutedTool("project_info", "返回指定项目的基本信息与本次授权能力。", EmptySchema(), authorizations, runner),
            RoutedTool("list_directory", "列出指定项目中的目录内容（相对路径，默认根目录）。",
                Schema(("path", "string", "目录相对路径，空字符串表示项目根目录")), authorizations, runner),
            RoutedTool("read_text_file", "读取指定项目中的文本文件（相对路径）。",
                Schema(("path", "string", "文件相对路径"), ("max_lines", "integer", "最多返回行数，默认 800")), authorizations, runner),
            RoutedTool("search_files", "在指定项目中搜索文本。",
                Schema(("query", "string", "要搜索的文本"), ("glob", "string", "文件名通配符，如 *.rs，默认 *"), ("max_results", "integer", "最多返回条数，默认 50")), authorizations, runner),
            RoutedTool("git_status", "返回指定项目的 Git 状态（porcelain 格式）。", EmptySchema(), authorizations, runner),
            RoutedTool("git_diff", "返回指定项目的 Git 差异。",
                Schema(("staged", "boolean", "是否返回暂存区差异")), authorizations, runner)
        ];
    }

    private static GatewayTool RoutedTool(
        string name,
        string description,
        JsonObject schema,
        ProjectAuthorizationRegistry authorizations,
        CommandRunner runner)
        => new(name, description, AddProjectId(schema),
            (arguments, cancellationToken) => InvokeRoutedAsync(name, arguments, authorizations, runner, cancellationToken),
            GatewayToolAnnotations.ReadOnlyClosed);

    private static async Task<JsonObject> InvokeRoutedAsync(
        string name,
        JsonObject arguments,
        ProjectAuthorizationRegistry authorizations,
        CommandRunner runner,
        CancellationToken cancellationToken)
    {
        if (arguments[ProjectId] is not JsonValue idValue
            || !idValue.TryGetValue<string>(out var rawId)
            || !Guid.TryParse(rawId, out var projectId)
            || projectId == Guid.Empty)
            return ToolErrors.InvalidArguments("project_id 必须是有效的项目 UUID。").ToContent();

        if (!authorizations.TryGetReadableProject(projectId, out var project, out var authorizationVersion))
            return AuthorizationDenied();

        SessionPolicy policy;
        try
        {
            // Always construct a new policy from the authorization snapshot. No UI selection
            // or prior session is trusted as a routing source.
            policy = SessionPolicyFactory.Create(project, CapabilityFlags.WebRead);
        }
        catch (Exception error)
        {
            return new ToolOutcome($"项目配置无效，无法执行读取：{error.Message}", IsError: true).ToContent();
        }

        var forwarded = (JsonObject)arguments.DeepClone();
        forwarded.Remove(ProjectId);
        JsonObject result;
        try
        {
            var target = ProjectReadTools.Create(policy, runner).Single(tool => tool.Name == name);
            result = await target.Invoke(forwarded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        // A disabled/deleted project must not receive a result that completed concurrently
        // with ReplaceProjects. Version comparison also handles a path or policy replacement.
        if (!authorizations.IsStillReadable(projectId, authorizationVersion)) return AuthorizationDenied();
        if (!IsError(result)) authorizations.RecordSuccessfulAccess(projectId);
        return result;
    }

    private static JsonObject ListProjects(ProjectAuthorizationRegistry authorizations,
        ProjectWriteService? writeService, Guid? connectionId)
    {
        var projects = new JsonArray();
        foreach (var project in authorizations.AuthorizedProjects())
        {
            var capabilities = CapabilityFlags.None;
            if (project.AllowWebRead) capabilities |= CapabilityFlags.WebRead;
            if (project.AllowCodexTasks) capabilities |= CapabilityFlags.WebDelegateCodex;
            if (project.AllowCodexTasks && project.AllowCodexWrite) capabilities |= CapabilityFlags.WebDelegateCodexWrite;
            var writeLease = writeService is not null && connectionId is { } id
                ? writeService.GetAutoApplyLease(project.Id, id, GatewayRequestContext.ClientIdentity) : null;
            if (writeLease is not null)
                capabilities |= CapabilityFlags.WebWrite;
            projects.Add(new JsonObject
            {
                [ProjectId] = project.Id.ToString("D"),
                ["name"] = project.Name,
                ["capabilities"] = capabilities.ToString(),
                ["can_prepare_changes"] = project.AllowWebRead && writeService is not null,
                ["changes_require_local_confirmation"] = writeLease is null,
                ["write_mode"] = writeLease is null ? "local_confirmation" : "auto_apply",
                ["auto_apply_expires_at"] = writeLease?.ExpiresAt.ToString("O"),
                ["deletion_requires_local_confirmation"] = writeLease?.AllowDeletion != true,
                ["codex_task_mode"] = !project.AllowCodexTasks ? "disabled" : project.AllowCodexWrite ? "workspace-write" : "read-only",
                ["collaboration_enabled"] = project.AllowWebRead && project.AllowCodexAskWeb
            });
        }
        return Success(new JsonObject { ["projects"] = projects });
    }

    private static JsonObject AuthorizationDenied()
        => new ToolOutcome("项目不存在，或未授权网页读取。", IsError: true).ToContent();

    private static JsonObject EmptySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };

    private static JsonObject Schema(params (string Name, string Type, string Description)[] properties)
    {
        var schemaProperties = new JsonObject();
        foreach (var (name, type, description) in properties)
        {
            schemaProperties[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description
            };
        }
        return new JsonObject { ["type"] = "object", ["properties"] = schemaProperties };
    }

    private static JsonObject AddProjectId(JsonObject schema)
    {
        var routed = (JsonObject)schema.DeepClone();
        var properties = routed["properties"]!.AsObject();
        properties[ProjectId] = new JsonObject
        {
            ["type"] = "string",
            ["format"] = "uuid",
            ["description"] = "要访问的已授权项目 UUID；请先通过 list_projects 获取。"
        };
        routed["required"] = new JsonArray(ProjectId);
        return routed;
    }

    private static JsonObject Success(JsonObject payload) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
        ["isError"] = false
    };

    private static bool IsError(JsonObject result)
        => result["isError"] is JsonValue value
           && value.TryGetValue<bool>(out var isError)
           && isError;
}
