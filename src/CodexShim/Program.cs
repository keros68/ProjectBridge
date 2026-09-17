using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ProjectBridge.CodexShim;

// JSONL must not start with a UTF-8 BOM; the app-server rejects it as invalid JSON.
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

// CodexShim：Transceiver bridge 以 CODEX 环境变量调用本程序来启动 Codex CLI。
// 运行时路径优先使用启动器发现结果（LPB_NODE / LPB_CODEX_SCRIPT），
// 与 LocalProjectBridge 的运行时发现顺序保持一致（设计文档 6.5）。

static string? FindOnPath(string fileName)
{
    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(part => Path.Combine(part.Trim('"'), fileName))
        .FirstOrDefault(File.Exists);
}

var node = Environment.GetEnvironmentVariable("LPB_NODE");
if (string.IsNullOrWhiteSpace(node) || !File.Exists(node))
    node = FindOnPath("node.exe") ?? @"C:\Program Files\nodejs\node.exe";

var codexScript = Environment.GetEnvironmentVariable("LPB_CODEX_SCRIPT");
if (string.IsNullOrWhiteSpace(codexScript) || !File.Exists(codexScript))
    codexScript = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "@openai", "codex", "bin", "codex.js");

if (!File.Exists(node) || !File.Exists(codexScript))
{
    Console.Error.WriteLine("ProjectBridge cannot find Node.js or the Codex CLI script.");
    return 2;
}

var appServer = args.Any(argument => string.Equals(argument, "app-server", StringComparison.Ordinal));
var appServerLaunch = appServer
    && !args.Any(argument => string.Equals(argument, "--help", StringComparison.Ordinal) || string.Equals(argument, "-h", StringComparison.Ordinal));
var projectRoot = Environment.GetEnvironmentVariable("LPB_PROJECT_ROOT");
var allowWrite = Environment.GetEnvironmentVariable("LPB_CODEX_ALLOW_WRITE") == "1";
if (appServerLaunch
    && (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)))
{
    Console.Error.WriteLine("ProjectBridge task bridge requires an existing LPB_PROJECT_ROOT.");
    return 4;
}

var startInfo = new ProcessStartInfo(node)
{
    UseShellExecute = false,
    RedirectStandardInput = appServerLaunch,
    RedirectStandardOutput = appServerLaunch,
    RedirectStandardError = appServerLaunch
};
if (appServerLaunch)
{
    startInfo.StandardInputEncoding = new UTF8Encoding(false);
    startInfo.StandardOutputEncoding = Encoding.UTF8;
    startInfo.StandardErrorEncoding = Encoding.UTF8;
}
startInfo.ArgumentList.Add(codexScript);
if (appServerLaunch)
{
    // Empty TOML tables merge with user config; mcp_servers={} does NOT disable
    // existing servers. Enumerate effective names and turn off each explicitly.
    var inventoryInfo = new ProcessStartInfo(node)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        WorkingDirectory = projectRoot!
    };
    foreach (var argument in new[] { codexScript, "mcp", "list", "--json" }) inventoryInfo.ArgumentList.Add(argument);
    using var inventory = Process.Start(inventoryInfo)!;
    var inventoryOutput = inventory.StandardOutput.ReadToEndAsync();
    var inventoryError = inventory.StandardError.ReadToEndAsync();
    try { await inventory.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (TimeoutException) { inventory.Kill(true); await inventory.WaitForExitAsync(); Console.Error.WriteLine("无法检查 Codex 工具配置，任务未启动。"); return 5; }
    await inventoryError;
    if (inventory.ExitCode != 0) { Console.Error.WriteLine("无法检查 Codex 工具配置，任务未启动。"); return 5; }
    if (JsonNode.Parse(await inventoryOutput) is not JsonArray servers)
    { Console.Error.WriteLine("Codex 工具配置格式无效，任务未启动。"); return 5; }
    foreach (var server in servers)
    {
        var name = server?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) continue;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_-]+$"))
        { Console.Error.WriteLine("Codex 工具名称无法安全覆盖，任务未启动。"); return 5; }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"mcp_servers.{name}.enabled=false");
        // Plugin-provided entries may not exist in config.toml. A disabled
        // override still needs a valid transport during config deserialization.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(server?["transport"]?["type"]?.GetValue<string>() == "stdio"
            ? $"mcp_servers.{name}.command=\"node\""
            : $"mcp_servers.{name}.url=\"http://127.0.0.1:1/mcp\"");
    }
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("web_search=\"disabled\"");
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("sandbox_workspace_write.network_access=false");
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("sandbox_workspace_write.exclude_tmpdir_env_var=true");
    startInfo.ArgumentList.Add("-c");
    startInfo.ArgumentList.Add("sandbox_workspace_write.exclude_slash_tmp=true");
    startInfo.WorkingDirectory = projectRoot!;
}
foreach (var argument in args) startInfo.ArgumentList.Add(argument);

using var process = Process.Start(startInfo);
if (process is null) return 3;
if (!appServerLaunch)
{
    await process.WaitForExitAsync();
    return process.ExitCode;
}

async Task ForwardInputAsync()
{
    try
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var rewritten = AppServerProtocolGuard.RewriteClientLine(line, projectRoot!, allowWrite);
            await process.StandardInput.WriteLineAsync(rewritten);
            await process.StandardInput.FlushAsync();
        }
    }
    catch (IOException) { }
    catch (InvalidOperationException) { }
    finally { try { process.StandardInput.Close(); } catch (InvalidOperationException) { } }
}

async Task CopyAsync(StreamReader source, TextWriter destination)
{
    var buffer = new char[4096];
    int read;
    while ((read = await source.ReadAsync(buffer)) > 0)
    {
        await destination.WriteAsync(buffer.AsMemory(0, read));
        await destination.FlushAsync();
    }
}

var outputTask = CopyAsync(process.StandardOutput, Console.Out);
var errorTask = CopyAsync(process.StandardError, Console.Error);
var inputTask = ForwardInputAsync();
await process.WaitForExitAsync();
try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
await Task.WhenAll(outputTask, errorTask);
return process.ExitCode;
