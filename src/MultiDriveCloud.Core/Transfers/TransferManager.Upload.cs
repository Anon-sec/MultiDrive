using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Transfers;

public sealed partial class TransferManager
{
    private sealed record UploadItem(string FullPath, IReadOnlyList<string> RelativeDirs, long Length);


    public async Task<EnqueueResult> EnqueueUploadAsync(IEnumerable<string> paths, Guid destinationFolderId, CancellationToken ct = default)
    {
        _ = await _index.GetFolderAsync(destinationFolderId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Le dossier de destination n'existe pas.");

        var skipped = new List<UploadSkip>();
        var items = new List<UploadItem>();
        var dirs = new List<IReadOnlyList<string>>();
        foreach (var raw in paths)
        {
            ct.ThrowIfCancellationRequested();
            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception) { skipped.Add(new UploadSkip(raw, "Chemin invalide.")); continue; }

            if (Directory.Exists(full))
            {
                var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) { skipped.Add(new UploadSkip(full, "Les racines de lecteurs ne sont pas importables directement.")); continue; }
                CollectDirectory(full, new[] { name }, items, dirs, skipped, ct);
            }
            else
            {
                var problem = PathGuard.ValidateSourceFile(full);
                if (problem is not null) { skipped.Add(new UploadSkip(full, problem)); continue; }
                items.Add(new UploadItem(full, Array.Empty<string>(), new FileInfo(full).Length));
            }
        }

        var settings = _settings.Current;
        var encrypted = settings.EncryptionEnabled;
        if (encrypted) await _keys.EnsureKeyAsync(ct).ConfigureAwait(false);

        var needed = items.Sum(i => ChunkManager.StoredSize(i.Length, encrypted));
        var usable = await _allocator.GetUsableBytesAsync(ct).ConfigureAwait(false);
        if (needed > usable) throw new InsufficientStorageException(needed, usable);

        var batchId = Guid.NewGuid();
        var folderCache = new Dictionary<string, Guid> { [""] = destinationFolderId };
        foreach (var d in dirs) await EnsureFolderPathAsync(destinationFolderId, d, folderCache, ct).ConfigureAwait(false);

        var count = 0;
        long totalBytes = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(item.FullPath);
            if (PathGuard.ValidateVirtualName(name) is { } nameProblem)
            {
                skipped.Add(new UploadSkip(item.FullPath, nameProblem));
                continue;
            }
            var info = new FileInfo(item.FullPath);
            if (!info.Exists) { skipped.Add(new UploadSkip(item.FullPath, "Fichier introuvable.")); continue; }

            var folderId = await EnsureFolderPathAsync(destinationFolderId, item.RelativeDirs, folderCache, ct).ConfigureAwait(false);
            var uniqueName = await VirtualNames.UniqueAsync(_index, folderId, name, ct).ConfigureAwait(false);
            var file = new VirtualFile
            {
                FileId = Guid.NewGuid(),
                FolderId = folderId,
                Name = uniqueName,
                TotalSize = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                CreatedAt = DateTime.UtcNow,
                Status = FileStatus.Pending,
                IsEncrypted = encrypted,
                ChunkSize = settings.ChunkSizeBytes,
                OriginalPath = item.FullPath,
            };
            var record = new TransferRecord
            {
                TransferId = Guid.NewGuid(),
                BatchId = batchId,
                FileId = file.FileId,
                Direction = TransferDirection.Upload,
                Name = uniqueName,
                State = TransferState.Queued,
                TotalBytes = info.Length,
                LocalPath = item.FullPath,
            };
            await _index.InsertUploadAsync(file, record, ct).ConfigureAwait(false);
            Register(record);
            count++;
            totalBytes += info.Length;
        }

        _log.LogInformation("{Count} fichier(s) ajouté(s) à la file d'envoi ({Skipped} ignoré(s)).", count, skipped.Count);
        ListChanged?.Invoke(this, EventArgs.Empty);
        Pump();
        return new EnqueueResult(batchId, count, totalBytes, skipped);
    }

    private static void CollectDirectory(string dir, IReadOnlyList<string> relative, List<UploadItem> items,
        List<IReadOnlyList<string>> dirs, List<UploadSkip> skipped, CancellationToken ct)
    {
        dirs.Add(relative);
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            skipped.Add(new UploadSkip(dir, "Dossier illisible : " + ex.Message));
            return;
        }
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                skipped.Add(new UploadSkip(entry.FullName, "Lien symbolique/jonction ignoré."));
                continue;
            }
            if (entry is DirectoryInfo sub)
                CollectDirectory(sub.FullName, relative.Append(sub.Name).ToList(), items, dirs, skipped, ct);
            else if (entry is FileInfo f)
                items.Add(new UploadItem(f.FullName, relative, f.Length));
        }
    }

    private async Task<Guid> EnsureFolderPathAsync(Guid rootId, IReadOnlyList<string> segments, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        var current = rootId;
        var key = "";
        foreach (var seg in segments)
        {
            key = key.Length == 0 ? seg : key + "\u0001" + seg;
            if (cache.TryGetValue(key, out var cached)) { current = cached; continue; }
            var existing = await _index.FindFolderAsync(current, seg, ct).ConfigureAwait(false);
            if (existing is null)
            {
                existing = new VirtualFolder { FolderId = Guid.NewGuid(), ParentId = current, Name = seg };
                await _index.InsertFolderAsync(existing, ct).ConfigureAwait(false);
            }
            cache[key] = existing.FolderId;
            current = existing.FolderId;
        }
        return current;
    }


    private async Task RunUploadAsync(ActiveTransfer t, CancellationToken ct)
    {
        var r = t.Record;
        var file = await _index.GetFileAsync(r.FileId!.Value, ct).ConfigureAwait(false)
            ?? throw new TransferException("Le fichier n'existe plus dans l'index.");
        var path = r.LocalPath;
        var problem = PathGuard.ValidateSourceFile(path);
        if (problem is not null) throw new TransferException($"Fichier source inutilisable ({problem}) : {path}");

        var info = new FileInfo(path);
        var chunks = (await _index.GetChunksAsync(file.FileId, ct).ConfigureAwait(false)).Where(c => c.Status != ChunkStatus.Deleted).ToList();

        if (chunks.Count > 0 && !SourceMatches(info, file))
        {
            _log.LogWarning("Le fichier source a changé depuis la dernière tentative, redémarrage de l'envoi : {Name}", file.Name);
            await ResetUploadAsync(file, ct).ConfigureAwait(false);
            chunks.Clear();
        }
        if (chunks.Count == 0)
        {
            file.TotalSize = info.Length;
            file.ModifiedAt = info.LastWriteTimeUtc;
            file.Sha256 = "";
            r.TotalBytes = info.Length;
            await _index.UpdateFileAsync(file, ct).ConfigureAwait(false);
        }

        byte[]? key = null;
        if (file.IsEncrypted)
            key = await _keys.GetKeyAsync(ct).ConfigureAwait(false)
                ?? throw new TransferException("La clé de chiffrement est introuvable : impossible d'envoyer ce fichier chiffré.");

        var leases = new ConcurrentDictionary<Guid, AllocationLease>();
        try
        {
            if (chunks.Count == 0)
            {
                t.Phase = "Planification";
                var plan = await _chunks.PlanAsync(file.FileId, file.TotalSize, file.ChunkSize, file.IsEncrypted, null, ct).ConfigureAwait(false);
                chunks = plan.Chunks.ToList();
                foreach (var kv in plan.Leases) leases[kv.Key] = kv.Value;
                await _index.InsertChunksAsync(chunks, ct).ConfigureAwait(false);
            }
            else
            {
                foreach (var c in chunks.Where(c => c.Status != ChunkStatus.Stored))
                    leases[c.ChunkId] = _allocator.Reserve(c.AccountId, c.StoredSize);
            }
            t.ResetProgress(chunks.Where(c => c.Status == ChunkStatus.Stored).Sum(c => c.PlainSize));
            await UpdateDestinationAsync(t, chunks).ConfigureAwait(false);
            await PersistAsync(t).ConfigureAwait(false);
            RaiseChanged(t);

            if (string.IsNullOrEmpty(file.Sha256) || chunks.Any(c => string.IsNullOrEmpty(c.Sha256)))
            {
                t.Phase = "Analyse";
                t.PhaseProgress = 0;
                var ranges = chunks.Select(c => new FragmentRange(c.Offset, c.PlainSize)).ToList();
                var total = Math.Max(1, file.TotalSize);
                FileHashResult hashes;
                try
                {
                    hashes = await IntegrityService.HashFileWithFragmentsAsync(path, ranges,
                        new SyncProgress<long>(b => t.PhaseProgress = Math.Min(1.0, (double)b / total)), ct).ConfigureAwait(false);
                }
                catch (IOException ex) when (ex is not FileNotFoundException)
                {
                    throw new TransferException("Le fichier source a changé ou est illisible pendant l'analyse : " + ex.Message, ex);
                }
                EnsureSourceUnchanged(path, file);
                file.Sha256 = hashes.FileSha256;
                for (var i = 0; i < chunks.Count; i++)
                {
                    chunks[i].Sha256 = hashes.Fragments[i].Sha256;
                    if (!file.IsEncrypted) chunks[i].StoredMd5 = hashes.Fragments[i].Md5;
                    await _index.UpdateChunkAsync(chunks[i], ct).ConfigureAwait(false);
                }
                await _index.UpdateFileAsync(file, ct).ConfigureAwait(false);
                t.PhaseProgress = -1;
            }

            t.Phase = "Envoi";
            var pending = chunks.Where(c => c.Status != ChunkStatus.Stored).OrderBy(c => c.ChunkIndex).ToList();
            await ForEachParallelAsync(pending, _settings.Current.FragmentParallelism,
                (chunk, token) => UploadChunkAsync(t, file, chunk, leases, key, path, chunks.Count, token), ct).ConfigureAwait(false);

            t.Phase = "Vérification";
            EnsureSourceUnchanged(path, file);
            var final = (await _index.GetChunksAsync(file.FileId, ct).ConfigureAwait(false)).Where(c => c.Status != ChunkStatus.Deleted).OrderBy(c => c.ChunkIndex).ToList();
            if (final.Any(c => c.Status != ChunkStatus.Stored || string.IsNullOrEmpty(c.RemoteFileId)))
                throw new TransferException("Tous les fragments ne sont pas confirmés sur les Drive : le fichier n'est pas considéré comme stocké.");
            try { ChunkManager.ValidateLayout(final, file.TotalSize); }
            catch (InvalidDataException ex) { throw new TransferException(ex.Message, ex); }

            file.Status = FileStatus.Stored;
            await _index.UpdateFileAsync(file, ct).ConfigureAwait(false);
            await UpdateDestinationAsync(t, final).ConfigureAwait(false);
            TryDeleteTempFor(file.FileId);
        }
        finally
        {
            foreach (var l in leases.Values) _allocator.Release(l);
        }
    }

    private static bool SourceMatches(FileInfo info, VirtualFile file) =>
        info.Length == file.TotalSize && Math.Abs((info.LastWriteTimeUtc - file.ModifiedAt).TotalSeconds) <= 2;

    private static void EnsureSourceUnchanged(string path, VirtualFile file)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new TransferException("Le fichier source a disparu pendant le transfert.");
        if (!SourceMatches(info, file))
            throw new TransferException("Le fichier source a été modifié pendant l'envoi ; il n'est pas considéré comme stocké. Relancez le transfert pour l'envoyer à nouveau.");
    }

    private async Task ResetUploadAsync(VirtualFile file, CancellationToken ct)
    {
        var report = await _cleaner.DeleteRemoteAsync(file.FileId, CancellationToken.None).ConfigureAwait(false);
        if (!report.Success)
            _log.LogWarning("{Count} ancien(s) fragment(s) n'ont pas pu être supprimés lors du redémarrage de l'envoi.", report.Failures.Count);
        await _index.DeleteChunksAsync(file.FileId, ct).ConfigureAwait(false);
        TryDeleteTempFor(file.FileId);
    }

    private async Task UpdateDestinationAsync(ActiveTransfer t, IEnumerable<FileChunk> chunks)
    {
        var ids = chunks.Select(c => c.AccountId).Distinct().ToList();
        var names = new List<string>();
        foreach (var id in ids)
        {
            var a = await _accounts.GetAsync(id).ConfigureAwait(false);
            names.Add(a?.Email ?? id);
        }
        t.Destination = string.Join(", ", names);
    }

    private async Task UploadChunkAsync(ActiveTransfer t, VirtualFile file, FileChunk chunk,
        ConcurrentDictionary<Guid, AllocationLease> leases, byte[]? key, string sourcePath, int chunkCount, CancellationToken ct)
    {
        var excluded = new HashSet<string>();
        var failures = 0;
        var maxRetries = _settings.Current.MaxRetries;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            EnsureSourceUnchanged(sourcePath, file);

            var account = await _accounts.GetAsync(chunk.AccountId, ct).ConfigureAwait(false);
            if (account is null || account.Status != AccountStatus.Connected)
            {
                excluded.Add(chunk.AccountId);
                await ReassignAsync(t, chunk, leases, excluded, ct).ConfigureAwait(false);
                continue;
            }
            var provider = _providers.Get(account.Provider);
            var fragmentKey = $"{file.FileId}:{chunk.ChunkIndex}";
            string? encPath = null;
            Stream? content = null;
            try
            {
                var wasInterrupted = chunk.Status == ChunkStatus.Uploading;
                var session = await _index.GetSessionAsync(chunk.ChunkId, ct).ConfigureAwait(false);
                if (session is not null && session.ExpiresAt <= DateTime.UtcNow)
                {
                    await _index.DeleteSessionAsync(chunk.ChunkId, ct).ConfigureAwait(false);
                    session = null;
                }
                chunk.Status = ChunkStatus.Uploading;
                await _index.UpdateChunkAsync(chunk, ct).ConfigureAwait(false);
                t.ChunkReset(chunk.ChunkId);

                if (file.IsEncrypted)
                {
                    var prepared = await PrepareEncryptedAsync(t, file, chunk, key!, sourcePath, ct).ConfigureAwait(false);
                    encPath = prepared.Path;
                    if (!prepared.Reused && session is not null)
                    {
                        await _index.DeleteSessionAsync(chunk.ChunkId, ct).ConfigureAwait(false);
                        session = null;
                    }
                    content = new FileStream(encPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                else
                {
                    content = new RangeReadStream(sourcePath, chunk.Offset, chunk.PlainSize);
                }

                RemoteFileInfo? remote = null;
                if (session is null && wasInterrupted)
                {
                    var existing = await provider.FindFragmentAsync(account.AccountId, fragmentKey, ct).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        if (existing.Size == chunk.StoredSize && (existing.Md5 is null || IntegrityService.HashEquals(existing.Md5, chunk.StoredMd5)))
                            remote = existing;
                        else
                            await provider.DeleteAsync(account.AccountId, existing.Id, ct).ConfigureAwait(false);
                    }
                }

                if (remote is null)
                {
                    var request = new UploadRequest
                    {
                        AccountId = account.AccountId,
                        RemoteName = $"{file.FileId:N}.{chunk.ChunkIndex:D5}.mdc",
                        FragmentKey = fragmentKey,
                        Properties = new Dictionary<string, string>
                        {
                            ["mdcFile"] = file.FileId.ToString(),
                            ["mdcIndex"] = chunk.ChunkIndex.ToString(),
                            ["mdcCount"] = chunkCount.ToString(),
                            ["mdcEnc"] = file.IsEncrypted ? "1" : "0",
                            ["mdcSha"] = chunk.Sha256,
                        },
                        Content = content,
                        Length = chunk.StoredSize,
                        ResumeSessionUri = session?.SessionUri,
                        OnSessionCreated = uri => SaveSession(t, chunk, uri),
                        Progress = new SyncProgress<long>(sent =>
                        {
                            var scaled = chunk.StoredSize <= 0 ? 0 : (long)((double)Math.Min(sent, chunk.StoredSize) / chunk.StoredSize * chunk.PlainSize);
                            t.ChunkProgress(chunk.ChunkId, scaled);
                        }),
                    };
                    remote = await provider.UploadAsync(request, ct).ConfigureAwait(false);
                }

                if (remote.Size != chunk.StoredSize)
                {
                    await TryDeleteRemoteAsync(provider, account.AccountId, remote.Id).ConfigureAwait(false);
                    throw new StorageProviderException(ProviderErrorKind.Transient,
                        $"Taille distante inattendue pour le fragment {chunk.ChunkIndex + 1} ({remote.Size} au lieu de {chunk.StoredSize}).");
                }
                if (remote.Md5 is not null && !IntegrityService.HashEquals(remote.Md5, chunk.StoredMd5))
                {
                    await TryDeleteRemoteAsync(provider, account.AccountId, remote.Id).ConfigureAwait(false);
                    throw new StorageProviderException(ProviderErrorKind.Transient,
                        $"Empreinte distante différente pour le fragment {chunk.ChunkIndex + 1} : transfert corrompu, nouvel essai.");
                }

                chunk.RemoteFileId = remote.Id;
                chunk.Status = ChunkStatus.Stored;
                await _index.UpdateChunkAsync(chunk, ct).ConfigureAwait(false);
                await _index.DeleteSessionAsync(chunk.ChunkId, CancellationToken.None).ConfigureAwait(false);
                if (leases.TryRemove(chunk.ChunkId, out var lease))
                    await _allocator.CompleteAsync(lease, chunk.StoredSize, CancellationToken.None).ConfigureAwait(false);
                t.ChunkCompleted(chunk.ChunkId, chunk.PlainSize);
                await PersistAsync(t).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (StorageProviderException ex)
            {
                t.ChunkReset(chunk.ChunkId);
                switch (ex.Kind)
                {
                    case ProviderErrorKind.Transient:
                    case ProviderErrorKind.RateLimited:
                        if (++failures > maxRetries) throw;
                        t.Record.RetryCount++;
                        _log.LogInformation("Fragment {Index} : erreur temporaire ({Kind}), nouvel essai {N}/{Max}.", chunk.ChunkIndex, ex.Kind, failures, maxRetries);
                        await _options.DelayAsync(Backoff(failures, ex.RetryAfter), ct).ConfigureAwait(false);
                        break;
                    case ProviderErrorKind.NotFound:
                        await _index.DeleteSessionAsync(chunk.ChunkId, CancellationToken.None).ConfigureAwait(false);
                        if (++failures > maxRetries) throw;
                        t.Record.RetryCount++;
                        break;
                    case ProviderErrorKind.AuthRequired:
                        await _accountService.MarkNeedsReauthorizationAsync(account.AccountId).ConfigureAwait(false);
                        excluded.Add(account.AccountId);
                        await ReassignAsync(t, chunk, leases, excluded, ct).ConfigureAwait(false);
                        break;
                    case ProviderErrorKind.StorageFull:
                        _allocator.MarkUnavailable(account.AccountId, TimeSpan.FromMinutes(30));
                        excluded.Add(account.AccountId);
                        await ReassignAsync(t, chunk, leases, excluded, ct).ConfigureAwait(false);
                        break;
                    case ProviderErrorKind.DailyQuota:
                        _allocator.MarkUnavailable(account.AccountId, TimeSpan.FromHours(6));
                        excluded.Add(account.AccountId);
                        await ReassignAsync(t, chunk, leases, excluded, ct).ConfigureAwait(false);
                        break;
                    default:
                        chunk.Status = ChunkStatus.Failed;
                        await _index.UpdateChunkAsync(chunk, CancellationToken.None).ConfigureAwait(false);
                        throw;
                }
            }
            finally
            {
                content?.Dispose();
                if (encPath is not null && chunk.Status == ChunkStatus.Stored) TryDelete(encPath);
            }
        }
    }

    private void SaveSession(ActiveTransfer t, FileChunk chunk, string uri)
    {
        _index.UpsertSessionAsync(new TransferSession
        {
            ChunkId = chunk.ChunkId,
            TransferId = t.Record.TransferId,
            SessionUri = uri,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + _options.SessionLifetime,
        }).GetAwaiter().GetResult();
    }

    private async Task TryDeleteRemoteAsync(IStorageProvider provider, string accountId, string remoteId)
    {
        try { await provider.DeleteAsync(accountId, remoteId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning("Suppression d'un fragment invalide impossible : {Type}", ex.GetType().Name); }
    }

    private async Task ReassignAsync(ActiveTransfer t, FileChunk chunk, ConcurrentDictionary<Guid, AllocationLease> leases,
        HashSet<string> excluded, CancellationToken ct)
    {
        if (leases.TryRemove(chunk.ChunkId, out var old)) _allocator.Release(old);
        var lease = await _allocator.TryAllocateAsync(chunk.StoredSize, chunk.StoredSize, excluded, ct).ConfigureAwait(false);
        if (lease is null)
        {
            var usable = await _allocator.GetUsableBytesAsync(ct).ConfigureAwait(false);
            chunk.Status = ChunkStatus.Failed;
            await _index.UpdateChunkAsync(chunk, CancellationToken.None).ConfigureAwait(false);
            throw new InsufficientStorageException(chunk.StoredSize, usable);
        }
        leases[chunk.ChunkId] = lease;
        _log.LogInformation("Fragment {Index} réaffecté à un autre compte.", chunk.ChunkIndex);
        chunk.AccountId = lease.AccountId;
        chunk.RemoteFileId = null;
        await _index.DeleteSessionAsync(chunk.ChunkId, ct).ConfigureAwait(false);
        await _index.UpdateChunkAsync(chunk, ct).ConfigureAwait(false);
        var all = await _index.GetChunksAsync(chunk.FileId, ct).ConfigureAwait(false);
        await UpdateDestinationAsync(t, all.Where(c => c.Status != ChunkStatus.Deleted)).ConfigureAwait(false);
    }

    private async Task<(string Path, bool Reused)> PrepareEncryptedAsync(ActiveTransfer t, VirtualFile file, FileChunk chunk,
        byte[] key, string sourcePath, CancellationToken ct)
    {
        var dir = _settings.Current.TempDirectory;
        Directory.CreateDirectory(dir);
        var enc = Path.Combine(dir, $"{file.FileId:N}.{chunk.ChunkIndex:D5}.enc");
        if (File.Exists(enc) && new FileInfo(enc).Length == chunk.StoredSize && !string.IsNullOrEmpty(chunk.StoredMd5))
            return (enc, true);

        var tmp = enc + ".tmp";
        string md5;
        await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        {
            using var hashing = new Md5WriteStream(output);
            await using var source = new RangeReadStream(sourcePath, chunk.Offset, chunk.PlainSize);
            await AesGcmStreamCipher.EncryptAsync(source, hashing, key, null, ct).ConfigureAwait(false);
            await hashing.FlushAsync(ct).ConfigureAwait(false);
            md5 = hashing.Md5Hex();
            if (hashing.BytesWritten != chunk.StoredSize)
                throw new TransferException("Taille du fragment chiffré inattendue.");
        }
        await FileUtil.MoveWithRetryAsync(tmp, enc, overwrite: true, ct).ConfigureAwait(false);
        chunk.StoredMd5 = md5;
        await _index.UpdateChunkAsync(chunk, ct).ConfigureAwait(false);
        return (enc, false);
    }
}
