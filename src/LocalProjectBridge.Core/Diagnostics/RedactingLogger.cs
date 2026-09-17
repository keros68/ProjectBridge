using System.Text.RegularExpressions;

namespace LocalProjectBridge.Core;

public sealed partial class RedactingLogger
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RedactingLogger(string appDataDirectory)
    {
        var directory = Path.Combine(appDataDirectory, "logs");
        Directory.CreateDirectory(directory);
        _logPath = Path.Combine(directory, "bridge.log");
    }

    public async Task WriteAsync(string level, string message)
    {
        var safe = Redact(message);
        await _gate.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(_logPath, $"{DateTimeOffset.Now:O} [{level}] {safe}{Environment.NewLine}");
        }
        finally { _gate.Release(); }
    }

    public string LogPath => _logPath;

    public string Redact(string message)
    {
        var safe = CredentialPattern().Replace(message, "$1[redacted]");
        return PairingCodePattern().Replace(safe, "$1[redacted]");
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?|api[_ -]?key\\s*[:=]\\s*|token\\s*[:=]\\s*)([^\\s,;]+)")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex("(?i)(pair(?:ing)?[_ -]?code\\s*[:=]\\s*)([^\\s,;]+)")]
    private static partial Regex PairingCodePattern();
}
