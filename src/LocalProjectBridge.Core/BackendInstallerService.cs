namespace LocalProjectBridge.Core;

public sealed class BackendInstallerService
{
    private const string TransceiverRemote = "https://github.com/mark9804/transceiver.git";
    private const string C2cRemote = "https://github.com/XiaoDuoYa/codex-with-chatgpt.git";
    private readonly CommandRunner _runner;
    private readonly RegistryStore _store;
    private readonly RuntimeDiscovery _discovery;

    public BackendInstallerService(CommandRunner runner, RegistryStore store, RuntimeDiscovery discovery)
    {
        _runner = runner;
        _store = store;
        _discovery = discovery;
    }

    public string BundledBackendsRoot => Path.Combine(AppContext.BaseDirectory, "backends");
    public string BackendsRoot => Directory.Exists(BundledBackendsRoot)
        ? BundledBackendsRoot
        : Path.Combine(_store.AppDataDirectory, "backends");
    public string TransceiverRoot => Path.Combine(BackendsRoot, "transceiver");
    public string C2cRoot => Path.Combine(BackendsRoot, "codex-with-chatgpt");

    public async Task<BackendStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var bundled = Directory.Exists(BundledBackendsRoot);
        var transceiverReady = (bundled || await IsCheckoutAsync(TransceiverRoot, TransceiverRemote, cancellationToken))
            && File.Exists(Path.Combine(TransceiverRoot, "plugins", "transceiver", "dist", "reverse-bridge", "index.mjs"));
        var c2cReady = (bundled || await IsCheckoutAsync(C2cRoot, C2cRemote, cancellationToken))
            && File.Exists(Path.Combine(C2cRoot, "bin", "c2c.js"));
        var built = c2cReady && Directory.Exists(Path.Combine(C2cRoot, "node_modules")) && Directory.Exists(Path.Combine(C2cRoot, "dist"));
        return new BackendStatus(
            transceiverReady,
            c2cReady,
            built,
            transceiverReady && !bundled ? await CommitAsync(TransceiverRoot, cancellationToken) : null,
            c2cReady && !bundled ? await CommitAsync(C2cRoot, cancellationToken) : null);
    }

    public async Task InstallOrUpdateAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(BundledBackendsRoot))
        {
            progress?.Report("正在检查并复用随程序附带的组件…");
            var bundledStatus = await InspectAsync(cancellationToken);
            if (!bundledStatus.TransceiverCheckoutReady || !bundledStatus.C2cCheckoutReady)
                throw new InvalidOperationException("程序组件不完整，请重新下载安装包。");
            if (!bundledStatus.C2cBuilt)
            {
                progress?.Report("正在准备缺失的本地组件…");
                await RequireSuccessAsync("corepack", ["pnpm", "install", "--frozen-lockfile"], C2cRoot, cancellationToken);
                progress?.Report("正在完成本地准备…");
                await RequireSuccessAsync("corepack", ["pnpm", "build"], C2cRoot, cancellationToken);
            }
            progress?.Report("已复用现有程序组件。");
            return;
        }

        Directory.CreateDirectory(BackendsRoot);
        progress?.Report("正在检查已安装的程序组件…");
        var installed = await InspectAsync(cancellationToken);
        if (installed.TransceiverCheckoutReady && installed.C2cCheckoutReady && installed.C2cBuilt)
        {
            progress?.Report("已复用现有程序组件。");
            return;
        }

        if (installed.TransceiverCheckoutReady || installed.C2cCheckoutReady)
            progress?.Report("正在复用已安装的程序组件，并准备缺失部分…");

        if (!installed.TransceiverCheckoutReady)
        {
            progress?.Report("正在准备缺失的 Transceiver 组件…");
            await EnsureCheckoutAsync(TransceiverRoot, TransceiverRemote, cancellationToken);
        }

        if (!installed.C2cCheckoutReady)
        {
            progress?.Report("正在准备缺失的 ChatGPT 连接组件…");
            await EnsureCheckoutAsync(C2cRoot, C2cRemote, cancellationToken);
        }

        if (!installed.C2cBuilt)
        {
            progress?.Report("正在完成缺失的本地准备…");
            await RequireSuccessAsync("corepack", ["pnpm", "install", "--frozen-lockfile"], C2cRoot, cancellationToken);
            progress?.Report("正在完成本地准备…");
            await RequireSuccessAsync("corepack", ["pnpm", "build"], C2cRoot, cancellationToken);
        }

        progress?.Report("程序组件已准备完成。");
    }

    private async Task EnsureCheckoutAsync(string target, string remote, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target))
        {
            await RequireSuccessAsync("git", ["clone", "--filter=blob:none", remote, target], BackendsRoot, cancellationToken);
            return;
        }
        if (!await IsCheckoutAsync(target, remote, cancellationToken))
            throw new InvalidOperationException($"{target} 已存在，但不是预期的上游仓库。请查看诊断信息。");
        var status = await _runner.RunAsync("git", ["status", "--porcelain"], target, cancellationToken);
        if (!status.Success || !string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new InvalidOperationException($"{Path.GetFileName(target)} 有本地修改，未自动更新。");
        await RequireSuccessAsync("git", ["pull", "--ff-only"], target, cancellationToken);
    }

    private async Task<bool> IsCheckoutAsync(string target, string expectedRemote, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(target, ".git"))) return false;
        var result = await _runner.RunAsync("git", ["remote", "get-url", "origin"], target, cancellationToken);
        if (!result.Success) return false;
        return NormalizeRemote(result.StandardOutput.Trim()) == NormalizeRemote(expectedRemote);
    }

    private async Task<string?> CommitAsync(string target, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync("git", ["rev-parse", "--short=12", "HEAD"], target, cancellationToken);
        return result.Success ? result.StandardOutput.Trim() : null;
    }

    private async Task RequireSuccessAsync(string executable, string[] arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        CommandResult result;
        if (string.Equals(executable, "corepack", StringComparison.OrdinalIgnoreCase))
        {
            var node = _discovery.Discover().Node ?? throw new InvalidOperationException("未找到 Node.js。请先从 https://nodejs.org/ 安装长期支持版，然后重新打开程序。");
            var corepackScript = Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "corepack", "dist", "corepack.js");
            if (!File.Exists(corepackScript)) throw new InvalidOperationException("未找到 Corepack。");
            result = await _runner.RunAsync(node, [corepackScript, .. arguments], workingDirectory, cancellationToken);
        }
        else result = await _runner.RunAsync(executable, arguments, workingDirectory, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} 执行失败：{_runner.Redact(result.StandardError.Trim())}");
    }

    private static string NormalizeRemote(string remote) => remote.Trim().TrimEnd('/').Replace(".git", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
}
