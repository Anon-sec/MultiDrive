using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Transfers;

public sealed partial class TransferManager
{
    internal const string PartSuffix = ".mdcpart";


    public async Task<EnqueueResult> EnqueueDownloadAsync(IEnumerable<ItemRef> items, string? targetDirectory, CancellationToken ct = default)
    {
        var target = string.IsNullOrWhiteSpace(targetDirectory) ? _settings.Current.DownloadDirectory : targetDirectory;
        if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException("Aucun dossier de téléchargement défini.");
        target = Path.GetFullPath(target);
        Directory.CreateDirectory(target);

        var batchId = Guid.NewGuid();
        var skipped = new List<UploadSkip>();
        var count = 0;
        long bytes = 0;

        async Task AddAsync(VirtualFile file, string localPath)
        {
            if (file.Status != FileStatus.Stored)
            {
                skipped.Add(new UploadSkip(file.Name, "Envoi non terminé : fichier pas encore disponible."));
                return;
            }
            var record = new TransferRecord
            {
                TransferId = Guid.NewGuid(),
                BatchId = batchId,
                FileId = file.FileId,
                Direction = TransferDirection.Download,
                Name = file.Name,
                State = TransferState.Queued,
                TotalBytes = file.TotalSize,
                LocalPath = localPath,
            };
            await _index.InsertTransferAsync(record, ct).ConfigureAwait(false);
            Register(record);
            count++;
            bytes += file.TotalSize;
        }

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.IsFolder)
            {
                var file = await _index.GetFileAsync(item.Id, ct).ConfigureAwait(false);
                if (file is null) continue;
                await AddAsync(file, PathGuard.UniqueLocalPath(PathGuard.CombineInside(target, file.Name))).ConfigureAwait(false);
            }
            else
            {
                var folder = await _index.GetFolderAsync(item.Id, ct).ConfigureAwait(false);
                if (folder is null) continue;
                var root = PathGuard.UniqueLocalPath(PathGuard.CombineInside(target, folder.Name));
                Directory.CreateDirectory(root);
                foreach (var (_, rel) in await _index.GetFoldersRecursiveAsync(folder.FolderId, ct).ConfigureAwait(false))
                    Directory.CreateDirectory(PathGuard.CombineInside(root, SplitPath(rel)));
                foreach (var entry in await _index.GetFilesRecursiveAsync(folder.FolderId, ct).ConfigureAwait(false))
                {
                    var segments = SplitPath(entry.FolderPath).Append(entry.File.Name).ToArray();
                    await AddAsync(entry.File, PathGuard.CombineInside(root, segments)).ConfigureAwait(false);
                }
            }
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
        Pump();
        return new EnqueueResult(batchId, count, bytes, skipped);
    }

    private static string[] SplitPath(string relative) =>
        relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);


    private async Task RunDownloadAsync(ActiveTransfer t, CancellationToken ct)
    {
        var r = t.Record;
        var file = await _index.GetFileAsync(r.FileId!.Value, ct).ConfigureAwait(false)
            ?? throw new TransferException("Fichier introuvable dans l'index.");
        if (file.Status != FileStatus.Stored)
            throw new TransferException("Ce fichier n'est pas entièrement stocké (envoi incomplet ou suppression en cours) : téléchargement impossible.");

        var chunks = (await _index.GetChunksAsync(file.FileId, ct).ConfigureAwait(false))
            .Where(c => c.Status != ChunkStatus.Deleted).OrderBy(c => c.ChunkIndex).ToList();
        try { ChunkManager.ValidateLayout(chunks, file.TotalSize); }
        catch (InvalidDataException ex) { throw new TransferException("Index incohérent, fragment manquant : " + ex.Message, ex); }
        var unconfirmed = chunks.Count(c => c.Status != ChunkStatus.Stored || string.IsNullOrEmpty(c.RemoteFileId));
        if (unconfirmed > 0)
            throw new TransferException($"{unconfirmed} fragment(s) ne sont pas confirmés dans l'index : le fichier ne peut pas être reconstruit.");

        byte[]? key = null;
        if (file.IsEncrypted)
            key = await _keys.GetKeyAsync(ct).ConfigureAwait(false)
                ?? throw new TransferException("Ce fichier est chiffré et la clé de chiffrement est introuvable. Importez la clé exportée (Paramètres › Chiffrement).");

        t.Phase = "Vérification des comptes";
        var accounts = new Dictionary<string, CloudAccount>();
        var problems = new List<string>();
        foreach (var id in chunks.Select(c => c.AccountId).Distinct())
        {
            var acc = await _accounts.GetAsync(id, ct).ConfigureAwait(false);
            if (acc is null) problems.Add($"compte inconnu ({id})");
            else if (acc.Status == AccountStatus.Disconnected) problems.Add($"{acc.Email} est déconnecté");
            else if (acc.Status == AccountStatus.NeedsReauthorization) problems.Add($"{acc.Email} doit être réautorisé");
            else accounts[id] = acc;
        }
        if (problems.Count > 0)
        {
            var affected = chunks.Count(c => !accounts.ContainsKey(c.AccountId));
            throw new TransferException(
                $"Téléchargement impossible : {affected} fragment(s) sur {chunks.Count} sont hébergés sur un compte inaccessible ({string.Join(" ; ", problems)}). " +
                "Reconnectez ce compte puis relancez le transfert.");
        }
        await UpdateDestinationAsync(t, chunks).ConfigureAwait(false);

        t.Phase = "Localisation des fragments";
        await ForEachParallelAsync(chunks, _settings.Current.FragmentParallelism, async (c, tk) =>
        {
            var acc = accounts[c.AccountId];
            var provider = _providers.Get(acc.Provider);
            var info = await RunWithRetryAsync(t, acc, () => provider.GetFileInfoAsync(acc.AccountId, c.RemoteFileId!, tk), tk).ConfigureAwait(false);
            if (info is null)
                throw new TransferException($"Fragment {c.ChunkIndex + 1}/{chunks.Count} introuvable sur {acc.Email} (supprimé du Drive ?). Le fichier ne peut pas être reconstruit.");
            if (info.Size != c.StoredSize)
                throw new TransferException($"Fragment {c.ChunkIndex + 1}/{chunks.Count} modifié sur {acc.Email} (taille {info.Size} au lieu de {c.StoredSize}).");
        }, ct).ConfigureAwait(false);

        var target = r.LocalPath;
        var part = target + PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partExisted = File.Exists(part) && new FileInfo(part).Length == file.TotalSize;
        EnsureDiskSpace(target, partExisted ? 0 : file.TotalSize);

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransferException("Impossible de créer le fichier de destination : " + ex.Message, ex);
        }

        var finalPath = target;
        try
        {
            if (RandomAccess.GetLength(handle) != file.TotalSize) RandomAccess.SetLength(handle, file.TotalSize);

            var done = new HashSet<int>();
            if (partExisted && r.BytesDone > 0)
            {
                t.Phase = "Reprise : contrôle des fragments déjà reçus";
                foreach (var c in chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    var sha = await IntegrityService.Sha256RangeAsync(handle, c.Offset, c.PlainSize, null, ct).ConfigureAwait(false);
                    if (IntegrityService.HashEquals(sha, c.Sha256)) done.Add(c.ChunkIndex);
                }
            }
            t.ResetProgress(chunks.Where(c => done.Contains(c.ChunkIndex)).Sum(c => c.PlainSize));

            t.Phase = "Téléchargement";
            var pending = chunks.Where(c => !done.Contains(c.ChunkIndex)).ToList();
            await ForEachParallelAsync(pending, _settings.Current.FragmentParallelism,
                (c, tk) => DownloadChunkAsync(t, file, c, accounts[c.AccountId], handle, key, chunks.Count, tk), ct).ConfigureAwait(false);

            t.Phase = "Vérification de l'intégrité";
            t.PhaseProgress = 0;
            RandomAccess.FlushToDisk(handle);
            var total = Math.Max(1, file.TotalSize);
            var fullHash = await IntegrityService.Sha256RangeAsync(handle, 0, file.TotalSize,
                new SyncProgress<long>(b => t.PhaseProgress = Math.Min(1.0, (double)b / total)), ct).ConfigureAwait(false);
            if (!IntegrityService.HashEquals(fullHash, file.Sha256))
            {
                handle.Dispose();
                TryDelete(part);
                throw new TransferException("L'empreinte SHA-256 du fichier reconstruit ne correspond pas à l'originale : le fichier est corrompu et n'a PAS été enregistré.");
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        handle.Dispose();

        finalPath = PathGuard.UniqueLocalPath(target);
        await FileUtil.MoveWithRetryAsync(part, finalPath, overwrite: false, ct).ConfigureAwait(false);
        try { File.SetLastWriteTimeUtc(finalPath, file.ModifiedAt); } catch { }
        r.LocalPath = finalPath;
        t.Phase = "Terminé";
    }

    private static void EnsureDiskSpace(string targetPath, long neededBytes)
    {
        if (neededBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < neededBytes + 16L * 1024 * 1024)
                throw new TransferException($"Espace disque insuffisant sur {root} : {Formatting.Bytes(neededBytes)} nécessaires, {Formatting.Bytes(free)} libres.");
        }
        catch (TransferException) { throw; }
        catch (Exception) { }
    }

    private async Task DownloadChunkAsync(ActiveTransfer t, VirtualFile file, FileChunk chunk, CloudAccount account,
        SafeFileHandle handle, byte[]? key, int chunkCount, CancellationToken ct)
    {
        var provider = _providers.Get(account.Provider);
        var label = $"{chunk.ChunkIndex + 1}/{chunkCount}";
        var maxRetries = _settings.Current.MaxRetries;
        var failures = 0;
        var corrupt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            t.ChunkReset(chunk.ChunkId);
            string? tmp = null;
            try
            {
                if (!file.IsEncrypted)
                {
                    var ws = new OffsetWriteStream(handle, chunk.Offset, chunk.StoredSize, written => t.ChunkProgress(chunk.ChunkId, written));
                    await provider.DownloadAsync(new DownloadRequest
                    {
                        AccountId = account.AccountId,
                        RemoteId = chunk.RemoteFileId!,
                        Destination = ws,
                    }, ct).ConfigureAwait(false);
                    if (ws.Written != chunk.StoredSize)
                        throw new StorageProviderException(ProviderErrorKind.Transient, $"Fragment {label} incomplet ({ws.Written}/{chunk.StoredSize} octets).");
                }
                else
                {
                    var dir = _settings.Current.TempDirectory;
                    Directory.CreateDirectory(dir);
                    tmp = Path.Combine(dir, $"{file.FileId:N}.dl{chunk.ChunkIndex:D5}.{t.Record.TransferId:N}.tmp");
                    await using (var tmpFs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
                    {
                        await provider.DownloadAsync(new DownloadRequest { AccountId = account.AccountId, RemoteId = chunk.RemoteFileId!, Destination = tmpFs }, ct).ConfigureAwait(false);
                        await tmpFs.FlushAsync(ct).ConfigureAwait(false);
                        if (tmpFs.Length != chunk.StoredSize)
                            throw new StorageProviderException(ProviderErrorKind.Transient, $"Fragment {label} incomplet ({tmpFs.Length}/{chunk.StoredSize} octets).");
                    }
                    if (!string.IsNullOrEmpty(chunk.StoredMd5))
                    {
                        var md5 = await IntegrityService.Md5FileAsync(tmp, ct).ConfigureAwait(false);
                        if (!IntegrityService.HashEquals(md5, chunk.StoredMd5))
                            throw new StorageProviderException(ProviderErrorKind.Transient, $"Fragment {label} corrompu pendant le téléchargement.");
                    }
                    await using var input = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var output = new OffsetWriteStream(handle, chunk.Offset, chunk.PlainSize, null);
                    await AesGcmStreamCipher.DecryptAsync(input, output, key!,
                        new SyncProgress<long>(b => t.ChunkProgress(chunk.ChunkId, b)), ct).ConfigureAwait(false);
                    if (output.Written != chunk.PlainSize)
                        throw new TransferException($"Fragment {label} : taille déchiffrée inattendue.");
                }

                var hash = await IntegrityService.Sha256RangeAsync(handle, chunk.Offset, chunk.PlainSize, null, ct).ConfigureAwait(false);
                if (!IntegrityService.HashEquals(hash, chunk.Sha256))
                {
                    if (++corrupt > 2)
                        throw new TransferException($"Fragment {label} corrompu : l'empreinte SHA-256 ne correspond pas après {corrupt} téléchargements. Le fichier n'a pas été reconstruit.");
                    t.Record.RetryCount++;
                    continue;
                }

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
                switch (ex.Kind)
                {
                    case ProviderErrorKind.Transient:
                    case ProviderErrorKind.RateLimited:
                    case ProviderErrorKind.DailyQuota:
                        if (++failures > maxRetries) throw;
                        t.Record.RetryCount++;
                        await _options.DelayAsync(Backoff(failures, ex.RetryAfter), ct).ConfigureAwait(false);
                        break;
                    case ProviderErrorKind.AuthRequired:
                        await _accountService.MarkNeedsReauthorizationAsync(account.AccountId).ConfigureAwait(false);
                        throw new TransferException($"Le compte {account.Email} doit être réautorisé : fragment {label} inaccessible.", ex);
                    case ProviderErrorKind.NotFound:
                        throw new TransferException($"Fragment {label} introuvable sur {account.Email} : le fichier ne peut pas être reconstruit.", ex);
                    default:
                        throw;
                }
            }
            catch (InvalidDataException ex)
            {
                if (++failures > maxRetries) throw new TransferException($"Fragment {label} : données reçues invalides.", ex);
                t.Record.RetryCount++;
            }
            catch (CryptographicException ex)
            {
                throw new TransferException($"Fragment {label} : déchiffrement impossible (clé incorrecte ou fragment altéré).", ex);
            }
            finally
            {
                if (tmp is not null) TryDelete(tmp);
            }
        }
    }

    private async Task<T> RunWithRetryAsync<T>(ActiveTransfer t, CloudAccount account, Func<Task<T>> action, CancellationToken ct)
    {
        var failures = 0;
        var max = _settings.Current.MaxRetries;
        while (true)
        {
            try { return await action().ConfigureAwait(false); }
            catch (StorageProviderException ex) when (ex.Kind is ProviderErrorKind.Transient or ProviderErrorKind.RateLimited && failures < max)
            {
                failures++;
                t.Record.RetryCount++;
                await _options.DelayAsync(Backoff(failures, ex.RetryAfter), ct).ConfigureAwait(false);
            }
            catch (StorageProviderException ex) when (ex.Kind == ProviderErrorKind.AuthRequired)
            {
                await _accountService.MarkNeedsReauthorizationAsync(account.AccountId).ConfigureAwait(false);
                throw new TransferException($"Le compte {account.Email} doit être réautorisé.", ex);
            }
        }
    }
}
