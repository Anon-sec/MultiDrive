using System.Windows;
using System.Windows.Input;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Storage;

namespace MultiDriveCloud.App.Views;

public partial class FolderPickerWindow : Window
{
    private readonly VirtualFileSystemService _vfs;
    private readonly HashSet<Guid> _excluded;
    private Guid _current = VirtualFolder.RootId;
    private Guid? _parent;

    public Guid? SelectedFolderId { get; private set; }

    public FolderPickerWindow(VirtualFileSystemService vfs, IEnumerable<Guid> excludedFolders, string prompt)
    {
        InitializeComponent();
        _vfs = vfs;
        _excluded = excludedFolders.ToHashSet();
        PromptText.Text = prompt;
        if (Application.Current is App app) app.Theme.ApplyTitleBar(this);
        if (Application.Current.MainWindow is { IsVisible: true } main && main != this) Owner = main;
        Loaded += async (_, _) => await LoadAsync(VirtualFolder.RootId);
    }

    private async Task LoadAsync(Guid folderId)
    {
        _current = folderId;
        var path = await _vfs.GetPathAsync(folderId);
        PathText.Text = "/" + string.Join("/", path.Select(f => f.Name));
        _parent = path.Count == 0 ? null : path.Count == 1 ? VirtualFolder.RootId : path[^2].FolderId;
        var (folders, _) = await _vfs.ListAsync(folderId);
        FolderList.ItemsSource = folders.Where(f => !_excluded.Contains(f.FolderId)).ToList();
        SelectButton.IsEnabled = !_excluded.Contains(folderId);
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_parent is Guid p) await LoadAsync(p);
    }

    private async void FolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is VirtualFolder f) await LoadAsync(f.FolderId);
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        SelectedFolderId = _current;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
