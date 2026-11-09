using Microsoft.Extensions.Logging;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;

namespace MultiDriveCloud.Core.Storage;

public sealed record AllocationCandidate(CloudAccount Account, long EffectiveFreeBytes);

public interface IAllocationStrategy
{
    string Name { get; }
    string DisplayName { get; }
    AllocationCandidate? Select(IReadOnlyList<AllocationCandidate> candidates, long requiredBytes);
}

public sealed class LargestFreeSpaceStrategy : IAllocationStrategy
{
    public string Name => "LargestFreeSpace";
    public string DisplayName => "Plus grand espace libre";

    public AllocationCandidate? Select(IReadOnlyList<AllocationCandidate> candidates, long requiredBytes) =>
        candidates.OrderByDescending(c => c.EffectiveFreeBytes).ThenBy(c => c.Account.AccountId, StringComparer.Ordinal).FirstOrDefault();
}

public sealed class BalancedUsageStrategy : IAllocationStrategy
{
    public string Name => "BalancedUsage";
    public string DisplayName => "Utilisation équilibrée (pourcentage libre)";

    public AllocationCandidate? Select(IReadOnlyList<AllocationCandidate> candidates, long requiredBytes) =>
        candidates.OrderByDescending(c => c.Account.TotalBytes <= 0 ? 0 : (double)c.EffectiveFreeBytes / c.Account.TotalBytes)
                  .ThenBy(c => c.Account.AccountId, StringComparer.Ordinal).FirstOrDefault();
}

public sealed class BestFitStrategy : IAllocationStrategy
{
    public string Name => "BestFit";
    public string DisplayName => "Remplir d'abord (meilleur ajustement)";

    public AllocationCandidate? Select(IReadOnlyList<AllocationCandidate> candidates, long requiredBytes) =>
        candidates.OrderBy(c => c.EffectiveFreeBytes).ThenBy(c => c.Account.AccountId, StringComparer.Ordinal).FirstOrDefault();
}

public sealed class AllocationLease
{
    public Guid Id { get; } = Guid.NewGuid();
    public string AccountId { get; internal set; } = "";
    public long Bytes { get; internal set; }
    internal bool Closed;
}

public sealed class InsufficientStorageException : Exception
{
    public long RequiredBytes { get; }
    public long AvailableBytes { get; }

    public InsufficientStorageException(long required, long available)
        : base($"Espace insuffisant : {Formatting.Bytes(required)} requis, {Formatting.Bytes(available)} disponibles (marge de sécurité incluse).")
    {
        RequiredBytes = required;
        AvailableBytes = available;
    }
}

public sealed class StorageAllocator
{
    private readonly IAccountRepository _accounts;
    private readonly ISettingsService _settings;
    private readonly IReadOnlyDictionary<string, IAllocationStrategy> _strategies;
    private readonly ILogger<StorageAllocator> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, long> _reserved = new();
    private readonly Dictionary<string, DateTime> _cooldownUntil = new();
    private readonly Func<DateTime> _clock;

    public static readonly long MinFragmentBytes = 4L * 1024 * 1024;

    public StorageAllocator(IAccountRepository accounts, ISettingsService settings, IEnumerable<IAllocationStrategy> strategies,
        ILogger<StorageAllocator> log, Func<DateTime>? clock = null)
    {
        _accounts = accounts;
        _settings = settings;
        _strategies = strategies.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public IEnumerable<IAllocationStrategy> Strategies => _strategies.Values;

    private IAllocationStrategy CurrentStrategy =>
        _strategies.TryGetValue(_settings.Current.AllocationStrategy, out var s) ? s : _strategies.Values.First();

    public long ReservedFor(string accountId)
    {
        lock (_reserved) return _reserved.GetValueOrDefault(accountId);
    }

    public void MarkUnavailable(string accountId, TimeSpan duration)
    {
        lock (_cooldownUntil) _cooldownUntil[accountId] = _clock() + duration;
        _log.LogWarning("Compte {AccountId} mis de côté pendant {Minutes:F0} min.", accountId, duration.TotalMinutes);
    }

    public void ClearCooldown(string accountId)
    {
        lock (_cooldownUntil) _cooldownUntil.Remove(accountId);
    }

    private bool InCooldown(string accountId)
    {
        lock (_cooldownUntil)
        {
            if (!_cooldownUntil.TryGetValue(accountId, out var until)) return false;
            if (until > _clock()) return true;
            _cooldownUntil.Remove(accountId);
            return false;
        }
    }

    private long EffectiveFree(CloudAccount a, long margin) =>
        Math.Max(0, a.AvailableBytes - margin - ReservedFor(a.AccountId));

    public async Task<long> GetUsableBytesAsync(CancellationToken ct = default)
    {
        var margin = _settings.Current.SafetyMarginBytes;
        var accounts = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
        return accounts.Where(a => a.Status == AccountStatus.Connected && !InCooldown(a.AccountId))
                       .Sum(a => EffectiveFree(a, margin));
    }

    public async Task<AllocationLease?> TryAllocateAsync(long desiredBytes, long minimumBytes,
        IReadOnlyCollection<string>? excludedAccounts = null, CancellationToken ct = default)
    {
        if (minimumBytes > desiredBytes) minimumBytes = desiredBytes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var margin = _settings.Current.SafetyMarginBytes;
            var all = await _accounts.GetAllAsync(ct).ConfigureAwait(false);
            var candidates = all
                .Where(a => a.Status == AccountStatus.Connected)
                .Where(a => excludedAccounts is null || !excludedAccounts.Contains(a.AccountId))
                .Where(a => !InCooldown(a.AccountId))
                .Select(a => new AllocationCandidate(a, EffectiveFree(a, margin)))
                .ToList();

            var fitting = candidates.Where(c => c.EffectiveFreeBytes >= desiredBytes).ToList();
            AllocationCandidate? chosen;
            long granted;
            if (fitting.Count > 0)
            {
                chosen = CurrentStrategy.Select(fitting, desiredBytes);
                granted = desiredBytes;
            }
            else
            {
                chosen = candidates.Where(c => c.EffectiveFreeBytes >= minimumBytes && c.EffectiveFreeBytes > 0)
                                   .OrderByDescending(c => c.EffectiveFreeBytes)
                                   .ThenBy(c => c.Account.AccountId, StringComparer.Ordinal)
                                   .FirstOrDefault();
                granted = chosen?.EffectiveFreeBytes ?? 0;
            }
            if (chosen is null) return null;

            lock (_reserved) _reserved[chosen.Account.AccountId] = _reserved.GetValueOrDefault(chosen.Account.AccountId) + granted;
            return new AllocationLease { AccountId = chosen.Account.AccountId, Bytes = granted };
        }
        finally { _gate.Release(); }
    }

    public AllocationLease Reserve(string accountId, long bytes)
    {
        lock (_reserved) _reserved[accountId] = _reserved.GetValueOrDefault(accountId) + bytes;
        return new AllocationLease { AccountId = accountId, Bytes = bytes };
    }

    public void Shrink(AllocationLease lease, long newBytes)
    {
        lock (_reserved)
        {
            if (lease.Closed || newBytes >= lease.Bytes) return;
            _reserved[lease.AccountId] = Math.Max(0, _reserved.GetValueOrDefault(lease.AccountId) - (lease.Bytes - newBytes));
            lease.Bytes = newBytes;
        }
    }

    public void Release(AllocationLease? lease)
    {
        if (lease is null) return;
        lock (_reserved)
        {
            if (lease.Closed) return;
            lease.Closed = true;
            _reserved[lease.AccountId] = Math.Max(0, _reserved.GetValueOrDefault(lease.AccountId) - lease.Bytes);
        }
    }

    public async Task CompleteAsync(AllocationLease lease, long storedBytes, CancellationToken ct = default)
    {
        Release(lease);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = await _accounts.GetAsync(lease.AccountId, ct).ConfigureAwait(false);
            if (account is null) return;
            account.UsedBytes += storedBytes;
            await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task FreedAsync(string accountId, long bytes, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var account = await _accounts.GetAsync(accountId, ct).ConfigureAwait(false);
            if (account is null) return;
            account.UsedBytes = Math.Max(0, account.UsedBytes - bytes);
            await _accounts.UpsertAsync(account, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

public static class Formatting
{
    public static string Bytes(long bytes)
    {
        string[] units = { "o", "Ko", "Mo", "Go", "To", "Po" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} {units[0]}" : $"{v:0.##} {units[i]}";
    }
}
