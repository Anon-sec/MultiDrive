using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.GoogleDrive.Authentication;

namespace MultiDriveCloud.App.ViewModels;

public sealed partial class AccountCardViewModel : ObservableObject
{
    public string AccountId { get; private set; } = "";

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusKind = "Muted";
    [ObservableProperty] private double _usedFraction;
    [ObservableProperty] private double _appFraction;
    [ObservableProperty] private string _usedText = "";
    [ObservableProperty] private string _freeText = "";
    [ObservableProperty] private string _storedText = "";
    [ObservableProperty] private string _syncText = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _needsReauthorization;
    [ObservableProperty] private bool _isDisconnected;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isUnlimited;
    public long StoredBytes { get; private set; }

    public void Update(CloudAccount a, long storedBytes)
    {
        AccountId = a.AccountId;
        StoredBytes = storedBytes;
        Email = a.Email;
        IsUnlimited = a.IsUnlimited;
        IsConnected = a.Status == AccountStatus.Connected;
        NeedsReauthorization = a.Status == AccountStatus.NeedsReauthorization;
        IsDisconnected = a.Status == AccountStatus.Disconnected;
        (StatusText, StatusKind) = a.Status switch
        {
            AccountStatus.Connected => ("Connecté", "Ok"),
            AccountStatus.NeedsReauthorization => ("Autorisation à renouveler", "Warn"),
            AccountStatus.Disconnected => ("Déconnecté", "Muted"),
            _ => ("Erreur", "Error"),
        };
        UsedFraction = a.IsUnlimited || a.TotalBytes <= 0 ? 0 : Math.Min(1, (double)a.UsedBytes / a.TotalBytes);
        AppFraction = a.IsUnlimited || a.TotalBytes <= 0 ? 0 : Math.Min(1, (double)storedBytes / a.TotalBytes);
        UsedText = a.IsUnlimited ? $"{Fmt.Bytes(a.UsedBytes)} utilisés · stockage illimité" : $"{Fmt.Bytes(a.UsedBytes)} utilisés sur {Fmt.Bytes(a.TotalBytes)}";
        FreeText = a.IsUnlimited ? "Illimité" : $"{Fmt.Bytes(a.AvailableBytes)} libres";
        StoredText = storedBytes > 0 ? $"{Fmt.Bytes(storedBytes)} stockés par MultiDrive Cloud" : "Aucun fragment MultiDrive Cloud";
        SyncText = a.LastSync is DateTime d ? "Quota actualisé le " + Fmt.Date(d) : "Quota jamais actualisé";
        Message = a.StatusMessage;
    }
}

public sealed partial class AccountsViewModel : ObservableObject, IActivatablePage
{
    private readonly AccountService _accounts;
    private readonly GoogleOAuthConfigProvider _oauthConfig;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private CancellationTokenSource? _addCts;

    [ObservableProperty] private string _setupHint = "";

    [RelayCommand]
    private void OpenSetupPage(string step)
    {
        var url = step switch
        {
            "project" => "https://console.cloud.google.com/projectcreate",
            "api" => "https://console.cloud.google.com/apis/library/drive.googleapis.com",
            "consent" => "https://console.cloud.google.com/apis/credentials/consent",
            _ => "https://console.cloud.google.com/apis/credentials/oauthclient",
        };
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task AutoImportAsync()
    {
        var found = GoogleOAuthConfigProvider.FindDownloadedJsonFiles();
        if (found.Count == 0)
        {
            SetupHint = "Aucun fichier client_secret_….json trouvé dans Téléchargements. Dans la console Google, cliquez sur « Télécharger le JSON » puis réessayez, ou choisissez le fichier à la main.";
            return;
        }
        await ImportFileAsync(found[0]);
    }

    [RelayCommand]
    private async Task BrowseImportAsync()
    {
        var path = _dialogs.PickOpenFile("Fichier JSON téléchargé depuis Google Cloud Console", "JSON (*.json)|*.json");
        if (path is not null) await ImportFileAsync(path);
    }

    private async Task ImportFileAsync(string path)
    {
        try
        {
            await _oauthConfig.ImportJsonAsync(path);
            SetupHint = "";
            OauthConfigured = true;
            _shell.Toast("Configuration Google importée. Vous pouvez ajouter vos comptes.");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            SetupHint = "Import impossible : " + ex.Message;
        }
    }

    public ObservableCollection<AccountCardViewModel> Cards { get; } = new();

    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _addStatus = "";
    [ObservableProperty] private bool _oauthConfigured = true;
    [ObservableProperty] private bool _isEmpty = true;

    public AccountsViewModel(AccountService accounts, GoogleOAuthConfigProvider oauthConfig, DialogService dialogs, ShellService shell)
    {
        _accounts = accounts;
        _oauthConfig = oauthConfig;
        _dialogs = dialogs;
        _shell = shell;
        accounts.AccountsChanged += (_, _) => Ui.Post(() => _ = ReloadAsync());
    }

    public void Activate() => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        OauthConfigured = await _oauthConfig.GetAsync() is not null;
        var list = await _accounts.GetAccountsAsync();
        foreach (var a in list)
        {
            var stored = await _accounts.GetStoredBytesAsync(a.AccountId);
            var card = Cards.FirstOrDefault(c => c.AccountId == a.AccountId);
            if (card is null) { card = new AccountCardViewModel(); Cards.Add(card); }
            card.Update(a, stored);
        }
        foreach (var gone in Cards.Where(c => list.All(a => a.AccountId != c.AccountId)).ToList()) Cards.Remove(gone);
        IsEmpty = Cards.Count == 0;
    }

    [RelayCommand]
    private async Task AddAccountAsync()
    {
        if (IsAdding) return;
        if (await _oauthConfig.GetAsync() is null)
        {
            OauthConfigured = false;
            _shell.Toast("Terminez d'abord la configuration Google (étapes ci-dessous).", error: true);
            return;
        }
        IsAdding = true;
        AddStatus = "Le navigateur s'est ouvert : connectez-vous à Google et autorisez l'accès à Google Drive…";
        _addCts = new CancellationTokenSource();
        try
        {
            var account = await _accounts.AddAccountAsync(ct: _addCts.Token);
            _shell.Toast($"Compte ajouté : {account.Email}");
        }
        catch (OperationCanceledException) { _shell.Toast("Ajout du compte annulé."); }
        catch (StorageProviderException ex) { _dialogs.Error("Impossible d'ajouter le compte", ex.Message); }
        finally
        {
            IsAdding = false;
            AddStatus = "";
            _addCts?.Dispose();
            _addCts = null;
        }
    }

    [RelayCommand]
    private void CancelAdd() => _addCts?.Cancel();

    [RelayCommand]
    private async Task RefreshAsync(AccountCardViewModel card)
    {
        card.IsBusy = true;
        try
        {
            await _accounts.RefreshAsync(card.AccountId);
            _shell.Toast("Quota actualisé : " + card.Email);
        }
        finally { card.IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        await _accounts.RefreshAllAsync();
        _shell.Toast("Quotas actualisés.");
    }

    [RelayCommand]
    private async Task ReauthorizeAsync(AccountCardViewModel card)
    {
        card.IsBusy = true;
        try
        {
            await _accounts.ReauthorizeAsync(card.AccountId);
            _shell.Toast("Autorisation renouvelée : " + card.Email);
        }
        catch (Exception ex) when (ex is StorageProviderException or InvalidOperationException)
        {
            _dialogs.Error("Réautorisation impossible", ex.Message);
        }
        finally { card.IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync(AccountCardViewModel card)
    {
        var warning = card.StoredBytes > 0
            ? $"{card.Email} héberge {Fmt.Bytes(card.StoredBytes)} de fragments de vos fichiers.\n\nSi vous le déconnectez, les fichiers qui en dépendent resteront listés mais ne pourront plus être téléchargés tant que le compte n'est pas reconnecté. Si ce compte est perdu définitivement, ces fichiers seront IRRÉCUPÉRABLES.\n\nDéconnecter quand même ?"
            : $"Déconnecter {card.Email} ? Les autorisations Google seront révoquées et les jetons effacés de cet ordinateur.";
        if (!_dialogs.Confirm("Déconnecter le compte", warning, "Déconnecter", danger: true)) return;
        card.IsBusy = true;
        try
        {
            await _accounts.DisconnectAsync(card.AccountId);
            _shell.Toast("Compte déconnecté : " + card.Email);
        }
        finally { card.IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveAsync(AccountCardViewModel card)
    {
        if (!_dialogs.Confirm("Retirer le compte", $"Retirer {card.Email} de la liste ?", "Retirer", danger: true)) return;
        try { await _accounts.RemoveAsync(card.AccountId); }
        catch (AccountInUseException ex) { _dialogs.Error("Compte encore utilisé", ex.Message); }
    }
}

public sealed partial class DashboardViewModel : ObservableObject, IActivatablePage
{
    private readonly AccountService _accounts;
    private readonly TransferManager _transfers;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _summaryDirty = true;
    private int _tick;

    public ObservableCollection<AccountCardViewModel> Cards { get; } = new();

    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _freeText = "—";
    [ObservableProperty] private string _usableText = "—";
    [ObservableProperty] private string _appUsedText = "—";
    [ObservableProperty] private string _otherUsedText = "—";
    [ObservableProperty] private double _usedFraction;
    [ObservableProperty] private double _appFraction;
    [ObservableProperty] private int _accountCount;
    [ObservableProperty] private int _connectedCount;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _folderCount;
    [ObservableProperty] private int _activeTransfers;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _remainingText = "—";
    [ObservableProperty] private double _globalProgress;
    [ObservableProperty] private bool _hasAccounts;

    private readonly ShellService _shell;

    [RelayCommand] private void GoToAccounts() => _shell.Navigate("Accounts");
    [RelayCommand] private void GoToFiles() => _shell.Navigate("Files");
    [RelayCommand] private void GoToTransfers() => _shell.Navigate("Transfers");

    public DashboardViewModel(AccountService accounts, TransferManager transfers, VirtualFileSystemService vfs, ShellService shell)
    {
        _accounts = accounts;
        _transfers = transfers;
        _shell = shell;
        accounts.AccountsChanged += (_, _) => _summaryDirty = true;
        vfs.Changed += (_, _) => _summaryDirty = true;
        transfers.TransferChanged += (_, s) =>
        {
            if (s.State is TransferState.Completed or TransferState.Cancelled or TransferState.Failed) _summaryDirty = true;
        };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Activate()
    {
        _summaryDirty = true;
        if (!_timer.IsEnabled) _timer.Start();
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        var t = _transfers.GetTotals();
        ActiveTransfers = t.Active;
        SpeedText = Fmt.Speed(t.BytesPerSecond);
        RemainingText = Fmt.Duration(t.Remaining);
        GlobalProgress = t.Progress;
        if (!_summaryDirty && ++_tick % 15 != 0) return;
        _summaryDirty = false;
        try
        {
            var s = await _accounts.GetSummaryAsync();
            AccountCount = s.AccountCount;
            ConnectedCount = s.ConnectedCount;
            FileCount = s.FileCount;
            FolderCount = s.FolderCount;
            TotalText = Fmt.Bytes(s.TotalCapacity);
            FreeText = Fmt.Bytes(s.FreeBytes);
            UsableText = Fmt.Bytes(s.UsableBytes);
            AppUsedText = Fmt.Bytes(s.UsedByApp);
            OtherUsedText = Fmt.Bytes(Math.Max(0, s.UsedOnAccounts - s.UsedByApp));
            UsedFraction = s.TotalCapacity <= 0 ? 0 : Math.Min(1, (double)s.UsedOnAccounts / s.TotalCapacity);
            AppFraction = s.TotalCapacity <= 0 ? 0 : Math.Min(1, (double)s.UsedByApp / s.TotalCapacity);
            HasAccounts = s.AccountCount > 0;

            var list = await _accounts.GetAccountsAsync();
            foreach (var a in list)
            {
                var stored = await _accounts.GetStoredBytesAsync(a.AccountId);
                var card = Cards.FirstOrDefault(c => c.AccountId == a.AccountId);
                if (card is null) { card = new AccountCardViewModel(); Cards.Add(card); }
                card.Update(a, stored);
            }
            foreach (var gone in Cards.Where(c => list.All(a => a.AccountId != c.AccountId)).ToList()) Cards.Remove(gone);
        }
        catch (Exception) { }
    }
}
