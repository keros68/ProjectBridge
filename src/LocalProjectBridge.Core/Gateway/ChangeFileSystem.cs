using System.Security.Cryptography;
using System.Text;

namespace LocalProjectBridge.Core.Gateway;

internal enum MutationDiskState { NotApplied, Applied, Uncertain }

internal static class ChangeFileSystem
{
    public const int MaxFileBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string? HashFile(string path)
        => File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null;

    public static MutationDiskState InspectMutation(ChangeRecord record, ChangeFileRecord file)
    {
        try
        {
            var root = ProjectPathGuard.CanonicalizeProjectRoot(record.ProjectRoot);
            var source = Path.GetFullPath(Path.Combine(root, file.Path));
            ProjectPathGuard.EnsureInsideProject(root, source);
            var sourceHash = HashFile(source);
            return file.Operation switch
            {
                ChangeOperationType.Patch => sourceHash == file.AfterHash
                    ? MutationDiskState.Applied
                    : sourceHash == file.BeforeHash ? MutationDiskState.NotApplied : MutationDiskState.Uncertain,
                ChangeOperationType.Create => sourceHash == file.AfterHash
                    ? MutationDiskState.Applied
                    : sourceHash is null ? MutationDiskState.NotApplied : MutationDiskState.Uncertain,
                ChangeOperationType.Delete => sourceHash is null
                    ? MutationDiskState.Applied
                    : sourceHash == file.BeforeHash ? MutationDiskState.NotApplied : MutationDiskState.Uncertain,
                ChangeOperationType.Rename => InspectRename(root, source, sourceHash, file),
                _ => MutationDiskState.Uncertain
            };
        }
        catch (Exception) { return MutationDiskState.Uncertain; }
    }

    private static MutationDiskState InspectRename(string root, string source, string? sourceHash, ChangeFileRecord file)
    {
        if (file.TargetPath is null) return MutationDiskState.Uncertain;
        var target = Path.GetFullPath(Path.Combine(root, file.TargetPath));
        ProjectPathGuard.EnsureInsideProject(root, target);
        var targetHash = HashFile(target);
        if (sourceHash is null && targetHash == file.AfterHash) return MutationDiskState.Applied;
        if (sourceHash == file.BeforeHash && targetHash is null) return MutationDiskState.NotApplied;
        return MutationDiskState.Uncertain;
    }

    public static (string Text, string Encoding, string NewLine) DecodeText(byte[] bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidOperationException("单个文件超过 2 MiB 限制。");
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = StrictUtf8.GetString(bom ? bytes.AsSpan(3) : bytes);
        if (text.Contains('\0')) throw new InvalidOperationException("文件包含二进制内容，不能直接编辑。");
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "CRLF"
            : text.Contains('\n') ? "LF" : "None";
        return (text, bom ? "utf-8-bom" : "utf-8", newLine);
    }

    public static byte[] EncodeText(string text, string encoding)
    {
        var content = StrictUtf8.GetBytes(text);
        if (encoding != "utf-8-bom") return content;
        var result = new byte[Encoding.UTF8.Preamble.Length + content.Length];
        Encoding.UTF8.Preamble.CopyTo(result.AsSpan());
        content.CopyTo(result, Encoding.UTF8.Preamble.Length);
        return result;
    }

    public static async Task WriteAtomicallyAsync(string path, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("目标路径缺少父目录。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("目标文件的父目录不存在。");
        var temporary = Path.Combine(directory, $".projectbridge-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
