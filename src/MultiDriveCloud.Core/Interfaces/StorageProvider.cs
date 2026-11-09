namespace MultiDriveCloud.Core.Interfaces;

public enum ProviderErrorKind
{
    Transient,
    RateLimited,
    AuthRequired,
    StorageFull,
    DailyQuota,
    NotFound,
    Fatal,
}

public sealed class StorageProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    public TimeSpan? RetryAfter { get; }

    public StorageProviderException(ProviderErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(message, inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }
}

public sealed record AuthorizedAccount(string AccountId, string Email, string DisplayName);

public sealed record AccountQuota(long TotalBytes, long UsedBytes, bool IsUnlimited);

public sealed record RemoteFileInfo(string Id, long Size, string? Md5);

public sealed class UploadRequest
{
    public required string AccountId { get; init; }
    public required string RemoteName { get; init; }
    public required string FragmentKey { get; init; }
    public IDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public required Stream Content { get; init; }
    public required long Length { get; init; }
    public string? ResumeSessionUri { get; init; }
    public Action<string>? OnSessionCreated { get; init; }
    public IProgress<long>? Progress { get; init; }
}

public sealed class DownloadRequest
{
    public required string AccountId { get; init; }
    public required string RemoteId { get; init; }
    public required Stream Destination { get; init; }
    public IProgress<long>? Progress { get; init; }
}

public interface IStorageProvider
{
    string ProviderId { get; }

    Task<AuthorizedAccount> AuthorizeAsync(string? loginHint, CancellationToken ct);

    Task<AccountQuota> GetQuotaAsync(string accountId, CancellationToken ct);

    Task<RemoteFileInfo> UploadAsync(UploadRequest request, CancellationToken ct);

    Task DownloadAsync(DownloadRequest request, CancellationToken ct);

    Task<RemoteFileInfo?> GetFileInfoAsync(string accountId, string remoteId, CancellationToken ct);

    Task<RemoteFileInfo?> FindFragmentAsync(string accountId, string fragmentKey, CancellationToken ct);

    Task DeleteAsync(string accountId, string remoteId, CancellationToken ct);

    Task DisconnectAsync(string accountId, CancellationToken ct);
}
