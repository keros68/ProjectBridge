using System.Text.Json.Serialization;

namespace LocalProjectBridge.Core.Gateway;

public enum ChangeOperationType { Patch, Create, Rename, Delete }
public enum ChangeStatus { Prepared, Applying, Applied, Partial, Interrupted, NeedsRecovery, Restored, RestorePartial, Rejected }
public enum ChangeFileStatus { Pending, Applied, Failed, MutationUncertain, Restored, RestoreConflict }

public sealed class ChangeFileRecord
{
    public int Index { get; set; }
    public ChangeOperationType Operation { get; set; }
    public required string Path { get; set; }
    public string? TargetPath { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public required string Encoding { get; set; }
    public required string NewLine { get; set; }
    public string? StagedFile { get; set; }
    public string? BackupFile { get; set; }
    public ChangeFileStatus Status { get; set; }
    public string? Error { get; set; }
}

public sealed class ChangeRecord
{
    public int SchemaVersion { get; set; } = 3;
    public Guid ChangeId { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public long RootVersion { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ClientId { get; set; }
    public required string ProjectRoot { get; set; }
    public Guid PrepareRequestId { get; set; }
    public string? PreparePayloadHash { get; set; }
    public Guid? ApplyRequestId { get; set; }
    public Guid? RestoreRequestId { get; set; }
    public List<Guid> RestoreRequestIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ChangeStatus Status { get; set; } = ChangeStatus.Prepared;
    public bool DeletionConfirmed { get; set; }
    public Guid? AutoApplyLeaseId { get; set; }
    public string? Error { get; set; }
    public required string Preview { get; set; }
    public List<ChangeFileRecord> Files { get; set; } = [];

    [JsonIgnore]
    public bool RequiresDeletionConfirmation => Files.Any(file => file.Operation == ChangeOperationType.Delete) && !DeletionConfirmed;

    [JsonIgnore]
    public string DisplaySummary => $"{UpdatedAt.ToLocalTime():MM-dd HH:mm} · {Status} · {Files.Count} 个文件";

    public bool HasRestoreRequest(Guid requestId)
        => RestoreRequestId == requestId || RestoreRequestIds?.Contains(requestId) == true;
}

public sealed record PreparedFile(
    ChangeOperationType Operation,
    string Path,
    string? TargetPath,
    byte[]? OriginalBytes,
    byte[]? NewBytes,
    string? BeforeHash,
    string? AfterHash,
    string Encoding,
    string NewLine,
    string Preview);

public interface IWriteFaultInjector
{
    void BeforeMutation(Guid changeId, int fileIndex);
    void AfterMutation(Guid changeId, int fileIndex);
    void BeforeAppliedJournalUpdate(Guid changeId, int fileIndex) { }
}

/// <summary>Test-only process interruption marker; production code never creates it.</summary>
public sealed class SimulatedWriteProcessCrashException(string message) : Exception(message);
