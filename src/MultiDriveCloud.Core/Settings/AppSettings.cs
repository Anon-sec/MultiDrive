using System.Globalization;
using MultiDriveCloud.Core.Interfaces;

namespace MultiDriveCloud.Core.Settings;

public sealed class AppSettings
{
    public const long MinChunkSize = 1L * 1024 * 1024;

    public int MaxConcurrentTransfers { get; set; } = 3;
    public int FragmentParallelism { get; set; } = 3;
    public long ChunkSizeBytes { get; set; } = 1024L * 1024 * 1024;
    public string AllocationStrategy { get; set; } = "LargestFreeSpace";
    public long SafetyMarginBytes { get; set; } = 256L * 1024 * 1024;
    public string DownloadDirectory { get; set; } = "";
    public string TempDirectory { get; set; } = "";
    public string Theme { get; set; } = "Dark";
    public bool NotificationsEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool EncryptionEnabled { get; set; }
    public int QuotaRefreshMinutes { get; set; } = 15;
    public int MaxRetries { get; set; } = 5;
    public string GoogleClientId { get; set; } = "";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        MaxConcurrentTransfers = Math.Clamp(MaxConcurrentTransfers, 1, 16);
        FragmentParallelism = Math.Clamp(FragmentParallelism, 1, 8);
        ChunkSizeBytes = Math.Max(MinChunkSize, ChunkSizeBytes);
        SafetyMarginBytes = Math.Max(0, SafetyMarginBytes);
        QuotaRefreshMinutes = Math.Clamp(QuotaRefreshMinutes, 1, 24 * 60);
        MaxRetries = Math.Clamp(MaxRetries, 0, 20);
        if (Theme != "Light") Theme = "Dark";
    }
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler? Changed;
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}

public sealed class SettingsService : ISettingsService
{
    private readonly IKeyValueStore _store;
    private readonly object _gate = new();
    private AppSettings _current = new();

    public SettingsService(IKeyValueStore store, string defaultDownloadDirectory, string defaultTempDirectory)
    {
        _store = store;
        _current.DownloadDirectory = defaultDownloadDirectory;
        _current.TempDirectory = defaultTempDirectory;
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current.Clone(); }
    }

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var values = await _store.GetAllAsync(ct).ConfigureAwait(false);
        AppSettings s;
        lock (_gate) s = _current.Clone();
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!values.TryGetValue(prop.Name, out var raw) || !prop.CanWrite) continue;
            try
            {
                object? v = prop.PropertyType switch
                {
                    var t when t == typeof(int) => int.Parse(raw, CultureInfo.InvariantCulture),
                    var t when t == typeof(long) => long.Parse(raw, CultureInfo.InvariantCulture),
                    var t when t == typeof(bool) => bool.Parse(raw),
                    _ => raw,
                };
                prop.SetValue(s, v);
            }
            catch (FormatException) { }
        }
        s.Normalize();
        lock (_gate) _current = s;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var s = settings.Clone();
        s.Normalize();
        foreach (var prop in typeof(AppSettings).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            var value = prop.GetValue(s);
            var text = value switch
            {
                bool b => b ? "True" : "False",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                null => "",
                _ => value.ToString() ?? "",
            };
            await _store.SetAsync(prop.Name, text, ct).ConfigureAwait(false);
        }
        lock (_gate) _current = s;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
