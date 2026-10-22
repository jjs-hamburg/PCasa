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
    private void BrowseSurface_ImagesDragRequested(object? sender,
        BrowseImageClickedEventArgs e)
    {
        if (_viewerOpen || !_selectedImageIds.Contains(e.ImageId)) return;
        var files = SelectedGalleryPaths().Where(File.Exists).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, files);
        // FileDrop is the native shell protocol, not a proprietary PCasa blob.
        // Therefore the same drag works inside PCasa and into Windows Explorer.
        var effect = DragDrop.DoDragDrop(BrowseSurface, data,
            DragDropEffects.Copy | DragDropEffects.Move);
        if (effect.HasFlag(DragDropEffects.Move))
            _ = ReconcileMovedExternalFilesAsync(files);
    }

    private async Task ReconcileMovedExternalFilesAsync(string[] originalPaths)
    {
        // Explorer moves the originals outside our scheduler. Reconcile ONLY
        // files which are actually absent after the shell completes the drag,
        // never assume that a requested drag effect means it succeeded.
        var missing = originalPaths.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length == 0) return;
        try
        {
            await _scheduler.EnqueueAsync(
                $"gallery:external-move:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive, token =>
                {
                    foreach (var source in missing)
                    {
                        token.ThrowIfCancellationRequested();
                        if (File.Exists(source)) continue;
                        if (CatalogImageIdAtPath(source) is long id)
                            new CatalogRemovalService(_database).DetachImage(id, source);
                    }
                    return Task.CompletedTask;
                });
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
        if (paths.Length == 0 || !Directory.Exists(target)) return false;
        var states = e.KeyStates;
        var control = (states & DragDropKeyStates.ControlKey) != 0;
        var shift = (states & DragDropKeyStates.ShiftKey) != 0;
        var preferMove = shift || (!control &&
            paths.All(path => SameVolume(path, target)));
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
        return folder is null || !IsMonitoredGalleryDestination(folder.LocalPath)
            ? null : folder.LocalPath;
    }

    private void GalleryFiles_DragOver(object sender, DragEventArgs e)
    {
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
        _ = TransferGalleryFilesAsync(files, target,
            effect.HasFlag(DragDropEffects.Move));
    }

    private string? TreeDropTarget(DragEventArgs e)
    {
        var start = e.OriginalSource as DependencyObject;
        var item = ItemsControl.ContainerFromElement(FoldersTree, start)
            as TreeViewItem;
        if (item?.Tag is not FolderTreeTag tag) return null;
        return Directory.Exists(tag.FullPath) &&
            IsMonitoredGalleryDestination(tag.FullPath)
                ? tag.FullPath : null;
    }

    private void FolderTreeFiles_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        var files = DropFiles(e);
        if (TreeDropTarget(e) is string target &&
            TryResolveDropEffect(e, files, target, out var effect))
            e.Effects = effect;
        e.Handled = true;
    }

    private void FolderTreeFiles_Drop(object sender, DragEventArgs e)
    {
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
        _ = TransferGalleryFilesAsync(files, target,
            effect.HasFlag(DragDropEffects.Move));
    }
}
