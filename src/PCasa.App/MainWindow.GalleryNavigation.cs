using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Image identity, not folder-relative pixels, owns the scroll position.
    private (long ImageId, double RelativeY)? _pendingGalleryAnchor;
    private readonly GallerySearchNavigationSession _tagSearchNavigation = new();
    private bool _restoringGalleryLayoutAnchor;
    private int _galleryLayoutRestoreRevision;

    private (long ImageId, double RelativeY)? CaptureGalleryAnchor()
        => BrowseSurface.CaptureImageAnchor(BrowseScrollViewer.VerticalOffset);

    private void QueueGalleryAnchorRestore()
        => QueueGalleryAnchorRestore(CaptureGalleryAnchor());

    private void QueueGalleryAnchorRestore((long ImageId, double RelativeY)? anchor)
    {
        _pendingGalleryAnchor ??= anchor;
        if (_pendingGalleryAnchor is not null)
            _restoringGalleryLayoutAnchor = true;
    }

    private void BrowseSurface_LayoutReflowed(
        object? sender, (long ImageId, double RelativeY) anchor)
    {
        _pendingGalleryAnchor ??= anchor;
        _restoringGalleryLayoutAnchor = true;
        RestoreAnchorAfterSidebarLayout();
    }

    private void RestorePendingGalleryAnchor()
    {
        if (_pendingGalleryAnchor is not { } anchor) return;
        var revision = ++_galleryLayoutRestoreRevision;
        _restoringGalleryLayoutAnchor = true;
        _pendingGalleryAnchor = null;
        try
        {
            RestoreGalleryAnchor(anchor);
        }
        finally
        {
            // ScrollChanged can be posted by WPF *after* changing the scroll
            // offset. Keep the tree's folder focus frozen until layout settles.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (revision == _galleryLayoutRestoreRevision)
                    _restoringGalleryLayoutAnchor = false;
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void RestoreGalleryAnchor((long ImageId, double RelativeY) anchor)
    {
        BrowseScrollViewer.UpdateLayout();
        if (BrowseSurface.GetImageAnchorOffset(anchor.ImageId, anchor.RelativeY) is double top)
        {
            BrowseScrollViewer.ScrollToVerticalOffset(Math.Max(0, top));
            BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }
    }

    private void RestoreTagSearchNavigation(
        GallerySearchNavigationSession.Snapshot original)
    {
        // Search-results scrolling and photo clicks are temporary navigation,
        // not a replacement for the folder selection before search began.
        _activePhotoFolderId = original.ActivePhotoFolderId is long id &&
            _photoFolders.Any(folder => folder.Id == id)
                ? id : null;

        var selectedTreeItem = original.SelectedTreePath is string path
            ? FindFolderTreeItemByPath(FoldersTree, path) : null;
        if (selectedTreeItem is null && _activePhotoFolderId is long photoFolderId)
            _folderTreeItems.TryGetValue(photoFolderId, out selectedTreeItem);
        if (selectedTreeItem is not null)
        {
            _syncingFolderSelection = true;
            try { selectedTreeItem.IsSelected = true; }
            finally { _syncingFolderSelection = false; }
        }

        // This is an explicit return to the ORIGINAL layout position. It wins
        // over any pending anchor captured inside filtered search results.
        _pendingGalleryAnchor = original.ImageAnchor;
        _restoringGalleryLayoutAnchor = true;
        if (_pendingGalleryAnchor is not null)
        {
            RestorePendingGalleryAnchor();
        }
        else
        {
            var revision = ++_galleryLayoutRestoreRevision;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (revision == _galleryLayoutRestoreRevision)
                    _restoringGalleryLayoutAnchor = false;
            }, DispatcherPriority.ContextIdle);
        }
    }

    private void RestoreAnchorAfterSidebarLayout()
    {
        _ = Dispatcher.BeginInvoke(() =>
            RestorePendingGalleryAnchor(), DispatcherPriority.Loaded);
    }
}
