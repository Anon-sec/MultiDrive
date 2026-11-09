using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.GoogleDrive.Authentication;
using MultiDriveCloud.Infrastructure;
using MultiDriveCloud.Infrastructure.Security;

namespace MultiDriveCloud.App.ViewModels;

public sealed partial class TransferItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public TransferDirection Direction { get; private set; }
    public string LocalPath { get; private set; } = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _directionIcon = "";
    [ObservableProperty] private string _directionText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _stateKind = "Muted";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private bool _isFinished;
    [ObservableProperty] private int _retries;
    public TransferState State { get; private set; }

    public TransferItemViewModel(Guid id) => Id = id;

    public void Update(TransferSnapshot s)
    {
        Direction = s.Direction;
        LocalPath = s.LocalPath;
        State = s.State;
        Name = s.Name;
        DirectionIcon = s.Direction == TransferDirection.Upload ? "" : "";
        DirectionText = s.Direction == TransferDirection.Upload ? "Envoi" : "Téléchargement";
        SizeText = s.State == TransferState.Completed || s.TotalBytes == 0 ? Fmt.Bytes(s.TotalBytes) : $"{Fmt.Bytes(s.BytesDone)} / {Fmt.Bytes(s.TotalBytes)}";
        var analysing = s.PhaseProgress is not null;
        Progress = analysing ? s.PhaseProgress!.Value : s.Progress;
        IsIndeterminate = s.State == TransferState.Running && s.PhaseProgress is null && s.BytesDone == 0 && s.TotalBytes > 0 && s.BytesPerSecond < 1;
        ProgressText = analysing ? $"{s.Phase} {Math.Round(Progress * 100):0} %" : $"{Math.Round(s.Progress * 100):0} %";
        SpeedText = s.State == TransferState.Running && !analysing ? Fmt.Speed(s.BytesPerSecond) : "";
        RemainingText = s.State == TransferState.Running && !analysing && s.Remaining is not null ? "reste " + Fmt.Duration(s.Remaining) : "";
        Destination = s.Destination;
        Error = s.State == TransferState.Failed ? s.ErrorMessage : null;
        Retries = s.RetryCount;
        (StateText, StateKind) = s.State switch
        {
            TransferState.Queued => ("En attente", "Muted"),
            TransferState.Running => (string.IsNullOrEmpty(s.Phase) ? "En cours" : s.Phase, "Busy"),
            TransferState.Paused => ("En pause", "Warn"),
            TransferState.Completed => ("Terminé", "Ok"),
            TransferState.Failed => ("Échec", "Error"),
            _ => ("Annulé", "Muted"),
        };
        CanPause = s.State is TransferState.Running or TransferState.Queued;
        CanResume = s.State is TransferState.Paused or TransferState.Failed;
        CanCancel = s.State is TransferState.Running or TransferState.Queued or TransferState.Paused or TransferState.Failed;
        IsFinished = s.State is TransferState.Completed or TransferState.Cancelled;
    }
}

public sealed record ErrorEntry(DateTime Time, string Name, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed partial class TransfersViewModel : ObservableObject, IActivatablePage
{
    private readonly TransferManager _transfers;
    private readonly AppPaths _paths;
    private readonly ShellService _shell;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<string> _loggedErrors = new();

    public ObservableCollection<TransferItemViewModel> Items { get; } = new();
    public ObservableCollection<ErrorEntry> Errors { get; } = new();

    [ObservableProperty] private string _summaryText = "Aucun transfert";
    [ObservableProperty] private double _globalProgress;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _remainingText = "—";
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _hasErrors;

    public TransfersViewModel(TransferManager transfers, AppPaths paths, ShellService shell)
    {
        _transfers = transfers;
        _paths = paths;
        _shell = shell;
        _timer.Tick += (_, _) => Sync();
        transfers.ListChanged += (_, _) => Ui.Post(Sync);
        transfers.TransferChanged += (_, s) =>
        {
            if (s.State == TransferState.Failed && s.ErrorMessage is not null && _loggedErrors.Add(s.TransferId + s.ErrorMessage))
                Ui.Post(() =>
                {
                    Errors.Insert(0, new ErrorEntry(DateTime.Now, s.Name, s.ErrorMessage));
                    while (Errors.Count > 200) Errors.RemoveAt(Errors.Count - 1);
                    HasErrors = true;
                });
        };
        _timer.Start();
    }

    public void Activate() => Sync();

    private void Sync()
    {
        var snaps = _transfers.GetSnapshots();
        var seen = new HashSet<Guid>();
        foreach (var s in snaps)
        {
            seen.Add(s.TransferId);
            var vm = Items.FirstOrDefault(i => i.Id == s.TransferId);
            if (vm is null)
            {
                vm = new TransferItemViewModel(s.TransferId);
                Items.Insert(0, vm);
            }
            vm.Update(s);
        }
        foreach (var gone in Items.Where(i => !seen.Contains(i.Id)).ToList()) Items.Remove(gone);
        IsEmpty = Items.Count == 0;

        var t = _transfers.GetTotals();
        GlobalProgress = t.Progress;
        SpeedText = Fmt.Speed(t.BytesPerSecond);
        RemainingText = Fmt.Duration(t.Remaining);
        var done = snaps.Count(s => s.State == TransferState.Completed);
        var failed = snaps.Count(s => s.State == TransferState.Failed);
        SummaryText = snaps.Count == 0 ? "Aucun transfert"
            : $"{t.Running} en cours · {t.Queued} en attente · {t.Paused} en pause · {done} terminé(s)" + (failed > 0 ? $" · {failed} en échec" : "");
    }

    [RelayCommand] private void Pause(TransferItemViewModel item) => _transfers.Pause(item.Id);
    [RelayCommand] private void Resume(TransferItemViewModel item) => _transfers.Resume(item.Id);
    [RelayCommand] private void Cancel(TransferItemViewModel item) => _transfers.Cancel(item.Id);
    [RelayCommand] private void PauseAll() => _transfers.PauseAll();
    [RelayCommand] private void ResumeAll() => _transfers.ResumeAll();
    [RelayCommand] private async Task ClearFinishedAsync() { await _transfers.ClearFinishedAsync(); Sync(); }
    [RelayCommand] private void ClearErrors() { Errors.Clear(); HasErrors = false; }

    [RelayCommand]
    private void OpenLogs() => Process.Start(new ProcessStartInfo(_paths.Logs) { UseShellExecute = true });

    [RelayCommand]
    private void ShowInFolder(TransferItemViewModel item)
    {
        var path = item.LocalPath;
        if (string.IsNullOrEmpty(path)) return;
        if (System.IO.File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
    }
}

public sealed partial class SettingsViewModel : ObservableObject, IActivatablePage
{
    private readonly ISettingsService _settings;
    private readonly StorageAllocator _allocator;
    private readonly GoogleOAuthConfigProvider _oauth;
    private readonly EncryptionKeyManager _keys;
    private readonly IIndexBackupService _backup;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly AppPaths _paths;
    private readonly TransferManager _transfers;
    private readonly ThemeService _theme;

    public IReadOnlyList<StrategyOption> Strategies { get; }
    public IReadOnlyList<string> Themes { get; } = new[] { "Dark", "Light" };

    [ObservableProperty] private int _maxConcurrentTransfers;
    [ObservableProperty] private int _fragmentParallelism;
    [ObservableProperty] private int _chunkSizeMb;
    [ObservableProperty] private int _safetyMarginMb;
    [ObservableProperty] private int _maxRetries;
    [ObservableProperty] private int _quotaRefreshMinutes;
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty] private string _tempDirectory = "";
    [ObservableProperty] private string _theme_ = "Dark";
    [ObservableProperty] private bool _notificationsEnabled;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _encryptionEnabled;
    [ObservableProperty] private StrategyOption? _selectedStrategy;
    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientSecret = "";
    [ObservableProperty] private string _oauthStatus = "";
    [ObservableProperty] private bool _oauthOk;
    [ObservableProperty] private string _keyStatus = "";
    [ObservableProperty] private string _dataFolder = "";
    [ObservableProperty] private string _savedText = "";

    public SettingsViewModel(ISettingsService settings, StorageAllocator allocator, GoogleOAuthConfigProvider oauth, EncryptionKeyManager keys,
        IIndexBackupService backup, DialogService dialogs, ShellService shell, AppPaths paths, TransferManager transfers, ThemeService theme)
    {
        _settings = settings;
        _allocator = allocator;
        _oauth = oauth;
        _keys = keys;
        _backup = backup;
        _dialogs = dialogs;
        _shell = shell;
        _paths = paths;
        _transfers = transfers;
        _theme = theme;
        Strategies = allocator.Strategies.Select(s => new StrategyOption(s.Name, s.DisplayName)).ToList();
        DataFolder = paths.Root;
        Load();
    }

    public void Activate() { Load(); _ = RefreshStatusAsync(); }

    private void Load()
    {
        var s = _settings.Current;
        MaxConcurrentTransfers = s.MaxConcurrentTransfers;
        FragmentParallelism = s.FragmentParallelism;
        ChunkSizeMb = (int)(s.ChunkSizeBytes / (1024 * 1024));
        SafetyMarginMb = (int)(s.SafetyMarginBytes / (1024 * 1024));
        MaxRetries = s.MaxRetries;
        QuotaRefreshMinutes = s.QuotaRefreshMinutes;
        DownloadDirectory = s.DownloadDirectory;
        TempDirectory = s.TempDirectory;
        Theme_ = s.Theme;
        NotificationsEnabled = s.NotificationsEnabled;
        StartWithWindows = s.StartWithWindows;
        EncryptionEnabled = s.EncryptionEnabled;
        SelectedStrategy = Strategies.FirstOrDefault(x => x.Name == s.AllocationStrategy) ?? Strategies.First();
        ClientId = s.GoogleClientId;
        ClientSecret = "";
    }

    private async Task RefreshStatusAsync()
    {
        var cfg = await _oauth.GetAsync();
        OauthOk = cfg is not null;
        OauthStatus = cfg is null
            ? "Non configuré : les comptes Google ne peuvent pas être ajoutés."
            : "Configuré (identifiant client " + (cfg.ClientId.Length > 18 ? cfg.ClientId[..18] + "…" : cfg.ClientId) + ")" + (string.IsNullOrEmpty(cfg.ClientSecret) ? " — sans secret client" : "");
        KeyStatus = await _keys.HasKeyAsync()
            ? "Une clé de chiffrement existe sur cet ordinateur (protégée par Windows)."
            : "Aucune clé : elle sera créée à la première activation du chiffrement.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var s = _settings.Current;
        s.MaxConcurrentTransfers = MaxConcurrentTransfers;
        s.FragmentParallelism = FragmentParallelism;
        s.ChunkSizeBytes = Math.Max(1, ChunkSizeMb) * 1024L * 1024;
        s.SafetyMarginBytes = Math.Max(0, SafetyMarginMb) * 1024L * 1024;
        s.MaxRetries = MaxRetries;
        s.QuotaRefreshMinutes = QuotaRefreshMinutes;
        s.DownloadDirectory = DownloadDirectory;
        s.TempDirectory = TempDirectory;
        s.Theme = Theme_;
        s.NotificationsEnabled = NotificationsEnabled;
        s.StartWithWindows = StartWithWindows;
        s.AllocationStrategy = SelectedStrategy?.Name ?? s.AllocationStrategy;

        var enablingEncryption = EncryptionEnabled && !s.EncryptionEnabled;
        s.EncryptionEnabled = EncryptionEnabled;
        try
        {
            Directory.CreateDirectory(s.DownloadDirectory);
            Directory.CreateDirectory(s.TempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _dialogs.Error("Dossier invalide", ex.Message);
            return;
        }
        await _settings.SaveAsync(s);
        _theme.Apply(s.Theme);
        try { StartupRegistration.Apply(s.StartWithWindows); }
        catch (Exception ex) { _dialogs.Error("Démarrage avec Windows", "Impossible de modifier le démarrage automatique : " + ex.Message); }

        if (enablingEncryption)
        {
            var created = await _keys.EnsureKeyAsync();
            if (created)
                _dialogs.Info("Chiffrement activé",
                    "Une clé AES-256 aléatoire a été créée et protégée par votre session Windows.\n\nIMPORTANT : exportez-la maintenant (bouton « Exporter la clé ») et conservez-la ailleurs. " +
                    "Sans cette clé, les fichiers chiffrés seront définitivement illisibles si Windows est réinstallé ou si votre profil est perdu.\n\nSeuls les nouveaux envois sont chiffrés.");
        }
        await RefreshStatusAsync();
        SavedText = "Enregistré à " + DateTime.Now.ToString("HH:mm:ss");
        _shell.Toast("Paramètres enregistrés.");
    }

    [RelayCommand]
    private void BrowseDownload()
    {
        var f = _dialogs.PickFolder("Dossier de téléchargement");
        if (f is not null) DownloadDirectory = f;
    }

    [RelayCommand]
    private void BrowseTemp()
    {
        var f = _dialogs.PickFolder("Dossier des fichiers temporaires");
        if (f is not null) TempDirectory = f;
    }


    [RelayCommand]
    private async Task SaveOAuthAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId)) { _dialogs.Error("Identifiant manquant", "Saisissez l'identifiant client OAuth (se termine par .apps.googleusercontent.com)."); return; }
        var existing = await _oauth.GetAsync();
        var secret = string.IsNullOrWhiteSpace(ClientSecret) ? existing?.ClientSecret : ClientSecret;
        await _oauth.SaveAsync(ClientId, secret);
        ClientSecret = "";
        await RefreshStatusAsync();
        _shell.Toast("Configuration OAuth enregistrée (secret protégé par Windows).");
    }

    [RelayCommand]
    private async Task ImportOAuthJsonAsync()
    {
        var path = _dialogs.PickOpenFile("JSON téléchargé depuis Google Cloud Console", "JSON (*.json)|*.json");
        if (path is null) return;
        try
        {
            await _oauth.ImportJsonAsync(path);
            Load();
            await RefreshStatusAsync();
            _shell.Toast("Configuration OAuth importée.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { _dialogs.Error("Import impossible", ex.Message); }
    }

    [RelayCommand]
    private void OpenOAuthGuide()
    {
        var guide = System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "GOOGLE_OAUTH.md");
        if (System.IO.File.Exists(guide)) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenCloudConsole() =>
        Process.Start(new ProcessStartInfo("https://console.cloud.google.com/apis/credentials") { UseShellExecute = true });


    [RelayCommand]
    private async Task ExportKeyAsync()
    {
        if (!await _keys.HasKeyAsync()) { _dialogs.Info("Aucune clé", "Activez le chiffrement et enregistrez d'abord les paramètres."); return; }
        var pass = _dialogs.Prompt("Exporter la clé de chiffrement", "Phrase secrète pour protéger le fichier (10 caractères minimum)", "", "Exporter");
        if (pass is null) return;
        var path = _dialogs.PickSaveFile("Enregistrer la clé", "Clé MultiDrive (*.mdckey)|*.mdckey", "multidrive-cle.mdckey");
        if (path is null) return;
        try
        {
            await _keys.ExportKeyAsync(path, pass);
            _shell.Toast("Clé exportée. Conservez ce fichier ET la phrase secrète en lieu sûr.");
        }
        catch (ArgumentException ex) { _dialogs.Error("Export impossible", ex.Message); }
    }

    [RelayCommand]
    private async Task ImportKeyAsync()
    {
        var path = _dialogs.PickOpenFile("Importer une clé", "Clé MultiDrive (*.mdckey)|*.mdckey|Tous les fichiers (*.*)|*.*");
        if (path is null) return;
        var pass = _dialogs.Prompt("Importer la clé de chiffrement", "Phrase secrète du fichier de clé", "", "Importer");
        if (pass is null) return;
        try
        {
            await _keys.ImportKeyAsync(path, pass, overwrite: false);
        }
        catch (KeyImportException ex) when (ex.Message.Contains("Une autre clé"))
        {
            if (!_dialogs.Confirm("Remplacer la clé existante ?", ex.Message, "Remplacer", danger: true)) return;
            await _keys.ImportKeyAsync(path, pass, overwrite: true);
        }
        catch (KeyImportException ex) { _dialogs.Error("Import impossible", ex.Message); return; }
        await RefreshStatusAsync();
        _shell.Toast("Clé importée.");
    }


    [RelayCommand]
    private async Task BackupIndexAsync()
    {
        var path = _dialogs.PickSaveFile("Sauvegarder l'index", "Sauvegarde d'index (*.db)|*.db", $"multidrive-index-{DateTime.Now:yyyyMMdd-HHmm}.db");
        if (path is null) return;
        await _backup.BackupAsync(path);
        _shell.Toast("Index sauvegardé : " + path);
    }

    [RelayCommand]
    private async Task RestoreIndexAsync()
    {
        if (_transfers.GetTotals().Active > 0)
        {
            _dialogs.Error("Transferts en cours", "Terminez ou annulez les transferts avant de restaurer l'index.");
            return;
        }
        var path = _dialogs.PickOpenFile("Restaurer l'index", "Sauvegarde d'index (*.db)|*.db");
        if (path is null) return;
        if (!_dialogs.Confirm("Restaurer l'index",
                "L'index actuel sera remplacé par la sauvegarde (une copie de sécurité de l'index actuel est conservée). " +
                "Les fichiers ajoutés ou supprimés depuis cette sauvegarde ne seront pas reflétés. L'application redémarrera.", "Restaurer", danger: true)) return;
        try
        {
            var safety = await _backup.RestoreAsync(path);
            _dialogs.Info("Index restauré", $"Copie de sécurité de l'ancien index :\n{safety}\n\nL'application va redémarrer.");
            ((App)System.Windows.Application.Current).Restart();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
        {
            _dialogs.Error("Restauration impossible", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportRecoveryAsync()
    {
        var path = _dialogs.PickSaveFile("Export de récupération", "JSON (*.json)|*.json", $"multidrive-recuperation-{DateTime.Now:yyyyMMdd}.json");
        if (path is null) return;
        await _backup.ExportRecoveryJsonAsync(path);
        _shell.Toast("Export de récupération créé (sans jetons ni clés).");
    }

    [RelayCommand]
    private void OpenDataFolder() => Process.Start(new ProcessStartInfo(_paths.Root) { UseShellExecute = true });
}

public sealed record StrategyOption(string Name, string Display)
{
    public override string ToString() => Display;
}
