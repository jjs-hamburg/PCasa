using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCasa.App.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> _dragTargetValidity =
        new(StringComparer.OrdinalIgnoreCase);
    private TreeViewItem? _highlightedDropFolder;

    private void HighlightDropFolder(TreeViewItem? target)
    {
        if (ReferenceEquals(_highlightedDropFolder, target)) return;
        // Apply the outline on the TreeViewItem template, not its Header.
        // The Header must remain a string for the existing icon/name template.
        if (_highlightedDropFolder is { } previous)
        {
            previous.Background = Brushes.Transparent;
            previous.BorderBrush = Brushes.Transparent;
            previous.BorderThickness = new Thickness(0);
        }
        _highlightedDropFolder = target;
        if (target is not null)
        {
            target.SetResourceReference(TreeViewItem.BackgroundProperty, "PcasaHoverBrush");
            target.BorderBrush = new SolidColorBrush(Color.FromRgb(57, 164, 218));
            target.BorderThickness = new Thickness(1);
        }
    }

    private bool ValidDragTarget(string path)
    {
        if (_dragTargetValidity.TryGetValue(path, out var valid)) return valid;
        valid = IsMonitoredGalleryDestination(path);
        _dragTargetValidity[path] = valid;
        return valid;
    }

    private void DragTarget_Leave(object sender, DragEventArgs e)
    {
        _dragTargetValidity.Clear();
        if (ReferenceEquals(sender, FoldersTree))
            HighlightDropFolder(null);
    }

    private void BrowseSurface_ImagesDragRequested(object? sender,
        BrowseImageClickedEventArgs e)
    {
        if (_viewerOpen || !_selectedImageIds.Contains(e.ImageId)) return;
        var files = SelectedGalleryPaths().Where(File.Exists).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, files);
        // FileDrop is the native shell protocol, not a proprietary PCasa blob.
        // Therefore the same drag works inside PCasa and into Windows Explorer.
        DragDropEffects effect;
        try
        {
            effect = DragDrop.DoDragDrop(BrowseSurface, data,
                DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally { HighlightDropFolder(null); }
        if (effect.HasFlag(DragDropEffects.Move))
            _ = ReconcileMovedExternalFilesAsync(files);
    }

    // File.Exists also returns false when Windows cannot reach a NAS or lacks
    // access. Only a confirmed absent FILE inside a reachable parent folder
    // may be detached after a shell move.
    private static bool ConfirmedMissingExternalMove(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return false;
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    private async Task ReconcileMovedExternalFilesAsync(string[] originalPaths)
    {
        if (originalPaths.Length == 0) return;
        // The checks can touch disconnected SMB paths; execute them off the
        // UI thread, under the same per-source lock used by other file actions.
        try
        {
            var changed = false;
            await _scheduler.EnqueueAsync(
                $"gallery:external-move:{Guid.NewGuid():N}",
                SchedulerPriority.ManualAction, token =>
                {
                    foreach (var source in originalPaths)
                    {
                        token.ThrowIfCancellationRequested();
                        lock (SourceFileLocks.ForPath(source))
                        {
                            if (!ConfirmedMissingExternalMove(source)) continue;
                            if (CatalogImageIdAtPath(source) is long id)
                                changed |= new CatalogRemovalService(_database)
                                    .DetachImage(id, source);
                        }
                    }
                    return Task.CompletedTask;
                });
            if (changed)
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.ExternalMove", ex); }
    }

    private static string[] DropFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)) return [];
        return e.Data.GetData(DataFormats.FileDrop, true) is string[] paths
            ? paths.Where(FolderScanner.IsSupportedImagePath).ToArray()
            : [];
    }

    private static bool TryResolveDropEffect(
        DragEventArgs e, string[] paths, string target,
        out DragDropEffects effect)
    {
        effect = DragDropEffects.None;
        if (paths.Length == 0) return false;
        var states = e.KeyStates;
        var control = (states & DragDropKeyStates.ControlKey) != 0;
        var shift = (states & DragDropKeyStates.ShiftKey) != 0;
        // Repeated drag-over events must not probe the same NAS for each
        // of thousands of selected files. One identity lookup per root.
        var sourceRoots = paths.GroupBy(path => Path.GetPathRoot(path) ?? path,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
        var preferMove = shift || (!control &&
            sourceRoots.All(path => SameVolume(path, target)));
        effect = preferMove ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((e.AllowedEffects & effect) == 0)
            effect = (e.AllowedEffects & DragDropEffects.Copy) != 0
                ? DragDropEffects.Copy
                : (e.AllowedEffects & DragDropEffects.Move) != 0
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
        return effect != DragDropEffects.None;
    }

    private string? GalleryDropTarget(DragEventArgs e)
    {
        var folderId = BrowseSurface.GetDropFolderId(
            e.GetPosition(BrowseSurface));
        var folder = folderId is long id
            ? _photoFolders.FirstOrDefault(f => f.Id == id) : null;
        return folder is null || !ValidDragTarget(folder.LocalPath)
            ? null : folder.LocalPath;
    }

    private static void AutoScrollOnDrag(ScrollViewer scroller, Point pointer)
    {
        // Native Explorer behavior: hovering at the view edges scrolls the
        // destination while the user continues holding the mouse button.
        const double edge = 36;
        const double step = 32;
        var height = scroller.ViewportHeight;
        if (height < 2 * edge) return;
        if (pointer.Y < edge && pointer.Y >= 0)
            scroller.ScrollToVerticalOffset(
                Math.Max(0, scroller.VerticalOffset - step));
        else if (pointer.Y > height - edge && pointer.Y <= height)
            scroller.ScrollToVerticalOffset(
                Math.Min(scroller.ScrollableHeight, scroller.VerticalOffset + step));
    }

    private void GalleryFiles_DragOver(object sender, DragEventArgs e)
    {
        AutoScrollOnDrag(BrowseScrollViewer, e.GetPosition(BrowseScrollViewer));
        e.Effects = DragDropEffects.None;
        var files = DropFiles(e);
        if (GalleryDropTarget(e) is string target &&
            TryResolveDropEffect(e, files, target, out var effect))
            e.Effects = effect;
        e.Handled = true;
    }

    private void GalleryFiles_Drop(object sender, DragEventArgs e)
    {
        var files = DropFiles(e);
        if (GalleryDropTarget(e) is not string target ||
            !TryResolveDropEffect(e, files, target, out var effect))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = effect;
        e.Handled = true;
        _dragTargetValidity.Clear();
        _ = TransferGalleryFilesAsync(files, target,
            effect.HasFlag(DragDropEffects.Move));
    }

    private string? TreeDropTargetFromElement(DependencyObject? element)
    {
        var item = FindAncestor<TreeViewItem>(element);
        if (item?.Tag is not FolderTreeTag tag ||
            !ReferenceEquals(FindAncestor<TreeView>(item), FoldersTree))
            return null;
        return ValidDragTarget(tag.FullPath) ? tag.FullPath : null;
    }

    private TreeViewItem? TreeDropItem(DragEventArgs e)
    {
        // Use the innermost tree item directly under the pointer.
        var hit = FoldersTree.InputHitTest(e.GetPosition(FoldersTree))
            as DependencyObject;
        var item = FindAncestor<TreeViewItem>(hit);
        if (item?.Tag is not FolderTreeTag tag ||
            !ReferenceEquals(FindAncestor<TreeView>(item), FoldersTree) ||
            !ValidDragTarget(tag.FullPath))
            return null;
        return item;
    }

    private string? TreeDropTarget(DragEventArgs e)
        => (TreeDropItem(e)?.Tag as FolderTreeTag)?.FullPath;

    private void FolderTreeFiles_DragOver(object sender, DragEventArgs e)
    {
        if (FoldersTree.Template.FindName("PART_ScrollViewer", FoldersTree)
            is ScrollViewer scroller)
            AutoScrollOnDrag(scroller, e.GetPosition(scroller));
        e.Effects = DragDropEffects.None;
        var files = DropFiles(e);
        var item = TreeDropItem(e);
        if (item?.Tag is FolderTreeTag tag &&
            TryResolveDropEffect(e, files, tag.FullPath, out var effect))
        {
            e.Effects = effect;
            HighlightDropFolder(item);
        }
        else HighlightDropFolder(null);
        e.Handled = true;
    }

    private void FolderTreeFiles_Drop(object sender, DragEventArgs e)
    {
        HighlightDropFolder(null);
        var files = DropFiles(e);
        if (TreeDropTarget(e) is not string target ||
            !TryResolveDropEffect(e, files, target, out var effect))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = effect;
        e.Handled = true;
        _dragTargetValidity.Clear();
        _ = TransferGalleryFilesAsync(files, target,
            effect.HasFlag(DragDropEffects.Move));
    }
}
