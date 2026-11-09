using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiDriveCloud.App.Services;
using MultiDriveCloud.Core.Models;
using MultiDriveCloud.Core.Settings;
using MultiDriveCloud.Core.Storage;
using MultiDriveCloud.Core.Transfers;

namespace MultiDriveCloud.App.ViewModels;

public sealed partial class ExplorerItem : ObservableObject
{
    public bool IsFolder { get; init; }
    public Guid Id { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public ItemRef Ref => new(IsFolder, Id);
    public bool IsStored { get; init; } = true;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _modifiedText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _subText;
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private string _iconKind = "File";
}

public sealed record BreadcrumbItem(string Name, Guid FolderId);

public sealed partial class FilesViewModel : ObservableObject, IActivatablePage
{
    private readonly VirtualFileSystemService _vfs;
    private readonly TransferManager _transfers;
    private readonly ISettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private CancellationTokenSource? _searchCts;
    private DateTime _lastRefreshRequest = DateTime.MinValue;
    private int _loading;

    public ObservableCollection<ExplorerItem> Items { get; } = new();
    public ObservableCollection<BreadcrumbItem> Breadcrumb { get; } = new();
    public IList<ExplorerItem> Selection { get; set; } = Array.Empty<ExplorerItem>();

    [ObservableProperty] private Guid _currentFolderId = VirtualFolder.RootId;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _sortKey = "Name";
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _canGoUp;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasSingleSelection;

    public FilesViewModel(VirtualFileSystemService vfs, TransferManager transfers, ISettingsService settings, DialogService dialogs, ShellService shell)
    {
        _vfs = vfs;
        _transfers = transfers;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        vfs.Changed += (_, _) => RequestRefresh();
        transfers.TransferChanged += (_, s) =>
        {
            if (s.Direction == TransferDirection.Upload && s.State is TransferState.Completed or TransferState.Failed or TransferState.Cancelled or TransferState.Queued)
                RequestRefresh();
        };
        transfers.ListChanged += (_, _) => RequestRefresh();
    }

    public void Activate() => RequestRefresh();

    private void RequestRefresh()
    {
        _lastRefreshRequest = DateTime.UtcNow;
        Ui.Post(async () =>
        {
            await Task.Delay(250);
            if (DateTime.UtcNow - _lastRefreshRequest < TimeSpan.FromMilliseconds(200)) return;
            await RefreshAsync();
        });
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        IsSearching = !string.IsNullOrWhiteSpace(value);
        Ui.Post(async () =>
        {
            try { await Task.Delay(300, token); await RefreshAsync(); }
            catch (OperationCanceledException) { }
        });
    }

    partial void OnSortKeyChanged(string value) => _ = RefreshAsync();
    partial void OnSortDescendingChanged(bool value) => _ = RefreshAsync();

    public void SetSelection(IList<ExplorerItem> selected)
    {
        Selection = selected;
        HasSelection = selected.Count > 0;
        HasSingleSelection = selected.Count == 1;
        if (selected.Count == 0) { SelectionText = ""; return; }
        var size = selected.Where(s => !s.IsFolder).Sum(s => s.Size);
        SelectionText = $"{selected.Count} sélectionné(s)" + (size > 0 ? $" · {Fmt.Bytes(size)}" : "");
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            var path = await _vfs.GetPathAsync(CurrentFolderId);
            Breadcrumb.Clear();
            Breadcrumb.Add(new BreadcrumbItem("Mon cloud", VirtualFolder.RootId));
            foreach (var f in path) Breadcrumb.Add(new BreadcrumbItem(f.Name, f.FolderId));
            CanGoUp = path.Count > 0;

            var items = new List<ExplorerItem>();
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var found = await _vfs.SearchAsync(SearchText.Trim());
                var cache = new Dictionary<Guid, string>();
                async Task<string> PathOf(Guid folderId)
                {
                    if (cache.TryGetValue(folderId, out var p)) return p;
                    var chain = await _vfs.GetPathAsync(folderId);
                    return cache[folderId] = "/" + string.Join("/", chain.Select(c => c.Name));
                }
                foreach (var f in found.Folders) items.Add(ToItem(f, await PathOf(f.ParentId ?? VirtualFolder.RootId)));
                foreach (var f in found.Files) items.Add(ToItem(f, await PathOf(f.FolderId)));
                SummaryText = $"{items.Count} résultat(s) pour « {SearchText.Trim()} »";
            }
            else
            {
                var (folders, files) = await _vfs.ListAsync(CurrentFolderId);
                items.AddRange(folders.Select(f => ToItem(f, null)));
                items.AddRange(files.Select(f => ToItem(f, null)));
                SummaryText = $"{folders.Count} dossier(s) · {files.Count} fichier(s) · {Fmt.Bytes(files.Sum(f => f.TotalSize))}";
            }

            Func<ExplorerItem, object> key = SortKey switch
            {
                "Size" => i => i.Size,
                "Date" => i => i.Modified,
                _ => i => i.Name.ToLowerInvariant(),
            };
            var ordered = SortDescending ? items.OrderByDescending(i => i.IsFolder).ThenByDescending(key) : items.OrderByDescending(i => i.IsFolder).ThenBy(key);
            var selectedIds = Selection.Select(s => s.Id).ToHashSet();
            Items.Clear();
            foreach (var i in ordered) Items.Add(i);
            IsEmpty = Items.Count == 0;
        }
        finally { Interlocked.Exchange(ref _loading, 0); }
    }

    private static ExplorerItem ToItem(VirtualFolder f, string? sub) => new()
    {
        IsFolder = true,
        Id = f.FolderId,
        Name = f.Name,
        Modified = f.ModifiedAt,
        ModifiedText = Fmt.Date(f.ModifiedAt),
        SizeText = "",
        Icon = "",
        IconKind = "Folder",
        SubText = sub,
    };

    private static ExplorerItem ToItem(VirtualFile f, string? sub)
    {
        var status = f.Status switch
        {
            FileStatus.Pending => "Envoi en cours…",
            FileStatus.Failed => "Échec",
            FileStatus.Deleting => "Suppression…",
            _ => f.IsEncrypted ? "Chiffré" : "",
        };
        return new ExplorerItem
        {
            IsFolder = false,
            Id = f.FileId,
            Name = f.Name,
            Size = f.TotalSize,
            Modified = f.ModifiedAt,
            IsStored = f.Status == FileStatus.Stored,
            SizeText = Fmt.Bytes(f.TotalSize),
            ModifiedText = Fmt.Date(f.ModifiedAt),
            StatusText = status,
            Icon = f.Status == FileStatus.Stored ? "" : "",
            IconKind = f.Status == FileStatus.Stored ? "File" : "Pending",
            SubText = sub,
        };
    }


    [RelayCommand]
    private async Task NavigateToAsync(BreadcrumbItem crumb)
    {
        SearchText = "";
        CurrentFolderId = crumb.FolderId;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        var path = await _vfs.GetPathAsync(CurrentFolderId);
        CurrentFolderId = path.Count <= 1 ? VirtualFolder.RootId : path[^2].FolderId;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenAsync(ExplorerItem? item)
    {
        if (item is null) return;
        if (item.IsFolder)
        {
            SearchText = "";
            CurrentFolderId = item.Id;
            await RefreshAsync();
        }
        else await DownloadItemsAsync(new[] { item }, null);
    }

    [RelayCommand]
    private void SortBy(string key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else { SortKey = key; SortDescending = false; }
    }


    [RelayCommand]
    private async Task NewFolderAsync()
    {
        var name = _dialogs.Prompt("Nouveau dossier", "Nom du dossier", "Nouveau dossier", "Créer");
        if (name is null) return;
        try { await _vfs.CreateFolderAsync(CurrentFolderId, name); }
        catch (Exception ex) when (ex is NameConflictException or ArgumentException) { _dialogs.Error("Création impossible", ex.Message); }
    }

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        var files = _dialogs.PickFiles();
        if (files is { Length: > 0 }) await ImportPathsAsync(files);
    }

    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        var folder = _dialogs.PickFolder("Importer un dossier complet");
        if (folder is not null) await ImportPathsAsync(new[] { folder });
    }

    public async Task ImportPathsAsync(IEnumerable<string> paths, Guid? destinationFolder = null)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        try
        {
            var result = await _transfers.EnqueueUploadAsync(list, destinationFolder ?? CurrentFolderId);
            var msg = $"{result.FileCount} fichier(s) ({Fmt.Bytes(result.TotalBytes)}) ajouté(s) à la file d'envoi.";
            if (result.Skipped.Count > 0) msg += $" {result.Skipped.Count} ignoré(s).";
            _shell.Toast(msg);
            if (result.Skipped.Count > 0)
                _dialogs.Info("Éléments ignorés", string.Join("\n", result.Skipped.Take(12).Select(s => $"• {System.IO.Path.GetFileName(s.Path)} : {s.Reason}")) +
                    (result.Skipped.Count > 12 ? $"\n… et {result.Skipped.Count - 12} autre(s)." : ""));
            await RefreshAsync();
        }
        catch (InsufficientStorageException ex)
        {
            _dialogs.Error("Espace insuffisant", ex.Message + "\n\nAjoutez un compte Google, libérez de l'espace ou réduisez la marge de sécurité dans les paramètres.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.Error("Import impossible", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (Selection.Count == 0) return;
        await DownloadItemsAsync(Selection.ToList(), null);
    }

    [RelayCommand]
    private async Task DownloadToAsync()
    {
        if (Selection.Count == 0) return;
        var target = _dialogs.PickFolder("Télécharger vers…");
        if (target is not null) await DownloadItemsAsync(Selection.ToList(), target);
    }

    private async Task DownloadItemsAsync(IReadOnlyList<ExplorerItem> items, string? target)
    {
        try
        {
            var result = await _transfers.EnqueueDownloadAsync(items.Select(i => i.Ref), target);
            var dir = target ?? _settings.Current.DownloadDirectory;
            if (result.FileCount == 0 && result.Skipped.Count > 0)
                _dialogs.Info("Téléchargement impossible", string.Join("\n", result.Skipped.Select(s => $"• {s.Path} : {s.Reason}")));
            else
                _shell.Toast($"{result.FileCount} fichier(s) en téléchargement vers {dir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.Error("Téléchargement impossible", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Selection.Count != 1) return;
        var item = Selection[0];
        var name = _dialogs.Prompt("Renommer", "Nouveau nom", item.Name, "Renommer");
        if (name is null) return;
        try { await _vfs.RenameAsync(item.Ref, name); }
        catch (Exception ex) when (ex is NameConflictException or ArgumentException or InvalidOperationException) { _dialogs.Error("Renommage impossible", ex.Message); }
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (Selection.Count == 0) return;
        var items = Selection.ToList();
        var target = _dialogs.PickVirtualFolder($"Déplacer {items.Count} élément(s) vers…", items.Where(i => i.IsFolder).Select(i => i.Id));
        if (target is Guid dest) await MoveToAsync(items.Select(i => i.Ref).ToList(), dest);
    }

    public async Task MoveToAsync(IReadOnlyList<ItemRef> refs, Guid destination)
    {
        try
        {
            await _vfs.MoveAsync(refs, destination);
            _shell.Toast($"{refs.Count} élément(s) déplacé(s).");
        }
        catch (Exception ex) when (ex is NameConflictException or InvalidOperationException) { _dialogs.Error("Déplacement impossible", ex.Message); }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selection.Count == 0) return;
        var items = Selection.ToList();
        var what = items.Count == 1 ? $"« {items[0].Name} »" : $"{items.Count} éléments";
        if (!_dialogs.Confirm("Supprimer définitivement",
                $"Supprimer {what} ?\n\nLes données seront effacées de vos comptes Google Drive. Cette action est irréversible. Vos fichiers locaux d'origine ne sont pas touchés.",
                "Supprimer", danger: true)) return;
        var report = await _vfs.DeleteAsync(items.Select(i => i.Ref).ToList());
        if (report.Success) _shell.Toast($"{report.FilesDeleted} fichier(s) supprimé(s).");
        else _dialogs.Error("Suppression incomplète", string.Join("\n\n", report.Problems) +
            "\n\nLes éléments concernés restent en attente de suppression et seront retentés au prochain démarrage.");
    }

    [RelayCommand]
    private async Task PropertiesAsync()
    {
        if (Selection.Count != 1 || Selection[0].IsFolder) return;
        var props = await _vfs.GetPropertiesAsync(Selection[0].Id);
        if (props is not null) _dialogs.ShowProperties(props);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";
}
