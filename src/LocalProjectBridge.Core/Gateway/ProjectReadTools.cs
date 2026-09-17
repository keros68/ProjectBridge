using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LocalProjectBridge.Core.Security;
using LocalProjectBridge.Core.Sessions;

namespace LocalProjectBridge.Core.Gateway;

/// <summary>
/// ProjectReader 工具集（设计文档 6.7）：工作区信息、目录、读取、搜索、Git 状态与差异。
/// 所有访问以 SessionPolicy 为唯一权限来源；路径逃逸与敏感文件规则在工具层强制执行。
/// </summary>
public static class ProjectReadTools
{
    private const long MaxReadBytes = 256 * 1024;
    private const long MaxScanFileBytes = 2 * 1024 * 1024;
    private const int MaxListEntries = 2000;
    private const int MaxSearchResults = 80;
    private const int MaxGitOutputChars = 120_000;

    public static IReadOnlyList<GatewayTool> Create(SessionPolicy policy, CommandRunner runner)
    {
        if (!policy.Allows(CapabilityFlags.WebRead))
            throw new InvalidOperationException("会话未开启“网页读取项目”能力。");
        var ignoreRules = SensitiveFilePolicy.LoadIgnoreRules(policy.CanonicalProjectPath);
        return new GatewayTool[]
        {
            CreateProjectInfo(policy),
            CreateListDirectory(policy, ignoreRules),
            CreateReadTextFile(policy, ignoreRules),
            CreateSearchFiles(policy, ignoreRules),
            CreateGitStatus(policy, runner, ignoreRules),
            CreateGitDiff(policy, runner, ignoreRules)
        }.Select(tool => tool with { Annotations = GatewayToolAnnotations.ReadOnlyClosed }).ToArray();
    }

    private static GatewayTool CreateProjectInfo(SessionPolicy policy) => new(
        "project_info",
        "返回当前连接项目的基本信息（名称与本次授权能力，不包含目录结构）。",
        Schema(),
        (_, _) => Task.FromResult(ToToolOutcome(new JsonObject
        {
            ["name"] = policy.ProjectDisplayName,
            ["capabilities"] = policy.EnabledCapabilities.ToString(),
            ["session_id"] = policy.SessionId.ToString("D")
        })));

    private static GatewayTool CreateListDirectory(SessionPolicy policy, IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules) => new(
        "list_directory",
        "列出项目中某个目录的内容（相对路径，默认根目录）。",
        Schema(("path", "string", "目录相对路径，空字符串表示项目根目录")),
        (arguments, _) =>
        {
            var relative = GetRelativePath(arguments);
            var directory = ResolveDirectory(policy, ignoreRules, relative);
            if (directory is null) return Task.FromResult(ToolErrors.NotFound(relative).ToContent());
            var entries = new JsonArray();
            var count = 0;
            foreach (var entry in directory.EnumerateFileSystemInfos()
                         .OrderBy(info => info is DirectoryInfo ? 0 : 1)
                         .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entryRelative = NormalizeRelative(Path.GetRelativePath(policy.CanonicalProjectPath, entry.FullName));
                if (SensitiveFilePolicy.IsSensitiveRelative(entryRelative, ignoreRules)) continue;
                if (count++ >= MaxListEntries) break;
                entries.Add(new JsonObject
                {
                    ["name"] = entry.Name,
                    ["type"] = entry is DirectoryInfo ? "directory" : "file",
                    ["path"] = entryRelative
                });
            }
            return Task.FromResult(ToToolOutcome(new JsonObject
            {
                ["path"] = relative,
                ["truncated"] = count >= MaxListEntries,
                ["entries"] = entries
            }));
        });

    private static GatewayTool CreateReadTextFile(SessionPolicy policy, IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules) => new(
        "read_text_file",
        "读取项目中的文本文件（相对路径）。超过 256KB 或疑似二进制的文件会被拒绝。",
        Schema(("path", "string", "文件相对路径"), ("max_lines", "integer", "最多返回行数，默认 800")),
        (arguments, _) =>
        {
            var relative = GetRelativePath(arguments);
            if (relative.Length == 0) return Task.FromResult(ToolErrors.InvalidArguments("path 不能为空。").ToContent());
            var fullPath = ResolveInsidePath(policy, relative);
            if (fullPath is null || !File.Exists(fullPath)) return Task.FromResult(ToolErrors.NotFound(relative).ToContent());
            if (SensitiveFilePolicy.IsSensitiveRelative(relative, ignoreRules))
                return Task.FromResult(ToolErrors.Sensitive(relative).ToContent());
            var info = new FileInfo(fullPath);
            if (info.Length > MaxReadBytes)
                return Task.FromResult(new ToolOutcome($"文件过大（{info.Length} 字节，上限 {MaxReadBytes}）。", IsError: true).ToContent());
            var maxLines = arguments["max_lines"] is JsonValue linesValue && linesValue.TryGetValue<int>(out var parsed) && parsed > 0
                ? Math.Min(parsed, 5000)
                : 800;
            using var stream = info.OpenRead();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = new StringBuilder();
            var lines = 0;
            int character;
            var reachedEnd = false;
            while (lines < maxLines && (character = reader.Peek()) >= 0)
            {
                var line = reader.ReadLine();
                if (line is null) { reachedEnd = true; break; }
                if (line.Contains('\0'))
                    return Task.FromResult(new ToolOutcome("文件疑似二进制，已拒绝读取。", IsError: true).ToContent());
                content.AppendLine(line);
                lines++;
            }
            reachedEnd = reachedEnd || reader.Peek() < 0;
            return Task.FromResult(ToToolOutcome(new JsonObject
            {
                ["path"] = relative,
                ["truncated"] = !reachedEnd,
                ["lines"] = lines,
                ["content"] = content.ToString()
            }));
        });

    private static GatewayTool CreateSearchFiles(SessionPolicy policy, IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules) => new(
        "search_files",
        "在项目文件内容中搜索文本（跳过敏感文件、.git 与 2MB 以上文件），返回命中的文件与行。",
        Schema(("query", "string", "要搜索的文本"), ("glob", "string", "文件名通配符，如 *.rs，默认 *"), ("max_results", "integer", "最多返回条数，默认 50")),
        (arguments, cancellationToken) =>
        {
            if (arguments["query"] is not JsonValue queryValue || !queryValue.TryGetValue<string>(out var query) || query.Length == 0)
                return Task.FromResult(ToolErrors.InvalidArguments("query 不能为空。").ToContent());
            var glob = arguments["glob"] is JsonValue globValue && globValue.TryGetValue<string>(out var parsedGlob) && parsedGlob.Length > 0
                ? parsedGlob
                : "*";
            var maxResults = arguments["max_results"] is JsonValue maxValue && maxValue.TryGetValue<int>(out var parsedMax) && parsedMax > 0
                ? Math.Min(parsedMax, MaxSearchResults)
                : MaxSearchResults;
            var matches = new JsonArray();
            var total = 0;
            var root = policy.CanonicalProjectPath;
            foreach (var file in SafeEnumerateFiles(policy, ignoreRules, root, glob))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (total >= maxResults) break;
                var length = new FileInfo(file).Length;
                if (length > MaxScanFileBytes) continue;
                string content;
                try { content = File.ReadAllText(file); }
                catch (IOException) { continue; }
                if (content.Contains('\0')) continue;
                var relative = NormalizeRelative(Path.GetRelativePath(root, file));
                var lines = content.Split('\n');
                for (var index = 0; index < lines.Length && total < maxResults; index++)
                {
                    if (lines[index].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    matches.Add(new JsonObject
                    {
                        ["path"] = relative,
                        ["line"] = index + 1,
                        ["text"] = lines[index].TrimEnd('\r')[..Math.Min(lines[index].TrimEnd('\r').Length, 500)]
                    });
                    total++;
                }
            }
            return Task.FromResult(ToToolOutcome(new JsonObject
            {
                ["query"] = query,
                ["truncated"] = total >= maxResults,
                ["matches"] = matches
            }));
        });

    private static GatewayTool CreateGitStatus(
        SessionPolicy policy,
        CommandRunner runner,
        IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules) => new(
        "git_status",
        "返回当前项目的 Git 状态（porcelain 格式）。",
        Schema(),
        async (_, cancellationToken) =>
        {
            var result = await runner.RunAsync("git", ["status", "--porcelain=v1", "-b", "-z"],
                policy.CanonicalProjectPath, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return new ToolOutcome("git status 失败。", IsError: true).ToContent();
            return ToToolOutcome(new JsonObject
            {
                ["output"] = Truncate(FilterGitStatus(result.StandardOutput, ignoreRules))
            });
        });

    private static GatewayTool CreateGitDiff(
        SessionPolicy policy,
        CommandRunner runner,
        IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules) => new(
        "git_diff",
        "返回当前项目的 Git 差异（默认工作区与 HEAD；staged=true 时返回暂存区差异）。",
        Schema(("staged", "boolean", "是否返回暂存区差异")),
        async (arguments, cancellationToken) =>
        {
            var staged = arguments["staged"] is JsonValue stagedValue && stagedValue.TryGetValue<bool>(out var isStaged) && isStaged;
            var changedPathsArguments = new List<string> { "diff" };
            if (staged) changedPathsArguments.Add("--staged");
            changedPathsArguments.AddRange(["--name-only", "-z", "--no-renames"]);
            var changedPaths = await runner.RunAsync("git", changedPathsArguments,
                policy.CanonicalProjectPath, cancellationToken).ConfigureAwait(false);
            if (!changedPaths.Success) return new ToolOutcome("git diff 失败。", IsError: true).ToContent();

            // 先以 NUL 分隔的 name-only 输出枚举候选路径，再仅把允许路径作为 literal pathspec
            // 传给 git。这样补丁在生成前就不会包含敏感文件名或内容，且 --no-renames 避免
            // 安全路径重命名自敏感路径时泄露旧路径。
            var allowedPaths = changedPaths.StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeRelative)
                .Where(path => !SensitiveFilePolicy.IsSensitiveRelative(path, ignoreRules))
                .ToArray();
            if (allowedPaths.Length == 0)
                return ToToolOutcome(new JsonObject { ["output"] = string.Empty });

            var diffArguments = new List<string> { "diff" };
            if (staged) diffArguments.Add("--staged");
            diffArguments.AddRange(["--no-color", "--no-renames", "--"]);
            diffArguments.AddRange(allowedPaths.Select(path => $":(literal){path}"));
            var result = await runner.RunAsync("git", diffArguments,
                policy.CanonicalProjectPath, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return new ToolOutcome("git diff 失败。", IsError: true).ToContent();
            return ToToolOutcome(new JsonObject
            {
                ["output"] = Truncate(result.StandardOutput)
            });
        });

    // ---- 路径解析：所有工具访问的强制入口 ----

    private static string GetRelativePath(JsonObject arguments)
    {
        if (arguments["path"] is not JsonValue value || !value.TryGetValue<string>(out var path))
            return string.Empty;
        return path.Replace('\\', '/').Trim('/');
    }

    private static DirectoryInfo? ResolveDirectory(SessionPolicy policy, IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules, string relative)
    {
        var fullPath = ResolveInsidePath(policy, relative);
        if (fullPath is null || !Directory.Exists(fullPath)) return null;
        if (SensitiveFilePolicy.IsSensitiveRelative(string.IsNullOrEmpty(relative) ? string.Empty : relative, ignoreRules))
            return null;
        return new DirectoryInfo(fullPath);
    }

    private static string? ResolveInsidePath(SessionPolicy policy, string relative)
    {
        if (Path.IsPathRooted(relative)) return null;
        if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("..")) return null;
        var combined = string.IsNullOrEmpty(relative)
            ? policy.CanonicalProjectPath
            : Path.Combine(policy.CanonicalProjectPath, relative.Replace('/', Path.DirectorySeparatorChar));
        // 目标若本身是符号链接/联接，IsInsideProject 通过最终路径解析仍会拒绝逃逸
        if (!ProjectPathGuard.IsInsideProject(policy.CanonicalProjectPath, combined)) return null;
        return combined;
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        SessionPolicy policy,
        IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules,
        string root,
        string glob)
    {
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            var files = new List<string>();
            var directories = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(current, glob));
                directories.AddRange(Directory.GetDirectories(current));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var file in files)
            {
                var relative = NormalizeRelative(Path.GetRelativePath(root, file));
                if (SensitiveFilePolicy.IsSensitiveRelative(relative, ignoreRules)) continue;
                if (!ProjectPathGuard.IsInsideProject(root, file)) continue;
                yield return file;
            }
            foreach (var directory in directories)
            {
                if (!ProjectPathGuard.IsInsideProject(root, directory)) continue; // 拒绝联接/符号链接出项目
                var relative = NormalizeRelative(Path.GetRelativePath(root, directory));
                if (SensitiveFilePolicy.IsSensitiveRelative(relative, ignoreRules)) continue;
                pending.Enqueue(directory);
            }
        }
    }

    private static string NormalizeRelative(string relative)
        => relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string FilterGitStatus(
        string output,
        IReadOnlyList<SensitiveFilePolicy.IgnoreRule> ignoreRules)
    {
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var filtered = new StringBuilder();
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.StartsWith("## ", StringComparison.Ordinal))
            {
                filtered.AppendLine(record);
                continue;
            }
            if (record.Length < 4) continue;

            var status = record[..3];
            var path = NormalizeRelative(record[3..]);
            var isRenameOrCopy = status[0] is 'R' or 'C' || status[1] is 'R' or 'C';
            var originalPath = isRenameOrCopy && index + 1 < records.Length
                ? NormalizeRelative(records[++index])
                : null;
            if (SensitiveFilePolicy.IsSensitiveRelative(path, ignoreRules)
                || (originalPath is not null && SensitiveFilePolicy.IsSensitiveRelative(originalPath, ignoreRules)))
                continue;

            filtered.Append(status).AppendLine(path);
        }
        return filtered.ToString();
    }

    private static string Truncate(string text)
        => text.Length <= MaxGitOutputChars ? text : text[..MaxGitOutputChars] + "\n…（输出已截断）";

    private static JsonObject ToToolOutcome(JsonObject payload)
        => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString() }),
            ["isError"] = false
        };

    private static JsonObject Schema(params (string Name, string Type, string Description)[] properties)
    {
        var propertiesObject = new JsonObject();
        foreach (var (name, type, description) in properties)
        {
            propertiesObject[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = description
            };
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertiesObject
        };
    }
}
