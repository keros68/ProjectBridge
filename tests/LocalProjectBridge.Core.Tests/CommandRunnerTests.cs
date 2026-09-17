using System.Text;
using LocalProjectBridge.Core;
using LocalProjectBridge.Core.Adapters;

namespace LocalProjectBridge.Core.Tests;

[CollectionDefinition("Console encoding", DisableParallelization = true)]
public sealed class ConsoleEncodingCollection;

[Collection("Console encoding")]
public sealed class CommandRunnerTests
{
    [Fact]
    public async Task Utf8BackendOutput_RemainsValidJsonWithChineseWindowsCodePage()
    {
        const string payload = """{"ok":true,"needsChoice":false,"loginPrompt":"完成后告诉我「好了」。"}""";
        const string diagnostic = "连接检查完成。";
        var directory = Directory.CreateTempSubdirectory("lpb-encoding").FullName;
        var originalEncoding = Console.OutputEncoding;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            Console.OutputEncoding = Encoding.GetEncoding(936);
            // Write actual UTF-8 bytes, as Node does, regardless of the parent's code page.
            var script = $"$bytes = [Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}'); [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length); "
                + $"$bytes = [Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(diagnostic))}'); [Console]::OpenStandardError().Write($bytes, 0, $bytes.Length)";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var runner = new CommandRunner(new RedactingLogger(directory));
            var result = await runner.RunAsync(powershell,
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);

            Assert.True(result.Success);
            Assert.Equal(payload, result.StandardOutput);
            Assert.Equal(diagnostic, result.StandardError);
            using var json = C2cAdapter.ParseJsonOutput(result.StandardOutput);
            Assert.Equal("完成后告诉我「好了」。", json.RootElement.GetProperty("loginPrompt").GetString());
        }
        finally
        {
            Console.OutputEncoding = originalEncoding;
            Directory.Delete(directory, recursive: true);
        }
    }
}
