using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.Tests.Support;
using static MultiDriveCloud.Tests.Support.TestEnv;

namespace MultiDriveCloud.Tests;

public class AccountTests
{
    [Fact]
    public async Task Adding_several_accounts_sums_their_real_free_space()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 15 * MB);
        await env.AddAccountAsync("b@test", 10 * MB);
        var c = await env.AddAccountAsync("c@test", 8 * MB);
        env.Fake.ByEmail("c@test").ExtraUsed = 3 * MB;        await env.AccountService.RefreshAsync(c.AccountId);

        var accounts = await env.AccountService.GetAccountsAsync();
        Assert.Equal(3, accounts.Count);
        var summary = await env.AccountService.GetSummaryAsync();
        Assert.Equal(33 * MB, summary.TotalCapacity);
        Assert.Equal(15 * MB + 10 * MB + 5 * MB, summary.FreeBytes);        Assert.Equal(summary.FreeBytes, summary.UsableBytes);        Assert.Equal(0, summary.UsedByApp);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Safety_margin_is_deducted_from_usable_space()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 20 * MB);
        await env.UpdateSettingsAsync(s => s.SafetyMarginBytes = 5 * MB);
        Assert.Equal(15 * MB, await env.Allocator.GetUsableBytesAsync());
        env.DeleteRoot();
    }

    [Fact]
    public async Task Disconnected_account_is_kept_but_never_used_for_new_data()
    {
        await using var env = new TestEnv();
        var a = await env.AddAccountAsync("a@test", 20 * MB);
        await env.AddAccountAsync("b@test", 20 * MB);
        await env.AccountService.DisconnectAsync(a.AccountId);

        Assert.True(env.Fake.ByEmail("a@test").Revoked);
        var lease = await env.Allocator.TryAllocateAsync(MB, MB);
        Assert.NotNull(lease);
        Assert.Equal("fake:b@test", lease!.AccountId);
        var summary = await env.AccountService.GetSummaryAsync();
        Assert.Equal(2, summary.AccountCount);
        Assert.Equal(1, summary.ConnectedCount);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Revoked_authorisation_is_detected_on_refresh()
    {
        await using var env = new TestEnv();
        var a = await env.AddAccountAsync("a@test", 20 * MB);
        env.Fake.ByEmail("a@test").Revoked = true;
        var refreshed = await env.AccountService.RefreshAsync(a.AccountId);
        Assert.Equal(AccountStatus.NeedsReauthorization, refreshed!.Status);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Account_holding_fragments_cannot_be_removed()
    {
        await using var env = new TestEnv();
        var a = await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("x.bin", 2 * MB));
        await Assert.ThrowsAsync<AccountInUseException>(() => env.AccountService.RemoveAsync(a.AccountId));
        env.DeleteRoot();
    }
}

public class AllocatorTests
{
    [Fact]
    public async Task Files_are_spread_over_several_accounts_automatically()
    {
        await using var env = new TestEnv(chunkSize: 64 * MB);
        await env.AddAccountAsync("a@test", 12 * MB);
        await env.AddAccountAsync("b@test", 12 * MB);
        await env.AddAccountAsync("c@test", 12 * MB);
        var files = Enumerable.Range(0, 6).Select(i => env.CreateFile($"f{i}.bin", 5 * MB, i + 1)).ToArray();

        await env.UploadAndWaitAsync(files);

        Assert.All(env.Snapshots(), s => Assert.Equal(TransferState.Completed, s.State));
        var used = env.Fake.Drives.Values.Select(d => d.Objects.Count).ToList();
        Assert.Equal(6, used.Sum());
        Assert.All(used, n => Assert.True(n >= 1 && n <= 3, $"distribution inattendue : {string.Join(",", used)}"));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Allocation_is_refused_when_space_is_insufficient_and_nothing_is_queued()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 6 * MB);
        await env.AddAccountAsync("b@test", 6 * MB);
        var big = env.CreateFile("big.bin", 20 * MB);

        var ex = await Assert.ThrowsAsync<InsufficientStorageException>(() =>
            env.Transfers.EnqueueUploadAsync(new[] { big }, VirtualFolder.RootId));

        Assert.True(ex.RequiredBytes > ex.AvailableBytes);
        Assert.Empty(env.Transfers.GetSnapshots());
        var (_, files) = await env.Vfs.ListAsync(VirtualFolder.RootId);
        Assert.Empty(files);
        Assert.True(File.Exists(big));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Concurrent_reservations_never_promise_the_same_space_twice()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 10 * MB);
        var l1 = await env.Allocator.TryAllocateAsync(6 * MB, 6 * MB);
        var l2 = await env.Allocator.TryAllocateAsync(6 * MB, 6 * MB);
        Assert.NotNull(l1);
        Assert.Null(l2);        env.Allocator.Release(l1);
        Assert.NotNull(await env.Allocator.TryAllocateAsync(6 * MB, 6 * MB));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Another_drive_is_used_when_one_reports_full_during_upload()
    {
        await using var env = new TestEnv(chunkSize: 64 * MB);
        var a = await env.AddAccountAsync("a@test", 100 * MB);        await env.AddAccountAsync("b@test", 50 * MB);
        env.Fake.FailUploadsRemaining[a.AccountId] = 1000;
        env.Fake.FailUploadKind[a.AccountId] = ProviderErrorKind.StorageFull;

        await env.UploadAndWaitAsync(env.CreateFile("one.bin", 3 * MB));

        Assert.Equal(TransferState.Completed, env.Snapshots().Single().State);
        Assert.Empty(env.Fake.ByEmail("a@test").Objects);
        Assert.Single(env.Fake.ByEmail("b@test").Objects);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Strategies_choose_as_documented()
    {
        var small = new CloudAccount { AccountId = "s", TotalBytes = 10, UsedBytes = 0 };
        var big = new CloudAccount { AccountId = "b", TotalBytes = 100, UsedBytes = 60 };
        var cands = new List<AllocationCandidate> { new(small, 10), new(big, 40) };
        Assert.Equal("b", new LargestFreeSpaceStrategy().Select(cands, 5)!.Account.AccountId);
        Assert.Equal("s", new BalancedUsageStrategy().Select(cands, 5)!.Account.AccountId);        Assert.Equal("s", new BestFitStrategy().Select(cands, 5)!.Account.AccountId);
        await Task.CompletedTask;
    }
}

public class ChunkTests
{
    [Fact]
    public void Split_covers_the_file_exactly_in_order()
    {
        var parts = ChunkManager.Split(25, 10);
        Assert.Equal(new[] { (0L, 10L), (10L, 10L), (20L, 5L) }, parts.Select(p => (p.Offset, p.Length)).ToArray());
        Assert.Single(ChunkManager.Split(0, 10));
    }

    [Fact]
    public async Task Big_file_is_split_over_three_accounts_and_rebuilt_identically()
    {
        await using var env = new TestEnv(chunkSize: 8 * MB);
        await env.AddAccountAsync("a@test", 10 * MB);
        await env.AddAccountAsync("b@test", 10 * MB);
        await env.AddAccountAsync("c@test", 10 * MB);
        var source = env.CreateFile("film.bin", 25 * MB + 12345, seed: 42);

        await env.UploadAndWaitAsync(source);

        var up = env.Snapshots(TransferDirection.Upload).Single();
        Assert.True(up.State == TransferState.Completed, up.ErrorMessage);
        var (_, files) = await env.Vfs.ListAsync(VirtualFolder.RootId);
        var file = Assert.Single(files);
        Assert.Equal(FileStatus.Stored, file.Status);
        var chunks = await env.Index.GetChunksAsync(file.FileId);
        Assert.True(chunks.Count >= 4);
        ChunkManager.ValidateLayout(chunks, file.TotalSize);
        Assert.Equal(3, chunks.Select(c => c.AccountId).Distinct().Count());
        Assert.All(chunks, c => { Assert.Equal(ChunkStatus.Stored, c.Status); Assert.False(string.IsNullOrEmpty(c.RemoteFileId)); Assert.Equal(64, c.Sha256.Length); });
        Assert.Equal(await IntegrityService.Sha256FileAsync(source), file.Sha256);

        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });
        var down = env.Snapshots(TransferDirection.Download).Single();
        Assert.True(down.State == TransferState.Completed, down.ErrorMessage);
        var restored = Path.Combine(env.DownloadDir, "film.bin");
        Assert.True(FilesEqual(source, restored));
        Assert.False(File.Exists(restored + ".mdcpart"));
        Assert.True(File.Exists(source));        env.DeleteRoot();
    }

    [Fact]
    public async Task Small_file_stays_whole_in_one_fragment()
    {
        await using var env = new TestEnv(chunkSize: 8 * MB);
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("small.bin", 3 * MB));
        var (_, files) = await env.Vfs.ListAsync(VirtualFolder.RootId);
        Assert.Single(await env.Index.GetChunksAsync(files.Single().FileId));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Empty_file_roundtrips()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 10 * MB);
        var empty = env.CreateFile("empty.txt", 0);
        await env.UploadAndWaitAsync(empty);
        var (_, files) = await env.Vfs.ListAsync(VirtualFolder.RootId);
        Assert.Equal(FileStatus.Stored, files.Single().Status);
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, files.Single().FileId) });
        Assert.Equal(0, new FileInfo(Path.Combine(env.DownloadDir, "empty.txt")).Length);
        env.DeleteRoot();
    }
}

public class IntegrityTests
{
    [Fact]
    public async Task Checksums_match_a_reference_and_detect_changes()
    {
        await using var env = new TestEnv();
        var path = env.CreateFile("data.bin", 3 * MB + 7, 7);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(expected, await IntegrityService.Sha256FileAsync(path));

        var ranges = ChunkManager.Split(new FileInfo(path).Length, MB);
        var result = await IntegrityService.HashFileWithFragmentsAsync(path, ranges, null, CancellationToken.None);
        Assert.Equal(expected, result.FileSha256);
        Assert.Equal(ranges.Count, result.Fragments.Count);
        Assert.Equal(result.Fragments[1].Sha256, await IntegrityService.Sha256RangeAsync(path, MB, MB));

        var bytes = await File.ReadAllBytesAsync(path);
        bytes[10] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        Assert.NotEqual(expected, await IntegrityService.Sha256FileAsync(path));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Corrupted_download_is_reported_and_never_announced_as_success()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("doc.bin", 2 * MB));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();

        env.Fake.CorruptEveryDownload = true;
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });

        var down = env.Snapshots(TransferDirection.Download).Single();
        Assert.Equal(TransferState.Failed, down.State);
        Assert.Contains("corrompu", down.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(env.DownloadDir, "doc.bin")));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Upload_corrupted_in_transit_is_detected_by_the_drive_checksum_and_retried()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        var source = env.CreateFile("doc.bin", 2 * MB);
        env.Fake.CorruptNextUploadOnce = true;

        await env.UploadAndWaitAsync(source);

        var up = env.Snapshots(TransferDirection.Upload).Single();
        Assert.True(up.State == TransferState.Completed, up.ErrorMessage);
        Assert.True(up.RetryCount >= 1);
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "doc.bin")));
        env.DeleteRoot();
    }
}
