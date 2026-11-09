using System.Collections.Concurrent;
using System.Security.Cryptography;
using MultiDriveCloud.Core.Interfaces;

namespace MultiDriveCloud.Tests.Support;

public sealed class FakeStorageProvider : IStorageProvider
{
    public sealed class Drive
    {
        public string AccountId = "";
        public string Email = "";
        public long Capacity;
        public long ExtraUsed;        public bool Revoked;
        public ConcurrentDictionary<string, StoredObject> Objects = new();
        public long Used => ExtraUsed + Objects.Values.Sum(o => (long)o.Data.Length);
    }

    public sealed class StoredObject
    {
        public string Id = "";
        public string Key = "";
        public byte[] Data = Array.Empty<byte>();
    }

    private sealed class Session
    {
        public MemoryStream Received = new();
        public string AccountId = "";
    }

    private readonly Queue<(string Email, long Capacity)> _identities = new();
    public ConcurrentDictionary<string, Drive> Drives { get; } = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public volatile bool NetworkDown;
    public ConcurrentDictionary<string, int> FailUploadsRemaining { get; } = new();    public ConcurrentDictionary<string, ProviderErrorKind> FailUploadKind { get; } = new();
    public volatile bool CorruptNextUploadOnce;
    public volatile bool CorruptEveryDownload;
    public volatile bool FailDeletes;
    public volatile string? HangOnFragmentSuffix;    public volatile bool HangReached;
    public Action<UploadRequest>? BeforeUpload;

    public ConcurrentDictionary<string, int> UploadCalls { get; } = new();    public ConcurrentDictionary<string, long> ResumeOffsets { get; } = new();    public ConcurrentDictionary<string, string> UploadedToAccount { get; } = new();    public int DownloadCalls;

    public string ProviderId => "fake";

    public Drive AddDrive(string email, long capacity)
    {
        var d = new Drive { AccountId = "fake:" + email, Email = email, Capacity = capacity };
        Drives[d.AccountId] = d;
        return d;
    }

    public void EnqueueIdentity(string email, long capacity) => _identities.Enqueue((email, capacity));

    public Drive ByEmail(string email) => Drives.Values.Single(d => d.Email == email);

    public int TotalObjects => Drives.Values.Sum(d => d.Objects.Count);

    public Task<AuthorizedAccount> AuthorizeAsync(string? loginHint, CancellationToken ct)
    {
        var (email, cap) = _identities.Dequeue();
        var d = Drives.Values.FirstOrDefault(x => x.Email == email) ?? AddDrive(email, cap);
        d.Revoked = false;
        return Task.FromResult(new AuthorizedAccount(d.AccountId, d.Email, d.Email));
    }

    private Drive Get(string accountId)
    {
        if (NetworkDown) throw new StorageProviderException(ProviderErrorKind.Transient, "Réseau coupé (simulé).");
        var d = Drives[accountId];
        if (d.Revoked) throw new StorageProviderException(ProviderErrorKind.AuthRequired, "Autorisation révoquée (simulée).");
        return d;
    }

    public Task<AccountQuota> GetQuotaAsync(string accountId, CancellationToken ct)
    {
        var d = Get(accountId);
        return Task.FromResult(new AccountQuota(d.Capacity, d.Used, false));
    }

    public async Task<RemoteFileInfo> UploadAsync(UploadRequest request, CancellationToken ct)
    {
        UploadCalls.AddOrUpdate(request.FragmentKey, 1, (_, n) => n + 1);
        BeforeUpload?.Invoke(request);
        var d = Get(request.AccountId);

        if (FailUploadsRemaining.TryGetValue(request.AccountId, out var left) && left > 0)
        {
            FailUploadsRemaining[request.AccountId] = left - 1;
            var kind = FailUploadKind.GetValueOrDefault(request.AccountId, ProviderErrorKind.Transient);
            throw new StorageProviderException(kind, $"Échec simulé ({kind}).");
        }

        Session session;
        string uri;
        if (request.ResumeSessionUri is { } existing && _sessions.TryGetValue(existing, out var s))
        {
            session = s;
            uri = existing;
            ResumeOffsets[request.FragmentKey] = session.Received.Length;
        }
        else
        {
            session = new Session { AccountId = request.AccountId };
            uri = "https://fake.invalid/upload?upload_id=" + Guid.NewGuid().ToString("N");
            _sessions[uri] = session;
            request.OnSessionCreated?.Invoke(uri);
        }

        if (d.Used + (request.Length - session.Received.Length) > d.Capacity)
            throw new StorageProviderException(ProviderErrorKind.StorageFull, "Quota dépassé (simulé).");

        request.Content.Seek(session.Received.Length, SeekOrigin.Begin);
        var buffer = new byte[256 * 1024];
        while (session.Received.Length < request.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (NetworkDown) throw new StorageProviderException(ProviderErrorKind.Transient, "Réseau coupé en cours d'envoi (simulé).");
            var want = (int)Math.Min(buffer.Length, request.Length - session.Received.Length);
            var n = await request.Content.ReadAsync(buffer.AsMemory(0, want), ct);
            if (n == 0) throw new IOException("Flux source plus court que prévu.");
            session.Received.Write(buffer, 0, n);
            request.Progress?.Report(session.Received.Length);

            if (HangOnFragmentSuffix is { } suffix && request.FragmentKey.EndsWith(suffix) && session.Received.Length >= 1024 * 1024)
            {
                HangReached = true;
                await Task.Delay(Timeout.Infinite, ct);            }
        }

        var data = session.Received.ToArray();
        _sessions.TryRemove(uri, out _);
        if (CorruptNextUploadOnce && data.Length > 0)
        {
            CorruptNextUploadOnce = false;
            data[data.Length / 2] ^= 0xFF;
        }
        var obj = new StoredObject { Id = Guid.NewGuid().ToString("N"), Key = request.FragmentKey, Data = data };
        d.Objects[obj.Id] = obj;
        UploadedToAccount[request.FragmentKey] = request.AccountId;
        return new RemoteFileInfo(obj.Id, data.Length, Convert.ToHexStringLower(MD5.HashData(data)));
    }

    public async Task DownloadAsync(DownloadRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref DownloadCalls);
        var d = Get(request.AccountId);
        if (!d.Objects.TryGetValue(request.RemoteId, out var obj))
            throw new StorageProviderException(ProviderErrorKind.NotFound, "Objet introuvable (simulé).");
        var data = (byte[])obj.Data.Clone();
        if (CorruptEveryDownload && data.Length > 0) data[0] ^= 0x55;
        const int step = 256 * 1024;
        for (var off = 0; off < data.Length; off += step)
        {
            ct.ThrowIfCancellationRequested();
            if (NetworkDown) throw new StorageProviderException(ProviderErrorKind.Transient, "Réseau coupé en cours de téléchargement (simulé).");
            var n = Math.Min(step, data.Length - off);
            await request.Destination.WriteAsync(data.AsMemory(off, n), ct);
            request.Progress?.Report(off + n);
        }
    }

    public Task<RemoteFileInfo?> GetFileInfoAsync(string accountId, string remoteId, CancellationToken ct)
    {
        var d = Get(accountId);
        return Task.FromResult(d.Objects.TryGetValue(remoteId, out var o)
            ? new RemoteFileInfo(o.Id, o.Data.Length, Convert.ToHexStringLower(MD5.HashData(o.Data)))
            : null);
    }

    public Task<RemoteFileInfo?> FindFragmentAsync(string accountId, string fragmentKey, CancellationToken ct)
    {
        var d = Get(accountId);
        var o = d.Objects.Values.FirstOrDefault(x => x.Key == fragmentKey);
        return Task.FromResult(o is null ? null : new RemoteFileInfo(o.Id, o.Data.Length, Convert.ToHexStringLower(MD5.HashData(o.Data))));
    }

    public Task DeleteAsync(string accountId, string remoteId, CancellationToken ct)
    {
        var d = Get(accountId);
        if (FailDeletes) throw new StorageProviderException(ProviderErrorKind.Fatal, "Suppression refusée (simulée).");
        d.Objects.TryRemove(remoteId, out _);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string accountId, CancellationToken ct)
    {
        if (Drives.TryGetValue(accountId, out var d)) d.Revoked = true;
        return Task.CompletedTask;
    }
}
