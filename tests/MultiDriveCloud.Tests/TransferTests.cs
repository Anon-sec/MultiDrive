using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.Infrastructure.Security;
using MultiDriveCloud.Tests.Support;
using static MultiDriveCloud.Tests.Support.TestEnv;

namespace MultiDriveCloud.Tests;

public class ResilienceTests
{
    [Fact]
    public async Task Temporary_network_errors_are_retried_automatically()
    {
        await using var env = new TestEnv();
        var a = await env.AddAccountAsync("a@test", 50 * MB);
        env.Fake.FailUploadsRemaining[a.AccountId] = 2;        var source = env.CreateFile("doc.bin", 3 * MB);

        await env.UploadAndWaitAsync(source);

        var up = env.Snapshots().Single();
        Assert.True(up.State == TransferState.Completed, up.ErrorMessage);
        Assert.True(up.RetryCount >= 2);
        Assert.Equal(3, env.Fake.UploadCalls.Values.Sum());
        env.DeleteRoot();
    }

    [Fact]
    public async Task Long_outage_fails_clearly_keeps_the_source_and_can_be_resumed()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        var source = env.CreateFile("doc.bin", 3 * MB);
        env.Fake.NetworkDown = true;

        await env.Transfers.EnqueueUploadAsync(new[] { source }, VirtualFolder.RootId);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var failed = env.Snapshots().Single();
        Assert.Equal(TransferState.Failed, failed.State);
        Assert.False(string.IsNullOrWhiteSpace(failed.ErrorMessage));
        Assert.True(File.Exists(source));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.NotEqual(FileStatus.Stored, file.Status);
        env.Fake.NetworkDown = false;
        env.Transfers.Resume(failed.TransferId);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, env.Snapshots().Single().State);
        Assert.Equal(FileStatus.Stored, (await env.Index.GetFileAsync(file.FileId))!.Status);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Interrupted_upload_resumes_after_restart_without_resending_finished_fragments()
    {
        var fake = new FakeStorageProvider();
        var secrets = new MemorySecretStore();
        var root = Path.Combine(Path.GetTempPath(), "mdc-tests-" + Guid.NewGuid().ToString("N"));
        string sourcePath;
        Guid fileId;

        await using (var env1 = new TestEnv(root, fake, secrets, chunkSize: 4 * MB, parallel: 1))
        {
            await env1.AddAccountAsync("a@test", 100 * MB);
            sourcePath = env1.CreateFile("big.bin", 12 * MB, 5);
            fake.HangOnFragmentSuffix = ":1";
            var result = await env1.Transfers.EnqueueUploadAsync(new[] { sourcePath }, VirtualFolder.RootId);
            for (var i = 0; i < 400 && !fake.HangReached; i++) await Task.Delay(25);
            Assert.True(fake.HangReached, "le fragment 1 aurait dû être en cours d'envoi");
            await env1.Transfers.ShutdownAsync();
            fileId = (await env1.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single().FileId;
            var chunks = await env1.Index.GetChunksAsync(fileId);
            Assert.Equal(ChunkStatus.Stored, chunks[0].Status);
            Assert.NotEqual(ChunkStatus.Stored, chunks[1].Status);
            Assert.NotNull(await env1.Index.GetSessionAsync(chunks[1].ChunkId));            Assert.Equal(TransferState.Queued, env1.Snapshots().Single().State);
            SqliteDatabaseRelease();
        }

        fake.HangOnFragmentSuffix = null;
        fake.HangReached = false;
        await using (var env2 = new TestEnv(root, fake, secrets, chunkSize: 4 * MB, parallel: 1))
        {
            await env2.Transfers.RecoverAsync();
            await env2.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(60));
            var snap = env2.Snapshots().Single();
            Assert.True(snap.State == TransferState.Completed, snap.ErrorMessage);
            Assert.Equal(FileStatus.Stored, (await env2.Index.GetFileAsync(fileId))!.Status);
            Assert.Equal(1, fake.UploadCalls[$"{fileId}:0"]);            Assert.True(fake.ResumeOffsets[$"{fileId}:1"] >= 1024 * 1024);            await env2.DownloadAndWaitAsync(new[] { new ItemRef(false, fileId) });
            Assert.True(FilesEqual(sourcePath, Path.Combine(env2.DownloadDir, "big.bin")));
            env2.DeleteRoot();
        }

        static void SqliteDatabaseRelease() => MultiDriveCloud.Infrastructure.Database.SqliteDatabase.ReleaseAll();
    }

    [Fact]
    public async Task Pause_then_resume_completes_and_cancel_cleans_remote_fragments()
    {
        var fake = new FakeStorageProvider();
        await using var env = new TestEnv(fake: fake, chunkSize: 4 * MB, parallel: 1);
        await env.AddAccountAsync("a@test", 100 * MB);
        var source = env.CreateFile("big.bin", 10 * MB, 9);

        fake.HangOnFragmentSuffix = ":1";
        await env.Transfers.EnqueueUploadAsync(new[] { source }, VirtualFolder.RootId);
        for (var i = 0; i < 400 && !fake.HangReached; i++) await Task.Delay(25);
        var id = env.Snapshots().Single().TransferId;
        env.Transfers.Pause(id);
        for (var i = 0; i < 400 && env.Transfers.GetSnapshot(id)!.State != TransferState.Paused; i++) await Task.Delay(25);
        Assert.Equal(TransferState.Paused, env.Transfers.GetSnapshot(id)!.State);

        fake.HangOnFragmentSuffix = null;
        env.Transfers.Resume(id);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, env.Transfers.GetSnapshot(id)!.State);

        var second = env.CreateFile("second.bin", 10 * MB, 10);
        fake.HangReached = false;
        fake.HangOnFragmentSuffix = ":1";
        await env.Transfers.EnqueueUploadAsync(new[] { second }, VirtualFolder.RootId);
        for (var i = 0; i < 400 && !fake.HangReached; i++) await Task.Delay(25);
        var id2 = env.Snapshots().First(s => s.Name == "second.bin").TransferId;
        env.Transfers.Cancel(id2);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Cancelled, env.Transfers.GetSnapshot(id2)!.State);
        var (_, files) = await env.Vfs.ListAsync(VirtualFolder.RootId);
        Assert.Equal(new[] { "big.bin" }, files.Select(f => f.Name).ToArray());
        Assert.Equal(3, env.Fake.TotalObjects);        Assert.True(File.Exists(second));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Disconnected_account_makes_download_fail_explicitly_then_succeed_after_reconnection()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 6 * MB);
        var b = await env.AddAccountAsync("b@test", 6 * MB);
        var source = env.CreateFile("pair.bin", 10 * MB, 3);
        await env.UploadAndWaitAsync(source);
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.Equal(2, (await env.Index.GetChunksAsync(file.FileId)).Select(c => c.AccountId).Distinct().Count());

        await env.AccountService.DisconnectAsync(b.AccountId);
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });

        var failed = env.Snapshots(TransferDirection.Download).Single();
        Assert.Equal(TransferState.Failed, failed.State);
        Assert.Contains("b@test", failed.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(env.DownloadDir, "pair.bin")));
        Assert.False(File.Exists(Path.Combine(env.DownloadDir, "pair.bin.mdcpart")));

        env.Fake.EnqueueIdentity("b@test", 6 * MB);        await env.AccountService.ReauthorizeAsync(b.AccountId);
        env.Transfers.Resume(failed.TransferId);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, env.Snapshots(TransferDirection.Download).Single().State);
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "pair.bin")));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Missing_remote_fragment_gives_an_explicit_error()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("f.bin", 9 * MB));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        var victim = env.Fake.ByEmail("a@test").Objects.Keys.First();
        env.Fake.ByEmail("a@test").Objects.TryRemove(victim, out _);

        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });

        var snap = env.Snapshots(TransferDirection.Download).Single();
        Assert.Equal(TransferState.Failed, snap.State);
        Assert.Contains("introuvable", snap.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(env.DownloadDir, "f.bin")));
        env.DeleteRoot();
    }

    [Fact]
    public async Task File_modified_during_upload_is_not_considered_stored()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        var source = env.CreateFile("live.bin", 2 * MB);
        env.Fake.BeforeUpload = _ =>
        {
            env.Fake.BeforeUpload = null;
            File.AppendAllText(source, "changed while uploading");
        };

        await env.UploadAndWaitAsync(source);

        var snap = env.Snapshots().Single();
        Assert.Equal(TransferState.Failed, snap.State);
        Assert.Contains("modifié", snap.ErrorMessage);
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.NotEqual(FileStatus.Stored, file.Status);
        Assert.True(File.Exists(source));

        env.Transfers.Resume(snap.TransferId);
        await env.Transfers.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(TransferState.Completed, env.Snapshots().Single().State);
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, file.FileId) });
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "live.bin")));
        env.DeleteRoot();
    }
}

public class VirtualFileSystemTests
{
    [Fact]
    public async Task Deleting_a_distributed_file_removes_every_remote_fragment()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 8 * MB);
        await env.AddAccountAsync("b@test", 8 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("d.bin", 12 * MB));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.True(env.Fake.TotalObjects >= 3);

        var report = await env.Vfs.DeleteAsync(new[] { new ItemRef(false, file.FileId) });

        Assert.True(report.Success);
        Assert.Equal(0, env.Fake.TotalObjects);
        Assert.Null(await env.Index.GetFileAsync(file.FileId));
        Assert.Empty(await env.Index.GetChunksAsync(file.FileId));
        Assert.Equal(0, (await env.Accounts.GetAllAsync()).Sum(a => a.UsedBytes));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Failed_remote_deletion_keeps_the_file_pending_and_can_be_retried()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("keep.bin", 2 * MB));
        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();

        env.Fake.FailDeletes = true;
        var report = await env.Vfs.DeleteAsync(new[] { new ItemRef(false, file.FileId) });
        Assert.False(report.Success);
        Assert.Equal(FileStatus.Deleting, (await env.Index.GetFileAsync(file.FileId))!.Status);
        Assert.Equal(1, env.Fake.TotalObjects);
        Assert.Empty((await env.Vfs.ListAsync(VirtualFolder.RootId)).Files);        Assert.Equal(1, await env.Vfs.CountPendingDeletionsAsync());

        env.Fake.FailDeletes = false;
        var retry = await env.Vfs.ResumePendingDeletionsAsync();
        Assert.True(retry.Success);
        Assert.Equal(0, env.Fake.TotalObjects);
        Assert.Null(await env.Index.GetFileAsync(file.FileId));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Whole_folder_tree_uploads_and_downloads_with_structure_preserved()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 30 * MB);
        await env.AddAccountAsync("b@test", 30 * MB);
        env.CreateFile(@"Photos\2024\a.jpg", 600_000, 1);
        env.CreateFile(@"Photos\2024\b.jpg", 700_000, 2);
        env.CreateFile(@"Photos\notes.txt", 1_000, 3);
        env.CreateFile(@"Photos\video.bin", 9 * MB, 4);
        Directory.CreateDirectory(Path.Combine(env.SourceDir, "Photos", "Empty"));

        var result = await env.UploadAndWaitAsync(Path.Combine(env.SourceDir, "Photos"));
        Assert.Equal(4, result.FileCount);
        Assert.All(env.Snapshots(), s => Assert.True(s.State == TransferState.Completed, s.ErrorMessage));

        var root = await env.Vfs.ListAsync(VirtualFolder.RootId);
        var photos = Assert.Single(root.Folders);
        Assert.Equal("Photos", photos.Name);
        var all = await env.Index.GetFilesRecursiveAsync(photos.FolderId);
        Assert.Equal(4, all.Count);
        Assert.Contains(all, e => e.FolderPath == "2024" && e.File.Name == "a.jpg");

        var target = Path.Combine(env.Root, "restored");
        await env.DownloadAndWaitAsync(new[] { new ItemRef(true, photos.FolderId) }, target);

        Assert.All(env.Snapshots(TransferDirection.Download), s => Assert.True(s.State == TransferState.Completed, s.ErrorMessage));
        foreach (var rel in new[] { @"2024\a.jpg", @"2024\b.jpg", "notes.txt", "video.bin" })
            Assert.True(FilesEqual(Path.Combine(env.SourceDir, "Photos", rel), Path.Combine(target, "Photos", rel)), rel);
        Assert.True(Directory.Exists(Path.Combine(target, "Photos", "Empty")));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Create_rename_move_search_and_conflicts()
    {
        await using var env = new TestEnv();
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.UploadAndWaitAsync(env.CreateFile("report.txt", 1000), env.CreateFile("other.txt", 1000, 2));

        var docs = await env.Vfs.CreateFolderAsync(VirtualFolder.RootId, "Docs");
        var sub = await env.Vfs.CreateFolderAsync(docs.FolderId, "Sub");
        await Assert.ThrowsAsync<NameConflictException>(() => env.Vfs.CreateFolderAsync(VirtualFolder.RootId, "docs"));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Vfs.CreateFolderAsync(VirtualFolder.RootId, "bad:name"));

        var report = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single(f => f.Name == "report.txt");
        await env.Vfs.MoveAsync(new[] { new ItemRef(false, report.FileId) }, sub.FolderId);
        Assert.Equal(sub.FolderId, (await env.Index.GetFileAsync(report.FileId))!.FolderId);

        await env.Vfs.RenameAsync(new ItemRef(false, report.FileId), "final.txt");
        var hits = await env.Vfs.SearchAsync("FINAL");
        Assert.Equal("final.txt", Assert.Single(hits.Files).Name);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            env.Vfs.MoveAsync(new[] { new ItemRef(true, docs.FolderId) }, sub.FolderId));        var other = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single(f => f.Name == "other.txt");
        await env.Vfs.RenameAsync(new ItemRef(false, other.FileId), "final.txt");        await Assert.ThrowsAsync<NameConflictException>(() =>
            env.Vfs.MoveAsync(new[] { new ItemRef(false, other.FileId) }, sub.FolderId));        Assert.Equal(VirtualFolder.RootId, (await env.Index.GetFileAsync(other.FileId))!.FolderId);

        var path = await env.Index.GetFolderPathAsync(sub.FolderId);
        Assert.Equal(new[] { "Docs", "Sub" }, path.Select(f => f.Name).ToArray());
        var props = await env.Vfs.GetPropertiesAsync(report.FileId);
        Assert.Equal("/Docs/Sub/final.txt", props!.VirtualPath);
        env.DeleteRoot();
    }
}

public class IndexBackupTests
{
    [Fact]
    public async Task Index_can_be_backed_up_and_restored_and_files_remain_downloadable()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 20 * MB);
        await env.AddAccountAsync("b@test", 20 * MB);
        var source = env.CreateFile("precious.bin", 9 * MB, 8);
        await env.UploadAndWaitAsync(source);
        var backup = Path.Combine(env.Root, "backup.db");
        await env.Backup.BackupAsync(backup);

        var file = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        await env.Index.DeleteFileAsync(file.FileId);
        Assert.Empty((await env.Vfs.ListAsync(VirtualFolder.RootId)).Files);

        var safety = await env.Backup.RestoreAsync(backup);
        Assert.True(File.Exists(safety));

        var restored = (await env.Vfs.ListAsync(VirtualFolder.RootId)).Files.Single();
        Assert.Equal(file.FileId, restored.FileId);
        Assert.Equal(file.Sha256, restored.Sha256);
        Assert.Equal(3, (await env.Index.GetChunksAsync(restored.FileId)).Count);
        await env.DownloadAndWaitAsync(new[] { new ItemRef(false, restored.FileId) });
        Assert.True(FilesEqual(source, Path.Combine(env.DownloadDir, "precious.bin")));
        env.DeleteRoot();
    }

    [Fact]
    public async Task Restore_refuses_a_file_that_is_not_a_backup()
    {
        await using var env = new TestEnv();
        var bogus = Path.Combine(env.Root, "bogus.db");
        await File.WriteAllTextAsync(bogus, "not a database at all, just text that is long enough");
        await Assert.ThrowsAnyAsync<Exception>(() => env.Backup.RestoreAsync(bogus));
        Assert.Empty((await env.Vfs.ListAsync(VirtualFolder.RootId)).Files);        env.DeleteRoot();
    }

    [Fact]
    public async Task Recovery_export_lists_fragments_without_any_secret()
    {
        await using var env = new TestEnv(chunkSize: 4 * MB);
        await env.AddAccountAsync("a@test", 50 * MB);
        await env.Secrets.SetAsync("google-tokens:fake", "ya29.SECRET_TOKEN_VALUE_123456");
        await env.UploadAndWaitAsync(env.CreateFile("x.bin", 5 * MB));
        var path = Path.Combine(env.Root, "recovery.json");

        await env.Backup.ExportRecoveryJsonAsync(path);

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("x.bin", json);
        Assert.Contains("remoteFileId", json);
        Assert.Contains("a@test", json);
        Assert.DoesNotContain("ya29", json);
        env.DeleteRoot();
    }

    [Fact]
    public async Task Settings_persist_across_instances()
    {
        var root = Path.Combine(Path.GetTempPath(), "mdc-tests-" + Guid.NewGuid().ToString("N"));
        await using (var env = new TestEnv(root))
        {
            await env.UpdateSettingsAsync(s => { s.MaxConcurrentTransfers = 5; s.Theme = "Light"; s.StartWithWindows = true; });
            MultiDriveCloud.Infrastructure.Database.SqliteDatabase.ReleaseAll();
        }
        await using (var env2 = new TestEnv(root))
        {
            var s = new MultiDriveCloud.Core.Settings.SettingsService(new MultiDriveCloud.Infrastructure.Database.KeyValueStore(env2.Db), "", "");
            await s.LoadAsync();
            Assert.Equal(5, s.Current.MaxConcurrentTransfers);
            Assert.Equal("Light", s.Current.Theme);
            Assert.True(s.Current.StartWithWindows);
            env2.DeleteRoot();
        }
    }
}

