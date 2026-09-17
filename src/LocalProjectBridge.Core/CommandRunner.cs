using System.Diagnostics;
using System.Text;
using LocalProjectBridge.Core.Processes;

namespace LocalProjectBridge.Core;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
}

public sealed class CommandRunner
{
    private readonly RedactingLogger _logger;

    public CommandRunner(RedactingLogger logger) => _logger = logger;

    public string Redact(string message) => _logger.Redact(message);

    public Task LogAsync(string level, string message) => _logger.WriteAsync(level, message);

    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyDictionary<string, string>? inheritEnvironment = null,
        JobObject? job = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Node and the tunnel CLIs emit UTF-8, including JSON. A Windows GUI
            // otherwise falls back to the system code page and can lose JSON quotes.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        if (environment is not null)
            foreach (var variable in environment) info.Environment[variable.Key] = variable.Value;
        if (inheritEnvironment is not null)
            foreach (var variable in inheritEnvironment) info.Environment[variable.Key] = variable.Value;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        try { job?.Attach(process); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (TimeoutException) { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr)) await _logger.WriteAsync(process.ExitCode == 0 ? "info" : "error", stderr.Trim());
        return new CommandResult(process.ExitCode, stdout, stderr);
    }
}
