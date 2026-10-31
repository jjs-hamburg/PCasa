using System.Windows.Threading;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Presentation of ONE committed catalog image, not a second catalog,
    // import queue or scheduler. All SQLite reads run inside the SAME
    // scheduler transfer package that committed the file. The WPF thread
    // receives only immutable source/target projection facts.
    private sealed record LiveGalleryCommit(
        int Tier,
        BrowseThumbnailIndexEntry Image,
        long? PreviousFolderId,
        long? ReplacedImageId,
        PhotoFolderSummary? PreviousFolder,
        PhotoFolderSummary TargetFolder,
        int CatalogImageCount);

    // Scanner publishes only a committed identity. The UI decides whether it
    // belongs to the currently selected photo folder; unrelated NAS progress
    // never rebuilds or moves the user's gallery.
    private void OnCatalogImageIndexed(string canonicalPath, long photoFolderId)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_browseInitialized || _folderManagerOpen ||
                _folderRemovalPending || _activePhotoFolderId != photoFolderId ||
                _activeBrowseTier is not int tier)
                return;
            // Catalog and EXIF already committed. Holding the visual update
            // while the user is scrolling avoids one full layout reflow per
            // incoming file. Only one fresh catalog snapshot is later needed.
            if (DeferScanGalleryPublication()) return;
            _ = InsertIndexedGalleryImageAsync(canonicalPath, photoFolderId, tier);
        }), DispatcherPriority.Background);
    }

    private async Task InsertIndexedGalleryImageAsync(
        string canonicalPath, long photoFolderId, int tier)
    {
        try
        {
            await _scheduler.RunAsync(
                $"gallery:committed-image:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var id = _imageCatalog.GetImageId(canonicalPath);
                    if (id is long imageId &&
                        _imageCatalog.GetImagePhotoFolderId(imageId) == photoFolderId)
                        PublishCommittedGalleryImage(tier, imageId, null);
                    return true;
                }, workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.LiveScannerCommit", ex);
        }
    }

    private void PublishCommittedGalleryImage(
        int? activeTier, long imageId, long? previousFolderId,
        long? replacedImageId = null)
    {
        if (activeTier is not int tier) return;

        try
        {
            var image = _browseCacheRepository?.GetIndexEntry(tier, imageId);
            if (image is null) return; // An uncached file needs final import.

            var target = _imageCatalog.GetPhotoFolderSummary(image.PhotoFolderId);
            if (target is null) return;
            var source = previousFolderId is long previous &&
                previous != image.PhotoFolderId
                ? _imageCatalog.GetPhotoFolderSummary(previous) : null;

            var publication = new LiveGalleryCommit(tier, image,
                previousFolderId, replacedImageId, source, target,
                _imageCatalog.CountAll());
            if (Dispatcher.HasShutdownStarted) return;
            _ = Dispatcher.BeginInvoke(
                new Action(() => _ = ApplyCommittedGalleryImageAsync(publication)),
                DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            // A display read cannot invalidate an already committed physical
            // move. The parent package always performs a final catalog refresh.
            PCasaDiagnostics.Write("Gallery.LiveCommit", ex);
        }
    }

    private async Task ApplyCommittedGalleryImageAsync(LiveGalleryCommit change)
    {
        if (!_browseInitialized || _browsePageCache is null ||
            _activeBrowseTier != change.Tier || Dispatcher.HasShutdownStarted)
            return;

        // An indexed image may have been fetched BEFORE scrolling began.
        // Guard the actual UI publication as well as the initial notification.
        if (DeferScanGalleryPublication()) return;
        var followGalleryStart = GalleryAtLeadingLiveEdge();
        var followGalleryEnd = GalleryAtTrailingLiveEdge();

        // If a complete catalog refresh was concurrently reading SQLite,
        // its previously sampled folder list must not supersede this newer
        // committed image. The transfer package will perform a final refresh.
        _browseRefreshRequests.Invalidate();

        var updated = _browseIndex.ToList();
        var earlierIndex = updated.FindIndex(item =>
            item.ImageId == change.Image.ImageId);
        if (earlierIndex >= 0) updated.RemoveAt(earlierIndex);
        if (change.ReplacedImageId is long replaced &&
            replaced != change.Image.ImageId)
        {
            updated.RemoveAll(item => item.ImageId == replaced);
            _selectedImageIds.Remove(replaced);
            _dragSelectionPathCache.Remove(replaced);
        }

        // Keep ALL other photo folders and gallery thumbnails. Change only
        // the source/target summaries. An empty photo folder loses its gallery
        // section, not its physical directory or TreeView item.
        var folders = _photoFolders.ToDictionary(folder => folder.Id);
        if (change.PreviousFolderId is long oldId &&
            oldId != change.Image.PhotoFolderId)
        {
            if (change.PreviousFolder is { } previous)
                folders[oldId] = previous;
            else
                folders.Remove(oldId);
        }
        folders[change.TargetFolder.Id] = change.TargetFolder;
        _photoFolders = folders.Values.OrderBy(folder => folder.CanonicalPath,
            StringComparer.OrdinalIgnoreCase).ToArray();
        _lastCatalogImageCount = change.CatalogImageCount;

        if (!_folderTreeItems.ContainsKey(change.TargetFolder.Id))
            ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                DiscoveredPhotoFolderId: change.TargetFolder.Id,
                DiscoveredPhotoFolderPath: change.TargetFolder.LocalPath));
        RefreshVisibleFolderCounts();

        // All gallery producers use exactly ONE comparator. A file moved
        // during a Size/Date/Dimensions sort must appear immediately at its
        // right ordinal within the target folder, not be inserted by name.
        // Binary search avoids a linear comparison scan over 500k photos.
        var treeOrder = CaptureGalleryFolderOrder();
        var chosen = CurrentGallerySort;
        if (_publishedGallerySort != chosen)
        {
            // Only needed if the user changed the sort while a file-transfer
            // package was running. Reconcile once, then all subsequent file
            // commits use inexpensive binary insertion.
            updated = GalleryFolderOrdering.Order(updated, treeOrder, chosen)
                .ToList();
            _publishedGallerySort = chosen;
        }
        var comparer = GalleryFolderOrdering.CreateComparer(treeOrder, chosen);
        var insertion = updated.BinarySearch(change.Image, comparer);
        if (insertion < 0) insertion = ~insertion;
        updated.Insert(insertion, change.Image);

        QueueGalleryAnchorRestore();
        _browseIndex = updated;
        _dragSelectionPathCache.Remove(change.Image.ImageId);
        try
        {
            // One established presentation path owns all search modes,
            // inversion, surface pages and folder visibility. A transfer
            // must never maintain an independent filtered-gallery state.
            await ApplyTagFilterAsync();
            UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.LiveProjection", ex);
        }
        finally
        {
            RestorePendingGalleryAnchor();
            if (followGalleryStart || followGalleryEnd)
            {
                BrowseScrollViewer.UpdateLayout();
                if (followGalleryStart)
                    BrowseScrollViewer.ScrollToTop();
                else
                    BrowseScrollViewer.ScrollToBottom();
                BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                    Math.Max(1, BrowseScrollViewer.ViewportHeight));
            }
        }
    }
}
