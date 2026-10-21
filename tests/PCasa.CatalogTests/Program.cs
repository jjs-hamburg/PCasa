using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        Run("Rename reconciles the catalog", RenameMustReconcile, failures);
        Run("Excluded subtree has no catalog or gallery entries", ExcludedSubtreeIsRemoved, failures);
        Run("Re-inclusion during pending exclusion must not prune catalog", ReincludeDuringExclusionDoesNotPrune, failures);

        Run("Missing thumbnail tier is scheduled for repair", MissingThumbnailIsRepaired, failures);
        Run("Externally deleted image is pruned", DeletedFileIsPruned, failures);
        Run("Deletion-only scan signals gallery refresh", DeletionOnlyScanMarksCatalogChanged, failures);
        Run("Catalog-backed selection drops deleted photos but retains uncached photos", SelectionUsesCatalogNotThumbs, failures);
        Run("Unavailable root never deletes catalog", UnavailableRootKeepsCatalog, failures);
        Run("Missing thumbnail tier is rebuilt", MissingThumbnailCanRebuild, failures);
        Run("Repeated browse index keeps decoded thumbnail pages", IdenticalIndexRetainsPage, failures);
        Run("Exclusion service removes and re-includes folder", ExclusionServiceRoundTrip, failures);
        Run("Canceled scan preserves catalog", CanceledScanPreservesCatalog, failures);
        Run("Cancellation in final progress cannot prune catalog", FinalProgressCancellationPreservesCatalog, failures);
        Run("Source disappearing after enumeration never deletes catalog", SourceLossAfterEnumerationPreservesCatalog, failures);
        Run("Repeated folder monitor disconnect and reconnect preserves catalog", RepeatedMonitorReconnectPreservesCatalog, failures);
        Run("Faulted file watcher is rebuilt after an Error event", FaultedWatcherIsRebuilt, failures);
        Run("Canceled reconciliation never commits deletions", CanceledReconciliationPreservesCatalog, failures);
        Run("Partial folder scan preserves siblings", PartialScanPreservesSibling, failures);
        Run("Nonrecursive scan preserves unseen descendants", NonrecursiveScanPreservesDescendants, failures);
        Run("Renamed directory reconciles entire subtree", RenamedDirectoryReconciles, failures);
        Run("Scan respects excluded subtree", ExcludedSubtreeNeverReappears, failures);
        Run("Modified source refreshes browse cache", ModifiedSourceRefreshesCache, failures);
        Run("Repeated reconciliation is idempotent", RepeatedReconciliationIsIdempotent, failures);
        Run("JPEG tag write and removal persist in original", JpegTagRoundTrip, failures);
        Run("Catalog cleanup has indexed pack and root lookups", CatalogCleanupIndexesExist, failures);
        Run("Unicode JPEG keyword leaves pixels untouched", JpegUnicodeTagPreservesPixels, failures);
        Run("EXIF fields survive tag write and removal", JpegExifFieldsSurviveTags, failures);
        Run("Recent quick tags persist and reorder", RecentQuickTagsPersist, failures);
        Run("Gallery identity survives sidebar width change", GalleryAnchorSurvivesWidthChange, failures);
        Run("Thumbnail resizing retains image anchor across folder boundaries", ResizeThumbnailPreservesImageAnchor, failures);
        Run("Thumbnail spacing preview and cancel retain current image", SpacingPreviewRetainsImageAnchor, failures);
        Run("Collapsing unrelated photo folder retains visible photo", CollapsedFolderKeepsVisibleImage, failures);
        Run("Hidden folder gap requests only visible thumbnail intervals", HiddenGalleryGapStaysSparse, failures);
        Run("Navigating a hidden folder expands its real tree ancestors", SelectedFolderExpandsTreeAncestors, failures);
        Run("Tag search saves original folder focus only once", TagSearchRetainsOriginalNavigation, failures);
        Run("Tag filter round trip restores image and selection", TagSearchGalleryRoundTrip, failures);
        Run("Old sidebar results cannot overwrite multi-selection or newer view", SidebarAsyncResultsRespectRequest, failures);
        Run("Older asynchronous gallery refresh cannot overwrite newer result", AsyncGalleryRefreshLatestWins, failures);
        Run("500000-image gallery reaches last photo", LargeGalleryLastImageReachable, failures);
        Run("Multiple selected images synchronize tag index", MultiImageTagSearchStaysConsistent, failures);
        Run("Concurrent scan and exclusion never repopulate deleted catalog", ConcurrentExclusionScan, failures);
        Run("Monitored folder detaches catalog before physical cache cleanup", FolderRemovalDefersPhysicalCleanup, failures);
        Run("Successful recursive scan clears obsolete nested failures", RecursiveScanResolvesOldFolderErrors, failures);
        Run("New watcher work survives an in-progress scan", PendingScanWorkSurvivesCompletion, failures);
        Console.WriteLine($"RESULT: {44 - failures.Count} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test, List<string> failures)
    {
        try { test(); Console.WriteLine("PASS: " + name); }
        catch (Exception error)
        {
            failures.Add(name);
            Console.Error.WriteLine($"FAIL: {name}: {error}");
        }
    }

    private static void RenameMustReconcile()
    {
        using var sandbox = new Sandbox();
        var first = Path.Combine(sandbox.Images, "before.jpg");
        WriteJpeg(first);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var second = Path.Combine(sandbox.Images, "after.jpg");
        File.Move(first, second);
        scanner.Scan(root);
        Require(db, 1);
        var images = new ImageCatalogRepository(db).GetPhotoFolders();
        if (images.Count != 1 || images[0].ImageCount != 1)
            throw new Exception("A renamed image must replace, not duplicate, its former catalog path.");
        using var connection = db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT local_path FROM images";
        if (!string.Equals(cmd.ExecuteScalar() as string, second, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Catalog still points to the old file name.");
    }

    private static void ExcludedSubtreeIsRemoved()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "sub");
        Directory.CreateDirectory(child);
        WriteJpeg(Path.Combine(child, "photo.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        new FolderRepository(db).SetExcluded(root.Id, child, true);
        var removed = new CatalogRemovalService(db).RemovePath(root.Id, child);
        if (removed != 1) throw new Exception($"Expected to remove one image; removed {removed}.");
        Require(db, 0);
        if (new BrowseCacheRepository(db).GetIndex(96).Count != 0)
            throw new Exception("Excluded image remains in gallery index.");
    }

    private static void MissingThumbnailIsRepaired()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "photo.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var profile = new BrowseThumbnailProfileRepository(db).Get();
        var cache = new BrowseCacheRepository(db);
        var tier = profile.Tiers[0];
        if (cache.GetIndex(tier).Count != 1)
            throw new Exception("Test setup did not create the expected gallery thumbnail.");

        using (var connection = db.OpenConnection())
        using (var delete = connection.CreateCommand())
        {
            // Simulate interrupted/corrupted cache bookkeeping:
            // the image exists, but its thumbnail and repair job do not.
            delete.CommandText = @"
                DELETE FROM thumbnail_entries WHERE tier = $tier;
                DELETE FROM browse_cache_jobs;";
            delete.Parameters.AddWithValue("$tier", tier);
            delete.ExecuteNonQuery();
        }
        if (cache.GetIndex(tier).Count != 0)
            throw new Exception("Test setup did not remove the gallery thumbnail.");
        if (cache.CountMissing(profile) == 0)
            throw new Exception("A missing thumbnail without a queued job is invisible to the repair mechanism.");
    }

    private static void DeletedFileIsPruned()
    {
        using var sandbox = new Sandbox();
        var file = Path.Combine(sandbox.Images, "to-delete.jpg");
        WriteJpeg(file);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        File.Delete(file);
        scanner.Scan(root);
        Require(db, 0);
        if (new BrowseCacheRepository(db).GetIndex(96).Count != 0)
            throw new Exception("Deleted original remains visible in the gallery index.");
    }

    private static void SelectionUsesCatalogNotThumbs()
    {
        using var sandbox = new Sandbox();
        var deletedFile = Path.Combine(sandbox.Images, "deleted.jpg");
        var keptFile = Path.Combine(sandbox.Images, "kept.jpg");
        WriteJpeg(deletedFile);
        WriteJpeg(keptFile);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);

        long deletedId = 0;
        long keptId = 0;
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, file_name FROM images";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "deleted.jpg") deletedId = reader.GetInt64(0);
                else if (reader.GetString(1) == "kept.jpg") keptId = reader.GetInt64(0);
            }
        }
        if (deletedId == 0 || keptId == 0)
            throw new Exception("Cannot resolve both test photo IDs.");

        File.Delete(deletedFile);
        scanner.Scan(root);
        Require(db, 1);

        // A valid image is still in the database when its thumbnail tier is
        // missing. Reconciling selection against the browse index would
        // incorrectly deselect that photo.
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM thumbnail_entries WHERE image_id = $id";
            command.Parameters.AddWithValue("$id", keptId);
            command.ExecuteNonQuery();
        }

        // Exercise more than one SQL parameter batch, including missing IDs.
        var selected = new[] { deletedId, keptId }
            .Concat(Enumerable.Range(90000, 501).Select(id => (long)id))
            .ToArray();
        var stillExists = new ImageCatalogRepository(db).GetExistingImageIds(selected);
        if (!stillExists.SetEquals(new[] { keptId }))
            throw new Exception("Selection has stale deleted IDs or lost an uncached image.");
    }

    private static void DeletionOnlyScanMarksCatalogChanged()
    {
        using var sandbox = new Sandbox();
        var kept = Path.Combine(sandbox.Images, "kept.jpg");
        var removed = Path.Combine(sandbox.Images, "removed.jpg");
        WriteJpeg(kept);
        WriteJpeg(removed);
        var (db, root, scanner) = sandbox.Prepare();
        var initial = scanner.Scan(root);
        if (!initial.CatalogChanged || initial.Updated != 2 || initial.Removed != 0)
            throw new Exception("Initial import was not recognized as a catalog change.");
        Require(db, 2);

        var unchanged = scanner.Scan(root);
        if (unchanged.CatalogChanged || unchanged.Removed != 0 || unchanged.Updated != 0)
            throw new Exception("Unchanged scan unnecessarily refreshed the catalog.");

        File.Delete(removed);
        var deletionOnly = scanner.Scan(root);
        if (deletionOnly.Updated != 0 || deletionOnly.Unchanged != 1 ||
            deletionOnly.Removed != 1 || !deletionOnly.CatalogChanged)
            throw new Exception("Deleting one picture did not signal a changed catalog.");
        Require(db, 1);

        File.Delete(kept);
        var empty = scanner.Scan(root);
        if (empty.ImageFilesSeen != 0 || empty.Removed != 1 ||
            !empty.CatalogChanged)
            throw new Exception("Completely empty folder lost its deletion event.");
        Require(db, 0);
        if (new BrowseCacheRepository(db).GetIndex(96).Count != 0)
            throw new Exception("Deleted images remain in browse thumbnail index.");

        var after = scanner.Scan(root);
        if (after.CatalogChanged || after.Removed != 0)
            throw new Exception("Repeated empty scan reported a phantom deletion.");
    }

    private static void UnavailableRootKeepsCatalog()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "protected.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var disconnected = sandbox.Images + "-unavailable";
        Directory.Move(sandbox.Images, disconnected);
        try
        {
            try { scanner.Scan(root); throw new Exception("Missing root was accepted as an empty scan."); }
            catch (DirectoryNotFoundException) { }
            Require(db, 1);
        }
        finally
        {
            Directory.Move(disconnected, sandbox.Images);
        }
    }

    private static void MissingThumbnailCanRebuild()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "repair.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        var cache = new BrowseCacheRepository(db);
        var profiles = new BrowseThumbnailProfileRepository(db);
        var tier = profiles.Get().Tiers[0];
        using (var connection = db.OpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM thumbnail_entries WHERE tier = $tier; DELETE FROM browse_cache_jobs;";
            cmd.Parameters.AddWithValue("$tier", tier);
            cmd.ExecuteNonQuery();
        }
        if (cache.CountMissing(profiles.Get()) != 1)
            throw new Exception("Corrupted thumbnail index not detected.");
        var built = new BrowseCacheBuilder(cache, profiles).BuildMissing();
        if (built.FailedImages != 0 || cache.GetIndex(tier).Count != 1)
            throw new Exception("A missing thumbnail could not be rebuilt.");
    }

    private static void IdenticalIndexRetainsPage()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "retained.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        var tier = new BrowseThumbnailProfileRepository(db).Get().Tiers[0];
        var entries = new BrowseCacheRepository(db).GetIndex(tier).ToArray();
        if (entries.Length != 1)
            throw new Exception("Expected exactly one cached thumbnail.");
        using var cache = new BrowseThumbnailPageCache();
        cache.SetIndex(entries);
        cache.WarmRangeAsync(0, 0).GetAwaiter().GetResult();
        if (!cache.TryGet(0, out var first) || first is null)
            throw new Exception("Cached thumbnail could not be loaded.");

        cache.SetIndex(entries.ToArray());
        if (!cache.TryGet(0, out var second) || !ReferenceEquals(first, second))
            throw new Exception("Reapplying identical index evicted decoded image pages.");

        cache.SetIndex(Array.Empty<BrowseThumbnailIndexEntry>());
        if (cache.Count != 0 || cache.TryGet(0, out _))
            throw new Exception("Changed browse index did not invalidate obsolete pages.");
    }

    private static void ExclusionServiceRoundTrip()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "child");
        Directory.CreateDirectory(child);
        WriteJpeg(Path.Combine(child, "reinclude.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var observed = new List<bool>();
        var service = new FolderExclusionService(new FolderRepository(db), db);
        service.ChangeAsync(root.Id, child, true,
            (_, _, excluded) => observed.Add(excluded)).GetAwaiter().GetResult();
        Require(db, 0);
        if (!new CatalogRemovalService(db).HasPendingMaintenance())
            throw new Exception("User-excluded subtree did not defer cache cleanup.");
        if (!File.Exists(Path.Combine(child, "reinclude.jpg")))
            throw new Exception("Excluding a subtree deleted an original image.");
        if (!new FolderRepository(db).GetExcludedPaths(root.Id)
                .Any(path => string.Equals(path, child, StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Exclusion was not persisted.");
        service.ChangeAsync(root.Id, child, false,
            (_, _, excluded) => observed.Add(excluded)).GetAwaiter().GetResult();
        scanner.Scan(root);
        Require(db, 1);
        if (!observed.SequenceEqual(new[] { true, false }))
            throw new Exception("Notifications must follow the actual persisted change.");
    }

    // G02: The exclusion callback is a deterministic synchronization point:
    // the old removal has not started yet, but the newer desired state is
    // already persisted. No sleeps or scheduler timing assumptions.
    private static void ReincludeDuringExclusionDoesNotPrune()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "child");
        Directory.CreateDirectory(child);
        var original = Path.Combine(child, "keep.jpg");
        WriteJpeg(original);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var service = new FolderExclusionService(new FolderRepository(db), db);
        var reincluded = false;
        service.ChangeAsync(root.Id, child, true,
            (_, _, excluded) =>
            {
                if (!excluded) return;
                service.ChangeAsync(root.Id, child, false)
                    .GetAwaiter().GetResult();
                reincluded = true;
            }).GetAwaiter().GetResult();

        if (!reincluded)
            throw new Exception("The controlled re-inclusion point was not reached.");
        if (new FolderRepository(db).GetExcludedPaths(root.Id).Count != 0)
            throw new Exception("The newer inclusion decision was not persisted.");
        Require(db, 1);
        if (!File.Exists(original))
            throw new Exception("The original file was unexpectedly removed.");
    }

    private static void CanceledScanPreservesCatalog()
    {
        using var sandbox = new Sandbox();
        var file = Path.Combine(sandbox.Images, "protected.jpg");
        WriteJpeg(file);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        File.Delete(file);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            scanner.Scan(root, cancellationToken: cancellation.Token);
            throw new Exception("A canceled scan unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
        Require(db, 1);
    }

    private static void FinalProgressCancellationPreservesCatalog()
    {
        using var sandbox = new Sandbox();
        var missing = Path.Combine(sandbox.Images, "missing.jpg");
        var present = Path.Combine(sandbox.Images, "present.jpg");
        WriteJpeg(missing);
        WriteJpeg(present);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        File.Delete(missing);

        using var cancellation = new CancellationTokenSource();
        var callbackRan = false;
        var progress = new SynchronousProgress<ScanProgress>(status =>
        {
            if (status.CurrentFile is not null) return;
            callbackRan = true;
            cancellation.Cancel();
        });

        try
        {
            scanner.Scan(root, progress, cancellation.Token);
            throw new Exception("Scan ignored cancellation during final progress.");
        }
        catch (OperationCanceledException) { }

        if (!callbackRan)
            throw new Exception("Final progress callback did not execute.");
        Require(db, 2); // No false delete after an interrupted scan.

        scanner.Scan(root);
        Require(db, 1); // Successful subsequent scan does reconcile.
    }

    private static void SourceLossAfterEnumerationPreservesCatalog()
    {
        using var sandbox = new Sandbox();
        var removedImage = Path.Combine(sandbox.Images, "missing.jpg");
        WriteJpeg(removedImage);
        WriteJpeg(Path.Combine(sandbox.Images, "kept.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        File.Delete(removedImage);

        var disconnected = sandbox.Images + "-offline";
        var disconnectedAtEnd = false;
        var progress = new SynchronousProgress<ScanProgress>(status =>
        {
            if (status.CurrentFile is not null || disconnectedAtEnd) return;
            Directory.Move(sandbox.Images, disconnected);
            disconnectedAtEnd = true;
        });
        try
        {
            try
            {
                scanner.Scan(root, progress);
                throw new Exception("Scanner treated vanished root as a successful scan.");
            }
            catch (DirectoryNotFoundException) { }
            if (!disconnectedAtEnd)
                throw new Exception("Root disappearance was not simulated.");
            Require(db, 2);
        }
        finally
        {
            if (disconnectedAtEnd)
                Directory.Move(disconnected, sandbox.Images);
        }

        // Once the source reconnects, a complete healthy rescan may prune.
        scanner.Scan(root);
        Require(db, 1);
    }

    private static void RepeatedMonitorReconnectPreservesCatalog()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "survives.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        using var monitor = new BackgroundFolderMonitor();
        var watched = new[] { root };
        var disconnected = sandbox.Images + "-disconnected";
        if (!monitor.Refresh(watched, enabled: true)[root.Id])
            throw new Exception("Initially reachable test root was reported offline.");

        // The second reconnect used to be a reproducible crash scenario.
        // Rebuild the watcher on every recovery, without ever treating its
        // temporary absence as a request to delete catalog entries.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            Directory.Move(sandbox.Images, disconnected);
            try
            {
                if (monitor.Refresh(watched, enabled: true)[root.Id])
                    throw new Exception($"Offline folder reported online in cycle {cycle}.");
                Require(db, 1);
            }
            finally
            {
                Directory.Move(disconnected, sandbox.Images);
            }

            if (!monitor.Refresh(watched, enabled: true)[root.Id])
                throw new Exception($"Reconnect not detected in cycle {cycle}.");
            scanner.Scan(root);
            Require(db, 1);
        }
    }

    private static void FaultedWatcherIsRebuilt()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "preserved.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var created = new List<FaultInjectingWatcher>();
        using var monitor = new BackgroundFolderMonitor(path =>
        {
            var watcher = new FaultInjectingWatcher(path);
            created.Add(watcher);
            return watcher;
        });
        var changes = new List<BackgroundFolderChange>();
        monitor.Changed += (_, change) => changes.Add(change);
        var roots = new[] { root };

        if (!monitor.Refresh(roots, enabled: true)[root.Id] || created.Count != 1)
            throw new Exception("First watcher was not created.");
        monitor.Refresh(roots, enabled: true);
        if (created.Count != 1)
            throw new Exception("Healthy watcher was needlessly recreated.");

        for (var cycle = 1; cycle <= 2; cycle++)
        {
            created[^1].SignalError();
            if (changes.Count != cycle ||
                changes[^1].Kind != BackgroundFolderChangeKind.RescanRoot ||
                !changes[^1].Recursive)
                throw new Exception("Watcher error failed to request full reconciliation.");
            if (!monitor.Refresh(roots, enabled: true)[root.Id] ||
                created.Count != cycle + 1)
                throw new Exception($"Faulted watcher reused in recovery cycle {cycle}.");
            monitor.Refresh(roots, enabled: true);
            if (created.Count != cycle + 1)
                throw new Exception("Healthy recovered watcher was unexpectedly replaced.");
            Require(db, 1);
        }
    }

    private sealed class FaultInjectingWatcher(string path) : FileSystemWatcher(path)
    {
        public void SignalError() =>
            OnError(new ErrorEventArgs(new IOException("Simulated watcher failure")));
    }

    private static void CanceledReconciliationPreservesCatalog()
    {
        using var sandbox = new Sandbox();
        var deleted = Path.Combine(sandbox.Images, "to-prune.jpg");
        WriteJpeg(deleted);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        File.Delete(deleted);

        using var cancellation = new CancellationTokenSource();
        var callbackRan = false;
        var progress = new SynchronousProgress<string>(status =>
        {
            if (!status.StartsWith("Bildkatalog bereinigen",
                    StringComparison.Ordinal)) return;
            callbackRan = true;
            cancellation.Cancel();
        });

        var cleanup = new CatalogRemovalService(db);
        try
        {
            cleanup.RemoveMissing(root.Id, sandbox.Images,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                progress: progress, cancellationToken: cancellation.Token);
            throw new Exception("Canceled catalog cleanup committed a deletion.");
        }
        catch (OperationCanceledException) { }

        if (!callbackRan)
            throw new Exception("Reconciliation progress callback did not run.");
        Require(db, 1);
        scanner.Scan(root);
        Require(db, 0);
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static void PartialScanPreservesSibling()
    {
        using var sandbox = new Sandbox();
        var a = Path.Combine(sandbox.Images, "A");
        var b = Path.Combine(sandbox.Images, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        var fileA = Path.Combine(a, "a.jpg");
        var fileB = Path.Combine(b, "b.jpg");
        WriteJpeg(fileA);
        WriteJpeg(fileB);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        File.Delete(fileA);
        File.Delete(fileB);
        scanner.ScanDirectory(root, a, recursive: true);
        Require(db, 1);
        using var connection = db.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT local_path FROM images";
        if (!string.Equals(cmd.ExecuteScalar() as string, fileB, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Partial scan removed an image outside its requested subtree.");
    }

    private static void NonrecursiveScanPreservesDescendants()
    {
        using var sandbox = new Sandbox();
        var parent = Path.Combine(sandbox.Images, "Parent");
        var child = Path.Combine(parent, "Nested");
        Directory.CreateDirectory(child);
        var topImage = Path.Combine(parent, "removed.jpg");
        var nestedImage = Path.Combine(child, "must-remain.jpg");
        WriteJpeg(topImage);
        WriteJpeg(nestedImage);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);

        File.Delete(topImage);
        scanner.ScanDirectory(root, parent, recursive: false);
        Require(db, 1);
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_path FROM images LIMIT 1";
        if (!string.Equals(command.ExecuteScalar() as string, nestedImage,
            StringComparison.OrdinalIgnoreCase))
            throw new Exception("Shallow reconciliation removed an image in an unscanned nested folder.");

        scanner.ScanDirectory(root, parent, recursive: true);
        Require(db, 1);
    }

    private static void RenamedDirectoryReconciles()
    {
        using var sandbox = new Sandbox();
        var previous = Path.Combine(sandbox.Images, "Old");
        Directory.CreateDirectory(previous);
        WriteJpeg(Path.Combine(previous, "one.jpg"));
        WriteJpeg(Path.Combine(previous, "two.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        var renamed = Path.Combine(sandbox.Images, "New");
        Directory.Move(previous, renamed);
        scanner.Scan(root);
        Require(db, 2);
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images WHERE local_path LIKE $prefix";
        command.Parameters.AddWithValue("$prefix", renamed + "%");
        if (Convert.ToInt32(command.ExecuteScalar()) != 2)
            throw new Exception("Directory rename did not update both image locations.");
    }

    private static void ExcludedSubtreeNeverReappears()
    {
        using var sandbox = new Sandbox();
        var excluded = Path.Combine(sandbox.Images, "Excluded");
        Directory.CreateDirectory(excluded);
        WriteJpeg(Path.Combine(excluded, "one.jpg"));
        WriteJpeg(Path.Combine(sandbox.Images, "visible.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        var service = new FolderExclusionService(new FolderRepository(db), db);
        service.ChangeAsync(root.Id, excluded, true).GetAwaiter().GetResult();
        Require(db, 1);
        scanner.Scan(root);
        Require(db, 1);
        WriteJpeg(Path.Combine(excluded, "new.jpg"));
        scanner.Scan(root);
        Require(db, 1);
        service.ChangeAsync(root.Id, excluded, false).GetAwaiter().GetResult();
        scanner.Scan(root);
        Require(db, 3);
    }

    private static void ModifiedSourceRefreshesCache()
    {
        using var sandbox = new Sandbox();
        var file = Path.Combine(sandbox.Images, "modified.jpg");
        WriteJpeg(file);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        var cache = new BrowseCacheRepository(db);
        var tier = new BrowseThumbnailProfileRepository(db).Get().Tiers[0];
        if (cache.GetIndex(tier).Count != 1)
            throw new Exception("Initial gallery cache is missing.");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(3));
        scanner.Scan(root);
        Require(db, 1);
        if (cache.GetIndex(tier).Count != 1 || cache.CountMissing(new BrowseThumbnailProfileRepository(db).Get()) != 0)
            throw new Exception("Modified source has an outdated or missing thumbnail.");
    }

    private static void RepeatedReconciliationIsIdempotent()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "one.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        for (var i = 0; i < 3; i++)
        {
            scanner.Scan(root);
            Require(db, 1);
        }
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM photo_folders";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new Exception("Repeated scan duplicated photo folder records.");
    }

    private static void JpegTagRoundTrip()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "keywords.jpg");
        WriteJpeg(path);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM images LIMIT 1";
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        var tags = new ImageTagRepository(db);
        tags.ChangeKeywords(id, "PCasaRegressionKeyword", remove: false);
        var value = ImageTagRepository.ReadKeywords(path);
        if (value is null || !value.Contains("PCasaRegressionKeyword", StringComparison.Ordinal))
            throw new Exception("JPEG metadata tag was not saved in the original.");
        tags.ChangeKeywords(id, "PCasaRegressionKeyword", remove: true);
        value = ImageTagRepository.ReadKeywords(path);
        if (value is null || value.Contains("PCasaRegressionKeyword", StringComparison.Ordinal))
            throw new Exception("Removing a tag did not update original JPEG metadata.");
    }

    private static void CatalogCleanupIndexesExist()
    {
        using var sandbox = new Sandbox();
        var (db, _, _) = sandbox.Prepare();
        using var connection = db.OpenConnection();
        foreach (var index in new[] {
            "idx_thumbnail_entries_pack_offset",
            "idx_images_root_local"
        })
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
            command.Parameters.AddWithValue("$name", index);
            if (Convert.ToInt32(command.ExecuteScalar()) != 1)
                throw new Exception("Missing cleanup performance index: " + index);
        }
    }

    private static void JpegUnicodeTagPreservesPixels()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "Bilder mit Umlauten Äußeres.jpg");
        WriteJpeg(path);
        var before = PixelHash(path);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        using var connection = db.OpenConnection();
        using var image = connection.CreateCommand();
        image.CommandText = "SELECT id FROM images LIMIT 1";
        var id = Convert.ToInt64(image.ExecuteScalar());
        var keywords = new ImageTagRepository(db);
        try
        {
            keywords.ChangeKeywords(id, "Frühstück São Paulo", remove: false);
        }
        catch (Exception ex)
        {
            var observed = ImageTagRepository.ReadKeywords(path);
            throw new Exception("Unicode JPEG keyword verification failed. WIC read = '" +
                observed + "' (codepoints " +
                string.Join(" ", (observed ?? "").Select(c => ((int)c).ToString("X4"))) + ")", ex);
        }
        keywords.ChangeKeywords(id, "PCasa-Second-Tag", remove: false);
        var written = ImageTagRepository.ReadKeywords(path) ?? "";
        if (!written.Contains("Frühstück São Paulo", StringComparison.Ordinal) ||
            !written.Contains("PCasa-Second-Tag", StringComparison.Ordinal))
            throw new Exception("Unicode keyword or existing keyword was lost.");
        keywords.ChangeKeywords(id, "PCasa-Second-Tag", remove: true);
        var remaining = ImageTagRepository.ReadKeywords(path) ?? "";
        if (!remaining.Contains("Frühstück São Paulo", StringComparison.Ordinal) ||
            remaining.Contains("PCasa-Second-Tag", StringComparison.Ordinal))
            throw new Exception("Removing one keyword damaged another keyword.");
        if (!before.SequenceEqual(PixelHash(path)))
            throw new Exception("JPEG decoded pixels changed during lossless tag editing.");
    }

    private static void JpegExifFieldsSurviveTags()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "metadata-preservation.jpg");
        WriteJpeg(path);
        RunExifTool("-overwrite_original",
            "-EXIF:DateTimeOriginal=2018:07:06 05:04:03",
            "-EXIF:Artist=Original Photographer", path);
        var dateBefore = RunExifTool("-s3", "-EXIF:DateTimeOriginal", path).Trim();
        var artistBefore = RunExifTool("-s3", "-EXIF:Artist", path).Trim();
        if (dateBefore != "2018:07:06 05:04:03" ||
            artistBefore != "Original Photographer")
            throw new Exception("EXIF metadata setup failed.");

        var pixelsBefore = PixelHash(path);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM images LIMIT 1";
        var id = Convert.ToInt64(command.ExecuteScalar());
        var tags = new ImageTagRepository(db);
        tags.ChangeKeywords(id, "TemporaryTag", remove: false);
        tags.ChangeKeywords(id, "TemporaryTag", remove: true);

        var dateAfter = RunExifTool("-s3", "-EXIF:DateTimeOriginal", path).Trim();
        var artistAfter = RunExifTool("-s3", "-EXIF:Artist", path).Trim();
        if (dateAfter != dateBefore || artistAfter != artistBefore)
            throw new Exception("EXIF metadata was changed by adding or removing keywords.");
        if (!pixelsBefore.SequenceEqual(PixelHash(path)))
            throw new Exception("Decoded pixels changed during metadata editing.");
        if ((ImageTagRepository.ReadKeywords(path) ?? "").Contains("TemporaryTag"))
            throw new Exception("Removed keyword still exists in the original JPEG.");
    }

    private static string RunExifTool(params string[] arguments)
    {
        var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Verified ExifTool missing in catalog tests.", executable);
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new IOException("ExifTool process could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ExifTool metadata preservation test timed out.");
        }
        var output = stdout.GetAwaiter().GetResult();
        var errors = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new IOException("ExifTool metadata preservation test failed: " + errors);
        return output;
    }

    private static byte[] PixelHash(string file)
    {
        using var stream = File.OpenRead(file);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return SHA256.HashData(pixels);
    }

    private static void RecentQuickTagsPersist()
    {
        using var sandbox = new Sandbox();
        var (db, _, _) = sandbox.Prepare();
        var first = new ImageTagRepository(db);
        first.RememberTag("Ein");
        first.RememberTag("Zwei");
        first.RememberTag("Ein");
        var second = new ImageTagRepository(db).GetRecentTags();
        if (second.Count != 2 || second[0] != "Ein" || second[1] != "Zwei")
            throw new Exception("Recently entered tags must survive new repository instances.");
    }

    private static void GalleryAnchorSurvivesWidthChange()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 90)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                i < 45 ? 1 : 2, 192, "unused.pack", 0, 1, 180, 120))
            .ToArray();
        var folders = new[] {
            new PhotoFolderSummary(1, 1, @"C:\\a", @"C:\\a", "A", 45),
            new PhotoFolderSummary(2, 1, @"C:\\b", @"C:\\b", "B", 45)
        };
        var gallery = new BrowseGallerySurface();
        cache.SetIndex(index);
        gallery.SetData(index, folders, cache);
        gallery.Measure(new System.Windows.Size(1100, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1100, gallery.DesiredSize.Height));
        var before = gallery.CaptureImageAnchor(1100)
            ?? throw new Exception("Failed to capture anchor before width change.");
        gallery.SetViewport(1100, 600);
        gallery.Measure(new System.Windows.Size(720, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 720, gallery.DesiredSize.Height));
        var afterTop = gallery.GetImageAnchorOffset(before.ImageId, before.RelativeY)
            ?? throw new Exception("Image identity disappeared after sidebar resize.");
        // Changing the number of columns cannot preserve the exact *first*
        // image in each row. It must preserve visibility of the anchored image
        // within the new viewport and keep the same source folder.
        gallery.SetViewport(afterTop, 600);
        var visible = gallery.GetVisibleIndexRange()
            ?? throw new Exception("No visible gallery range after width change.");
        var anchoredIndex = Array.FindIndex(index, entry => entry.ImageId == before.ImageId);
        if (anchoredIndex < visible.First || anchoredIndex > visible.Last)
            throw new Exception("Sidebar resize scrolled the anchored photo outside the viewport.");
        var after = gallery.CaptureImageAnchor(afterTop)
            ?? throw new Exception("Failed to capture anchor after width change.");
        if (index[(int)after.ImageId - 1].PhotoFolderId !=
            index[(int)before.ImageId - 1].PhotoFolderId)
            throw new Exception("Sidebar layout changed the source folder.");
    }

    private static void ResizeThumbnailPreservesImageAnchor()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 180)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                i < 90 ? 7 : 8, 192, "not-loaded.pack", 0, 1,
                i % 3 == 0 ? 120 : 200, i % 3 == 0 ? 190 : 120))
            .ToArray();
        var folders = new[] {
            new PhotoFolderSummary(7, 1, @"C:\\photos\\A",
                @"C:\\photos\\A", "A", 90),
            new PhotoFolderSummary(8, 1, @"C:\\photos\\B",
                @"C:\\photos\\B", "B", 90)
        };
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, folders, cache);
        gallery.Measure(new System.Windows.Size(1090, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1090, gallery.DesiredSize.Height));

        var original = gallery.CaptureImageAnchor(2800)
            ?? throw new Exception("Cannot capture gallery position.");
        var originalIndex = Array.FindIndex(index, photo => photo.ImageId == original.ImageId);
        if (originalIndex < 0) throw new Exception("Original photo is missing.");

        foreach (var size in new[] { 125d, 320d, 170d, 280d, 180d })
        {
            gallery.SetThumbnailWidth(size);
            gallery.Measure(new System.Windows.Size(830, double.PositiveInfinity));
            gallery.Arrange(new System.Windows.Rect(0, 0, 830, gallery.DesiredSize.Height));
            var target = gallery.GetImageAnchorOffset(original.ImageId, original.RelativeY)
                ?? throw new Exception("Image anchor lost after thumbnail resizing.");
            gallery.SetViewport(Math.Max(0, target), 580);
            var range = gallery.GetVisibleIndexRange()
                ?? throw new Exception("Gallery has no thumbnails after reflow.");
            if (originalIndex < range.First || originalIndex > range.Last)
                throw new Exception($"Photo {original.ImageId} moved outside viewport at {size}px.");
        }
    }

    private static void SpacingPreviewRetainsImageAnchor()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 240)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                i < 120 ? 5 : 6, 192, "not-loaded.pack", 0, 1,
                i % 3 == 0 ? 120 : 200, i % 3 == 0 ? 190 : 120))
            .ToArray();
        var folders = new[] {
            new PhotoFolderSummary(5, 1, @"C:\\pictures\\A",
                @"C:\\pictures\\A", "A", 120),
            new PhotoFolderSummary(6, 1, @"C:\\pictures\\B",
                @"C:\\pictures\\B", "B", 120)
        };
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, folders, cache);
        gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));
        var original = gallery.CaptureImageAnchor(2900)
            ?? throw new Exception("Cannot capture spacing dialog anchor.");
        var originalIndex = Array.FindIndex(index, image => image.ImageId == original.ImageId);
        if (originalIndex < 0) throw new Exception("Original photo missing.");

        gallery.BeginSpacingPreview();
        foreach (var spacing in new[] { 0d, 20d, 40d, 5d, 10d })
        {
            gallery.SetThumbnailSpacing(spacing);
            gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
            gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));
            var anchorTop = gallery.GetImageAnchorOffset(
                original.ImageId, original.RelativeY)
                ?? throw new Exception("Live spacing adjustment lost the current image.");
            gallery.SetViewport(Math.Max(0, anchorTop), 600);
            var range = gallery.GetVisibleIndexRange()
                ?? throw new Exception("Spacing preview left gallery empty.");
            if (originalIndex < range.First || originalIndex > range.Last)
                throw new Exception($"Spacing {spacing} replaced the anchored photo.");
        }

        gallery.EndSpacingPreview();
        gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));
        var finalTop = gallery.GetImageAnchorOffset(original.ImageId, original.RelativeY)
            ?? throw new Exception("Ending preview lost the original image.");
        gallery.SetViewport(Math.Max(0, finalTop), 600);
        var finalRange = gallery.GetVisibleIndexRange()
            ?? throw new Exception("No images after spacing dialog closed.");
        if (originalIndex < finalRange.First || originalIndex > finalRange.Last)
            throw new Exception("Closing spacing dialog changed gallery image.");
    }

    private static void CollapsedFolderKeepsVisibleImage()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 240)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                20 + i / 80, 192, "not-loaded.pack", 0, 1, 200, 130))
            .ToArray();
        var folders = Enumerable.Range(0, 3)
            .Select(i => new PhotoFolderSummary(20 + i, 1,
                @"C:\\photos\\folder" + i, @"C:\\photos\\folder" + i,
                "Folder " + i, 80)).ToArray();
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, folders, cache);
        gallery.Measure(new System.Windows.Size(1050, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1050, gallery.DesiredSize.Height));

        var original = gallery.CaptureImageAnchor(gallery.GetFolderOffset(22)!.Value + 450)
            ?? throw new Exception("No original gallery anchor.");
        var selectedIndex = Array.FindIndex(index, item => item.ImageId == original.ImageId);
        if (selectedIndex < 0) throw new Exception("Anchored photo missing.");

        if (!gallery.SetHiddenFolders(new HashSet<long> { 21 }))
            throw new Exception("Hiding a visible section was not recognized.");
        gallery.Measure(new System.Windows.Size(1050, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1050, gallery.DesiredSize.Height));
        var afterCollapse = gallery.GetImageAnchorOffset(original.ImageId, original.RelativeY)
            ?? throw new Exception("Collapse lost image in unrelated folder.");
        gallery.SetViewport(Math.Max(0, afterCollapse), 600);
        var visible = gallery.GetVisibleIndexRange()
            ?? throw new Exception("Collapsed gallery has no visible images.");
        if (selectedIndex < visible.First || selectedIndex > visible.Last)
            throw new Exception("Collapse unexpectedly scrolled to a different photo.");

        if (gallery.SetHiddenFolders(new HashSet<long> { 21 }))
            throw new Exception("Identical hidden folder set triggered another reflow.");
        if (!gallery.SetHiddenFolders(new HashSet<long>()))
            throw new Exception("Reopening hidden folder was not recognized.");
        gallery.Measure(new System.Windows.Size(1050, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1050, gallery.DesiredSize.Height));
        if (gallery.GetImageAnchorOffset(original.ImageId, original.RelativeY) is null)
            throw new Exception("Image identity disappeared when folder was reopened.");
    }

    private static void SelectedFolderExpandsTreeAncestors()
    {
        var tree = new System.Windows.Controls.TreeView();
        var root = new System.Windows.Controls.TreeViewItem { Header = "Root" };
        var parent = new System.Windows.Controls.TreeViewItem { Header = "Parent" };
        var target = new System.Windows.Controls.TreeViewItem { Header = "Photo folder" };
        tree.Items.Add(root);
        root.Items.Add(parent);
        parent.Items.Add(target);
        root.IsExpanded = false;
        parent.IsExpanded = false;

        if (!FolderTreeNavigation.ExpandAncestors(target))
            throw new Exception("Navigation did not recognize collapsed ancestors.");
        if (!root.IsExpanded || !parent.IsExpanded)
            throw new Exception("Selected photo folder remains hidden in its tree.");
        if (FolderTreeNavigation.ExpandAncestors(target))
            throw new Exception("An already visible folder caused repeated tree mutations.");
    }

    private static void AsyncGalleryRefreshLatestWins()
    {
        var requests = new LatestViewRequest();
        var published = new List<string>();
        var oldReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task PublishAfterAsync(long generation, string name,
            TaskCompletionSource<bool> ready)
        {
            await ready.Task;
            if (requests.IsCurrent(generation))
                published.Add(name);
        }

        var oldGeneration = requests.Begin();
        var oldWorker = PublishAfterAsync(oldGeneration, "obsolete index", oldReady);
        var newGeneration = requests.Begin();
        var newWorker = PublishAfterAsync(newGeneration, "current index", newReady);

        // Complete the NEW request first, then the OLD one.
        newReady.SetResult(true);
        newWorker.GetAwaiter().GetResult();
        oldReady.SetResult(true);
        oldWorker.GetAwaiter().GetResult();
        if (published.Count != 1 || published[0] != "current index")
            throw new Exception("Late gallery refresh rolled back a newer index.");

        requests.Invalidate();
        if (requests.IsCurrent(newGeneration))
            throw new Exception("Closed gallery accepted an old async refresh.");
    }

    private static void SidebarAsyncResultsRespectRequest()
    {
        var requests = new SidebarInfoRequestGate();
        var selected = new HashSet<long> { 101 };
        var initial = requests.Begin("Info", selected);
        if (!requests.IsCurrent(initial, "Info", selected))
            throw new Exception("A current Info request was unexpectedly rejected.");

        // Old load fails after a second photo has been selected. Its error
        // must never replace the multi-selection explanation.
        selected.Add(202);
        if (requests.IsCurrent(initial, "Info", selected))
            throw new Exception("Old read/error may overwrite multi-selection.");

        selected.Remove(202);
        var newer = requests.Begin("Info", selected);
        if (requests.IsCurrent(initial, "Info", selected) ||
            !requests.IsCurrent(newer, "Info", selected))
            throw new Exception("Older request may overwrite a newer Info result.");

        var gps = requests.Begin("GPS", selected);
        if (requests.IsCurrent(newer, "GPS", selected) ||
            !requests.IsCurrent(gps, "GPS", selected))
            throw new Exception("Switching sidebar mode did not invalidate Info.");

        // Same mode and image after closing/reopening is also a new session.
        requests.Invalidate();
        if (requests.IsCurrent(gps, "GPS", selected))
            throw new Exception("Closed sidebar accepts an obsolete result.");
        var reopened = requests.Begin("GPS", selected);
        if (!requests.IsCurrent(reopened, "GPS", selected))
            throw new Exception("Reopened sidebar cannot display current result.");
    }

    private static void TagSearchRetainsOriginalNavigation()
    {
        var session = new GallerySearchNavigationSession();
        var original = new GallerySearchNavigationSession.Snapshot(
            (ImageId: 91L, RelativeY: -14d), ActivePhotoFolderId: 7L,
            SelectedTreePath: @"C:\\pictures");
        session.Begin(original);
        // Changing the search phrase and clicking search results must not
        // rewrite the original folder focus or its return image position.
        session.Begin(new GallerySearchNavigationSession.Snapshot(
            (ImageId: 201L, RelativeY: 0d), ActivePhotoFolderId: 9L,
            SelectedTreePath: @"C:\\pictures\\results"));
        if (!session.IsActive || session.End() != original || session.IsActive)
            throw new Exception("Search navigation discarded the original focus.");
        if (session.End() is not null)
            throw new Exception("The previous search state leaked into a new search.");

        // A selected parent may have no photo folder ID or image anchor.
        var parent = new GallerySearchNavigationSession.Snapshot(
            null, null, @"C:\\pictures\\parent");
        session.Begin(parent);
        if (session.End() != parent)
            throw new Exception("Parent folder selection without a photo ID was lost.");
    }

    private static void TagSearchGalleryRoundTrip()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 180)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                i < 90 ? 1 : 2, 192, "not-loaded.pack", 0, 1,
                160, 120)).ToArray();
        var folders = new[] {
            new PhotoFolderSummary(1, 1, @"C:\\photos\\one",
                @"C:\\photos\\one", "One", 90),
            new PhotoFolderSummary(2, 1, @"C:\\photos\\two",
                @"C:\\photos\\two", "Two", 90)
        };
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, folders, cache);
        var selected = new HashSet<long> { 34, 121 };
        gallery.SetSelection(selected);
        gallery.Measure(new System.Windows.Size(1040, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1040, gallery.DesiredSize.Height));

        var anchor = gallery.CaptureImageAnchor(2600)
            ?? throw new Exception("Gallery has no original scroll anchor.");
        var originalIndex = Array.FindIndex(index, image => image.ImageId == anchor.ImageId);
        if (originalIndex < 0) throw new Exception("Original image not found.");

        var session = new GallerySearchNavigationSession();
        session.Begin(new GallerySearchNavigationSession.Snapshot(
            anchor, 1L, @"C:\\photos"));
        var filtered = index.Where(image => image.ImageId >= 120 && image.ImageId <= 132)
            .ToArray();
        cache.SetIndex(filtered);
        gallery.SetData(filtered, folders, cache);
        gallery.SetSelection(selected);
        gallery.Measure(new System.Windows.Size(760, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 760, gallery.DesiredSize.Height));
        gallery.SetViewport(0, 600);
        if (!selected.SetEquals(new[] { 34L, 121L }))
            throw new Exception("Filtering changed the user's multiselection.");

        cache.SetIndex(index);
        gallery.SetData(index, folders, cache);
        gallery.Measure(new System.Windows.Size(760, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 760, gallery.DesiredSize.Height));
        var returned = session.End()
            ?? throw new Exception("Search return state missing.");
        var position = gallery.GetImageAnchorOffset(returned.ImageAnchor!.Value.ImageId,
            returned.ImageAnchor!.Value.RelativeY)
            ?? throw new Exception("Original image was not restored after search.");
        gallery.SetViewport(Math.Max(0, position), 600);
        var range = gallery.GetVisibleIndexRange()
            ?? throw new Exception("No visible thumbnails after search.");
        if (originalIndex < range.First || originalIndex > range.Last)
            throw new Exception("Gallery returned to wrong image after filter.");
        if (returned.ActivePhotoFolderId != 1L ||
            returned.SelectedTreePath != @"C:\\photos")
            throw new Exception("Search altered pre-existing folder focus.");
    }

    private static void HiddenGalleryGapStaysSparse()
    {
        using var cache = new BrowseThumbnailPageCache();
        var index = Enumerable.Range(0, 2020)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1,
                i < 10 ? 10 : i < 2010 ? 11 : 12,
                192, "missing-but-optional.pack", 0, 1, 190, 130))
            .ToArray();
        var folders = new[] {
            new PhotoFolderSummary(10, 1, @"C:\\gallery\\first",
                @"C:\\gallery\\first", "First", 10),
            new PhotoFolderSummary(11, 1, @"C:\\gallery\\hidden",
                @"C:\\gallery\\hidden", "Hidden", 2000),
            new PhotoFolderSummary(12, 1, @"C:\\gallery\\last",
                @"C:\\gallery\\last", "Last", 10)
        };
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, folders, cache);
        gallery.SetHiddenFolders(new HashSet<long> { 11 });
        gallery.Measure(new System.Windows.Size(1040, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1040, gallery.DesiredSize.Height));
        gallery.SetViewport(0, 2500);
        var ranges = gallery.GetVisibleIndexRanges();
        if (ranges.Count != 2 || ranges[0].First != 0 || ranges[0].Last != 9 ||
            ranges[1].First != 2010 || ranges[1].Last != 2019)
            throw new Exception("Visible cache intervals include collapsed image rows.");
        gallery.WarmVisibleAsync().GetAwaiter().GetResult();
    }

    private static void LargeGalleryLastImageReachable()
    {
        const int imageCount = 500_000;
        var index = Enumerable.Range(0, imageCount)
            .Select(i => new BrowseThumbnailIndexEntry(i + 1, 1, 192,
                "test-only.pack", 0, 1,
                i % 2 == 0 ? 180 : 120, i % 2 == 0 ? 120 : 180))
            .ToArray();
        var folder = new PhotoFolderSummary(1, 1, @"C:\\photos",
            @"C:\\photos", "Photos", imageCount);
        using var cache = new BrowseThumbnailPageCache();
        cache.SetIndex(index);
        var gallery = new BrowseGallerySurface();
        gallery.SetData(index, new[] { folder }, cache);
        gallery.Measure(new System.Windows.Size(1080, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 1080, gallery.DesiredSize.Height));

        if (gallery.GetFolderOffset(1) is null)
            throw new Exception("Folder section disappeared from large layout.");
        var lastOffset = gallery.GetImageOffset(imageCount - 1)
            ?? throw new Exception("Last image has no scroll target.");
        // Match ScrollViewer's real bottom boundary: it cannot scroll
        // beyond total content height minus its viewport height.
        var scrollableBottom = Math.Max(0, gallery.DesiredSize.Height - 600);
        if (lastOffset > gallery.DesiredSize.Height)
            throw new Exception("Last image scroll target exceeds gallery extent.");
        gallery.SetViewport(scrollableBottom, 600);
        var visible = gallery.GetVisibleIndexRange()
            ?? throw new Exception("End of gallery has no visible images.");
        if (visible.Last != imageCount - 1)
            throw new Exception("Gallery does not expose the last catalog image.");
    }

    private static void MultiImageTagSearchStaysConsistent()
    {
        using var sandbox = new Sandbox();
        foreach (var name in new[] { "one.jpg", "two.jpg", "three.jpg" })
            WriteJpeg(Path.Combine(sandbox.Images, name));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 3);
        var ids = new List<long>();
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM images ORDER BY file_name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        }
        var tags = new ImageTagRepository(db);
        foreach (var id in ids.Take(2))
            tags.ChangeKeywords(id, "PCasaSelected", remove: false);
        if (tags.SearchImageIds("PCasaSelected").Count != 2)
            throw new Exception("Tag search omitted a tagged image from the selection.");
        tags.ChangeKeywords(ids[0], "PCasaSelected", remove: true);
        scanner.Scan(root);
        Require(db, 3);
        if (!tags.SearchImageIds("PCasaSelected").SetEquals(new[] { ids[1] }))
            throw new Exception("Tag search was stale after selective removal and rescan.");
    }

    private static void FolderRemovalDefersPhysicalCleanup()
    {
        using var sandbox = new Sandbox();
        var otherFolder = Path.Combine(
            Path.GetDirectoryName(sandbox.Images)!, "OtherImages");
        Directory.CreateDirectory(otherFolder);
        var originalA = Path.Combine(sandbox.Images, "remove.jpg");
        var originalB = Path.Combine(otherFolder, "keep.jpg");
        WriteJpeg(originalA);
        WriteJpeg(originalB);

        var (db, root, scanner) = sandbox.Prepare();
        var repository = new FolderRepository(db);
        repository.Add(otherFolder);
        var other = repository.GetAll().Single(f =>
            string.Equals(f.LocalPath, otherFolder, StringComparison.OrdinalIgnoreCase));
        scanner.Scan(root);
        scanner.Scan(other);
        Require(db, 2);

        // Both images have already passed through the full import pipeline,
        // including the catalog's foreign-key relationships.
        var privateCacheBase = Path.Combine(
            Path.GetDirectoryName(sandbox.Images)!, "IsolatedCleanupCache");
        var privateThumbs = Path.Combine(privateCacheBase, "thumbnails");
        var privatePacks = Path.Combine(privateCacheBase, "browse-cache");
        Directory.CreateDirectory(privateThumbs);
        Directory.CreateDirectory(privatePacks);
        var liveThumb = Path.Combine(privateThumbs, "live.jpg");
        var orphanThumb = Path.Combine(privateThumbs, "orphan.jpg");
        var orphanPack = Path.Combine(privatePacks, "orphan.pack");
        File.WriteAllBytes(liveThumb, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(orphanThumb, new byte[] { 4, 5 });
        File.WriteAllBytes(orphanPack, new byte[] { 6, 7, 8 });

        using (var connection = db.OpenConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "UPDATE images SET thumbnail_path = $path WHERE folder_id = $id;";
            cmd.Parameters.AddWithValue("$path", liveThumb);
            cmd.Parameters.AddWithValue("$id", other.Id);
            if (cmd.ExecuteNonQuery() != 1)
                throw new Exception("Surviving image thumbnail was not prepared.");
        }

        var removedStatus = new List<string>();
        repository.Remove(root.Id, new SynchronousProgress<string>(removedStatus.Add));
        Require(db, 1);
        if (repository.GetAll().Count != 1 ||
            repository.GetAll()[0].Id != other.Id)
            throw new Exception("Folder detachment removed another monitored root.");
        if (!File.Exists(originalA) || !File.Exists(originalB))
            throw new Exception("Removing a folder deleted original image files.");
        if (!File.Exists(orphanThumb) || !File.Exists(orphanPack))
            throw new Exception("User-facing removal performed slow physical cleanup.");
        if (removedStatus.Count == 0)
            throw new Exception("Removal did not report its progress.");

        var maintenance = new CatalogRemovalService(db, privateCacheBase);
        // A fresh service represents restarting PCasa after the root was removed.
        if (!maintenance.HasPendingMaintenance())
            throw new Exception("Deferred cache cleanup was not saved to SQLite.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            maintenance.RunPendingMaintenance(cancelled.Token);
            throw new Exception("Cancelled maintenance was not interrupted.");
        }
        catch (OperationCanceledException) { }
        if (!maintenance.HasPendingMaintenance())
            throw new Exception("Interrupted cleanup lost its durable pending state.");

        if (!maintenance.RunPendingMaintenance())
            throw new Exception("Cleanup did not report its committed cache update.");
        if (maintenance.HasPendingMaintenance())
            throw new Exception("Finished cleanup still has a pending marker.");
        if (File.Exists(orphanThumb) || File.Exists(orphanPack))
            throw new Exception("Deferred cleanup left orphaned cache objects.");
        if (!File.Exists(liveThumb))
            throw new Exception("Deferred cleanup deleted a referenced thumbnail.");
        Require(db, 1);
        if (maintenance.RunPendingMaintenance())
            throw new Exception("Repeated maintenance fabricated new cache changes.");
        Require(db, 1);
    }

    private static void ConcurrentExclusionScan()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "Exclude");
        Directory.CreateDirectory(child);
        var removedPaths = Enumerable.Range(1, 8).Select(i =>
            Path.Combine(child, "photo" + i + ".jpg")).ToArray();
        foreach (var file in removedPaths) WriteJpeg(file);
        WriteJpeg(Path.Combine(sandbox.Images, "keep.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 9);
        var service = new FolderExclusionService(new FolderRepository(db), db);
        var concurrentScan = Task.Run(() => scanner.Scan(root));
        var exclusion = service.ChangeAsync(root.Id, child, true);
        Task.WhenAll(concurrentScan, exclusion).GetAwaiter().GetResult();
        Require(db, 1);
        foreach (var file in removedPaths)
            if (!File.Exists(file))
                throw new Exception("Catalog-only removal deleted an original photo.");
        scanner.Scan(root);
        Require(db, 1);
    }

    private static void RecursiveScanResolvesOldFolderErrors()
    {
        using var sandbox = new Sandbox();
        var tracker = new FolderScanStatusTracker();
        var root = sandbox.Images;
        var child = Path.Combine(root, "Nested");
        var unrelated = root + "-Unrelated";

        tracker.Set(child, "Failed");
        tracker.Set(unrelated, "Failed");
        if (tracker.Get(root)?.Code != "Failed")
            throw new Exception("Nested failure was not reflected in parent status.");

        tracker.Set(root, "Working", recursive: true);
        tracker.Set(root, "Ready", recursive: true);
        if (tracker.Get(root)?.Code != "Ready" ||
            tracker.Get(child)?.Code != "Ready")
            throw new Exception("Successful recursive rescan left a stale warning.");
        if (tracker.Get(unrelated)?.Code != "Failed")
            throw new Exception("Scan cleared failures outside the inspected subtree.");

        tracker.Set(child, "Failed");
        tracker.Set(root, "Working", recursive: false);
        tracker.Set(root, "Ready", recursive: false);
        if (tracker.Get(root)?.Code != "Failed")
            throw new Exception("Shallow scan hid an uninspected child failure.");

        tracker.Forget(child);
        if (tracker.Get(root)?.Code != "Ready")
            throw new Exception("Forgotten subtree left a stale aggregate failure.");
    }

    private static void PendingScanWorkSurvivesCompletion()
    {
        using var sandbox = new Sandbox();
        var tracker = new FolderScanStatusTracker();
        var root = sandbox.Images;
        var child = Path.Combine(root, "NewWork");

        tracker.Set(root, "Pending", recursive: true);
        tracker.Set(root, "Working", recursive: true);
        tracker.Set(child, "Pending", recursive: false);
        tracker.Set(root, "Ready", recursive: true);
        if (tracker.Get(child)?.Code != "Pending" ||
            tracker.Get(root)?.Code != "Pending")
            throw new Exception("Successful parent scan discarded a new child request.");

        tracker.Set(child, "Ready", recursive: false);
        if (tracker.Get(root)?.Code != "Ready")
            throw new Exception("Completed child update left parent pending.");

        tracker.Set(root, "Working", recursive: true);
        tracker.Set(root, "Pending", recursive: true);
        tracker.Set(root, "Ready", recursive: true);
        if (tracker.Get(root)?.Code != "Pending")
            throw new Exception("New request for the currently scanned folder was lost.");

        tracker.Set(root, "Working", recursive: true);
        tracker.Set(root, "Ready", recursive: true);
        if (tracker.Get(root)?.Code != "Ready")
            throw new Exception("Replayed update never returned to Ready.");

        // A shallow scan can receive a new recursive request while running.
        // Its completion must leave the new subtree request visible.
        tracker.Set(root, "Working", recursive: false);
        tracker.Set(root, "Pending", recursive: true);
        tracker.Set(root, "Ready", recursive: false);
        if (tracker.Get(child)?.Code != "Pending")
            throw new Exception("Queued recursive scan lost its subtree scope.");
    }

    private static void Require(LocalDatabase database, int count)
    {
        var actual = new ImageCatalogRepository(database).CountAll();
        if (actual != count)
            throw new Exception($"Expected {count} catalog images, found {actual}.");
    }

    private static void WriteJpeg(string path)
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 75; pixels[i + 1] = 125;
            pixels[i + 2] = 180; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(64, 64, 96, 96,
            PixelFormats.Bgr32, null, pixels, 64 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            "PCasa-CatalogTests-" + Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(_root, "Images");

        public Sandbox() => Directory.CreateDirectory(Images);

        public (LocalDatabase Database, PCasa.App.Models.MonitoredFolder Root, FolderScanner Scanner) Prepare()
        {
            var db = new LocalDatabase(Path.Combine(_root, "test.db"));
            db.Initialize();
            var folders = new FolderRepository(db);
            folders.Add(Images);
            var root = folders.GetAll().Single();
            return (db, root,
                new FolderScanner(new ImageCatalogRepository(db), new ThumbnailCache(), folders,
                    new BrowseThumbnailProfileRepository(db)));
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
