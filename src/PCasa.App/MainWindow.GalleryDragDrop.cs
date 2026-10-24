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
    // Optional origin metadata; FileDrop remains the native shell format.
    private const string InternalGalleryDragFormat = "PCasa.InternalGalleryDrag";
    // Set only for a right-button gesture. Left drops preserve the established
    // Move / Ctrl+Copy behavior; right drops show Copy/Move/Cancel.
    private const string InternalGalleryRightDragFormat =
        "PCasa.InternalGalleryRightDrag";
    // OLE reports Move for both an accepted in-app drop and a move to
    // Explorer. Only the latter needs physical-source reconciliation.
    // These two flags describe ONE synchronous native drag lifetime; they
    // are not an alternative scheduler/task state.
    private bool _nativeGalleryDragInProgress;
    private bool _internalGalleryDropAccepted;

    private static bool NeedsExternalMoveReconciliation(
        DragDropEffects result, bool acceptedInternalDrop)
        => result.HasFlag(DragDropEffects.Move) && !acceptedInternalDrop;

    private void RecordAcceptedInternalGalleryDrop(DragEventArgs e)
    {
        if (_nativeGalleryDragInProgress &&
            e.Data.GetDataPresent(InternalGalleryDragFormat, autoConvert: false))
            _internalGalleryDropAccepted = true;
    }

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
        // Drag-over is purely a UI hint. Do not query SQLite once per
        // pointer event: the authoritative check remains in the scheduler
        // when the actual transfer begins.
        var owner = FolderExclusionRules.FindOwner(
            path, _dropTargetProjection.Select(entry => entry.Folder));
        valid = owner is not null &&
            !FolderExclusionRules.IsExcluded(path,
                DisplayedExcludedPaths(owner.Id));
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
        var selected = _selectedImageIds.ToArray();
        try
        {
            // No async/await before native DoDragDrop. A missed path lookup
            // is preloaded for the NEXT gesture instead of pretending the
            // current mouse button is still held after an awaited DB read.
            var files = PreparedDragFilesOrQueueLookup(selected);
            if (files is null || files.Length == 0 || _viewerOpen ||
                Dispatcher.HasShutdownStarted || !_selectedImageIds.SetEquals(selected))
                return;
            var data = new DataObject(DataFormats.FileDrop, files);
            data.SetData(InternalGalleryDragFormat, true);
            if (e.DragButton == MouseButton.Right)
                data.SetData(InternalGalleryRightDragFormat, true);
            DragDropEffects effect;
            _nativeGalleryDragInProgress = true;
            _internalGalleryDropAccepted = false;
            bool acceptedInternalDrop;
            try
            {
                effect = DragDrop.DoDragDrop(BrowseSurface, data,
                    DragDropEffects.Copy | DragDropEffects.Move);
            }
            finally
            {
                acceptedInternalDrop = _internalGalleryDropAccepted;
                _nativeGalleryDragInProgress = false;
                _internalGalleryDropAccepted = false;
                HighlightDropFolder(null);
            }
            // An in-app drop already owns ONE scheduler transfer package.
            // Starting an external-move reconciliation here used to enqueue
            // another manual job and preempt that package mid-transfer.
            if (NeedsExternalMoveReconciliation(effect, acceptedInternalDrop))
                _ = ReconcileMovedExternalFilesAsync(files);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.StartDrag", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = "Bilder konnten nicht zum Ziehen vorbereitet werden";
        }
    }

    // File.Exists also returns false when Windows cannot reach a NAS or lacks
    // access. Only a confirmed absent FILE inside a reachable parent folder
    // may be detached after a shell move.
    private static bool ConfirmedMissingExternalMove(string path)
        => SourceFilePresence.Probe(path) == FilePresence.ConfirmedMissing;

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

    private static bool RightDragNeedsChoice(
        IDataObject data, bool acceptedInternalDrop)
        => acceptedInternalDrop && data.GetDataPresent(
            InternalGalleryRightDragFormat, autoConvert: false);

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
        // DragOver runs on WPF's dispatcher at pointer frequency. Never
        // classify mapped drives or query a disconnected NAS here.
        // Physical same-volume validation still runs inside the scheduler.
        var targetVolume = Path.GetPathRoot(target);
        var sourceVolumes = paths.Select(Path.GetPathRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sameRoot = sourceVolumes.All(volume =>
            !string.IsNullOrEmpty(volume) &&
            string.Equals(volume, targetVolume,
                StringComparison.OrdinalIgnoreCase));
        var internalDrag = e.Data.GetDataPresent(
            InternalGalleryDragFormat, autoConvert: false);
        effect = PreferredDropEffect(
            internalDrag, control, shift, sameRoot, e.AllowedEffects);
        return effect != DragDropEffects.None;
    }

    // The app's own thumbnails MOVE by default (Ctrl = Copy).
    // External Explorer FileDrop keeps same-/cross-volume semantics.
    private static DragDropEffects PreferredDropEffect(
        bool internalPcasaDrag, bool control, bool shift,
        bool sameRoot, DragDropEffects allowed)
    {
        var move = shift || (!control && (internalPcasaDrag || sameRoot));
        var wanted = move ? DragDropEffects.Move : DragDropEffects.Copy;
        if ((allowed & wanted) != 0) return wanted;
        return (allowed & DragDropEffects.Copy) != 0
            ? DragDropEffects.Copy
            : (allowed & DragDropEffects.Move) != 0
                ? DragDropEffects.Move
                : DragDropEffects.None;
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
            StatusText.Text = "Ablage abgewiesen: kein gültiger überwachter Zielordner";
            return;
        }
        e.Effects = effect;
        e.Handled = true;
        RecordAcceptedInternalGalleryDrop(e);
        _dragTargetValidity.Clear();
        if (RightDragNeedsChoice(e.Data, _nativeGalleryDragInProgress))
        {
            // Show the decision AFTER the native drag releases the mouse.
            // Merely hovering a target never mutates the catalog.
            _ = Dispatcher.BeginInvoke(new Action(() =>
                ShowGalleryTreeDropChoice(BrowseSurface, files, target)),
                System.Windows.Threading.DispatcherPriority.Input);
        }
        else
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
        // Capture the drop hit-test while the highlighted TreeViewItem is
        // still laid out. Clearing its border may change the hit-test target.
        var target = TreeDropTarget(e);
        HighlightDropFolder(null);
        var files = DropFiles(e);
        if (target is null ||
            !TryResolveDropEffect(e, files, target, out var effect))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            StatusText.Text = "Ablage abgewiesen: kein gültiger überwachter Zielordner";
            return;
        }
        e.Effects = effect;
        e.Handled = true;
        RecordAcceptedInternalGalleryDrop(e);
        _dragTargetValidity.Clear();
        if (RightDragNeedsChoice(e.Data, _nativeGalleryDragInProgress))
        {
            // Right-drag shows the three choices. Left-drag still moves
            // directly (Ctrl copies), as agreed, and an external Explorer
            // drop retains native Windows modifier semantics.
            var dropItem = FindFolderTreeItemByPath(FoldersTree, target);
            _ = Dispatcher.BeginInvoke(new Action(() =>
                ShowGalleryTreeDropChoice(
                    (FrameworkElement?)dropItem ?? FoldersTree, files, target)),
                System.Windows.Threading.DispatcherPriority.Input);
        }
        else
            _ = TransferGalleryFilesAsync(files, target,
                effect.HasFlag(DragDropEffects.Move));
    }

    private void ShowGalleryTreeDropChoice(
        FrameworkElement targetElement, string[] files, string destination)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = targetElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        menu.SetResourceReference(Control.BackgroundProperty, "PcasaPanelBrush");
        var copy = new MenuItem { Header = "Kopieren" };
        var move = new MenuItem { Header = "Verschieben" };
        var cancel = new MenuItem { Header = "Abbrechen" };
        copy.Click += (_, _) => _ = TransferGalleryFilesAsync(
            files, destination, move: false);
        move.Click += (_, _) => _ = TransferGalleryFilesAsync(
            files, destination, move: true);
        cancel.Click += (_, _) => menu.IsOpen = false;
        menu.Items.Add(copy);
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        menu.Items.Add(cancel);
        menu.IsOpen = true;
    }
}
