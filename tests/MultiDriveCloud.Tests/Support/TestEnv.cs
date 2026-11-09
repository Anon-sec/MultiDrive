using Microsoft.Extensions.Logging.Abstractions;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.Infrastructure;
using MultiDriveCloud.Infrastructure.Database;
using MultiDriveCloud.Infrastructure.Security;

namespace MultiDriveCloud.Tests.Support;

public sealed class TestEnv : IAsyncDisposable
{
    public const long MB = 1024 * 1024;

    public string Root { get; }
    public AppPaths Paths { get; }
    public SqliteDatabase Db { get; }
    public FakeStorageProvider Fake { get; }
    public MemorySecretStore Secrets { get; }
    public AccountRepository Accounts { get; }
    public IndexRepository Index { get; }
    public ISettingsService Settings { get; }
    public StorageAllocator Allocator { get; }
    public ChunkManager Chunks { get; }
    public AccountService AccountService { get; }
    public FragmentCleaner Cleaner { get; }
    public EncryptionKeyManager Keys { get; }
    public TransferManager Transfers { get; }
    public VirtualFileSystemService Vfs { get; }
    public IndexBackupService Backup { get; }

    public string SourceDir => Path.Combine(Root, "source");
    public string DownloadDir => Path.Combine(Root, "downloads");

    public TestEnv(string? existingRoot = null, FakeStorageProvider? fake = null, MemorySecretStore? secrets = null, long chunkSize = 4 * MB, int parallel = 2)
    {
        Root = existingRoot ?? Path.Combine(Path.GetTempPath(), "mdc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(DownloadDir);
        Paths = new AppPaths(Path.Combine(Root, "data"));
        Db = new SqliteDatabase(Paths.Database);
        Db.Migrate();
        Fake = fake ?? new FakeStorageProvider();
        Secrets = secrets ?? new MemorySecretStore();
        Accounts = new AccountRepository(Db);
        Index = new IndexRepository(Db, new PassthroughProtector());
        Settings = new SettingsService(new KeyValueStore(Db), DownloadDir, Paths.Temp);
        Settings.LoadAsync().GetAwaiter().GetResult();
        var s = Settings.Current;
        s.SafetyMarginBytes = 0;
        s.ChunkSizeBytes = chunkSize;
        s.FragmentParallelism = parallel;
        s.MaxRetries = 3;
        s.DownloadDirectory = DownloadDir;
        s.TempDirectory = Paths.Temp;
        Settings.SaveAsync(s).GetAwaiter().GetResult();

        var strategies = new IAllocationStrategy[] { new LargestFreeSpaceStrategy(), new BalancedUsageStrategy(), new BestFitStrategy() };
        Allocator = new StorageAllocator(Accounts, Settings, strategies, NullLogger<StorageAllocator>.Instance);
        Chunks = new ChunkManager(Allocator);
        var registry = new ProviderRegistry(new[] { Fake });
        AccountService = new AccountService(registry, Accounts, Index, Allocator, NullLogger<AccountService>.Instance);
        Cleaner = new FragmentCleaner(registry, Accounts, Index, Allocator, NullLogger<FragmentCleaner>.Instance);
        Keys = new EncryptionKeyManager(Secrets);
        Transfers = new TransferManager(Index, Accounts, registry, Allocator, Chunks, Cleaner, Settings, Keys, AccountService,
            NullLogger<TransferManager>.Instance,
            new TransferManagerOptions { DelayAsync = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } });
        Vfs = new VirtualFileSystemService(Index, Accounts, Cleaner, Transfers, NullLogger<VirtualFileSystemService>.Instance);
        Backup = new IndexBackupService(Db, Index, Accounts);
    }

    public async Task<CloudAccount> AddAccountAsync(string email, long capacity)
    {
        Fake.EnqueueIdentity(email, capacity);
        return await AccountService.AddAccountAsync();
    }

    public async Task UpdateSettingsAsync(Action<AppSettings> change)
    {
        var s = Settings.Current;
        change(s);
        await Settings.SaveAsync(s);
    }

    public string CreateFile(string relativePath, long size, int seed = 1)
    {
        var path = Path.Combine(SourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rng = new Random(seed);
        using var fs = File.Create(path);
        var buffer = new byte[256 * 1024];
        long written = 0;
        while (written < size)
        {
            rng.NextBytes(buffer);
            var n = (int)Math.Min(buffer.Length, size - written);
            fs.Write(buffer, 0, n);
            written += n;
        }
        return path;
    }

    public async Task<EnqueueResult> UploadAndWaitAsync(params string[] paths)
    {
        var result = await Transfers.EnqueueUploadAsync(paths, VirtualFolder.RootId);
        await Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        return result;
    }

    public async Task<EnqueueResult> DownloadAndWaitAsync(IEnumerable<ItemRef> items, string? target = null)
    {
        var result = await Transfers.EnqueueDownloadAsync(items, target ?? DownloadDir);
        await Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(60));
        return result;
    }

    public IReadOnlyList<TransferSnapshot> Snapshots(TransferDirection? dir = null) =>
        Transfers.GetSnapshots().Where(s => dir is null || s.Direction == dir).ToList();

    public static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        var ba = new byte[81920];
        var bb = new byte[81920];
        while (true)
        {
            var na = sa.Read(ba, 0, ba.Length);
            var nb = 0;
            while (nb < na) { var r = sb.Read(bb, nb, na - nb); if (r == 0) return false; nb += r; }
            if (na == 0) return true;
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, na))) return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Transfers.ShutdownAsync();
        SqliteDatabase.ReleaseAll();
    }

    public void DeleteRoot()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
