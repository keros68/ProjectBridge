using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalProjectBridge.Core.Gateway;

public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _root;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ChangeRecord> _records = [];

    public ChangeJournal(string root)
    {
        _root = Directory.CreateDirectory(root).FullName;
        LoadAndReconcile();
    }

    public string RootDirectory => _root;
    public bool IsHealthy { get; private set; } = true;
    public string? LoadError { get; private set; }
    public event EventHandler? Changed;

    public IReadOnlyList<ChangeRecord> List(Guid? projectId = null)
    {
        lock (_gate)
            return _records.Values
                .Where(record => projectId is null || record.ProjectId == projectId)
                .OrderByDescending(record => record.UpdatedAt)
                .Select(Clone).ToArray();
    }

    public ChangeRecord? Get(Guid changeId)
    {
        lock (_gate) return _records.TryGetValue(changeId, out var record) ? Clone(record) : null;
    }

    public ChangeRecord? FindRequest(Guid requestId)
    {
        lock (_gate)
            return _records.Values.FirstOrDefault(record => record.PrepareRequestId == requestId
                || record.ApplyRequestId == requestId || record.HasRestoreRequest(requestId)) is { } found
                ? Clone(found) : null;
    }

    public async Task AddPreparedAsync(ChangeRecord record, IReadOnlyDictionary<int, byte[]> stagedFiles,
        CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(ChangeDirectory(record.ChangeId)).FullName;
        foreach (var pair in stagedFiles)
        {
            var name = $"{pair.Key:D2}.new";
            var path = Path.Combine(directory, name);
            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(pair.Value, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (ChangeFileSystem.HashFile(path) != ChangeFileSystem.Hash(pair.Value))
                throw new IOException("暂存内容落盘校验失败。");
            record.Files.Single(file => file.Index == pair.Key).StagedFile = name;
        }
        lock (_gate)
        {
            if (_records.Values.Any(existing => existing.PrepareRequestId == record.PrepareRequestId))
                throw new InvalidOperationException("请求 ID 已被其他修改使用。");
            _records.Add(record.ChangeId, Clone(record));
        }
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(ChangeRecord record, CancellationToken cancellationToken = default)
    {
        record.UpdatedAt = DateTimeOffset.Now;
        lock (_gate) _records[record.ChangeId] = Clone(record);
        await SaveAsync(record, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ConfirmDeletionAsync(Guid changeId, CancellationToken cancellationToken = default)
    {
        var record = Get(changeId) ?? throw new KeyNotFoundException("未找到修改记录。");
        if (record.Status != ChangeStatus.Prepared || record.Files.All(file => file.Operation != ChangeOperationType.Delete))
            throw new InvalidOperationException("该修改没有等待确认的删除操作。");
        record.DeletionConfirmed = true;
        await UpdateAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public byte[] ReadStagedVerified(ChangeRecord record, ChangeFileRecord file)
    {
        try
        {
            var changeRoot = ChangeDirectory(record.ChangeId);
            var path = Path.GetFullPath(Path.Combine(changeRoot, file.StagedFile
                ?? throw new InvalidOperationException("修改计划缺少暂存内容。")));
            if (!ProjectPathGuard.IsInsideProject(changeRoot, path))
                throw new InvalidOperationException("暂存内容路径无效。");
            var bytes = File.ReadAllBytes(path);
            if (file.AfterHash is null || ChangeFileSystem.Hash(bytes) != file.AfterHash)
                throw new InvalidDataException("暂存内容与修改预览不一致。");
            return bytes;
        }
        catch (Exception error) when (error is IOException or InvalidDataException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new WriteOperationException("staged_content_invalid",
                "暂存内容缺失、不可读或与修改预览不一致，已拒绝提交。", action: "重新生成修改预览；不要继续使用这份暂存内容。");
        }
    }

    private async Task SaveAsync(ChangeRecord record, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateDirectory(ChangeDirectory(record.ChangeId)).FullName;
        var path = Path.Combine(directory, "record.json");
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }

    private string ChangeDirectory(Guid changeId) => Path.Combine(_root, changeId.ToString("N"));

    private void LoadAndReconcile()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "record.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ChangeRecord>(File.ReadAllText(path), JsonOptions);
                if (record is null || record.ChangeId == Guid.Empty) continue;
                record.RestoreRequestIds ??= [];
                var existingRestoreRequest = record.RestoreRequestId;
                var migratedRestoreRequest = existingRestoreRequest.HasValue
                    && !record.RestoreRequestIds.Contains(existingRestoreRequest.Value);
                if (migratedRestoreRequest) record.RestoreRequestIds.Add(existingRestoreRequest!.Value);
                if (record.Status == ChangeStatus.Applying) ReconcileInterrupted(record);
                _records[record.ChangeId] = record;
                if (migratedRestoreRequest || record.Status is ChangeStatus.Interrupted or ChangeStatus.NeedsRecovery)
                    SaveAsync(record, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception error) when (File.Exists(path))
            {
                IsHealthy = false;
                LoadError ??= $"无法读取修改记录：{Path.GetFileName(Path.GetDirectoryName(path))}（{error.GetType().Name}）";
            }
        }
    }

    private static void ReconcileInterrupted(ChangeRecord record)
    {
        var needsRecovery = false;
        foreach (var file in record.Files)
        {
            switch (ChangeFileSystem.InspectMutation(record, file))
            {
                case MutationDiskState.Applied:
                    file.Status = ChangeFileStatus.Applied;
                    needsRecovery = true;
                    break;
                case MutationDiskState.NotApplied:
                    file.Status = ChangeFileStatus.Pending;
                    break;
                default:
                    file.Status = ChangeFileStatus.MutationUncertain;
                    needsRecovery = true;
                    break;
            }
        }
        record.Status = needsRecovery ? ChangeStatus.NeedsRecovery : ChangeStatus.Interrupted;
        record.Error = "程序在应用修改期间中断；未自动继续，需检查或恢复。";
        record.UpdatedAt = DateTimeOffset.Now;
    }

    private static ChangeRecord Clone(ChangeRecord record)
        => JsonSerializer.Deserialize<ChangeRecord>(JsonSerializer.Serialize(record, JsonOptions), JsonOptions)!;
}
