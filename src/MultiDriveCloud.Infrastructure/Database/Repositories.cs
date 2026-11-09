using Dapper;
using Microsoft.Data.Sqlite;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Infrastructure.Security;

namespace MultiDriveCloud.Infrastructure.Database;

public sealed class AccountRepository : IAccountRepository
{
    private readonly SqliteDatabase _db;
    public AccountRepository(SqliteDatabase db) => _db = db;

    public async Task<IReadOnlyList<CloudAccount>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<CloudAccount>(new CommandDefinition(
            "SELECT * FROM Accounts ORDER BY CreatedAt, Email", cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToList();
    }

    public async Task<CloudAccount?> GetAsync(string accountId, CancellationToken ct = default)
    {
        using var conn = _db.Open();
        return await conn.QueryFirstOrDefaultAsync<CloudAccount>(new CommandDefinition(
            "SELECT * FROM Accounts WHERE AccountId=@accountId", new { accountId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task UpsertAsync(CloudAccount a, CancellationToken ct = default)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Accounts (AccountId, Email, Provider, DisplayName, TotalBytes, UsedBytes, IsUnlimited, LastSync, Status, StatusMessage, CreatedAt)
            VALUES (@AccountId, @Email, @Provider, @DisplayName, @TotalBytes, @UsedBytes, @IsUnlimited, @LastSync, @Status, @StatusMessage, @CreatedAt)
            ON CONFLICT(AccountId) DO UPDATE SET
                Email=excluded.Email, Provider=excluded.Provider, DisplayName=excluded.DisplayName,
                TotalBytes=excluded.TotalBytes, UsedBytes=excluded.UsedBytes, IsUnlimited=excluded.IsUnlimited,
                LastSync=excluded.LastSync, Status=excluded.Status, StatusMessage=excluded.StatusMessage
            """, a, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string accountId, CancellationToken ct = default)
    {
        using var conn = _db.Open();
        try
        {
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM Accounts WHERE AccountId=@accountId", new { accountId }, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new AccountInUseException("Ce compte est encore référencé par des fragments de fichiers.");
        }
    }
}

public sealed class KeyValueStore : IKeyValueStore
{
    private readonly SqliteDatabase _db;
    public KeyValueStore(SqliteDatabase db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        using var conn = _db.Open();
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("SELECT Value FROM Settings WHERE Key=@key", new { key }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO Settings (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value",
            new { key, value }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<(string Key, string Value)>(new CommandDefinition("SELECT Key, Value FROM Settings", cancellationToken: ct)).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Key, r => r.Value);
    }
}

public sealed class IndexRepository : IIndexRepository
{
    private readonly SqliteDatabase _db;
    private readonly ISecretProtector _protector;

    public IndexRepository(SqliteDatabase db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    private sealed class FileWithPath : VirtualFile
    {
        public string FolderPath { get; set; } = "";
    }

    private sealed class FolderWithPath : VirtualFolder
    {
        public string RelPath { get; set; } = "";
    }

    private static CommandDefinition Cmd(string sql, object? param, CancellationToken ct, System.Data.IDbTransaction? tx = null) =>
        new(sql, param, tx, cancellationToken: ct);


    public async Task<VirtualFolder?> GetFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return await c.QueryFirstOrDefaultAsync<VirtualFolder>(Cmd("SELECT * FROM VirtualFolders WHERE FolderId=@folderId", new { folderId }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VirtualFolder>> GetSubFoldersAsync(Guid parentId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return (await c.QueryAsync<VirtualFolder>(Cmd("SELECT * FROM VirtualFolders WHERE ParentId=@parentId ORDER BY Name COLLATE NOCASE", new { parentId }, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<VirtualFolder?> FindFolderAsync(Guid parentId, string name, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return await c.QueryFirstOrDefaultAsync<VirtualFolder>(Cmd("SELECT * FROM VirtualFolders WHERE ParentId=@parentId AND Name=@name COLLATE NOCASE", new { parentId, name }, ct)).ConfigureAwait(false);
    }

    public async Task InsertFolderAsync(VirtualFolder f, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("INSERT INTO VirtualFolders (FolderId, ParentId, Name, CreatedAt, ModifiedAt) VALUES (@FolderId, @ParentId, @Name, @CreatedAt, @ModifiedAt)", f, ct)).ConfigureAwait(false);
    }

    public async Task UpdateFolderAsync(VirtualFolder f, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("UPDATE VirtualFolders SET ParentId=@ParentId, Name=@Name, ModifiedAt=@ModifiedAt WHERE FolderId=@FolderId", f, ct)).ConfigureAwait(false);
    }

    public async Task DeleteFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM VirtualFolders WHERE FolderId=@folderId AND ParentId IS NOT NULL", new { folderId }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VirtualFolder>> GetFolderPathAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        const string sql = """
            WITH RECURSIVE up(FolderId, ParentId, Name, CreatedAt, ModifiedAt, Depth) AS (
                SELECT FolderId, ParentId, Name, CreatedAt, ModifiedAt, 0 FROM VirtualFolders WHERE FolderId=@folderId
                UNION ALL
                SELECT f.FolderId, f.ParentId, f.Name, f.CreatedAt, f.ModifiedAt, up.Depth+1
                FROM VirtualFolders f JOIN up ON f.FolderId = up.ParentId)
            SELECT FolderId, ParentId, Name, CreatedAt, ModifiedAt FROM up WHERE ParentId IS NOT NULL ORDER BY Depth DESC
            """;
        return (await c.QueryAsync<VirtualFolder>(Cmd(sql, new { folderId }, ct)).ConfigureAwait(false)).ToList();
    }


    public async Task<VirtualFile?> GetFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return await c.QueryFirstOrDefaultAsync<VirtualFile>(Cmd("SELECT * FROM VirtualFiles WHERE FileId=@fileId", new { fileId }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VirtualFile>> GetFilesAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return (await c.QueryAsync<VirtualFile>(Cmd("SELECT * FROM VirtualFiles WHERE FolderId=@folderId ORDER BY Name COLLATE NOCASE", new { folderId }, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<VirtualFile?> FindFileAsync(Guid folderId, string name, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return await c.QueryFirstOrDefaultAsync<VirtualFile>(Cmd("SELECT * FROM VirtualFiles WHERE FolderId=@folderId AND Name=@name COLLATE NOCASE", new { folderId, name }, ct)).ConfigureAwait(false);
    }

    private const string InsertFileSql = """
        INSERT INTO VirtualFiles (FileId, FolderId, Name, TotalSize, Sha256, CreatedAt, ModifiedAt, Status, IsEncrypted, ChunkSize, OriginalPath)
        VALUES (@FileId, @FolderId, @Name, @TotalSize, @Sha256, @CreatedAt, @ModifiedAt, @Status, @IsEncrypted, @ChunkSize, @OriginalPath)
        """;

    public async Task InsertFileAsync(VirtualFile f, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd(InsertFileSql, f, ct)).ConfigureAwait(false);
    }

    public async Task UpdateFileAsync(VirtualFile f, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("""
            UPDATE VirtualFiles SET FolderId=@FolderId, Name=@Name, TotalSize=@TotalSize, Sha256=@Sha256, ModifiedAt=@ModifiedAt,
                   Status=@Status, IsEncrypted=@IsEncrypted, ChunkSize=@ChunkSize, OriginalPath=@OriginalPath
            WHERE FileId=@FileId
            """, f, ct)).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(Guid fileId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM VirtualFiles WHERE FileId=@fileId", new { fileId }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VirtualFile>> GetFilesByStatusAsync(FileStatus status, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return (await c.QueryAsync<VirtualFile>(Cmd("SELECT * FROM VirtualFiles WHERE Status=@status", new { status = (int)status }, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task<SearchResult> SearchAsync(string text, int limit, CancellationToken ct = default)
    {
        var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        using var c = _db.Open();
        var folders = await c.QueryAsync<VirtualFolder>(Cmd(
            "SELECT * FROM VirtualFolders WHERE ParentId IS NOT NULL AND Name LIKE @like ESCAPE '\\' ORDER BY Name COLLATE NOCASE LIMIT @limit", new { like, limit }, ct)).ConfigureAwait(false);
        var files = await c.QueryAsync<VirtualFile>(Cmd(
            "SELECT * FROM VirtualFiles WHERE Name LIKE @like ESCAPE '\\' ORDER BY Name COLLATE NOCASE LIMIT @limit", new { like, limit }, ct)).ConfigureAwait(false);
        return new SearchResult(folders.ToList(), files.ToList());
    }

    public async Task<IReadOnlyList<FileEntry>> GetFilesRecursiveAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        const string sql = """
            WITH RECURSIVE tree(FolderId, Path) AS (
                SELECT @folderId, ''
                UNION ALL
                SELECT f.FolderId, CASE WHEN t.Path = '' THEN f.Name ELSE t.Path || '/' || f.Name END
                FROM VirtualFolders f JOIN tree t ON f.ParentId = t.FolderId)
            SELECT vf.*, tree.Path AS FolderPath FROM VirtualFiles vf JOIN tree ON vf.FolderId = tree.FolderId
            ORDER BY tree.Path, vf.Name COLLATE NOCASE
            """;
        var rows = await c.QueryAsync<FileWithPath>(Cmd(sql, new { folderId }, ct)).ConfigureAwait(false);
        return rows.Select(r => new FileEntry(r, r.FolderPath)).ToList();
    }

    public async Task<IReadOnlyList<(VirtualFolder Folder, string RelativePath)>> GetFoldersRecursiveAsync(Guid folderId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        const string sql = """
            WITH RECURSIVE tree(FolderId, Path) AS (
                SELECT @folderId, ''
                UNION ALL
                SELECT f.FolderId, CASE WHEN t.Path = '' THEN f.Name ELSE t.Path || '/' || f.Name END
                FROM VirtualFolders f JOIN tree t ON f.ParentId = t.FolderId)
            SELECT vf.*, tree.Path AS RelPath FROM VirtualFolders vf JOIN tree ON vf.FolderId = tree.FolderId
            WHERE tree.Path <> '' ORDER BY tree.Path
            """;
        var rows = await c.QueryAsync<FolderWithPath>(Cmd(sql, new { folderId }, ct)).ConfigureAwait(false);
        return rows.Select(r => ((VirtualFolder)r, r.RelPath)).ToList();
    }


    public async Task<IReadOnlyList<FileChunk>> GetChunksAsync(Guid fileId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return (await c.QueryAsync<FileChunk>(Cmd("SELECT * FROM FileChunks WHERE FileId=@fileId ORDER BY ChunkIndex", new { fileId }, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task InsertChunksAsync(IEnumerable<FileChunk> chunks, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(Cmd("""
            INSERT INTO FileChunks (ChunkId, FileId, ChunkIndex, Offset, PlainSize, StoredSize, Sha256, StoredMd5, AccountId, RemoteFileId, Status)
            VALUES (@ChunkId, @FileId, @ChunkIndex, @Offset, @PlainSize, @StoredSize, @Sha256, @StoredMd5, @AccountId, @RemoteFileId, @Status)
            """, chunks.ToList(), ct, tx)).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task UpdateChunkAsync(FileChunk k, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("""
            UPDATE FileChunks SET Sha256=@Sha256, StoredMd5=@StoredMd5, StoredSize=@StoredSize, AccountId=@AccountId,
                   RemoteFileId=@RemoteFileId, Status=@Status
            WHERE ChunkId=@ChunkId
            """, k, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FileChunk>> GetChunksByStatusAsync(IEnumerable<ChunkStatus> statuses, CancellationToken ct = default)
    {
        var list = statuses.Select(s => (int)s).ToArray();
        using var c = _db.Open();
        return (await c.QueryAsync<FileChunk>(Cmd("SELECT * FROM FileChunks WHERE Status IN @list", new { list }, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task DeleteChunksAsync(Guid fileId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM FileChunks WHERE FileId=@fileId", new { fileId }, ct)).ConfigureAwait(false);
    }


    private const string InsertTransferSql = """
        INSERT INTO Transfers (TransferId, BatchId, FileId, Direction, Name, State, TotalBytes, BytesDone, RetryCount, ErrorMessage, LocalPath, CreatedAt, UpdatedAt, CompletedAt)
        VALUES (@TransferId, @BatchId, @FileId, @Direction, @Name, @State, @TotalBytes, @BytesDone, @RetryCount, @ErrorMessage, @LocalPath, @CreatedAt, @UpdatedAt, @CompletedAt)
        """;

    public async Task InsertTransferAsync(TransferRecord t, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd(InsertTransferSql, t, ct)).ConfigureAwait(false);
    }

    public async Task InsertUploadAsync(VirtualFile file, TransferRecord transfer, CancellationToken ct = default)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(Cmd(InsertFileSql, file, ct, tx)).ConfigureAwait(false);
        await c.ExecuteAsync(Cmd(InsertTransferSql, transfer, ct, tx)).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task UpdateTransferAsync(TransferRecord t, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("""
            UPDATE Transfers SET State=@State, TotalBytes=@TotalBytes, BytesDone=@BytesDone, RetryCount=@RetryCount,
                   ErrorMessage=@ErrorMessage, LocalPath=@LocalPath, UpdatedAt=@UpdatedAt, CompletedAt=@CompletedAt
            WHERE TransferId=@TransferId
            """, t, ct)).ConfigureAwait(false);
    }

    public async Task<TransferRecord?> GetTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        return await c.QueryFirstOrDefaultAsync<TransferRecord>(Cmd("SELECT * FROM Transfers WHERE TransferId=@transferId", new { transferId }, ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TransferRecord>> GetTransfersAsync(CancellationToken ct = default)
    {
        using var c = _db.Open();
        return (await c.QueryAsync<TransferRecord>(Cmd("SELECT * FROM Transfers ORDER BY CreatedAt", null, ct)).ConfigureAwait(false)).ToList();
    }

    public async Task DeleteTransferAsync(Guid transferId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM Transfers WHERE TransferId=@transferId", new { transferId }, ct)).ConfigureAwait(false);
    }

    public async Task ClearFinishedTransfersAsync(CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM Transfers WHERE State IN (@completed, @cancelled)",
            new { completed = (int)TransferState.Completed, cancelled = (int)TransferState.Cancelled }, ct)).ConfigureAwait(false);
    }


    private sealed class SessionRow
    {
        public Guid ChunkId { get; set; }
        public Guid TransferId { get; set; }
        public string SessionUri { get; set; } = "";
        public long BytesUploaded { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public async Task UpsertSessionAsync(TransferSession s, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("""
            INSERT INTO TransferSessions (ChunkId, TransferId, SessionUri, BytesUploaded, CreatedAt, ExpiresAt)
            VALUES (@ChunkId, @TransferId, @SessionUri, @BytesUploaded, @CreatedAt, @ExpiresAt)
            ON CONFLICT(ChunkId) DO UPDATE SET TransferId=excluded.TransferId, SessionUri=excluded.SessionUri,
                BytesUploaded=excluded.BytesUploaded, CreatedAt=excluded.CreatedAt, ExpiresAt=excluded.ExpiresAt
            """, new
        {
            s.ChunkId, s.TransferId, SessionUri = _protector.Protect(s.SessionUri), s.BytesUploaded, s.CreatedAt, s.ExpiresAt,
        }, ct)).ConfigureAwait(false);
    }

    public async Task<TransferSession?> GetSessionAsync(Guid chunkId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        var row = await c.QueryFirstOrDefaultAsync<SessionRow>(Cmd("SELECT * FROM TransferSessions WHERE ChunkId=@chunkId", new { chunkId }, ct)).ConfigureAwait(false);
        if (row is null) return null;
        string uri;
        try { uri = _protector.Unprotect(row.SessionUri); }
        catch (System.Security.Cryptography.CryptographicException) { await DeleteSessionAsync(chunkId, ct).ConfigureAwait(false); return null; }
        return new TransferSession { ChunkId = row.ChunkId, TransferId = row.TransferId, SessionUri = uri, BytesUploaded = row.BytesUploaded, CreatedAt = row.CreatedAt, ExpiresAt = row.ExpiresAt };
    }

    public async Task DeleteSessionAsync(Guid chunkId, CancellationToken ct = default)
    {
        using var c = _db.Open();
        await c.ExecuteAsync(Cmd("DELETE FROM TransferSessions WHERE ChunkId=@chunkId", new { chunkId }, ct)).ConfigureAwait(false);
    }


    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var c = _db.Open();
        var files = await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM VirtualFiles WHERE Status=@s", new { s = (int)FileStatus.Stored }, ct)).ConfigureAwait(false);
        var folders = await c.ExecuteScalarAsync<int>(Cmd("SELECT COUNT(*) FROM VirtualFolders WHERE ParentId IS NOT NULL", null, ct)).ConfigureAwait(false);
        var perAccount = await c.QueryAsync<(string AccountId, long Bytes)>(Cmd(
            "SELECT AccountId, COALESCE(SUM(StoredSize),0) FROM FileChunks WHERE Status=@s GROUP BY AccountId", new { s = (int)ChunkStatus.Stored }, ct)).ConfigureAwait(false);
        var dict = perAccount.ToDictionary(x => x.AccountId, x => x.Bytes);
        return new IndexStats(files, folders, dict.Values.Sum(), dict);
    }
}
