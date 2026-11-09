namespace MultiDriveCloud.Core.Models;

public enum AccountStatus
{
    Connected = 0,
    NeedsReauthorization = 1,
    Error = 2,
    Disconnected = 3,
}

public sealed class CloudAccount
{
    public string AccountId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Provider { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public bool IsUnlimited { get; set; }
    public DateTime? LastSync { get; set; }
    public AccountStatus Status { get; set; }
    public string? StatusMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long AvailableBytes => Math.Max(0, TotalBytes - UsedBytes);

    public CloudAccount Clone() => (CloudAccount)MemberwiseClone();
}

public enum FileStatus
{
    Pending = 0,
    Stored = 1,
    Failed = 2,
    Deleting = 3,
}

public class VirtualFolder
{
    public Guid FolderId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

    public static readonly Guid RootId = new("00000000-0000-0000-0000-000000000001");
}

public class VirtualFile
{
    public Guid FileId { get; set; }
    public Guid FolderId { get; set; }
    public string Name { get; set; } = "";
    public long TotalSize { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public FileStatus Status { get; set; }
    public bool IsEncrypted { get; set; }
    public long ChunkSize { get; set; }
    public string? OriginalPath { get; set; }
}

public enum ChunkStatus
{
    Planned = 0,
    Uploading = 1,
    Stored = 2,
    Failed = 3,
    Deleted = 4,
}

public sealed class FileChunk
{
    public Guid ChunkId { get; set; }
    public Guid FileId { get; set; }
    public int ChunkIndex { get; set; }
    public long Offset { get; set; }
    public long PlainSize { get; set; }
    public long StoredSize { get; set; }
    public string Sha256 { get; set; } = "";
    public string StoredMd5 { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string? RemoteFileId { get; set; }
    public ChunkStatus Status { get; set; }
}

public enum TransferDirection
{
    Upload = 0,
    Download = 1,
}

public enum TransferState
{
    Queued = 0,
    Running = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed class TransferRecord
{
    public Guid TransferId { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? FileId { get; set; }
    public TransferDirection Direction { get; set; }
    public string Name { get; set; } = "";
    public TransferState State { get; set; }
    public long TotalBytes { get; set; }
    public long BytesDone { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string LocalPath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class TransferSession
{
    public Guid ChunkId { get; set; }
    public Guid TransferId { get; set; }
    public string SessionUri { get; set; } = "";
    public long BytesUploaded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}

public sealed record StorageSummary(
    int AccountCount,
    int ConnectedCount,
    long TotalCapacity,
    long UsedOnAccounts,
    long FreeBytes,
    long UsableBytes,
    long UsedByApp,
    int FileCount,
    int FolderCount);

