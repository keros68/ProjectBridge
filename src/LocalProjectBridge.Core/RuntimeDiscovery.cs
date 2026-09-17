namespace LocalProjectBridge.Core;

public sealed class RuntimeDiscovery
{
    public RuntimePaths Discover()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var node = FindOnPath("node.exe") ?? FirstExisting(
            @"C:\Program Files\nodejs\node.exe",
            Path.Combine(local, "Programs", "nodejs", "node.exe"));
        var codexScript = FirstExisting(
            Environment.GetEnvironmentVariable("LPB_CODEX_SCRIPT") ?? string.Empty,
            Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "bin", "codex.js"));
        var appBase = AppContext.BaseDirectory;
        var bundledRuntime = Path.Combine(appBase, "runtime");
        node = FirstExisting(
            Path.Combine(bundledRuntime, "node.exe"),
            node ?? string.Empty);
        var shim = FirstExisting(
            Path.Combine(appBase, "ProjectBridge.CodexShim.exe"),
            Path.Combine(appBase, "LocalProjectBridge.CodexShim.exe"),
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "CodexShim", "bin", "Debug", "net10.0", "ProjectBridge.CodexShim.exe")),
            Path.GetFullPath(Path.Combine(appBase, "..", "..", "..", "..", "CodexShim", "bin", "Debug", "net10.0", "LocalProjectBridge.CodexShim.exe")));
        var bundledBackends = Path.Combine(appBase, "backends");
        var managedRoot = Directory.Exists(bundledBackends)
            ? bundledBackends
            : Path.Combine(local, "LocalProjectBridge", "backends");
        var reverseBridge = FirstExisting(
            Path.Combine(managedRoot, "transceiver", "plugins", "transceiver", "dist", "reverse-bridge", "index.mjs"),
            FindNewest(local, "transceiver", "index.mjs", path =>
                path.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}reverse-bridge{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) ?? string.Empty);
        var tunnel = FirstExisting(
            Path.Combine(bundledRuntime, "tunnel-client.exe"),
            FindOnPath("tunnel-client.exe") ?? string.Empty,
            Path.Combine(local, "Programs", "tunnel-client", "full", "tunnel-client.exe"));
        var c2cCli = FirstExisting(Path.Combine(managedRoot, "codex-with-chatgpt", "bin", "c2c.js"));
        var cloudflared = FirstExisting(
            Path.Combine(bundledRuntime, "cloudflared.exe"),
            FindOnPath("cloudflared.exe") ?? string.Empty,
            Path.Combine(local, "Programs", "tunnel-client", "full", "cloudflared.exe"));
        return new RuntimePaths(node, codexScript, shim, reverseBridge, tunnel, c2cCli, cloudflared);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Combine(directory.Trim('"'), name))
        .FirstOrDefault(File.Exists);

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);

    private static string? FindNewest(string root, string requiredSegment, string fileName, Func<string, bool> predicate)
    {
        try
        {
            var searchRoot = Path.Combine(root, requiredSegment);
            if (!Directory.Exists(searchRoot)) return null;
            return Directory.EnumerateFiles(searchRoot, fileName, SearchOption.AllDirectories)
                .Where(predicate)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
    }
}
