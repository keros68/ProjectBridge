namespace LocalProjectBridge.Core.Gateway;

public sealed class BackupStore
{
    private readonly string _root;
    public BackupStore(string root) => _root = Directory.CreateDirectory(root).FullName;

    public async Task<string?> SaveAsync(Guid changeId, int index, byte[]? original, string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (original is null) return null;
        if (expectedHash is null || ChangeFileSystem.Hash(original) != expectedHash)
            throw new WriteOperationException("backup_baseline_changed", "备份内容与预览基线不一致，已拒绝提交。", true);
        var directory = Directory.CreateDirectory(Path.Combine(_root, changeId.ToString("N"), "backups")).FullName;
        var relative = Path.Combine("backups", $"{index:D2}.bin");
        var path = Path.Combine(directory, $"{index:D2}.bin");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(original, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        if (ChangeFileSystem.HashFile(path) != expectedHash)
            throw new WriteOperationException("backup_content_invalid", "恢复资料落盘校验失败，已拒绝提交。", action: "检查本机存储后重新生成修改预览。");
        return relative;
    }

    public byte[] ReadVerified(Guid changeId, string relativePath, string? expectedHash)
    {
        try
        {
            var changeRoot = Path.Combine(_root, changeId.ToString("N"));
            var path = Path.GetFullPath(Path.Combine(changeRoot, relativePath));
            if (!ProjectPathGuard.IsInsideProject(changeRoot, path))
                throw new InvalidOperationException("恢复资料路径无效。");
            var bytes = File.ReadAllBytes(path);
            if (expectedHash is null || ChangeFileSystem.Hash(bytes) != expectedHash)
                throw new InvalidDataException("恢复资料与原始文件哈希不一致。");
            return bytes;
        }
        catch (Exception error) when (error is IOException or InvalidDataException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new WriteOperationException("backup_content_invalid",
                "恢复资料缺失、不可读或完整性校验失败，未修改项目文件。", action: "保留现有项目文件和恢复资料，并在本机查看修改记录。");
        }
    }
}
