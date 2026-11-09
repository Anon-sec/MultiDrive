using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiDriveCloud.App.ViewModels;
using MultiDriveCloud.Core.Transfers;

namespace MultiDriveCloud.App.Views;

public partial class FilesView : UserControl
{
    private const string InternalFormat = "MultiDriveCloud.Items";
    private Point _dragStart;
    private bool _dragCandidate;

    public FilesView() => InitializeComponent();

    private FilesViewModel Vm => (FilesViewModel)DataContext;

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm.SetSelection(List.SelectedItems.Cast<ExplorerItem>().ToList());

    private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ExplorerItem item)
            await Vm.OpenCommand.ExecuteAsync(item);
    }

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container is null) { List.UnselectAll(); return; }
        if (!container.IsSelected) { List.UnselectAll(); container.IsSelected = true; }
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete: Vm.DeleteCommand.Execute(null); e.Handled = true; break;
            case Key.F2: Vm.RenameCommand.Execute(null); e.Handled = true; break;
            case Key.F5: Vm.RefreshCommand.Execute(null); e.Handled = true; break;
            case Key.Back: Vm.GoUpCommand.Execute(null); e.Handled = true; break;
            case Key.Enter when List.SelectedItem is ExplorerItem item:
                _ = Vm.OpenCommand.ExecuteAsync(item);
                e.Handled = true;
                break;
        }
    }


    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { IsSelected: true } or not null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_dragCandidate || Vm.Selection.Count == 0) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = false;
        var refs = Vm.Selection.Where(s => s.IsStored || s.IsFolder).Select(s => s.Ref).ToArray();
        if (refs.Length == 0) return;
        var data = new DataObject(InternalFormat, refs);
        DragDrop.DoDragDrop(List, data, DragDropEffects.Move);
    }


    private ExplorerItem? FolderUnder(DragEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ExplorerItem;
        return item is { IsFolder: true } ? item : null;
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        var internalItems = e.Data.GetDataPresent(InternalFormat);
        var folder = FolderUnder(e);
        if (!files && !internalItems) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

        if (internalItems)
        {
            var refs = (ItemRef[])e.Data.GetData(InternalFormat)!;
            var valid = folder is not null && refs.All(r => r.Id != folder.Id);
            e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
            DropOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            DropText.Text = folder is not null ? $"Importer dans « {folder.Name} »" : "Déposez pour importer dans ce dossier";
            DropOverlay.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void List_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var folder = FolderUnder(e);
        if (e.Data.GetDataPresent(InternalFormat))
        {
            if (folder is null) return;
            var refs = ((ItemRef[])e.Data.GetData(InternalFormat)!).Where(r => r.Id != folder.Id).ToList();
            if (refs.Count > 0) await Vm.MoveToAsync(refs, folder.Id);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            await Vm.ImportPathsAsync(paths, folder?.Id);
        }
        e.Handled = true;
    }
}
