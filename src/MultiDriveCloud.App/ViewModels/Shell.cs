using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;

namespace MultiDriveCloud.App.ViewModels;

public static class Ui
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}

public sealed class ShellService
{
    public event Action<string>? NavigateRequested;
    public event Action<string, bool>? ToastRequested;

    public void Navigate(string page) => Ui.Post(() => NavigateRequested?.Invoke(page));
    public void Toast(string message, bool error = false) => Ui.Post(() => ToastRequested?.Invoke(message, error));
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly TransferManager _transfers;
    private readonly Dictionary<string, ObservableObject> _pages;

    public DashboardViewModel Dashboard { get; }
    public AccountsViewModel Accounts { get; }
    public FilesViewModel Files { get; }
    public TransfersViewModel Transfers { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private ObservableObject _currentPage;
    [ObservableProperty] private string _currentKey = "Dashboard";
    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _toastIsError;
    [ObservableProperty] private bool _toastVisible;
    [ObservableProperty] private string _statusText = "Prêt";
    [ObservableProperty] private string _speedText = "";

    public MainViewModel(DashboardViewModel dashboard, AccountsViewModel accounts, FilesViewModel files,
        TransfersViewModel transfers, SettingsViewModel settings, ShellService shell, TransferManager transferManager)
    {
        Dashboard = dashboard;
        Accounts = accounts;
        Files = files;
        Transfers = transfers;
        Settings = settings;
        _transfers = transferManager;
        _pages = new Dictionary<string, ObservableObject>
        {
            ["Dashboard"] = dashboard, ["Accounts"] = accounts, ["Files"] = files, ["Transfers"] = transfers, ["Settings"] = settings,
        };
        _currentPage = dashboard;

        shell.NavigateRequested += Navigate;
        shell.ToastRequested += ShowToast;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        dashboard.Activate();
    }

    [RelayCommand]
    private void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;
        CurrentKey = key;
        CurrentPage = page;
        (page as IActivatablePage)?.Activate();
    }

    private void ShowToast(string message, bool error)
    {
        ToastText = message;
        ToastIsError = error;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast() { _toastTimer.Stop(); ToastVisible = false; }

    private void UpdateStatus()
    {
        var t = _transfers.GetTotals();
        if (t.Active == 0) { StatusText = "Aucun transfert en cours"; SpeedText = ""; return; }
        StatusText = $"{t.Running} en cours · {t.Queued} en attente" + (t.Paused > 0 ? $" · {t.Paused} en pause" : "") +
                     $" · {Math.Round(t.Progress * 100):0} %";
        SpeedText = t.BytesPerSecond > 1 ? $"{Fmt.Speed(t.BytesPerSecond)} · reste {Fmt.Duration(t.Remaining)}" : "";
    }
}

public interface IActivatablePage
{
    void Activate();
}
