using MultiDriveCloud.Core.Models;

namespace MultiDriveCloud.Core.Interfaces;

public interface IAccountRepository
{
    Task<IReadOnlyList<CloudAccount>> GetAllAsync(CancellationToken ct = default);
    Task<CloudAccount?> GetAsync(string accountId, CancellationToken ct = default);
    Task UpsertAsync(CloudAccount account, CancellationToken ct = default);
    Task DeleteAsync(string accountId, CancellationToken ct = default);
}

public sealed record FileEntry(VirtualFile File, string FolderPath);

public sealed record SearchResult(IReadOnlyList<VirtualFolder> Folders, IReadOnlyList<VirtualFile> Files);

public interface IIndexRepository
{
    Task<VirtualFolder?> GetFolderAsync(Guid folderId, CancellationToken ct = default);
    Task<IReadOnlyList<VirtualFolder>> GetSubFoldersAsync(Guid parentId, CancellationToken ct = default);
    Task<VirtualFolder?> FindFolderAsync(Guid parentId, string name, CancellationToken ct = default);
    Task InsertFolderAsync(VirtualFolder folder, CancellationToken ct = default);
    Task UpdateFolderAsync(VirtualFolder folder, CancellationToken ct = default);
    Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default);
    Task<IReadOnlyList<VirtualFolder>> GetFolderPathAsync(Guid folderId, CancellationToken ct = default);

    Task<VirtualFile?> GetFileAsync(Guid fileId, CancellationToken ct = default);
    Task<IReadOnlyList<VirtualFile>> GetFilesAsync(Guid folderId, CancellationToken ct = default);
    Task<VirtualFile?> FindFileAsync(Guid folderId, string name, CancellationToken ct = default);
    Task InsertFileAsync(VirtualFile file, CancellationToken ct = default);
    Task UpdateFileAsync(VirtualFile file, CancellationToken ct = default);
    Task DeleteFileAsync(Guid fileId, CancellationToken ct = default);
    Task<IReadOnlyList<VirtualFile>> GetFilesByStatusAsync(FileStatus status, CancellationToken ct = default);
    Task<SearchResult> SearchAsync(string text, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<FileEntry>> GetFilesRecursiveAsync(Guid folderId, CancellationToken ct = default);
    Task<IReadOnlyList<(VirtualFolder Folder, string RelativePath)>> GetFoldersRecursiveAsync(Guid folderId, CancellationToken ct = default);

    Task<IReadOnlyList<FileChunk>> GetChunksAsync(Guid fileId, CancellationToken ct = default);
    Task InsertChunksAsync(IEnumerable<FileChunk> chunks, CancellationToken ct = default);
    Task UpdateChunkAsync(FileChunk chunk, CancellationToken ct = default);
    Task<IReadOnlyList<FileChunk>> GetChunksByStatusAsync(IEnumerable<ChunkStatus> statuses, CancellationToken ct = default);
    Task DeleteChunksAsync(Guid fileId, CancellationToken ct = default);

    Task InsertTransferAsync(TransferRecord transfer, CancellationToken ct = default);
    Task InsertUploadAsync(VirtualFile file, TransferRecord transfer, CancellationToken ct = default);
    Task UpdateTransferAsync(TransferRecord transfer, CancellationToken ct = default);
    Task<TransferRecord?> GetTransferAsync(Guid transferId, CancellationToken ct = default);
    Task<IReadOnlyList<TransferRecord>> GetTransfersAsync(CancellationToken ct = default);
    Task DeleteTransferAsync(Guid transferId, CancellationToken ct = default);
    Task ClearFinishedTransfersAsync(CancellationToken ct = default);

    Task UpsertSessionAsync(TransferSession session, CancellationToken ct = default);
    Task<TransferSession?> GetSessionAsync(Guid chunkId, CancellationToken ct = default);
    Task DeleteSessionAsync(Guid chunkId, CancellationToken ct = default);

    Task<IndexStats> GetStatsAsync(CancellationToken ct = default);
}

public sealed record IndexStats(int StoredFiles, int Folders, long StoredBytesTotal, IReadOnlyDictionary<string, long> StoredBytesByAccount);

public interface IKeyValueStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default);
}

public interface ISecretStore
{
    Task SetAsync(string name, string value, CancellationToken ct = default);
    Task<string?> GetAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}

public interface IEncryptionKeyProvider
{
    Task<byte[]?> GetKeyAsync(CancellationToken ct = default);
    Task<bool> EnsureKeyAsync(CancellationToken ct = default);
}

public interface IIndexBackupService
{
    Task BackupAsync(string destinationPath, CancellationToken ct = default);
    Task<string> RestoreAsync(string backupPath, CancellationToken ct = default);
    Task ExportRecoveryJsonAsync(string destinationPath, CancellationToken ct = default);
}
