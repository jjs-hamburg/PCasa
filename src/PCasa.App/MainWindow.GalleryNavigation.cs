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

    // A user's explicit folder click supersedes any delayed relocation
    // publication or automatic post-drop scroll. This is view intent, not
    // a separate work queue.
    private long _explicitGalleryNavigationVersion;

    // Scan publication is PRESENTATION only: the scanner and SQLite commit
    // immediately. While the user navigates rapidly, keep the published
    // gallery projection stable and coalesce all incoming scan notifications.
    // This timer never schedules source IO or owns a second catalog index.
    private DispatcherTimer? _scanGalleryScrollSettleTimer;
    private bool _scanGalleryPublicationDeferred;
    private static readonly TimeSpan ScanGalleryScrollQuietPeriod =
        TimeSpan.FromMilliseconds(500);

    private bool GalleryAtLiveEdge()
    {
        var height = BrowseScrollViewer.ScrollableHeight;
        var offset = BrowseScrollViewer.VerticalOffset;
        return height <= 2 || offset <= 2 || height - offset <= 2;
    }

    private bool GalleryScrollIsActive()
        => _scanGalleryScrollSettleTimer?.IsEnabled == true &&
           !GalleryAtLiveEdge();

    private void RegisterGalleryUserScroll()
    {
        // An automatic anchor posted before this input cannot pull the user
        // back afterwards. This is the established navigation revision.
        MarkExplicitGalleryNavigation();
        if (GalleryAtLiveEdge())
        {
            _scanGalleryScrollSettleTimer?.Stop();
            PublishDeferredScanGallery();
            return;
        }
        if (_scanGalleryScrollSettleTimer is null)
        {
            _scanGalleryScrollSettleTimer = new DispatcherTimer(
                DispatcherPriority.Background)
            {
                Interval = ScanGalleryScrollQuietPeriod
            };
            _scanGalleryScrollSettleTimer.Tick += (_, _) =>
            {
                _scanGalleryScrollSettleTimer.Stop();
                PublishDeferredScanGallery();
            };
        }
        _scanGalleryScrollSettleTimer.Stop();
        _scanGalleryScrollSettleTimer.Start();
    }

    private bool DeferScanGalleryPublication()
    {
        if (!GalleryScrollIsActive()) return false;
        _scanGalleryPublicationDeferred = true;
        return true;
    }

    private void PublishDeferredScanGallery()
    {
        if (!_scanGalleryPublicationDeferred || !_browseInitialized ||
            Dispatcher.HasShutdownStarted || _shutdownStarted)
            return;
        _scanGalleryPublicationDeferred = false;
        // One latest catalog snapshot, not a replay of thousands of stale
        // thumbnail insertions. User-triggered UI publications bypass this
        // gate and keep their existing priority.
        System.Threading.Interlocked.Exchange(ref _catalogPublicationPending, 0);
        _catalogPublicationTimer?.Stop();
        _ = RefreshBrowseGalleryAsync(forceIndexReload: true,
            warmViewport: false, fromScanner: true);
    }

    private void MarkExplicitGalleryNavigation()
    {
        _explicitGalleryNavigationVersion++;
        _pendingGalleryAnchor = null;
        _galleryLayoutRestoreRevision++;
        _restoringGalleryLayoutAnchor = false;
    }

    private (long ImageId, double RelativeY)? CaptureGalleryAnchor()
        => BrowseSurface.CaptureImageAnchor(BrowseScrollViewer.VerticalOffset);

    private void QueueGalleryAnchorRestore()
        => QueueGalleryAnchorRestore(CaptureGalleryAnchor());

    private void QueueGalleryAnchorRestore((long ImageId, double RelativeY)? anchor)
    {
        // The hidden viewer must not let a scan replace its opening location
        // with the current pixel row as new photos are inserted above it.
        if (_viewerOpen && _viewerOpeningGalleryAnchor is { } viewerAnchor)
            anchor = viewerAnchor;
        // The first index publication starts with the gallery at image zero.
        // That temporary startup anchor must not overwrite the saved user's
        // position before RestoreGalleryPositionOnce has applied it.
        if (!_galleryPositionRestored && _appearance.RestoreGalleryPosition &&
            _explicitGalleryNavigationVersion == 0)
            return;
        _pendingGalleryAnchor ??= anchor;
        if (_pendingGalleryAnchor is not null)
            _restoringGalleryLayoutAnchor = true;
    }

    private void BrowseSurface_LayoutReflowed(
        object? sender, (long ImageId, double RelativeY) anchor)
    {
        if (_viewerOpen && _viewerOpeningGalleryAnchor is { } viewerAnchor)
            anchor = viewerAnchor;
        if (!_galleryPositionRestored && _appearance.RestoreGalleryPosition &&
            _explicitGalleryNavigationVersion == 0)
            return;
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

    // A settings change is a presentation-only operation, never a gallery
    // navigation. Restore the PRE-DIALOG image identity after WPF finishes
    // resizing/reflowing its rows, and preserve the original active folder.
    // A subsequent explicit user navigation always takes precedence.
    private void RestoreAppearanceGalleryAnchor(
        (long ImageId, double RelativeY)? anchor, double oldOffset,
        long navigationVersion, long? activeFolderId)
    {
        if (_explicitGalleryNavigationVersion != navigationVersion ||
            Dispatcher.HasShutdownStarted) return;
        var revision = ++_galleryLayoutRestoreRevision;
        _restoringGalleryLayoutAnchor = true;
        BrowseScrollViewer.UpdateLayout();
        var target = anchor is { } image
            ? BrowseSurface.GetImageAnchorOffset(image.ImageId, image.RelativeY)
            : null;
        BrowseScrollViewer.ScrollToVerticalOffset(
            Math.Max(0, target ?? oldOffset));
        BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
            Math.Max(1, BrowseScrollViewer.ViewportHeight));
        _activePhotoFolderId = activeFolderId;
        // A reflow can have published another pending anchor meanwhile.
        // It is never allowed to overwrite the original appearance snapshot.
        _pendingGalleryAnchor = null;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (revision == _galleryLayoutRestoreRevision)
                _restoringGalleryLayoutAnchor = false;
        }, DispatcherPriority.ContextIdle);
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
