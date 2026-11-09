using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Util;

namespace MultiDriveCloud.Core.Storage;

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, IStorageProvider> _providers;

    public ProviderRegistry(IEnumerable<IStorageProvider> providers) =>
        _providers = providers.ToDictionary(p => p.ProviderId, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<IStorageProvider> All => _providers.Values;

    public IStorageProvider Get(string providerId) =>
        _providers.TryGetValue(providerId, out var p)
            ? p
            : throw new InvalidOperationException($"Fournisseur de stockage inconnu : {providerId}");

    public IStorageProvider Default => _providers.Values.First();
}

public sealed class AccountInUseException : Exception
{
    public AccountInUseException(string message) : base(message) { }
}

public sealed class AccountService
{
    private readonly ProviderRegistry _providers;
    private readonly IAccountRepository _accounts;
    private readonly IIndexRepository _index;
    private readonly StorageAllocator _allocator;
    private readonly ILogger<AccountService> _log;

    public AccountService(ProviderRegistry providers, IAccountRepository accounts, IIndexRepository index,
        StorageAllocator allocator, ILogger<AccountService> log)
    {
        _providers = providers;
        _accounts = accounts;
        _index = index;
        _allocator = allocator;
        _log = log;
    }

    public event EventHandler? AccountsChanged;

    private void Changed() => AccountsChanged?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<CloudAccount>> GetAccountsAsync(CancellationToken ct = default) => _accounts.GetAllAsync(ct);

    public async Task<CloudAccount> AddAccountAsync(string? providerId = null, string? loginHint = null, CancellationToken ct = default)
    {
        var provider = providerId is null ? _providers.Default : _providers.Get(providerId);
        var authorized = await provider.AuthorizeAsync(loginHint, ct).ConfigureAwait(false);
        AccountQuota quota;
        try
        {
            quota = await provider.GetQuotaAsync(authorized.AccountId, ct).ConfigureAwait(false);
        }
        catch
        {
            var existing = await _accounts.GetAsync(authorized.AccountId, CancellationToken.None).ConfigureAwait(false);
            if (existing is null)
                await provider.DisconnectAsync(authorized.AccountId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var account = await _accounts.GetAsync(authorized.AccountId, ct).ConfigureAwait(false) ?? new CloudAccount
        {
            AccountId = authorized.AccountId,
            Provider = provider.ProviderId,
            CreatedAt = DateTime.UtcNow,
        };
        account.Email = authorized.Email;
        account.DisplayName = authorized.DisplayName;
        ApplyQuota(account, quota);
        account.Status = AccountStatus.Connected;
        account.StatusMessage = null;
        await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        _allocator.ClearCooldown(account.AccountId);
        _log.LogInformation("Compte ajouté : {Email}", account.Email);
        Changed();
        return account;
    }

    public async Task<CloudAccount> ReauthorizeAsync(string accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Compte introuvable.");
        var provider = _providers.Get(account.Provider);
        var authorized = await provider.AuthorizeAsync(account.Email, ct).ConfigureAwait(false);
        if (authorized.AccountId != account.AccountId)
        {
            await provider.DisconnectAsync(authorized.AccountId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Vous vous êtes connecté avec {authorized.Email} au lieu de {account.Email}. Recommencez avec le bon compte Google.");
        }
        var quota = await provider.GetQuotaAsync(account.AccountId, ct).ConfigureAwait(false);
        ApplyQuota(account, quota);
        account.Status = AccountStatus.Connected;
        account.StatusMessage = null;
        await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        _allocator.ClearCooldown(account.AccountId);
        Changed();
        return account;
    }

    private static void ApplyQuota(CloudAccount account, AccountQuota quota)
    {
        account.TotalBytes = quota.TotalBytes;
        account.UsedBytes = quota.UsedBytes;
        account.IsUnlimited = quota.IsUnlimited;
        account.LastSync = DateTime.UtcNow;
    }

    public async Task<CloudAccount?> RefreshAsync(string accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null || account.Status == AccountStatus.Disconnected) return account;
        var provider = _providers.Get(account.Provider);
        try
        {
            var quota = await provider.GetQuotaAsync(accountId, ct).ConfigureAwait(false);
            ApplyQuota(account, quota);
            account.Status = AccountStatus.Connected;
            account.StatusMessage = null;
        }
        catch (StorageProviderException ex) when (ex.Kind == ProviderErrorKind.AuthRequired)
        {
            account.Status = AccountStatus.NeedsReauthorization;
            account.StatusMessage = "L'autorisation a expiré ou a été révoquée. Reconnectez le compte.";
            _log.LogWarning("Compte {Email} : autorisation à renouveler.", account.Email);
        }
        catch (StorageProviderException ex)
        {
            account.StatusMessage = "Actualisation impossible : " + Redactor.Redact(ex.Message);
            _log.LogWarning("Actualisation du quota impossible pour {Email} ({Kind}).", account.Email, ex.Kind);
        }
        await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        Changed();
        return account;
    }

    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var all = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(all.Where(a => a.Status != AccountStatus.Disconnected)
            .Select(a => RefreshAsync(a.AccountId, ct))).ConfigureAwait(false);
    }

    public async Task MarkNeedsReauthorizationAsync(string accountId, string? message = null)
    {
        var account = await _accounts.GetAsync(accountId).ConfigureAwait(false);
        if (account is null || account.Status == AccountStatus.Disconnected) return;
        account.Status = AccountStatus.NeedsReauthorization;
        account.StatusMessage = message ?? "L'autorisation a expiré ou a été révoquée.";
        await _accounts.UpsertAsync(account).ConfigureAwait(false);
        Changed();
    }

    public async Task DisconnectAsync(string accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null) return;
        var provider = _providers.Get(account.Provider);
        try { await provider.DisconnectAsync(accountId, ct).ConfigureAwait(false); }
        catch (StorageProviderException ex) { _log.LogWarning("Révocation distante impossible ({Kind}).", ex.Kind); }
        account.Status = AccountStatus.Disconnected;
        account.StatusMessage = "Compte déconnecté.";
        await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task<long> GetStoredBytesAsync(string accountId, CancellationToken ct = default)
    {
        var stats = await _index.GetStatsAsync(ct).ConfigureAwait(false);
        return stats.StoredBytesByAccount.GetValueOrDefault(accountId);
    }

    public async Task RemoveAsync(string accountId, CancellationToken ct = default)
    {
        var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false);
        if (account is null) return;
        var stored = await GetStoredBytesAsync(accountId, ct).ConfigureAwait(false);
        if (stored > 0)
            throw new AccountInUseException(
                $"{account.Email} contient {Formatting.Bytes(stored)} de fragments. Supprimez ou re-téléchargez ces fichiers avant de retirer le compte, " +
                "sinon ils deviendraient irrécupérables. Vous pouvez seulement le déconnecter.");
        if (account.Status != AccountStatus.Disconnected)
            await DisconnectAsync(accountId, ct).ConfigureAwait(false);
        await _accounts.DeleteAsync(accountId, ct).ConfigureAwait(false);
        Changed();
    }

    public async Task<StorageSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        var stats = await _index.GetStatsAsync(ct).ConfigureAwait(false);
        var connected = accounts.Where(a => a.Status == AccountStatus.Connected).ToList();
        var usable = await _allocator.GetUsableBytesAsync(ct).ConfigureAwait(false);
        return new StorageSummary(
            AccountCount: accounts.Count,
            ConnectedCount: connected.Count,
            TotalCapacity: connected.Sum(a => a.TotalBytes),
            UsedOnAccounts: connected.Sum(a => a.UsedBytes),
            FreeBytes: connected.Sum(a => a.AvailableBytes),
            UsableBytes: usable,
            UsedByApp: stats.StoredBytesTotal,
            FileCount: stats.StoredFiles,
            FolderCount: stats.Folders);
    }
}
