using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace MultiDriveCloud.Infrastructure.Database;

public sealed class SqliteDatabase
{
    private static int _handlersRegistered;

    public string DatabasePath { get; }
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
        RegisterTypeHandlers();
    }

    private static void RegisterTypeHandlers()
    {
        if (Interlocked.Exchange(ref _handlersRegistered, 1) == 1) return;
        SqlMapper.AddTypeHandler(new GuidHandler());
        SqlMapper.AddTypeHandler(new DateTimeHandler());
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public static void ReleaseAll() => SqliteConnection.ClearAllPools();

    public int CurrentVersion()
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("PRAGMA user_version;");
    }

    public static int LatestVersion => Migrations.All.Max(m => m.Version);

    public void Migrate()
    {
        using (var conn = Open())
            conn.Execute("PRAGMA journal_mode=WAL;");
        using var c = Open();
        var version = c.ExecuteScalar<int>("PRAGMA user_version;");
        if (version > LatestVersion)
            throw new InvalidOperationException(
                $"La base de données (version {version}) a été créée par une version plus récente de l'application (version {LatestVersion} connue).");
        foreach (var m in Migrations.All.Where(m => m.Version > version).OrderBy(m => m.Version))
        {
            using var tx = c.BeginTransaction();
            c.Execute(m.Sql, transaction: tx);
            c.Execute($"PRAGMA user_version = {m.Version};", transaction: tx);
            tx.Commit();
        }
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value) => value is Guid g ? g : Guid.Parse((string)value);
        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("D");
        }
    }

    private sealed class DateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override DateTime Parse(object value) => value switch
        {
            DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Utc),
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            long ticks => new DateTime(ticks, DateTimeKind.Utc),
            _ => throw new InvalidCastException("Date invalide dans la base."),
        };

        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
    }
}

internal sealed record Migration(int Version, string Sql);

internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> All = new[]
    {
        new Migration(1, """
            CREATE TABLE Accounts (
                AccountId     TEXT PRIMARY KEY,
                Email         TEXT NOT NULL,
                Provider      TEXT NOT NULL,
                DisplayName   TEXT NOT NULL DEFAULT '',
                TotalBytes    INTEGER NOT NULL DEFAULT 0,
                UsedBytes     INTEGER NOT NULL DEFAULT 0,
                IsUnlimited   INTEGER NOT NULL DEFAULT 0,
                LastSync      TEXT NULL,
                Status        INTEGER NOT NULL DEFAULT 0,
                StatusMessage TEXT NULL,
                CreatedAt     TEXT NOT NULL
            );

            CREATE TABLE VirtualFolders (
                FolderId   TEXT PRIMARY KEY,
                ParentId   TEXT NULL REFERENCES VirtualFolders(FolderId) ON DELETE CASCADE,
                Name       TEXT NOT NULL,
                CreatedAt  TEXT NOT NULL,
                ModifiedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX UX_VirtualFolders_Parent_Name ON VirtualFolders(ParentId, Name COLLATE NOCASE) WHERE ParentId IS NOT NULL;
            INSERT INTO VirtualFolders (FolderId, ParentId, Name, CreatedAt, ModifiedAt)
            VALUES ('00000000-0000-0000-0000-000000000001', NULL, '', strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            CREATE TABLE VirtualFiles (
                FileId       TEXT PRIMARY KEY,
                FolderId     TEXT NOT NULL REFERENCES VirtualFolders(FolderId) ON DELETE CASCADE,
                Name         TEXT NOT NULL,
                TotalSize    INTEGER NOT NULL,
                Sha256       TEXT NOT NULL DEFAULT '',
                CreatedAt    TEXT NOT NULL,
                ModifiedAt   TEXT NOT NULL,
                Status       INTEGER NOT NULL,
                IsEncrypted  INTEGER NOT NULL DEFAULT 0,
                ChunkSize    INTEGER NOT NULL DEFAULT 0,
                OriginalPath TEXT NULL
            );
            CREATE UNIQUE INDEX UX_VirtualFiles_Folder_Name ON VirtualFiles(FolderId, Name COLLATE NOCASE);
            CREATE INDEX IX_VirtualFiles_Status ON VirtualFiles(Status);

            CREATE TABLE FileChunks (
                ChunkId      TEXT PRIMARY KEY,
                FileId       TEXT NOT NULL REFERENCES VirtualFiles(FileId) ON DELETE CASCADE,
                ChunkIndex   INTEGER NOT NULL,
                Offset       INTEGER NOT NULL,
                PlainSize    INTEGER NOT NULL,
                StoredSize   INTEGER NOT NULL,
                Sha256       TEXT NOT NULL DEFAULT '',
                StoredMd5    TEXT NOT NULL DEFAULT '',
                AccountId    TEXT NOT NULL REFERENCES Accounts(AccountId) ON DELETE RESTRICT,
                RemoteFileId TEXT NULL,
                Status       INTEGER NOT NULL,
                UNIQUE (FileId, ChunkIndex)
            );
            CREATE INDEX IX_FileChunks_Account ON FileChunks(AccountId);
            CREATE INDEX IX_FileChunks_Status ON FileChunks(Status);

            CREATE TABLE Transfers (
                TransferId   TEXT PRIMARY KEY,
                BatchId      TEXT NULL,
                FileId       TEXT NULL REFERENCES VirtualFiles(FileId) ON DELETE SET NULL,
                Direction    INTEGER NOT NULL,
                Name         TEXT NOT NULL,
                State        INTEGER NOT NULL,
                TotalBytes   INTEGER NOT NULL,
                BytesDone    INTEGER NOT NULL DEFAULT 0,
                RetryCount   INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT NULL,
                LocalPath    TEXT NOT NULL,
                CreatedAt    TEXT NOT NULL,
                UpdatedAt    TEXT NOT NULL,
                CompletedAt  TEXT NULL
            );
            CREATE INDEX IX_Transfers_State ON Transfers(State);

            CREATE TABLE TransferSessions (
                ChunkId       TEXT PRIMARY KEY REFERENCES FileChunks(ChunkId) ON DELETE CASCADE,
                TransferId    TEXT NOT NULL,
                SessionUri    TEXT NOT NULL,
                BytesUploaded INTEGER NOT NULL DEFAULT 0,
                CreatedAt     TEXT NOT NULL,
                ExpiresAt     TEXT NOT NULL
            );

            CREATE TABLE Settings (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """),
    };
}
