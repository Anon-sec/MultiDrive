using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using MultiDriveCloud.App.Views;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;

namespace MultiDriveCloud.App.Services;

public sealed class DialogService
{
    private readonly VirtualFileSystemService _vfs;

    public DialogService(VirtualFileSystemService vfs) => _vfs = vfs;

    public bool Confirm(string title, string message, string confirmText = "Confirmer", bool danger = false)
    {
        var w = new DialogWindow(title, message, confirmText, showCancel: true, danger, null, null);
        return w.ShowDialog() == true;
    }

    public void Info(string title, string message) =>
        new DialogWindow(title, message, "OK", showCancel: false, danger: false, null, null).ShowDialog();

    public void Error(string title, string message) => Info(title, message);

    public string? Prompt(string title, string label, string initial = "", string confirmText = "OK")
    {
        var w = new DialogWindow(title, title, confirmText, showCancel: true, danger: false, label, initial);
        return w.ShowDialog() == true ? w.InputText : null;
    }

    public string[]? PickFiles()
    {
        var dlg = new OpenFileDialog { Title = "Importer des fichiers", Multiselect = true, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName, OverwritePrompt = true };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public Guid? PickVirtualFolder(string prompt, IEnumerable<Guid> excluded)
    {
        var w = new FolderPickerWindow(_vfs, excluded, prompt);
        return w.ShowDialog() == true ? w.SelectedFolderId : null;
    }

    public void ShowProperties(FileProperties properties) => new PropertiesWindow(properties).ShowDialog();
}

public sealed class NotificationService : IDisposable
{
    private readonly ISettingsService _settings;
    private System.Windows.Forms.NotifyIcon? _icon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public NotificationService(ISettingsService settings) => _settings = settings;

    public void Start()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/MultiDriveCloud;component/Resources/app.ico"))?.Stream;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir MultiDrive Cloud", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => ExitRequested?.Invoke());
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
            Text = "MultiDrive Cloud",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void Notify(string title, string message, bool error = false)
    {
        if (!_settings.Current.NotificationsEnabled || _icon is null) return;
        _icon.ShowBalloonTip(5000, title, message.Length > 220 ? message[..220] + "…" : message,
            error ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info);
    }

    public void SetTooltip(string text)
    {
        if (_icon is not null) _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }
}

public static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MultiDriveCloud";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null) key.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
