using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Transfers;

public sealed partial class TransferManager : IAsyncDisposable
{
    private readonly IIndexRepository _index;
    private readonly IAccountRepository _accounts;
    private readonly ProviderRegistry _providers;
    private readonly StorageAllocator _allocator;
    private readonly ChunkManager _chunks;
    private readonly FragmentCleaner _cleaner;
    private readonly ISettingsService _settings;
    private readonly IEncryptionKeyProvider _keys;
    private readonly AccountService _accountService;
    private readonly ILogger<TransferManager> _log;
    private readonly TransferManagerOptions _options;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveTransfer> _all = new();
    private bool _shuttingDown;

    public TransferManager(IIndexRepository index, IAccountRepository accounts, ProviderRegistry providers,
        StorageAllocator allocator, ChunkManager chunks, FragmentCleaner cleaner, ISettingsService settings,
        IEncryptionKeyProvider keys, AccountService accountService, ILogger<TransferManager> log,
        TransferManagerOptions? options = null)
    {
        _index = index;
        _accounts = accounts;
        _providers = providers;
        _allocator = allocator;
        _chunks = chunks;
        _cleaner = cleaner;
        _settings = settings;
        _keys = keys;
        _accountService = accountService;
        _log = log;
        _options = options ?? new TransferManagerOptions();
        _settings.Changed += (_, _) => Pump();
    }

    public event EventHandler<TransferSnapshot>? TransferChanged;

    public event EventHandler? ListChanged;


    public IReadOnlyList<TransferSnapshot> GetSnapshots()
    {
        lock (_gate) return _all.Values.OrderByDescending(t => t.Record.CreatedAt).Select(Snapshot).ToList();
    }

    public TransferSnapshot? GetSnapshot(Guid id)
    {
        lock (_gate) return _all.TryGetValue(id, out var t) ? Snapshot(t) : null;
    }

    public TransferTotals GetTotals()
    {
        lock (_gate)
        {
            var active = _all.Values.Where(t => t.Record.State is TransferState.Queued or TransferState.Running or TransferState.Paused).ToList();
            var total = active.Sum(t => t.Record.TotalBytes);
            var done = active.Sum(t => Math.Min(t.Done, t.Record.TotalBytes));
            var speed = active.Where(t => t.Record.State == TransferState.Running).Sum(t => t.Meter.BytesPerSecond);
            TimeSpan? eta = speed > 1 ? TimeSpan.FromSeconds(Math.Max(0, total - done) / speed) : null;
            return new TransferTotals(
                active.Count(t => t.Record.State == TransferState.Running),
                active.Count(t => t.Record.State == TransferState.Queued),
                active.Count(t => t.Record.State == TransferState.Paused),
                total, done, speed, eta);
        }
    }

    private TransferSnapshot Snapshot(ActiveTransfer t)
    {
        var r = t.Record;
        var done = r.State == TransferState.Completed ? r.TotalBytes : Math.Min(r.TotalBytes, Math.Max(t.Done, r.State == TransferState.Running ? 0 : r.BytesDone));
        var speed = r.State == TransferState.Running ? t.Meter.BytesPerSecond : 0;
        TimeSpan? eta = speed > 1 ? TimeSpan.FromSeconds(Math.Max(0, r.TotalBytes - done) / speed) : null;
        var pp = t.PhaseProgress;
        return new TransferSnapshot(r.TransferId, r.BatchId, r.FileId, r.Direction, r.Name, r.State, r.TotalBytes, done,
            speed, eta, r.RetryCount, r.ErrorMessage, r.LocalPath, t.Phase, pp >= 0 ? pp : null, t.Destination, r.CreatedAt);
    }

    private void RaiseChanged(ActiveTransfer t)
    {
        TransferSnapshot snap;
        lock (_gate) snap = Snapshot(t);
        TransferChanged?.Invoke(this, snap);
    }

    private async Task PersistAsync(ActiveTransfer t)
    {
        t.Record.UpdatedAt = DateTime.UtcNow;
        t.Record.BytesDone = Math.Min(t.Record.TotalBytes, t.Done);
        try { await _index.UpdateTransferAsync(t.Record).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Impossible d'enregistrer l'état du transfert."); }
    }


    private void Register(TransferRecord record, string destination = "")
    {
        var t = new ActiveTransfer { Record = record };
        t.Destination = destination;
        t.ResetProgress(record.State == TransferState.Completed ? record.TotalBytes : record.BytesDone);
        lock (_gate) _all[record.TransferId] = t;
    }

    private void Pump()
    {
        var toStart = new List<ActiveTransfer>();
        lock (_gate)
        {
            if (_shuttingDown) return;
            var max = Math.Max(1, _settings.Current.MaxConcurrentTransfers);
            var running = _all.Values.Count(t => t.Record.State == TransferState.Running);
            foreach (var t in _all.Values.Where(t => t.Record.State == TransferState.Queued).OrderBy(t => t.Record.CreatedAt))
            {
                if (running >= max) break;
                t.Record.State = TransferState.Running;
                t.Stop = StopReason.None;
                t.Cts = new CancellationTokenSource();
                running++;
                toStart.Add(t);
            }
        }
        foreach (var t in toStart)
        {
            var captured = t;
            captured.RunTask = Task.Run(() => RunAsync(captured));
        }
    }

    public void Pause(Guid transferId)
    {
        ActiveTransfer? t;
        lock (_gate)
        {
            if (!_all.TryGetValue(transferId, out t)) return;
            if (t.Record.State == TransferState.Queued) t.Record.State = TransferState.Paused;
            else if (t.Record.State == TransferState.Running) { t.Stop = StopReason.Pause; t.Cts?.Cancel(); return; }
            else return;
        }
        _ = PersistAsync(t).ContinueWith(_ => RaiseChanged(t));
    }

    public void PauseAll()
    {
        foreach (var s in GetSnapshots().Where(s => s.State is TransferState.Running or TransferState.Queued)) Pause(s.TransferId);
    }

    public void Resume(Guid transferId)
    {
        ActiveTransfer? t;
        lock (_gate)
        {
            if (!_all.TryGetValue(transferId, out t)) return;
            if (t.Record.State is not (TransferState.Paused or TransferState.Failed)) return;
            t.Record.State = TransferState.Queued;
            t.Record.ErrorMessage = null;
            t.Record.RetryCount = 0;
        }
        _ = PersistAsync(t).ContinueWith(_ => { RaiseChanged(t); Pump(); });
    }

    public void ResumeAll()
    {
        foreach (var s in GetSnapshots().Where(s => s.State is TransferState.Paused or TransferState.Failed)) Resume(s.TransferId);
    }

    public void Cancel(Guid transferId)
    {
        ActiveTransfer? t;
        lock (_gate)
        {
            if (!_all.TryGetValue(transferId, out t)) return;
            switch (t.Record.State)
            {
                case TransferState.Running:
                    t.Stop = StopReason.Cancel;
                    t.Cts?.Cancel();
                    return;
                case TransferState.Queued or TransferState.Paused or TransferState.Failed:
                    t.Record.State = TransferState.Cancelled;
                    break;
                default:
                    return;
            }
        }
        _ = Task.Run(async () =>
        {
            await CleanupCancelledAsync(t).ConfigureAwait(false);
            await PersistAsync(t).ConfigureAwait(false);
            RaiseChanged(t);
            Pump();
        });
    }

    public async Task ClearFinishedAsync()
    {
        lock (_gate)
        {
            foreach (var id in _all.Where(kv => kv.Value.Record.State is TransferState.Completed or TransferState.Cancelled)
                                   .Select(kv => kv.Key).ToList())
                _all.Remove(id);
        }
        await _index.ClearFinishedTransfersAsync().ConfigureAwait(false);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task WaitForIdleAsync(CancellationToken ct = default) => WaitForIdleAsync(TimeSpan.FromMinutes(30), ct);

    public async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task[] running;
            bool pendingQueued;
            lock (_gate)
            {
                running = _all.Values.Where(t => t.Record.State == TransferState.Running && t.RunTask is not null).Select(t => t.RunTask!).ToArray();
                pendingQueued = _all.Values.Any(t => t.Record.State == TransferState.Queued);
            }
            if (running.Length == 0 && !pendingQueued) return;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Les transferts ne se sont pas terminés à temps.");
            if (running.Length > 0) await Task.WhenAny(Task.WhenAll(running), Task.Delay(50, ct)).ConfigureAwait(false);
            else await Task.Delay(20, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }


    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var records = await _index.GetTransfersAsync(ct).ConfigureAwait(false);
        foreach (var r in records)
        {
            if (r.State == TransferState.Running) r.State = TransferState.Queued;
            if (r.State is TransferState.Queued or TransferState.Paused)
            {
                var file = r.FileId is Guid fid ? await _index.GetFileAsync(fid, ct).ConfigureAwait(false) : null;
                if (file is null) { r.State = TransferState.Cancelled; r.ErrorMessage = "Fichier introuvable dans l'index."; }
            }
            Register(r);
            if (r.State is TransferState.Queued or TransferState.Cancelled) await _index.UpdateTransferAsync(r, ct).ConfigureAwait(false);
        }

        foreach (var c in await _index.GetChunksByStatusAsync(new[] { ChunkStatus.Uploading }, ct).ConfigureAwait(false))
        {
            c.Status = ChunkStatus.Planned;
            await _index.UpdateChunkAsync(c, ct).ConfigureAwait(false);
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
        Pump();
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        List<ActiveTransfer> running;
        lock (_gate)
        {
            _shuttingDown = true;
            running = _all.Values.Where(t => t.Record.State == TransferState.Running).ToList();
            foreach (var t in running) { t.Stop = StopReason.Shutdown; t.Cts?.Cancel(); }
        }
        var tasks = running.Select(t => t.RunTask).Where(t => t is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
    }


    private async Task RunAsync(ActiveTransfer t)
    {
        var ct = t.Cts!.Token;
        t.Phase = "Démarrage";
        t.PhaseProgress = -1;
        await PersistAsync(t).ConfigureAwait(false);
        RaiseChanged(t);
        var r = t.Record;
        try
        {
            if (r.Direction == TransferDirection.Upload) await RunUploadAsync(t, ct).ConfigureAwait(false);
            else await RunDownloadAsync(t, ct).ConfigureAwait(false);
            lock (_gate)
            {
                r.State = TransferState.Completed;
                r.ErrorMessage = null;
                r.CompletedAt = DateTime.UtcNow;
                r.BytesDone = r.TotalBytes;
                t.Phase = "Terminé";
                t.PhaseProgress = -1;
            }
            _log.LogInformation("Transfert terminé : {Name}", r.Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            switch (t.Stop)
            {
                case StopReason.Cancel:
                    await CleanupCancelledAsync(t).ConfigureAwait(false);
                    lock (_gate) { r.State = TransferState.Cancelled; t.Phase = "Annulé"; }
                    break;
                case StopReason.Shutdown:
                    lock (_gate) { r.State = TransferState.Queued; t.Phase = "Interrompu"; }
                    break;
                default:
                    lock (_gate) { r.State = TransferState.Paused; t.Phase = "En pause"; }
                    break;
            }
        }
        catch (Exception ex)
        {
            var message = Describe(ex);
            _log.LogWarning("Transfert en échec : {Name} — {Message}", r.Name, message);
            lock (_gate)
            {
                r.State = TransferState.Failed;
                r.ErrorMessage = message;
                t.Phase = "Échec";
            }
        }
        finally
        {
            t.PhaseProgress = -1;
            await PersistAsync(t).ConfigureAwait(false);
            lock (_gate) { t.Cts?.Dispose(); t.Cts = null; }
            RaiseChanged(t);
            Pump();
        }
    }

    internal static string Describe(Exception ex) => Redactor.Redact(ex switch
    {
        StorageProviderException s => s.Kind switch
        {
            ProviderErrorKind.AuthRequired => "Autorisation Google expirée ou révoquée : reconnectez le compte. " + s.Message,
            ProviderErrorKind.StorageFull => "Le Drive est plein. " + s.Message,
            ProviderErrorKind.DailyQuota => "Quota quotidien Google atteint. " + s.Message,
            ProviderErrorKind.RateLimited => "Limite de débit Google atteinte. " + s.Message,
            ProviderErrorKind.Transient => "Erreur réseau persistante : " + s.Message,
            _ => s.Message,
        },
        InsufficientStorageException or TransferException => ex.Message,
        System.Security.Cryptography.CryptographicException => "Chiffrement/déchiffrement impossible : " + ex.Message,
        UnauthorizedAccessException => "Accès refusé : " + ex.Message,
        IOException => "Erreur disque : " + ex.Message,
        InvalidDataException => ex.Message,
        _ => ex.GetType().Name + " : " + ex.Message,
    });

    private async Task CleanupCancelledAsync(ActiveTransfer t)
    {
        var r = t.Record;
        try
        {
            if (r.Direction == TransferDirection.Upload && r.FileId is Guid fid)
            {
                var file = await _index.GetFileAsync(fid).ConfigureAwait(false);
                if (file is not null && file.Status != FileStatus.Stored)
                {
                    var report = await _cleaner.DeleteRemoteAsync(fid).ConfigureAwait(false);
                    if (report.Success) await _index.DeleteFileAsync(fid).ConfigureAwait(false);
                    else
                    {
                        file.Status = FileStatus.Deleting;
                        await _index.UpdateFileAsync(file).ConfigureAwait(false);
                    }
                }
                TryDeleteTempFor(fid);
            }
            else if (r.Direction == TransferDirection.Download)
            {
                TryDelete(r.LocalPath + PartSuffix);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Nettoyage après annulation incomplet."); }
    }

    private void TryDeleteTempFor(Guid fileId)
    {
        try
        {
            var dir = _settings.Current.TempDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, $"{fileId:N}.*")) TryDelete(f);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }


    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var seconds = Math.Min(_options.MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(attempt, 10)));
        var delay = TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
        return retryAfter is { } ra && ra > delay ? ra : delay;
    }

    private async Task<(IStorageProvider Provider, CloudAccount Account)> GetProviderAsync(string accountId, CancellationToken ct)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new TransferException("Compte introuvable dans l'index.");
        return (_providers.Get(account.Provider), account);
    }

    private static async Task ForEachParallelAsync<T>(IEnumerable<T> items, int degree, Func<T, CancellationToken, Task> action, CancellationToken ct)
    {
        Exception? first = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree), CancellationToken = linked.Token },
                async (item, token) =>
                {
                    try { await action(item, token).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref first, ex, null);
                        linked.Cancel();
                        throw;
                    }
                }).ConfigureAwait(false);
        }
        catch
        {
            if (first is not null && !ct.IsCancellationRequested) ExceptionDispatchInfo.Capture(first).Throw();
            throw;
        }
    }
}
