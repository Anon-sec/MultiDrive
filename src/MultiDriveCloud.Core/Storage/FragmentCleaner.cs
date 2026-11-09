using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Storage;

public sealed record CleanupFailure(int ChunkIndex, string AccountLabel, string Reason);

public sealed record CleanupReport(int Deleted, IReadOnlyList<CleanupFailure> Failures)
{
    public bool Success => Failures.Count == 0;
}

public sealed class FragmentCleaner
{
    private readonly ProviderRegistry _providers;
    private readonly IAccountRepository _accounts;
    private readonly IIndexRepository _index;
    private readonly StorageAllocator _allocator;
    private readonly ILogger<FragmentCleaner> _log;

    public FragmentCleaner(ProviderRegistry providers, IAccountRepository accounts, IIndexRepository index,
        StorageAllocator allocator, ILogger<FragmentCleaner> log)
    {
        _providers = providers;
        _accounts = accounts;
        _index = index;
        _allocator = allocator;
        _log = log;
    }

    public async Task<CleanupReport> DeleteRemoteAsync(Guid fileId, CancellationToken ct = default)
    {
        var chunks = await _index.GetChunksAsync(fileId, ct).ConfigureAwait(false);
        var failures = new List<CleanupFailure>();
        var deleted = 0;
        foreach (var chunk in chunks)
        {
            if (chunk.Status == ChunkStatus.Deleted) continue;
            var account = await _accounts.GetAsync(chunk.AccountId, ct).ConfigureAwait(false);
            if (account is null || account.Status is AccountStatus.Disconnected or AccountStatus.NeedsReauthorization)
            {
                if (chunk.RemoteFileId is not null || chunk.Status is ChunkStatus.Uploading)
                    failures.Add(new CleanupFailure(chunk.ChunkIndex, account?.Email ?? chunk.AccountId,
                        "compte déconnecté ou à réautoriser"));
                else
                    await MarkDeletedAsync(chunk, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                var provider = _providers.Get(account.Provider);
                var remoteId = chunk.RemoteFileId;
                if (remoteId is null && chunk.Status is ChunkStatus.Uploading or ChunkStatus.Planned or ChunkStatus.Failed)
                {
                    var orphan = await WithRetryAsync(() => provider.FindFragmentAsync(account.AccountId, $"{fileId}:{chunk.ChunkIndex}", ct), ct).ConfigureAwait(false);
                    remoteId = orphan?.Id;
                }
                if (remoteId is not null)
                {
                    var id = remoteId;
                    await WithRetryAsync(async () => { await provider.DeleteAsync(account.AccountId, id, ct).ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);
                    if (chunk.Status == ChunkStatus.Stored)
                        await _allocator.FreedAsync(account.AccountId, chunk.StoredSize, ct).ConfigureAwait(false);
                    deleted++;
                }
                await MarkDeletedAsync(chunk, ct).ConfigureAwait(false);
            }
            catch (StorageProviderException ex) when (ex.Kind == ProviderErrorKind.NotFound)
            {
                await MarkDeletedAsync(chunk, ct).ConfigureAwait(false);
            }
            catch (StorageProviderException ex)
            {
                _log.LogWarning("Suppression du fragment {Index} sur {Email} impossible : {Kind}", chunk.ChunkIndex, account.Email, ex.Kind);
                failures.Add(new CleanupFailure(chunk.ChunkIndex, account.Email, Redactor.Redact(ex.Message)));
            }
        }
        return new CleanupReport(deleted, failures);
    }

    private async Task MarkDeletedAsync(FileChunk chunk, CancellationToken ct)
    {
        chunk.RemoteFileId = null;
        chunk.Status = ChunkStatus.Deleted;
        await _index.UpdateChunkAsync(chunk, ct).ConfigureAwait(false);
    }

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (StorageProviderException ex) when (ex.Kind is ProviderErrorKind.Transient or ProviderErrorKind.RateLimited && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }
    }
}
