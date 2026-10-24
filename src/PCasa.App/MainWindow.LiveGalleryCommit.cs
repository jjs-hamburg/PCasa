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
        PhotoFolderSummary? PreviousFolder,
        PhotoFolderSummary TargetFolder,
        int CatalogImageCount);

    private void PublishCommittedGalleryImage(
        int? activeTier, long imageId, long? previousFolderId)
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
                previousFolderId, source, target, _imageCatalog.CountAll());
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

        // If a complete catalog refresh was concurrently reading SQLite,
        // its previously sampled folder list must not supersede this newer
        // committed image. The transfer package will perform a final refresh.
        _browseRefreshRequests.Invalidate();

        var updated = _browseIndex.ToList();
        var earlierIndex = updated.FindIndex(item =>
            item.ImageId == change.Image.ImageId);
        if (earlierIndex >= 0) updated.RemoveAt(earlierIndex);

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

        // The existing index is sorted by tree traversal and filename.
        // Inserting one image at its proper position avoids sorting/reloading
        // the entire catalog on each NAS transfer step.
        var treeOrder = CaptureGalleryFolderOrder();
        static int Position(long folderId, IReadOnlyDictionary<long, int> positions)
            => positions.TryGetValue(folderId, out var value)
                ? value : int.MaxValue;
        var targetPosition = Position(change.Image.PhotoFolderId, treeOrder);
        var insertion = updated.FindIndex(item =>
        {
            var relative = Position(item.PhotoFolderId, treeOrder)
                .CompareTo(targetPosition);
            if (relative != 0) return relative > 0;
            relative = StringComparer.OrdinalIgnoreCase.Compare(
                item.FileName, change.Image.FileName);
            return relative > 0 ||
                relative == 0 && item.ImageId > change.Image.ImageId;
        });
        if (insertion < 0) updated.Add(change.Image);
        else updated.Insert(insertion, change.Image);

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
        }
    }
}
