using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.App.ViewModels;
using MultiDriveCloud.Core.Interfaces;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;
using MultiDriveCloud.GoogleDrive.Authentication;
using MultiDriveCloud.GoogleDrive.Services;
using MultiDriveCloud.Infrastructure;
using MultiDriveCloud.Infrastructure.Database;
using MultiDriveCloud.Infrastructure.Logging;
using MultiDriveCloud.Infrastructure.Security;

namespace MultiDriveCloud.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private ServiceProvider? _services;
    private ILogger<App>? _log;
    private readonly DispatcherTimer _quotaTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _lastQuotaRefresh = DateTime.UtcNow;
    private int _completedSinceIdle;
    private bool _exiting;

    public ThemeService Theme { get; } = new();
    public ShellService Shell { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var restarted = e.Args.Contains("--restarted");
        _mutex = new Mutex(true, @"Local\MultiDriveCloud.SingleInstance", out var created);
        if (!created && !(restarted && _mutex.WaitOne(TimeSpan.FromSeconds(15))))
        {
            MessageBox.Show("MultiDrive Cloud est déjà ouvert (voir l'icône dans la zone de notification).", "MultiDrive Cloud",
                MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log?.LogCritical("Exception non gérée : {Type}", args.ExceptionObject.GetType().Name);
        TaskScheduler.UnobservedTaskException += (_, args) => { _log?.LogError(args.Exception, "Tâche en échec non observée"); args.SetObserved(); };

        try
        {
            _services = BuildServices();
            _log = _services.GetRequiredService<ILogger<App>>();
            _services.GetRequiredService<SqliteDatabase>().Migrate();
            var settings = _services.GetRequiredService<ISettingsService>();
            await settings.LoadAsync();
            Theme.Apply(settings.Current.Theme);

            var notifications = _services.GetRequiredService<NotificationService>();
            notifications.OpenRequested += ShowMainWindow;
            notifications.ExitRequested += () => _ = RequestExitAsync(force: false);
            notifications.Start();

            var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            MainWindow = window;
            Theme.ApplyTitleBar(window);
            if (!e.Args.Contains("--minimized")) window.Show();

            _ = InitializeBackgroundAsync();
        }
        catch (Exception ex)
        {
            _log?.LogCritical(ex, "Échec du démarrage");
            MessageBox.Show("MultiDrive Cloud n'a pas pu démarrer :\n\n" + MultiDriveCloud.Core.Util.Redactor.Redact(ex.Message) +
                            "\n\nLe journal se trouve dans %LOCALAPPDATA%\\MultiDriveCloud\\logs.", "MultiDrive Cloud", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private ServiceProvider BuildServices()
    {
        var paths = new AppPaths();
        var sc = new ServiceCollection();
        sc.AddLogging(b =>
        {
            b.ClearProviders();
            b.AddProvider(new FileLoggerProvider(paths.Logs));
            b.SetMinimumLevel(LogLevel.Information);
        });

        sc.AddSingleton(paths);
        sc.AddSingleton(new SqliteDatabase(paths.Database));
        sc.AddSingleton<ISecretProtector, DpapiProtector>();
        sc.AddSingleton<ISecretStore>(sp => new DpapiSecretStore(paths.Secrets, sp.GetRequiredService<ISecretProtector>()));
        sc.AddSingleton<IKeyValueStore, KeyValueStore>();
        sc.AddSingleton<ISettingsService>(sp => new SettingsService(sp.GetRequiredService<IKeyValueStore>(), paths.DefaultDownloads, paths.Temp));
        sc.AddSingleton<IAccountRepository, AccountRepository>();
        sc.AddSingleton<IIndexRepository, IndexRepository>();
        sc.AddSingleton<IIndexBackupService, IndexBackupService>();

        sc.AddSingleton<IAllocationStrategy, LargestFreeSpaceStrategy>();
        sc.AddSingleton<IAllocationStrategy, BalancedUsageStrategy>();
        sc.AddSingleton<IAllocationStrategy, BestFitStrategy>();
        sc.AddSingleton(sp => new StorageAllocator(sp.GetRequiredService<IAccountRepository>(), sp.GetRequiredService<ISettingsService>(),
            sp.GetServices<IAllocationStrategy>(), sp.GetRequiredService<ILogger<StorageAllocator>>()));
        sc.AddSingleton<ChunkManager>();

        sc.AddSingleton<IBrowserLauncher, ShellBrowserLauncher>();
        sc.AddSingleton(sp => new GoogleOAuthClient(sp.GetRequiredService<IBrowserLauncher>()));
        sc.AddSingleton(sp => new GoogleOAuthConfigProvider(sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<ISecretStore>(),
            paths.Root, AppContext.BaseDirectory));
        sc.AddSingleton<IGoogleOAuthConfigProvider>(sp => sp.GetRequiredService<GoogleOAuthConfigProvider>());
        sc.AddSingleton<GoogleTokenManager>();
        sc.AddSingleton<IStorageProvider, GoogleDriveStorageProvider>();
        sc.AddSingleton<ProviderRegistry>();

        sc.AddSingleton<EncryptionKeyManager>();
        sc.AddSingleton<IEncryptionKeyProvider>(sp => sp.GetRequiredService<EncryptionKeyManager>());
        sc.AddSingleton<AccountService>();
        sc.AddSingleton<FragmentCleaner>();
        sc.AddSingleton<TransferManagerOptions>();
        sc.AddSingleton<TransferManager>();
        sc.AddSingleton<VirtualFileSystemService>();

        sc.AddSingleton(Theme);
        sc.AddSingleton(Shell);
        sc.AddSingleton<NotificationService>();
        sc.AddSingleton<DialogService>();
        sc.AddSingleton<DashboardViewModel>();
        sc.AddSingleton<AccountsViewModel>();
        sc.AddSingleton<FilesViewModel>();
        sc.AddSingleton<TransfersViewModel>();
        sc.AddSingleton<SettingsViewModel>();
        sc.AddSingleton<MainViewModel>();
        return sc.BuildServiceProvider();
    }

    private async Task InitializeBackgroundAsync()
    {
        var sp = _services!;
        var transfers = sp.GetRequiredService<TransferManager>();
        var notifications = sp.GetRequiredService<NotificationService>();
        var settings = sp.GetRequiredService<ISettingsService>();
        var accounts = sp.GetRequiredService<AccountService>();
        var vfs = sp.GetRequiredService<VirtualFileSystemService>();

        transfers.TransferChanged += (_, s) => OnTransferChanged(s, transfers, notifications);
        try
        {
            await accounts.RefreshAllAsync();
            await transfers.RecoverAsync();
            var report = await vfs.ResumePendingDeletionsAsync();
            if (!report.Success)
                Shell.Toast("Certaines suppressions sont en attente (compte indisponible). Elles seront retentées.", error: true);
            if (settings.Current.StartWithWindows)
                try { StartupRegistration.Apply(true); } catch { }
        }
        catch (Exception ex) { _log?.LogError(ex, "Initialisation en arrière-plan incomplète"); }

        _lastQuotaRefresh = DateTime.UtcNow;
        _quotaTimer.Tick += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastQuotaRefresh < TimeSpan.FromMinutes(settings.Current.QuotaRefreshMinutes)) return;
            _lastQuotaRefresh = DateTime.UtcNow;
            try { await accounts.RefreshAllAsync(); } catch (Exception ex) { _log?.LogWarning(ex, "Actualisation périodique des quotas échouée"); }
        };
        _quotaTimer.Start();
    }

    private void OnTransferChanged(TransferSnapshot s, TransferManager transfers, NotificationService notifications)
    {
        if (s.State == TransferState.Failed)
            Dispatcher.BeginInvoke(() => notifications.Notify("Transfert en échec", $"{s.Name} : {s.ErrorMessage}", error: true));
        else if (s.State == TransferState.Completed)
        {
            Interlocked.Increment(ref _completedSinceIdle);
            if (transfers.GetTotals().Active == 0)
            {
                var n = Interlocked.Exchange(ref _completedSinceIdle, 0);
                if (n > 0)
                    Dispatcher.BeginInvoke(() =>
                    {
                        notifications.Notify("Transferts terminés", n == 1 ? $"« {s.Name} » est terminé." : $"{n} transferts terminés.");
                        notifications.SetTooltip("MultiDrive Cloud — à jour");
                    });
            }
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.LogError(e.Exception, "Exception d'interface non gérée");
        e.Handled = true;
        MessageBox.Show("Une erreur inattendue s'est produite :\n\n" + MultiDriveCloud.Core.Util.Redactor.Redact(e.Exception.Message) +
                        "\n\nVos données et transferts sont conservés. Détails dans le journal.", "MultiDrive Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            var w = MainWindow;
            if (w is null) return;
            w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
        });
    }

    public async Task RequestExitAsync(bool force)
    {
        if (_exiting) return;
        var transfers = _services?.GetService<TransferManager>();
        if (!force && transfers is not null && transfers.GetTotals().Active > 0)
        {
            ShowMainWindow();
            var ok = _services!.GetRequiredService<DialogService>().Confirm("Quitter MultiDrive Cloud",
                "Des transferts sont en cours ou en attente. Ils seront interrompus proprement et reprendront automatiquement au prochain lancement.\n\nQuitter maintenant ?",
                "Quitter", danger: true);
            if (!ok) return;
        }
        _exiting = true;
        _quotaTimer.Stop();
        if (transfers is not null) await transfers.ShutdownAsync();
        _services?.GetService<NotificationService>()?.Dispose();
        MainWindow?.Hide();
        Shutdown();
    }

    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null) Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
        _ = RequestExitAsync(force: true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        try { _services?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log?.LogWarning(ex, "Libération des services incomplète"); }
        base.OnExit(e);
    }
}
