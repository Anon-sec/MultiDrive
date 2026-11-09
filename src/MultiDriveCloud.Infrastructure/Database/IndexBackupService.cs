using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;

namespace MultiDriveCloud.Infrastructure.Database;

public sealed class IndexBackupService : IIndexBackupService
{
    private readonly SqliteDatabase _db;
    private readonly IIndexRepository _index;
    private readonly IAccountRepository _accounts;

    public IndexBackupService(SqliteDatabase db, IIndexRepository index, IAccountRepository accounts)
    {
        _db = db;
        _index = index;
        _accounts = accounts;
    }

    public async Task BackupAsync(string destinationPath, CancellationToken ct = default)
    {
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tmp = destinationPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var conn = _db.Open())
        {
            await conn.ExecuteAsync(new CommandDefinition($"VACUUM INTO '{tmp.Replace("'", "''")}'", cancellationToken: ct)).ConfigureAwait(false);
        }
        File.Move(tmp, destinationPath, overwrite: true);
    }

    public async Task<string> RestoreAsync(string backupPath, CancellationToken ct = default)
    {
        backupPath = Path.GetFullPath(backupPath);
        if (!File.Exists(backupPath)) throw new FileNotFoundException("Sauvegarde introuvable.", backupPath);
        if (string.Equals(backupPath, Path.GetFullPath(_db.DatabasePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("La sauvegarde est la base active.");

        var cs = new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using (var probe = new SqliteConnection(cs))
        {
            probe.Open();
            var integrity = await probe.ExecuteScalarAsync<string>("PRAGMA integrity_check;").ConfigureAwait(false);
            if (integrity != "ok") throw new InvalidDataException("La sauvegarde est corrompue (integrity_check : " + integrity + ").");
            var tables = (await probe.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'").ConfigureAwait(false)).ToHashSet();
            foreach (var required in new[] { "Accounts", "VirtualFiles", "VirtualFolders", "FileChunks", "Transfers" })
                if (!tables.Contains(required)) throw new InvalidDataException("Ce fichier n'est pas une sauvegarde de MultiDrive Cloud (table " + required + " absente).");
            var version = await probe.ExecuteScalarAsync<int>("PRAGMA user_version;").ConfigureAwait(false);
            if (version > SqliteDatabase.LatestVersion)
                throw new InvalidDataException("Cette sauvegarde provient d'une version plus récente de l'application.");
        }

        var live = Path.GetFullPath(_db.DatabasePath);
        var safety = Path.Combine(Path.GetDirectoryName(live)!, $"index-before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await BackupAsync(safety, ct).ConfigureAwait(false);

        SqliteDatabase.ReleaseAll();
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(live + suffix)) File.Delete(live + suffix);
        File.Copy(backupPath, live, overwrite: true);
        _db.Migrate();
        return safety;
    }

    public async Task ExportRecoveryJsonAsync(string destinationPath, CancellationToken ct = default)
    {
        var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        var accountById = accounts.ToDictionary(a => a.AccountId);
        var files = await _index.GetFilesRecursiveAsync(VirtualFolder.RootId, ct).ConfigureAwait(false);
        var list = new List<object>();
        foreach (var entry in files.Where(f => f.File.Status != FileStatus.Pending))
        {
            var f = entry.File;
            var chunks = await _index.GetChunksAsync(f.FileId, ct).ConfigureAwait(false);
            list.Add(new
            {
                fileId = f.FileId,
                path = (entry.FolderPath.Length == 0 ? "" : entry.FolderPath + "/") + f.Name,
                size = f.TotalSize,
                sha256 = f.Sha256,
                encrypted = f.IsEncrypted,
                status = f.Status.ToString(),
                modifiedUtc = f.ModifiedAt,
                fragments = chunks.Where(c => c.Status != ChunkStatus.Deleted).Select(c => new
                {
                    index = c.ChunkIndex,
                    offset = c.Offset,
                    plainSize = c.PlainSize,
                    storedSize = c.StoredSize,
                    sha256 = c.Sha256,
                    storedMd5 = c.StoredMd5,
                    accountId = c.AccountId,
                    accountEmail = accountById.TryGetValue(c.AccountId, out var a) ? a.Email : null,
                    remoteFileId = c.RemoteFileId,
                    remoteName = $"{f.FileId:N}.{c.ChunkIndex:D5}.mdc",
                }),
            });
        }
        var doc = new
        {
            format = "multidrive-recovery",
            version = 1,
            exportedUtc = DateTime.UtcNow,
            note = "Aucun jeton ni clé de chiffrement dans ce fichier. Voir docs/RECOVERY.md.",
            accounts = accounts.Select(a => new { a.AccountId, a.Email, a.Provider }),
            files = list,
        };
        await File.WriteAllTextAsync(destinationPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
    }
}
