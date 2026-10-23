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
    private static int Main(string[] args)
    {
        if (args.Contains("--exif-probe"))
        {
            try { ExifCatalogRoundTrip(); Console.WriteLine("EXIF PROBE PASS"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("EXIF PROBE FAIL: " + ex); return 1; }
        }
        var failures = new List<string>();
        Run("Scheduler prioritizes queued user work", SchedulerPriorityOrdering, failures);
        Run("Scheduler cancels stale source work", SchedulerRejectsStaleGeneration, failures);
        Run("Scheduler coalesces duplicate pending work", SchedulerCoalescesPendingWork, failures);
        Run("Scheduler cancels queued work on shutdown", SchedulerShutdownCancelsQueuedWork, failures);
        Run("Scheduler does not merge canceled queued work", SchedulerReplacesCanceledPendingWork, failures);
        Run("Scheduler rejects source invalidated during execution", SchedulerRejectsMidFlightInvalidation, failures);
        Run("Running background job yields to user work and resumes", SchedulerYieldsToUserAndResumes, failures);
        Run("Preempted manual scan waits for requested file mutation then resumes", ManualMutationOvertakesYieldingManualScan, failures);
        Run("Interactive work starts while noninterruptible background blocks", SchedulerInteractiveDoesNotWaitForBlockedBackground, failures);
        Run("Viewer work does not wait for blocked catalog", SchedulerViewerReadIsIndependent, failures);
        Run("Catalog reads do not wait behind blocked background scans", SchedulerCatalogReadIsIndependent, failures);
        Run("User scan can start despite uninterruptible background I/O", SchedulerManualScanDoesNotWaitForBlockedBackground, failures);
        Run("Explicit manual scan overtakes blocked initial import", SchedulerManualOvertakesBlockedInitialImport, failures);
        Run("Manual action bypasses blocked background source probe", SchedulerManualBypassesBlockedSourceProbe, failures);
        Run("Canceled queued thumbnail page is re-requestable", CanceledQueuedThumbnailPageCanBeReloaded, failures);
        Run("Root removal proceeds despite noninterruptible scan work", FolderRemovalBypassesBlockedBackground, failures);
        Run("Source locks serialize one photo without globally blocking other photos", SourceFileLocksAreScoped, failures);
        Run("Running user work yields to interactive work", SchedulerInteractivePreemptsUserWork, failures);
        Run("Enqueue does not synchronously check source validity on UI thread", SchedulerEnqueueDoesNotProbeSource, failures);
        Run("Rename reconciles the catalog", RenameMustReconcile, failures);
        Run("Failed image decoding does not block safe rename pruning", DecodeFailureStillReconcilesKnownMissingPaths, failures);
        Run("Excluded subtree has no catalog or gallery entries", ExcludedSubtreeIsRemoved, failures);
        Run("Re-inclusion during pending exclusion must not prune catalog", ReincludeDuringExclusionDoesNotPrune, failures);
        Run("Source generation survives remove and re-add", SourceGenerationSurvivesReAdd, failures);
        Run("User-added empty root is immediately persisted and resumes initial import after restart", NewlyAddedFolderInitialImportSurvivesRestart, failures);
        Run("Folder manager changes are atomic on OK with no staged side effects", FolderManagerSelectionCommitsAtomically, failures);
        Run("Changed exclusion advances source generation", ExclusionAdvancesGeneration, failures);
        Run("Old exclusion cleanup cannot delete newer same-path exclusion", OldExclusionGenerationDoesNotPrune, failures);
        Run("Watcher delete defers cache compaction", WatcherDeletionDefersCacheMaintenance, failures);
        Run("Compacted pack file names are never reused", CompactedPackNamesAreNeverReused, failures);
        Run("Cache compaction yields to manual deletion and retains later requests", MaintenanceCompactionDoesNotBlockManualDeletion, failures);
        Run("Pack append during compaction retains newly indexed bytes", ConcurrentAppendDuringCompactionKeepsBothImages, failures);
        Run("Scan reconciliation defers physical cache compaction", ScanReconciliationDefersCacheMaintenance, failures);
        Run("Manual removal passes slow scan preparation without stale commit", ManualRemovalOvertakesScanReconciliationPreparation, failures);
        Run("Stale scan batch rolls back", StaleScanBatchRollsBack, failures);
        Run("Stale scan cleanup preserves images", StaleScanCleanupPreservesImages, failures);
        Run("Older background scan cannot prune a newer manual scan", OldBackgroundScanDoesNotPruneManualResults, failures);
        Run("Stale watcher delete cannot overwrite newer folder decision", StaleWatcherDeleteIsRejected, failures);
        Run("Stale cache writer cannot overwrite a newer image version", StaleCacheWriteDoesNotReplaceCurrent, failures);
        Run("Stale cache completion cannot clear a newer repair job", StaleCacheCompletionPreservesRepairJob, failures);
        Run("Source change in final scan progress rejects stale pruning", StaleScanFinalProgressRejectsPruning, failures);

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
        Run("JPEG EXIF orientation 6/8 is honored by viewer without altering original", JpegExifOrientationViewer, failures);
        Run("Viewer uses decoded EXIF axes at original pixel size", ViewerDecodedGeometry, failures);
        Run("RAW preview applies container orientation exactly once", RawPreviewOrientation, failures);
        Run("Scanner exposes committed image folders before scan completion", ScannerPublishesFoldersIncrementally, failures);
        Run("RAW camera extensions participate in the existing scan pipeline", RawFormatsRecognized, failures);
        Run("Recent quick tags persist and reorder", RecentQuickTagsPersist, failures);
        Run("EXIF catalog stores complete tags and GPS with no cache rebuild on backfill", ExifCatalogRoundTrip, failures);
        Run("Explorer gallery selection: ctrl, shift range and selected folder Ctrl+A", GalleryExplorerSelection, failures);
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
        Console.WriteLine($"RESULT: catalog regression suite complete, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void SchedulerPriorityOrdering()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var first = scheduler.EnqueueAsync("blocking", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var background = scheduler.EnqueueAsync("later-background", SchedulerPriority.Background,
                _ => { order.Add("background"); return Task.CompletedTask; });
            var user = scheduler.EnqueueAsync("user", SchedulerPriority.UserAction,
                _ => { order.Add("user"); return Task.CompletedTask; });
            release.TrySetResult();
            await Task.WhenAll(first, background, user).WaitAsync(TimeSpan.FromSeconds(5));
            if (!order.SequenceEqual(new[] { "user", "background" }))
                throw new Exception("Queued user action did not outrank background work.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerYieldsToUserAndResumes()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var background = scheduler.EnqueueAsync("long-background",
                SchedulerPriority.Background, async token =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var userRan = false;
            var user = scheduler.EnqueueAsync("user", SchedulerPriority.Interactive,
                _ => { userRan = true; return Task.CompletedTask; });
            await user.WaitAsync(TimeSpan.FromSeconds(5));
            await background.WaitAsync(TimeSpan.FromSeconds(5));
            if (!userRan || attempts != 2 || background.IsCanceled)
                throw new Exception("Background job did not yield and resume.");
            if (scheduler.Snapshot().Single(x => x.Key == "long-background").State
                != SchedulerJobState.Completed)
                throw new Exception("Resumed operation was not recorded as completed.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void ManualMutationOvertakesYieldingManualScan()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var scanStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var order = new List<string>();
            var scan = scheduler.EnqueueAsync("manual-folder-scan",
                SchedulerPriority.ManualAction, async token =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        scanStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    order.Add("scan resumed");
                }, workKind: SchedulerWorkKind.CatalogScan);
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var transfer = scheduler.EnqueueAsync("gallery:file-transfer:must-run-first",
                SchedulerPriority.ManualAction,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    order.Add("file moved");
                    scheduler.ReportProgress("gallery:file-transfer:must-run-first",
                        "Galerie aktualisiert", 1, 1);
                    return Task.CompletedTask;
                }, workKind: SchedulerWorkKind.Catalog);
            await Task.WhenAll(scan, transfer).WaitAsync(TimeSpan.FromSeconds(5));
            if (!order.SequenceEqual(["file moved", "scan resumed"]) || attempts != 2)
                throw new Exception(
                    "The yielded manual scan re-entered ahead of the requested file mutation.");
            var jobs = scheduler.Snapshot();
            var mutation = jobs.Single(x => x.Key == "gallery:file-transfer:must-run-first");
            if (mutation.State != SchedulerJobState.Completed ||
                mutation.Phase != "Galerie aktualisiert")
                throw new Exception("Manual operation status/progress was not authoritative.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerInteractivePreemptsUserWork()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var firstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var user = scheduler.EnqueueAsync("bulk-tag-write", SchedulerPriority.UserAction,
                async token =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        firstRun.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                });
            await firstRun.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var interactiveRan = false;
            var interactive = scheduler.EnqueueAsync("delete-photo", SchedulerPriority.Interactive,
                _ => { interactiveRan = true; return Task.CompletedTask; });
            await Task.WhenAll(user, interactive).WaitAsync(TimeSpan.FromSeconds(5));
            if (!interactiveRan || attempts != 2)
                throw new Exception("Interactive work failed to preempt user bulk work.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerEnqueueDoesNotProbeSource()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync("block", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var probes = 0;
            var first = scheduler.EnqueueAsync("same-generation", SchedulerPriority.Background,
                _ => Task.CompletedTask, () => { Interlocked.Increment(ref probes); return true; });
            var duplicate = scheduler.EnqueueAsync("same-generation", SchedulerPriority.Background,
                _ => throw new Exception("Coalesced work ran twice."));
            if (probes != 0 || !ReferenceEquals(first, duplicate))
                throw new Exception("Enqueue performed a synchronous source probe.");
            release.TrySetResult();
            await Task.WhenAll(blocker, first).WaitAsync(TimeSpan.FromSeconds(5));
            if (probes == 0) throw new Exception("Execution did not validate the source.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerInteractiveDoesNotWaitForBlockedBackground()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = scheduler.EnqueueAsync("blocked-smb", SchedulerPriority.Background,
                async _ => { started.TrySetResult(); await release.Task; });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var ran = false;
                await scheduler.EnqueueAsync("remove-folder", SchedulerPriority.Interactive,
                    _ => { ran = true; return Task.CompletedTask; })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!ran || worker.IsCompleted)
                    throw new Exception("Interactive catalog lane was blocked by background work.");
            }
            finally
            {
                release.TrySetResult();
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerCatalogReadIsIndependent()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanner = scheduler.EnqueueAsync("offline-network-scan", SchedulerPriority.Background,
                async _ =>
                {
                    started.TrySetResult();
                    await release.Task;
                });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var readCompleted = false;
                await scheduler.EnqueueAsync("gallery-catalog-read", SchedulerPriority.Interactive,
                    _ =>
                    {
                        readCompleted = true;
                        return Task.CompletedTask;
                    }, workKind: SchedulerWorkKind.CatalogRead).WaitAsync(TimeSpan.FromSeconds(5));
                if (!readCompleted || scanner.IsCompleted)
                    throw new Exception("UI catalog read waited behind a network scan.");
            }
            finally
            {
                release.TrySetResult();
                await scanner.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerViewerReadIsIndependent()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalog = scheduler.EnqueueAsync("catalog-blocker", SchedulerPriority.Interactive,
                async _ => { started.TrySetResult(); await release.Task; });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var loaded = false;
                await scheduler.EnqueueAsync("viewer-original", SchedulerPriority.Interactive,
                    _ => { loaded = true; return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.ViewerRead).WaitAsync(TimeSpan.FromSeconds(5));
                if (!loaded || catalog.IsCompleted)
                    throw new Exception("Viewer read waited behind unrelated catalog work.");
            }
            finally
            {
                release.TrySetResult();
                await catalog.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void CanceledQueuedThumbnailPageCanBeReloaded()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var startedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = scheduler.EnqueueAsync("occupy-thumbnail-a", SchedulerPriority.UserAction,
                async token =>
                {
                    startedA.TrySetResult();
                    await release.Task.WaitAsync(token);
                }, workKind: SchedulerWorkKind.ThumbnailRead);
            var b = scheduler.EnqueueAsync("occupy-thumbnail-b", SchedulerPriority.UserAction,
                async token =>
                {
                    startedB.TrySetResult();
                    await release.Task.WaitAsync(token);
                }, workKind: SchedulerWorkKind.ThumbnailRead);
            try
            {
                await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(5));
                using var pages = new BrowseThumbnailPageCache(scheduler);
                pages.SetIndex(Enumerable.Range(0, 4096)
                    .Select(i => new BrowseThumbnailIndexEntry(
                        10000 + i, 1, 96, "__pcasa_missing_pack__.pack", 0, 1, 100, 100))
                    .ToArray());
                var old = pages.WarmRangeAsync(0, 31);
                pages.UpdateViewport(3200, 3231, 1); // cancel page 0 while still queued
                release.TrySetResult();
                await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
                await old.WaitAsync(TimeSpan.FromSeconds(10));

                // A canceled pre-start job must remove its placeholder from
                // the same page cache; otherwise a revisit never retries.
                var field = typeof(BrowseThumbnailPageCache).GetField("_pages",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("Expected thumbnail page cache field missing.");
                var entries = field.GetValue(pages) as System.Collections.IDictionary
                    ?? throw new Exception("Thumbnail page storage missing.");
                if (entries.Contains(0))
                    throw new Exception("Canceled pending thumbnail page remained in cache.");
                await pages.WarmRangeAsync(0, 31).WaitAsync(TimeSpan.FromSeconds(10));
                if (!entries.Contains(0))
                    throw new Exception("Canceled thumbnail page cannot be requested again.");
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerManualScanDoesNotWaitForBlockedBackground()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var background = scheduler.EnqueueAsync("blocked-network-enumeration",
                SchedulerPriority.Background,
                async _ => { started.TrySetResult(); await release.Task; });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var manualRan = false;
                await scheduler.EnqueueAsync("manual-folder-scan",
                    SchedulerPriority.UserAction,
                    _ => { manualRan = true; return Task.CompletedTask; })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!manualRan || background.IsCompleted)
                    throw new Exception("Manual scan waited behind non-cancelable background IO.");
            }
            finally
            {
                release.TrySetResult();
                await background.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerManualOvertakesBlockedInitialImport()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initial = scheduler.EnqueueAsync("initial-import:slow-nas",
                SchedulerPriority.UserAction,
                async _ => { started.TrySetResult(); await release.Task; });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var executed = false;
                await scheduler.EnqueueAsync("manual:chosen-directory",
                    SchedulerPriority.ManualAction,
                    _ => { executed = true; return Task.CompletedTask; })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!executed || initial.IsCompleted)
                    throw new Exception("Manual scan waited for blocked initial import.");
            }
            finally
            {
                release.TrySetResult();
                await initial.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerManualBypassesBlockedSourceProbe()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = scheduler.RunAsync("blocked-smb-probe", SchedulerPriority.Background,
                _ =>
                {
                    started.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    return true;
                }, workKind: SchedulerWorkKind.SourceProbe);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var ran = false;
                await scheduler.EnqueueAsync("manual-scan-during-probe", SchedulerPriority.ManualAction,
                    _ => { ran = true; return Task.CompletedTask; })
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!ran || blocked.IsCompleted)
                    throw new Exception("Manual scan was blocked by a slow SMB probe.");
            }
            finally
            {
                release.TrySetResult();
                await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void FolderRemovalBypassesBlockedBackground()
    {
        static async Task Check()
        {
            using var sandbox = new Sandbox();
            var imagePath = Path.Combine(sandbox.Images, "original.jpg");
            WriteJpeg(imagePath);
            var (db, root, scanner) = sandbox.Prepare();
            scanner.Scan(root);
            Require(db, 1);

            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var background = scheduler.EnqueueAsync("network-scan", SchedulerPriority.Background,
                async _ => { started.TrySetResult(); await release.Task; });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await scheduler.EnqueueAsync("remove-root", SchedulerPriority.Interactive,
                    _ =>
                    {
                        new FolderRepository(db).Remove(root.Id);
                        return Task.CompletedTask;
                    }).WaitAsync(TimeSpan.FromSeconds(5));
                Require(db, 0);
                if (!File.Exists(imagePath))
                    throw new Exception("Removing monitored folder touched original file.");
                if (background.IsCompleted)
                    throw new Exception("Test background operation was not actually blocked.");
            }
            finally
            {
                release.TrySetResult();
                await background.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SourceFileLocksAreScoped()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcasa-source-lock-regression");
        var gateA = SourceFileLocks.ForPath(Path.Combine(root, "A.jpg"));
        object? gateB = null;
        for (var index = 0; index < 4096; index++)
        {
            var candidate = SourceFileLocks.ForPath(Path.Combine(root, $"B-{index}.jpg"));
            if (!ReferenceEquals(candidate, gateA)) { gateB = candidate; break; }
        }
        if (gateB is null) throw new Exception("Every source shares one global gate.");

        lock (gateA)
        {
            // Other file paths must remain accessible on another thread.
            var unrelated = Task.Run(() =>
            {
                lock (gateB) { return true; }
            });
            if (!unrelated.Wait(TimeSpan.FromSeconds(5)) || !unrelated.Result)
                throw new Exception("Unrelated image IO blocked behind a source gate.");

            var sameFileEntered = Task.Run(() =>
            {
                var entered = Monitor.TryEnter(gateA);
                if (entered) Monitor.Exit(gateA);
                return entered;
            }).GetAwaiter().GetResult();
            if (sameFileEntered)
                throw new Exception("Concurrent readers/writers of the same image were not serialized.");
        }
    }

    private static void SchedulerRejectsStaleGeneration()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync("block", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = true;
            var executed = false;
            var stale = scheduler.EnqueueAsync("stale", SchedulerPriority.Background,
                _ => { executed = true; return Task.CompletedTask; }, () => current);
            current = false;
            release.TrySetResult();
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
            try { await stale.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TaskCanceledException) { }
            if (executed || !stale.IsCanceled ||
                scheduler.Snapshot().Last().State != SchedulerJobState.Canceled)
                throw new Exception("Stale queued work was not canceled.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerCoalescesPendingWork()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync("block", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var executions = 0;
            var first = scheduler.EnqueueAsync("same-key", SchedulerPriority.Background,
                _ => { executions++; return Task.CompletedTask; });
            var duplicate = scheduler.EnqueueAsync("same-key", SchedulerPriority.UserAction,
                _ => { executions++; return Task.CompletedTask; });
            if (!ReferenceEquals(first, duplicate))
                throw new Exception("Duplicate pending work did not share completion.");
            release.TrySetResult();
            await Task.WhenAll(blocker, first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
            if (executions != 1)
                throw new Exception("Duplicate pending work executed more than once.");
            var record = scheduler.Snapshot().Single(x => x.Key == "same-key");
            if (record.State != SchedulerJobState.Completed ||
                record.Priority != SchedulerPriority.UserAction)
                throw new Exception("Coalesced work did not retain promoted priority.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerShutdownCancelsQueuedWork()
    {
        static async Task Check()
        {
            var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var executed = false;
            var running = scheduler.EnqueueAsync("running", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = scheduler.EnqueueAsync("pending", SchedulerPriority.Background,
                _ => { executed = true; return Task.CompletedTask; });
            await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (executed || !running.IsCanceled || !pending.IsCanceled ||
                scheduler.Snapshot().Any(x => x.State is SchedulerJobState.Running or SchedulerJobState.Queued))
                throw new Exception("Scheduler shutdown did not cancel active and pending jobs.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerReplacesCanceledPendingWork()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync("block", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var oldExecuted = false;
            var newExecuted = false;
            var old = scheduler.EnqueueAsync("folder", SchedulerPriority.Background,
                _ => { oldExecuted = true; return Task.CompletedTask; },
                cancellationToken: cancellation.Token);
            cancellation.Cancel();
            var fresh = scheduler.EnqueueAsync("folder", SchedulerPriority.UserAction,
                _ => { newExecuted = true; return Task.CompletedTask; });
            if (ReferenceEquals(old, fresh))
                throw new Exception("Fresh request was merged into canceled request.");
            release.TrySetResult();
            await Task.WhenAll(blocker, fresh).WaitAsync(TimeSpan.FromSeconds(5));
            try { await old.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TaskCanceledException) { }
            if (oldExecuted || !newExecuted || !old.IsCanceled)
                throw new Exception("Canceled work ran or fresh work was lost.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerRejectsMidFlightInvalidation()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var current = true;
            var job = scheduler.EnqueueAsync("source", SchedulerPriority.Background,
                async token =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(token);
                },
                () => current);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            current = false;
            release.TrySetResult();
            try { await job.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TaskCanceledException) { }
            if (!job.IsCanceled ||
                scheduler.Snapshot().Single(x => x.Key == "source").State != SchedulerJobState.Canceled)
                throw new Exception("Invalidated running source was reported as completed.");
        }
        Check().GetAwaiter().GetResult();
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

    private static void OldExclusionGenerationDoesNotPrune()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "sub");
        Directory.CreateDirectory(child);
        WriteJpeg(Path.Combine(child, "photo.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var folders = new FolderRepository(db);
        var previous = folders.SetExcluded(root.Id, child, true);
        folders.SetExcluded(root.Id, child, false);
        var current = folders.SetExcluded(root.Id, child, true);
        if (previous == current)
            throw new Exception("A repeated exclusion reused its earlier generation.");
        var rejected = false;
        try
        {
            new CatalogRemovalService(db).DetachSubtree(root.Id, child,
                sourceCanonicalPath: root.CanonicalPath, expectedGeneration: previous);
        }
        catch (OperationCanceledException) { rejected = true; }
        if (!rejected) throw new Exception("Stale exclusion cleanup was accepted.");
        Require(db, 1);
        new CatalogRemovalService(db).DetachSubtree(root.Id, child,
            sourceCanonicalPath: root.CanonicalPath, expectedGeneration: current);
        Require(db, 0);
    }

    private static void WatcherDeletionDefersCacheMaintenance()
    {
        using var sandbox = new Sandbox();
        var photo = Path.Combine(sandbox.Images, "photo.jpg");
        WriteJpeg(photo);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var generation = new FolderRepository(db)
            .GetSourceState(root.CanonicalPath)!.Value.Generation;
        var removal = new CatalogRemovalService(db);
        removal.RemovePath(root.Id, photo, sourceCanonicalPath: root.CanonicalPath,
            expectedGeneration: generation, deferCleanup: true);
        Require(db, 0);
        if (!removal.HasPendingMaintenance())
            throw new Exception("Watcher delete did not persist deferred cache cleanup.");
    }

    private static void CompactedPackNamesAreNeverReused()
    {
        // A packed thumbnail reader may hold an open handle to a deleted pack.
        // A new pack must never use that old path for different image bytes.
        var cacheRoot = Path.Combine(Path.GetTempPath(),
            "PCasa-PackIdentity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tiers = new[]
            {
                new PackedThumbnailStore.PreparedThumbnailTier(96,
                    new byte[] { 1, 2, 3 }, 1, 1)
            };
            string original;
            using (var store = new PackedThumbnailStore(cacheRoot))
                original = store.AppendPrepared(tiers).Single().PackFile;
            File.Delete(Path.Combine(cacheRoot, original));
            string replacement;
            using (var store = new PackedThumbnailStore(cacheRoot))
                replacement = store.AppendPrepared(tiers).Single().PackFile;
            if (string.Equals(original, replacement, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Compacted pack file name was recycled while readers could retain its old handle.");
            if (!File.Exists(Path.Combine(cacheRoot, replacement)))
                throw new Exception("New pack data was not created after reclamation.");
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static void ConcurrentAppendDuringCompactionKeepsBothImages()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "first.jpg"));
        WriteJpeg(Path.Combine(sandbox.Images, "second.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);

        var rootDir = Path.Combine(Path.GetDirectoryName(sandbox.Images)!, "ConcurrentPack");
        var packDir = Path.Combine(rootDir, "browse-cache");
        Directory.CreateDirectory(packDir);
        var oldPack = Path.Combine(packDir, "old-pack.pack");
        File.WriteAllBytes(oldPack, new byte[] { 1, 2, 3, 0, 4, 5, 6, 0, 0, 0 });

        long first, second;
        using (var dbConnection = db.OpenConnection())
        using (var cmd = dbConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM images ORDER BY file_name;";
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) throw new Exception("Missing first image.");
                first = reader.GetInt64(0);
                if (!reader.Read()) throw new Exception("Missing second image.");
                second = reader.GetInt64(0);
            }
            cmd.CommandText = """
                UPDATE thumbnail_entries SET pack_file = 'old-pack.pack',
                    byte_offset = 0, byte_length = 3
                WHERE image_id = $id AND tier = 96;
                UPDATE cache_maintenance
                SET cleanup_pending = 1, request_generation = request_generation + 1
                WHERE id = 1;
                """;
            cmd.Parameters.AddWithValue("$id", first);
            cmd.ExecuteNonQuery();
        }

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var maintenance = new CatalogRemovalService(db, rootDir, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Pack append gate timed out.");
        });
        var job = Task.Run(() => maintenance.RunPendingMaintenance());
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Compaction did not observe its first image.");
            // Simulate the second writer appending and committing a valid row
            // to the same original pack while its earlier rows are copied.
            using var dbConnection = db.OpenConnection();
            using var write = dbConnection.CreateCommand();
            write.CommandText = """
                UPDATE thumbnail_entries SET pack_file = 'old-pack.pack',
                    byte_offset = 4, byte_length = 3
                WHERE image_id = $id AND tier = 96;
                """;
            write.Parameters.AddWithValue("$id", second);
            if (write.ExecuteNonQuery() != 1)
                throw new Exception("Concurrent appended image was not indexed.");
        }
        finally { release.Set(); }

        if (!job.GetAwaiter().GetResult())
            throw new Exception("Compaction did not finish.");
        if (!File.Exists(oldPack))
            throw new Exception("Pack containing a concurrent append was deleted.");
        var index = new BrowseCacheRepository(db).GetIndex(96);
        if (index.Count != 2 ||
            !index.Any(entry => entry.ImageId == first &&
                 entry.PackFile.StartsWith("compact-", StringComparison.Ordinal)) ||
            !index.Any(entry => entry.ImageId == second &&
                 entry.PackFile == "old-pack.pack"))
            throw new Exception("Compaction lost or overwrote a concurrently appended entry.");
        if (!maintenance.HasPendingMaintenance())
            throw new Exception("Compaction lost its pending retry marker.");

        // First retry migrates the remaining old entry; the next reclaims
        // the retired pack after the catalog index has been refreshed.
        var retry = new CatalogRemovalService(db, rootDir);
        for (var attempt = 0; attempt < 3 && retry.HasPendingMaintenance(); attempt++)
            retry.RunPendingMaintenance();
        if (retry.HasPendingMaintenance() || File.Exists(oldPack))
            throw new Exception("Compaction failed to converge after a concurrent append.");
        if (new BrowseCacheRepository(db).GetIndex(96).Count != 2)
            throw new Exception("Pack compaction retry changed the valid gallery image count.");
    }

    private static void MaintenanceCompactionDoesNotBlockManualDeletion()
    {
        using var sandbox = new Sandbox();
        var imagePath = Path.Combine(sandbox.Images, "compacting.jpg");
        WriteJpeg(imagePath);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var cacheBase = Path.Combine(Path.GetDirectoryName(sandbox.Images)!, "PrivatePack");
        var packDir = Path.Combine(cacheBase, "browse-cache");
        Directory.CreateDirectory(packDir);
        var oldPack = Path.Combine(packDir, "test-old.pack");
        File.WriteAllBytes(oldPack, new byte[] { 1, 2, 3, 4, 5, 6 });
        long id;
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id FROM images LIMIT 1;
                """;
            id = Convert.ToInt64(command.ExecuteScalar());
            command.CommandText = """
                UPDATE thumbnail_entries
                SET pack_file = 'test-old.pack', byte_offset = 0, byte_length = 3
                WHERE image_id = $id AND tier = 96;
                UPDATE cache_maintenance
                SET cleanup_pending = 1, request_generation = request_generation + 1
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var maintenance = new CatalogRemovalService(db, cacheBase, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Compaction copy gate timed out.");
        });
        var maintenanceTask = Task.Run(() => maintenance.RunPendingMaintenance());
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Pack copy was not reached.");
            var removal = Task.Run(() =>
                new CatalogRemovalService(db).DetachImage(id, imagePath));
            if (!removal.Wait(TimeSpan.FromSeconds(5)) || !removal.Result)
                throw new Exception("Manual image deletion blocked behind pack copying.");
        }
        finally
        {
            release.Set();
        }

        if (!maintenanceTask.GetAwaiter().GetResult())
            throw new Exception("Maintenance stopped without processing its request.");
        if (!maintenance.HasPendingMaintenance())
            throw new Exception("A newer deletion request was wrongly acknowledged by old maintenance.");
        Require(db, 0);
        // A second pass removes now-orphaned packs and acknowledges the
        // request only after the catalog has reached its new desired state.
        var retry = new CatalogRemovalService(db, cacheBase);
        if (!retry.RunPendingMaintenance() || retry.HasPendingMaintenance())
            throw new Exception("Durable maintenance was not completed on retry.");
        if (File.Exists(oldPack))
            throw new Exception("Orphaned original pack was not reclaimed.");
    }

    private static void ManualRemovalOvertakesScanReconciliationPreparation()
    {
        using var sandbox = new Sandbox();
        var photo = Path.Combine(sandbox.Images, "pending.jpg");
        WriteJpeg(photo);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var observed = new GatedObservedPaths(new[] { photo }, entered, release);
        var generation = new FolderRepository(db)
            .GetSourceState(root.CanonicalPath)!.Value.Generation;
        var operation = Task.Run(() => new CatalogRemovalService(db).RemoveMissing(
            root.Id, sandbox.Images, observed,
            sourceCanonicalPath: root.CanonicalPath,
            expectedGeneration: generation));
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Reconciliation did not reach its preparation gate.");
            // Removing a monitored root must not wait for an older scan's
            // potentially huge TEMP-table build under the cache lock.
            var removal = Task.Run(() => new FolderRepository(db).Remove(root.Id));
            if (!removal.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Manual folder removal waited for reconciliation preparation.");
        }
        finally
        {
            release.Set();
        }
        try
        {
            operation.GetAwaiter().GetResult();
            throw new Exception("Outdated scan was permitted to commit after root removal.");
        }
        catch (OperationCanceledException) { }
        Require(db, 0);
    }

    private static void ScanReconciliationDefersCacheMaintenance()
    {
        using var sandbox = new Sandbox();
        var photo = Path.Combine(sandbox.Images, "prune.jpg");
        WriteJpeg(photo);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var maintenance = new CatalogRemovalService(db);
        if (maintenance.HasPendingMaintenance())
            throw new Exception("Initial scan unexpectedly requested cache reclamation.");

        File.Delete(photo);
        var scan = scanner.Scan(root);
        if (scan.Removed != 1 || !scan.CatalogChanged)
            throw new Exception("Reconciliation failed to remove the missing image.");
        Require(db, 0);
        if (!maintenance.HasPendingMaintenance())
            throw new Exception("Scan reconciliation ran synchronous cleanup instead of scheduling idle maintenance.");
        if (new BrowseCacheRepository(db).GetIndex(96).Count != 0)
            throw new Exception("Deferred physical cleanup left a deleted image visible in the gallery.");
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

    private static void DecodeFailureStillReconcilesKnownMissingPaths()
    {
        using var sandbox = new Sandbox();
        var removedPath = Path.Combine(sandbox.Images, "renamed-original.jpg");
        var retained = Path.Combine(sandbox.Images, "valid.jpg");
        WriteJpeg(removedPath);
        WriteJpeg(retained);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);

        // A broken supported file has an observed path; its decoding failure
        // must not prevent removal of unrelated missing catalog rows.
        var broken = Path.Combine(sandbox.Images, "broken.jpg");
        File.WriteAllBytes(broken, new byte[] { 0, 1, 2, 3, 4 });
        File.Delete(removedPath);
        var result = scanner.Scan(root);
        if (result.Failed != 1 || result.Removed != 1)
            throw new Exception($"Failed-image reconciliation: failed={result.Failed}, removed={result.Removed}");
        Require(db, 1);
        using var conn = db.OpenConnection();
        using var query = conn.CreateCommand();
        query.CommandText = "SELECT local_path FROM images";
        if (!string.Equals(query.ExecuteScalar() as string, retained,
            StringComparison.OrdinalIgnoreCase))
            throw new Exception("Old name survived despite complete directory inventory.");
        if (new BrowseCacheRepository(db).GetIndex(96).Any(entry =>
            string.Equals(entry.FileName, "renamed-original.jpg", StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Old name survived in browse index.");
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
    private static void FolderManagerSelectionCommitsAtomically()
    {
        using var sandbox = new Sandbox();
        var a = Path.Combine(sandbox.Images, "A");
        var b = Path.Combine(sandbox.Images, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        WriteJpeg(Path.Combine(a, "a.jpg"));
        WriteJpeg(Path.Combine(b, "b.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        var repository = new FolderRepository(db);
        var childRoot = Path.Combine(Path.GetDirectoryName(sandbox.Images)!, "Separate");
        Directory.CreateDirectory(childRoot);
        WriteJpeg(Path.Combine(childRoot, "new.jpg"));

        var selection = new FolderManagerSelection(
            new[]
            {
                new FolderManagerRootChoice(root.LocalPath, new[] { a }, false),
                new FolderManagerRootChoice(childRoot, Array.Empty<string>(), false)
            }, BackgroundScanEnabled: false);

        // The dialog is an in-memory view model. Constructing/changing it
        // must not mutate SQLite; a canceled commit must be a full rollback.
        if (repository.GetAll().Count != 1 || repository.GetExcludedPaths(root.Id).Count != 0)
            throw new Exception("An unconfirmed folder decision changed the database.");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                new FolderManagerSelectionService(db).Apply(selection, canceled.Token);
                throw new Exception("Canceled folder manager transaction was committed.");
            }
            catch (OperationCanceledException) { }
        }
        Require(db, 2);
        if (repository.GetAll().Count != 1 || repository.GetExcludedPaths(root.Id).Count != 0)
            throw new Exception("Cancel left a partially changed root/exclusion.");

        if (!new FolderManagerSelectionService(db).Apply(selection))
            throw new Exception("Manager OK reported no changes.");
        if (repository.GetAll().Count != 2 ||
            repository.GetExcludedPaths(root.Id).Count != 1 ||
            new ImageCatalogRepository(db).CountAll() != 1)
            throw new Exception("Manager OK did not atomically add the root and prune exclusion.");
        var added = repository.GetAll().Single(f => string.Equals(
            f.LocalPath, childRoot, StringComparison.OrdinalIgnoreCase));
        if (!repository.GetPendingInitialScans().Any(f => f.Id == added.Id))
            throw new Exception("New root was not queued for the scheduler after OK.");
        scanner.Scan(added);
        Require(db, 2);
        if (repository.GetPendingInitialScans().Any(f => f.Id == added.Id))
            throw new Exception("The accepted initial import was not acknowledged.");

        var unchanged = new FolderManagerSelection(
            new[]
            {
                new FolderManagerRootChoice(root.LocalPath, new[] { a }, false),
                new FolderManagerRootChoice(childRoot, Array.Empty<string>(), false)
            }, BackgroundScanEnabled: false);
        if (new FolderManagerSelectionService(db).Apply(unchanged))
            throw new Exception("Reopening and accepting unchanged manager selection mutated the catalog.");
        Require(db, 2);

        var reincluded = new FolderManagerSelection(
            new[]
            {
                new FolderManagerRootChoice(root.LocalPath, Array.Empty<string>(), false),
                new FolderManagerRootChoice(childRoot, Array.Empty<string>(), false)
            }, BackgroundScanEnabled: false);
        if (!new FolderManagerSelectionService(db).Apply(reincluded))
            throw new Exception("Subtree re-inclusion was not persisted.");
        if (!repository.GetPendingInitialScans().Any(f => f.Id == root.Id))
            throw new Exception("Re-included subtree did not request a prioritized root scan.");
        scanner.Scan(root);
        Require(db, 3);
    }

    private static void NewlyAddedFolderInitialImportSurvivesRestart()
    {
        using var sandbox = new Sandbox();
        var (db, root, scanner) = sandbox.Prepare();
        var folders = new FolderRepository(db);

        // A monitored root is durable and discoverable in the folder tree
        // BEFORE even the first image has been scanned or cached.
        if (new FolderRepository(db).GetAll().All(f => f.Id != root.Id) ||
            new ImageCatalogRepository(db).CountAll() != 0 ||
            folders.GetPendingInitialScans().All(f => f.Id != root.Id))
            throw new Exception("Adding an empty root was not instantly persisted with its scan request.");

        // Simulate a shutdown/cancellation before import: only DB state
        // survives, not any in-memory task, timer or enabled watcher.
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                scanner.Scan(root, cancellationToken: canceled.Token);
                throw new Exception("Canceled initial scan was silently accepted.");
            }
            catch (OperationCanceledException) { }
        }
        if (new FolderRepository(db).GetPendingInitialScans().All(f => f.Id != root.Id))
            throw new Exception("Canceled first import lost its durable request.");

        var photo = Path.Combine(sandbox.Images, "first.jpg");
        WriteJpeg(photo);
        var generationBefore = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        scanner.Scan(root);
        Require(db, 1);
        if (new FolderRepository(db).GetPendingInitialScans().Any(f => f.Id == root.Id))
            throw new Exception("Successful full import did not acknowledge its durable request.");

        // Remove and immediately re-add the same path. The old source token
        // cannot acknowledge or overwrite the newer root's pending import.
        folders.Remove(root.Id);
        folders.Add(sandbox.Images);
        var replacement = new FolderRepository(db).GetAll().Single();
        if (replacement.Id == root.Id ||
            new FolderRepository(db).GetPendingInitialScans().All(f => f.Id != replacement.Id))
            throw new Exception("Re-added folder was not independently queued for first import.");
        if (folders.CompleteInitialScan(replacement.Id, generationBefore))
            throw new Exception("Obsolete initial scan acknowledged a newer re-add.");
        if (folders.GetPendingInitialScans().All(f => f.Id != replacement.Id))
            throw new Exception("Old scan erased the new import request.");
        scanner.Scan(replacement);
        Require(db, 1);
        if (new FolderRepository(db).GetPendingInitialScans().Count != 0)
            throw new Exception("Re-added folder import did not converge.");
    }

    private static void SourceGenerationSurvivesReAdd()
    {
        using var sandbox = new Sandbox();
        var (db, root, _) = sandbox.Prepare();
        var folders = new FolderRepository(db);
        (long generation, long present) State()
        {
            using var connection = db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT generation, desired_present
                FROM scheduler_sources WHERE canonical_path = $path;
                """;
            command.Parameters.AddWithValue("$path", root.CanonicalPath);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new Exception("Durable source identity missing.");
            return (reader.GetInt64(0), reader.GetInt64(1));
        }

        var initial = State();
        if (initial != (1L, 1L)) throw new Exception("Unexpected initial source generation.");
        folders.Add(sandbox.Images);
        if (State() != initial) throw new Exception("Duplicate add changed source generation.");
        folders.Remove(root.Id);
        if (State() != (2L, 0L)) throw new Exception("Removal did not fence old work.");
        folders.Add(sandbox.Images);
        if (State() != (3L, 1L)) throw new Exception("Re-add did not advance source generation.");
        db.Initialize();
        if (State() != (3L, 1L)) throw new Exception("Migration reset existing source generation.");
    }

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

    private static void ExclusionAdvancesGeneration()
    {
        using var sandbox = new Sandbox();
        var (db, root, _) = sandbox.Prepare();
        var folders = new FolderRepository(db);
        var child = Path.Combine(sandbox.Images, "Excluded");
        var g1 = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        folders.SetExcluded(root.Id, child, true);
        var g2 = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        if (g2 != g1 + 1) throw new Exception("Exclusion did not invalidate scans.");
        folders.SetExcluded(root.Id, child, true);
        if (folders.GetSourceState(root.CanonicalPath)!.Value.Generation != g2)
            throw new Exception("Duplicate request advanced source generation.");
        folders.SetExcluded(root.Id, child, false);
        if (folders.GetSourceState(root.CanonicalPath)!.Value.Generation != g2 + 1)
            throw new Exception("Re-inclusion did not invalidate scans.");
    }

    private static void StaleScanBatchRollsBack()
    {
        using var sandbox = new Sandbox();
        var (db, root, _) = sandbox.Prepare();
        var folders = new FolderRepository(db);
        var gen = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        using var session = new ImageCatalogRepository(db).CreateScanSession(
            root.CanonicalPath, root.Id, gen);
        folders.SetExcluded(root.Id, Path.Combine(sandbox.Images, "Excluded"), true);
        var rejected = false;
        try
        {
            session.GetOrCreatePhotoFolder(root.Id, sandbox.Images, root.CanonicalPath, "Old");
            session.FlushPending();
        }
        catch (OperationCanceledException) { rejected = true; }
        if (!rejected) throw new Exception("Old scan was allowed to commit.");
        using var dbRead = db.OpenConnection();
        using var command = dbRead.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM photo_folders;";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw new Exception("Stale scan left a committed photo folder.");
    }

    private static void StaleScanCleanupPreservesImages()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "Keep.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var folders = new FolderRepository(db);
        var oldGen = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        folders.SetExcluded(root.Id, Path.Combine(sandbox.Images, "Excluded"), true);
        var rejected = false;
        try
        {
            new CatalogRemovalService(db).RemoveMissing(
                root.Id, sandbox.Images, new HashSet<string>(),
                sourceCanonicalPath: root.CanonicalPath, expectedGeneration: oldGen);
        }
        catch (OperationCanceledException) { rejected = true; }
        if (!rejected) throw new Exception("Old scan was allowed to prune.");
        Require(db, 1);
    }

    private static void StaleCacheCompletionPreservesRepairJob()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "new-version.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        var repo = new BrowseCacheRepository(db);
        var profile = new BrowseThumbnailProfileRepository(db).Get();
        long imageId, oldTicks;
        using (var connection = db.OpenConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, modified_utc_ticks FROM images LIMIT 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new Exception("Test image missing.");
            imageId = reader.GetInt64(0);
            oldTicks = reader.GetInt64(1);
        }
        using (var connection = db.OpenConnection())
        using (var changed = connection.CreateCommand())
        {
            changed.CommandText = "UPDATE images SET modified_utc_ticks = $ticks WHERE id = $id;";
            changed.Parameters.AddWithValue("$ticks", oldTicks + 1);
            changed.Parameters.AddWithValue("$id", imageId);
            changed.ExecuteNonQuery();
        }
        using (var session = repo.CreateWriteSession())
        {
            session.MarkComplete(imageId, oldTicks, profile);
            session.Commit();
        }
        using var verify = db.OpenConnection();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM browse_cache_jobs WHERE image_id = $id;";
        read.Parameters.AddWithValue("$id", imageId);
        if (Convert.ToInt64(read.ExecuteScalar()) != 1)
            throw new Exception("Old cache job cleared the new image's repair request.");
    }

    private static void StaleCacheWriteDoesNotReplaceCurrent()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "versioned.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        var profile = new BrowseThumbnailProfileRepository(db).Get();
        var tier = profile.Tiers[0];
        long imageId, oldTicks;
        string oldPack;
        using (var connection = db.OpenConnection())
        {
            using var image = connection.CreateCommand();
            image.CommandText = "SELECT id, modified_utc_ticks FROM images LIMIT 1;";
            using var reader = image.ExecuteReader();
            if (!reader.Read()) throw new Exception("Test image missing.");
            imageId = reader.GetInt64(0);
            oldTicks = reader.GetInt64(1);
        }
        using (var connection = db.OpenConnection())
        {
            using var indexed = connection.CreateCommand();
            indexed.CommandText = "SELECT pack_file FROM thumbnail_entries WHERE image_id = $id AND tier = $tier;";
            indexed.Parameters.AddWithValue("$id", imageId);
            indexed.Parameters.AddWithValue("$tier", tier);
            oldPack = indexed.ExecuteScalar() as string
                ?? throw new Exception("Test thumbnail entry missing.");
            using var changed = connection.CreateCommand();
            changed.CommandText = "UPDATE images SET modified_utc_ticks = $ticks WHERE id = $id;";
            changed.Parameters.AddWithValue("$ticks", oldTicks + 1);
            changed.Parameters.AddWithValue("$id", imageId);
            changed.ExecuteNonQuery();
        }
        using (var session = new BrowseCacheRepository(db).CreateWriteSession())
        {
            session.Save(imageId, oldTicks, new PackedThumbnailLocation(
                tier, "stale-pack.pack", 0, 100, 100, 100));
            session.Commit();
        }
        using (var connection = db.OpenConnection())
        using (var indexed = connection.CreateCommand())
        {
            indexed.CommandText = "SELECT pack_file FROM thumbnail_entries WHERE image_id = $id AND tier = $tier;";
            indexed.Parameters.AddWithValue("$id", imageId);
            indexed.Parameters.AddWithValue("$tier", tier);
            if (!string.Equals(indexed.ExecuteScalar() as string, oldPack,
                    StringComparison.Ordinal))
                throw new Exception("Old image source overwrote a newer cache entry.");
        }
    }

    private static void OldBackgroundScanDoesNotPruneManualResults()
    {
        static async Task Check()
        {
            using var sandbox = new Sandbox();
            var first = Path.Combine(sandbox.Images, "first.jpg");
            var second = Path.Combine(sandbox.Images, "second.jpg");
            WriteJpeg(first);
            var (db, root, backgroundScanner) = sandbox.Prepare();
            backgroundScanner.Scan(root);
            Require(db, 1);

            var pausedAtReconciliation = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var finalProgress = new InlineProgress<ScanProgress>(scan =>
            {
                if (scan.CurrentFile is not null) return;
                pausedAtReconciliation.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            });
            var old = Task.Run(() => backgroundScanner.Scan(root, finalProgress));
            try
            {
                await pausedAtReconciliation.Task.WaitAsync(TimeSpan.FromSeconds(15));
                WriteJpeg(second);
                var folders = new FolderRepository(db);
                var previousGeneration = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
                var newerGeneration = folders.BeginManualScan(root.CanonicalPath);
                if (newerGeneration != previousGeneration + 1)
                    throw new Exception("Manual scan did not advance source generation.");
                var manualScanner = new FolderScanner(
                    new ImageCatalogRepository(db), new ThumbnailCache(),
                    folders, new BrowseThumbnailProfileRepository(db));
                manualScanner.Scan(root);
                Require(db, 2);
            }
            finally
            {
                release.TrySetResult();
            }

            var rejected = false;
            try { await old.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (OperationCanceledException) { rejected = true; }
            if (!rejected) throw new Exception("Older background scan was allowed to reconcile.");
            Require(db, 2);
        }
        Check().GetAwaiter().GetResult();
    }

    private static void StaleWatcherDeleteIsRejected()
    {
        using var sandbox = new Sandbox();
        var file = Path.Combine(sandbox.Images, "keep.jpg");
        WriteJpeg(file);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var folders = new FolderRepository(db);
        var generation = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        folders.SetExcluded(root.Id, Path.Combine(sandbox.Images, "Other"), true);
        var rejected = false;
        try
        {
            new CatalogRemovalService(db).RemovePath(root.Id, file,
                sourceCanonicalPath: root.CanonicalPath, expectedGeneration: generation);
        }
        catch (OperationCanceledException) { rejected = true; }
        if (!rejected) throw new Exception("Stale watcher work deleted catalog data.");
        Require(db, 1);
    }

    private sealed class InlineProgress<T>(Action<T> onProgress) : IProgress<T>
    {
        public void Report(T value) => onProgress(value);
    }

    private static void StaleScanFinalProgressRejectsPruning()
    {
        using var sandbox = new Sandbox();
        var deleted = Path.Combine(sandbox.Images, "deleted.jpg");
        var retained = Path.Combine(sandbox.Images, "retained.jpg");
        WriteJpeg(deleted);
        WriteJpeg(retained);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 2);
        File.Delete(deleted);
        var folders = new FolderRepository(db);
        var changes = 0;
        var callback = new InlineProgress<ScanProgress>(p =>
        {
            if (p.CurrentFile is not null) return;
            folders.SetExcluded(root.Id, Path.Combine(sandbox.Images, "Other"), true);
            changes++;
        });
        var canceled = false;
        try { scanner.Scan(root, callback); }
        catch (OperationCanceledException) { canceled = true; }
        if (!canceled || changes != 1)
            throw new Exception("Final scan boundary failed to reject obsolete work.");
        Require(db, 2);
        scanner.Scan(root);
        Require(db, 1);
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

    private static void RawFormatsRecognized()
    {
        foreach (var extension in new[]
            { ".ARW", ".nef", ".NRW", ".cr2", ".cr3", ".DNG", ".RAF",
              ".RW2", ".orf", ".pef", ".srw", ".crw" })
        {
            var image = Path.Combine(Path.GetTempPath(), "camera" + extension);
            if (!RawImageSource.IsRawPath(image) ||
                !FolderScanner.IsSupportedImagePath(image))
                throw new Exception("Camera RAW not accepted by central scanner: " + extension);
        }
        if (RawImageSource.IsRawPath("photo.jpg") ||
            RawImageSource.Resolve("photo.jpg") != "photo.jpg")
            throw new Exception("RAW source handler changed JPEG code path.");
    }

    private static void ScannerPublishesFoldersIncrementally()
    {
        using var sandbox = new Sandbox();
        var first = Path.Combine(sandbox.Images, "01-First");
        var second = Path.Combine(sandbox.Images, "02-Second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteJpeg(Path.Combine(first, "photo1.jpg"));
        WriteJpeg(Path.Combine(second, "photo2.jpg"));
        var (db, root, scanner) = sandbox.Prepare();

        using var cancel = new CancellationTokenSource();
        ScanProgress? discovered = null;
        var callback = new InlineProgress<ScanProgress>(progress =>
        {
            if (progress.DiscoveredPhotoFolderId is not long folderId) return;
            if (discovered is not null) return;
            discovered = progress;

            // The directory event is published only after the first image
            // and its thumbnail index have committed. This is before the
            // scan has enumerated the second directory.
            var indexed = new ImageCatalogRepository(db).GetPhotoFolders();
            if (!indexed.Any(folder => folder.Id == folderId &&
                string.Equals(folder.LocalPath,
                    progress.DiscoveredPhotoFolderPath,
                    StringComparison.OrdinalIgnoreCase) && folder.ImageCount > 0))
                throw new Exception("Folder progress event preceded catalog commit.");
            if (new ImageCatalogRepository(db).CountAll() != 1)
                throw new Exception("Folder event was not incremental.");
            cancel.Cancel();
        });
        try
        {
            scanner.Scan(root, callback, cancel.Token);
            throw new Exception("Scanner ignored cancellation after first directory.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        if (discovered is not { DiscoveredPhotoFolderId: > 0 } ||
            new ImageCatalogRepository(db).CountAll() != 1)
            throw new Exception("New directory was not visible before completion.");

        var subsequent = new List<string>();
        var complete = new InlineProgress<ScanProgress>(p =>
        {
            if (p.DiscoveredPhotoFolderPath is string path)
                subsequent.Add(path);
        });
        scanner.Scan(root, complete);
        if (new ImageCatalogRepository(db).CountAll() != 2 ||
            !subsequent.Contains(second, StringComparer.OrdinalIgnoreCase))
            throw new Exception("Subsequent scan did not publish newly discovered sibling.");
    }

    private static void ViewerDecodedGeometry()
    {
        // The Samsung original is stored 4032 x 3024 but EXIF 6 yields a
        // 3024 x 4032 portrait. The bitmap and viewer surface must have the
        // same aspect; Fit, 100% and the navigator use corrected dimensions.
        if (!ViewerImageGeometry.NeedsAxisSwap(4032, 3024, 3024, 4032))
            throw new Exception("EXIF 90-degree axes were not recognized.");
        if (ViewerImageGeometry.NeedsAxisSwap(4032, 3024, 4032, 3024))
            throw new Exception("Normal landscape was accidentally rotated.");
        if (ViewerImageGeometry.NeedsAxisSwap(4032, 3024, 1024, 768))
            throw new Exception("Downsampled landscape was accidentally rotated.");
        if (!ViewerImageGeometry.NeedsAxisSwap(4032, 3024, 384, 512))
            throw new Exception("Packed portrait preview was not recognized.");
        if (ViewerImageGeometry.NeedsAxisSwap(1000, 1000, 64, 64))
            throw new Exception("Square image must not swap axes.");
    }

    private static void RawPreviewOrientation()
    {
        using var sandbox = new Sandbox();
        // Rotation is pixel geometry. Use a valid JPEG as a reproducible
        // unrotated preview, then apply the very same transform used for RAW.
        // A true Sony/Nikon binary is still needed to verify camera tags.
        var original = Path.Combine(sandbox.Images, "embedded-preview.jpg");
        const int width = 96, height = 48;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 50;
            pixels[i + 1] = 100;
            pixels[i + 2] = 180;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(original)) encoder.Save(output);
        var sha = SHA256.HashData(File.ReadAllBytes(original));
        foreach (var orientation in new[] { 1, 3, 6, 8 })
        {
            using var unrotated = NetVips.Image.Thumbnail(original, 256,
                height: 256, size: NetVips.Enums.Size.Down, noRotate: true);
            using var oriented = RawImageSource.ApplyOrientation(unrotated, orientation);
            var swap = orientation is 6 or 8;
            if (oriented.Width != (swap ? height : width) ||
                oriented.Height != (swap ? width : height))
                throw new Exception(
                    $"RAW preview EXIF {orientation}: {oriented.Width}x{oriented.Height} was not correctly rotated.");
            if (!sha.SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))))
                throw new Exception("RAW preview orientation modified its source.");
        }
    }

    private static void JpegExifOrientationViewer()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "original-exif.jpg");
        const int originalWidth = 96, originalHeight = 48;
        var pixels = new byte[originalWidth * originalHeight * 4];
        for (var y = 0; y < originalHeight; y++)
        for (var x = 0; x < originalWidth; x++)
        {
            var p = (y * originalWidth + x) * 4;
            pixels[p] = x < originalWidth / 2 ? (byte)20 : (byte)200;
            pixels[p + 1] = y < originalHeight / 2 ? (byte)50 : (byte)170;
            pixels[p + 2] = 110;
            pixels[p + 3] = 255;
        }
        var source = BitmapSource.Create(originalWidth, originalHeight, 96, 96,
            PixelFormats.Bgr32, null, pixels, originalWidth * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using (var stream = File.Create(path)) encoder.Save(stream);

        foreach (var orientation in new[] { 1, 3, 6, 8 })
        {
            RunExifTool("-overwrite_original", $"-EXIF:Orientation#={orientation}", path);
            var shaBefore = SHA256.HashData(File.ReadAllBytes(path));
            using (var input = File.OpenRead(path))
            {
                if (ExifOrientation.Read(input) != orientation)
                    throw new Exception($"EXIF Orientation={orientation} was not read from JPEG.");
            }
            var info = new PhotoViewerImageInfo(1, path, Path.GetFileName(path),
                originalWidth, originalHeight);
            var actual = PhotoViewerImageLoader.Load(info, 4096,
                CancellationToken.None);
            var expectedWidth = orientation is 6 or 8
                ? originalHeight : originalWidth;
            var expectedHeight = orientation is 6 or 8
                ? originalWidth : originalHeight;
            if (actual.PixelWidth != expectedWidth ||
                actual.PixelHeight != expectedHeight)
                throw new Exception(
                    $"Orientation={orientation}: Viewer {actual.PixelWidth}x{actual.PixelHeight}, expected {expectedWidth}x{expectedHeight}.");
            if (!shaBefore.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))))
                throw new Exception("Viewing a JPEG unexpectedly changed source bytes.");
        }
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

    private static void ExifCatalogRoundTrip()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "gps-camera.jpg");
        WriteJpeg(path);
        RunExifTool("-overwrite_original",
            "-EXIF:Make=Sony",
            "-EXIF:Model=ILCE-7M4",
            "-EXIF:DateTimeOriginal=2024:07:06 05:04:03",
            "-EXIF:GPSLatitude=53.5", "-EXIF:GPSLatitudeRef=N",
            "-EXIF:GPSLongitude=10.25", "-EXIF:GPSLongitudeRef=E",
            "-EXIF:Artist=Test Photographer", path);
        var (db, root, scanner) = sandbox.Prepare();
        var result = scanner.Scan(root);
        if (result.Failed != 0 || result.Updated != 1)
            throw new Exception("Source with EXIF was not indexed successfully.");
        using var conn = db.OpenConnection();
        static (long Id, string Cache, string? Json, string? Make, string? Model,
            double? Lat, double? Lon) Read(Microsoft.Data.Sqlite.SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT i.id, i.thumbnail_path, x.exif_json, x.camera_make,
                    x.camera_model, x.gps_latitude, x.gps_longitude
                FROM images i LEFT JOIN image_exif x ON x.image_id = i.id
                WHERE i.file_name = 'gps-camera.jpg';
                """;
            using var row = cmd.ExecuteReader();
            if (!row.Read()) throw new Exception("Missing EXIF catalog image.");
            return (row.GetInt64(0), row.GetString(1),
                row.IsDBNull(2) ? null : row.GetString(2),
                row.IsDBNull(3) ? null : row.GetString(3),
                row.IsDBNull(4) ? null : row.GetString(4),
                row.IsDBNull(5) ? null : row.GetDouble(5),
                row.IsDBNull(6) ? null : row.GetDouble(6));
        }
        var before = Read(conn);
        var browseIndex = new BrowseCacheRepository(db).GetIndex(192);
        var indexed = browseIndex.FirstOrDefault(image => image.ImageId == before.Id)
            ?? throw new Exception("Imported image not in thumbnail catalog.");
        if (!indexed.HasGps || indexed.HasTags || indexed.IsRaw ||
            indexed.FileName != "gps-camera.jpg")
            throw new Exception("Thumbnail labels/GPS flags disagree with EXIF catalog.");
        if (before.Make != "Sony" || before.Model != "ILCE-7M4" ||
            Math.Abs((before.Lat ?? 0) - 53.5) > 0.001 ||
            Math.Abs((before.Lon ?? 0) - 10.25) > 0.001 ||
            before.Json is null || !before.Json.Contains("Test Photographer") ||
            !before.Json.Contains("DateTimeOriginal"))
            throw new Exception($"Camera/GPS/full EXIF tags were not preserved: make={before.Make}, model={before.Model}, lat={before.Lat}, lon={before.Lon}, raw={before.Json}");
        // Simulate upgrading an old catalog: strip ONLY metadata, never cache.
        using (var remove = conn.CreateCommand())
        {
            remove.CommandText = "DELETE FROM image_exif WHERE image_id = $id;";
            remove.Parameters.AddWithValue("$id", before.Id);
            remove.ExecuteNonQuery();
        }
        result = scanner.Scan(root);
        var after = Read(conn);
        if (result.Updated != 0 || after.Id != before.Id ||
            after.Cache != before.Cache || after.Json is null ||
            after.Make != "Sony" || Math.Abs((after.Lat ?? 0) - 53.5) > 0.001)
            throw new Exception("Legacy EXIF backfill unexpectedly rebuilt thumbnails or changed identity.");
        using (var missing = conn.CreateCommand())
        {
            missing.CommandText = "SELECT COUNT(*) FROM image_exif WHERE image_id=$id;";
            missing.Parameters.AddWithValue("$id", before.Id);
            if (Convert.ToInt32(missing.ExecuteScalar()) != 1)
                throw new Exception("Backfill failed to create exactly one EXIF row.");
        }
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

    private static void GalleryExplorerSelection()
    {
        var model = new GallerySelection();
        var visible = Enumerable.Range(0, 8).Select(i =>
            new BrowseThumbnailIndexEntry(i + 1, i < 5 ? 10 : 20,
                192, "test.pack", 0, 100, 96, 96)).ToArray();
        var selected = new HashSet<long>();
        model.Click(2, System.Windows.Input.ModifierKeys.None, visible, selected);
        model.Click(4, System.Windows.Input.ModifierKeys.Control, visible, selected);
        if (!GallerySelection.PreserveGroupUntilDragDecision(2,
                System.Windows.Input.ModifierKeys.None, selected) ||
            !GallerySelection.PreserveGroupUntilDragDecision(2,
                System.Windows.Input.ModifierKeys.Control, selected) ||
            GallerySelection.PreserveGroupUntilDragDecision(3,
                System.Windows.Input.ModifierKeys.None, selected))
            throw new Exception("Selected-photo CTRL drag collapsed selection before drag threshold.");
        if (!selected.SetEquals(new long[] { 2, 4 }))
            throw new Exception("Ctrl-click did not toggle the clicked image.");
        model.Click(5, System.Windows.Input.ModifierKeys.Shift, visible, selected);
        if (!selected.SetEquals(new long[] { 2, 3, 4, 5 }))
            throw new Exception("Shift did not select anchored contiguous range.");
        model.Click(3, System.Windows.Input.ModifierKeys.Control |
            System.Windows.Input.ModifierKeys.Shift, visible, selected);
        if (!selected.SetEquals(new long[] { 2, 3, 4, 5 }))
            throw new Exception("Ctrl+Shift removed photos from an existing selection.");
        model.Click(2, System.Windows.Input.ModifierKeys.None, visible, selected);
        model.Click(7, System.Windows.Input.ModifierKeys.Control, visible, selected);
        model.Click(4, System.Windows.Input.ModifierKeys.Control |
            System.Windows.Input.ModifierKeys.Shift, visible, selected);
        if (!selected.SetEquals(new long[] { 2, 3, 4, 7 }))
            throw new Exception("Ctrl+Shift did not add its range to the selection.");
        model.SelectFolder(20, visible, selected);
        if (!selected.SetEquals(new long[] { 6, 7, 8 }) || model.FocusedId != 6)
            throw new Exception("Ctrl+A selected images from an unrelated folder.");
        model.Click(3, System.Windows.Input.ModifierKeys.Shift, visible, selected);
        if (!selected.SetEquals(new long[] { 3, 4, 5, 6 }))
            throw new Exception("Shift range did not span adjacent visible photo folders.");
        model.Click(4, System.Windows.Input.ModifierKeys.None, visible, selected);
        var moved = model.MoveFocus(-1, true, visible, selected);
        if (moved != 3 || !selected.SetEquals(new long[] { 3, 4 }))
            throw new Exception("Shift-arrow did not extend selection by one photo.");
        model.Click(5, System.Windows.Input.ModifierKeys.None, visible, selected);
        moved = model.MoveFocus(1, false, visible, selected);
        if (moved != 6 || !selected.SetEquals(new long[] { 6 }))
            throw new Exception("Arrow navigation stopped at the next photo folder.");
        moved = model.MoveFocus(-1, true, visible, selected);
        if (moved != 5 || !selected.SetEquals(new long[] { 5, 6 }))
            throw new Exception("Shift-arrow failed across a photo folder boundary.");

        var withHidden = Enumerable.Range(0, 8).Select(i =>
            new BrowseThumbnailIndexEntry(i + 1,
                i < 3 ? 10 : i < 5 ? 20 : 30,
                192, "test.pack", 0, 100, 96, 96)).ToArray();
        var hidden = new HashSet<long> { 20 };
        model.Click(2, System.Windows.Input.ModifierKeys.None,
            withHidden, selected, hidden);
        model.Click(8, System.Windows.Input.ModifierKeys.Shift,
            withHidden, selected, hidden);
        if (!selected.SetEquals(new long[] { 2, 3, 6, 7, 8 }))
            throw new Exception("Shift range selected photos from collapsed folders.");
        model.Click(3, System.Windows.Input.ModifierKeys.None,
            withHidden, selected, hidden);
        moved = model.MoveFocus(1, false, withHidden, selected, hidden);
        if (moved != 6 || !selected.SetEquals(new long[] { 6 }))
            throw new Exception("Arrow navigation stopped in a collapsed photo folder.");
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

        gallery.SetThumbnailSpacing(160);
        gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));
        if (Math.Abs((double)typeof(BrowseGallerySurface).GetField("_rowGap",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(gallery)! - 160) > 0.01)
            throw new Exception("Large spacing setting did not change vertical spacing.");

        gallery.EndSpacingPreview();
        // No filename caption: the gap between uniform landscape photo rows
        // must be exactly the same as the chosen horizontal minimum.
        gallery.SetThumbnailAnnotations(false, true);
        gallery.SetThumbnailSpacing(25);
        gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));
        var captionFreeSections = (System.Collections.IEnumerable)typeof(BrowseGallerySurface)
            .GetField("_sections", System.Reflection.BindingFlags.NonPublic |
                                   System.Reflection.BindingFlags.Instance)!
            .GetValue(gallery)!;
        var first = captionFreeSections.Cast<object>().First();
        var offsets = (double[])first.GetType().GetProperty("RowOffsets")!
            .GetValue(first)!;
        var photoHeights = (double[])first.GetType().GetProperty("RowImageHeights")!
            .GetValue(first)!;
        if (Math.Abs(offsets[1] - offsets[0] - photoHeights[0] - 25) > 0.05)
            throw new Exception("Row spacing without captions is not the configured spacing.");
        gallery.SetThumbnailSpacing(160);
        gallery.Measure(new System.Windows.Size(970, double.PositiveInfinity));
        gallery.Arrange(new System.Windows.Rect(0, 0, 970, gallery.DesiredSize.Height));

        // The live slider's value is BOTH the row spacing and the actual
        // MINIMUM between consecutive image columns. Excess horizontal width
        // is distributed; it is never subtracted from the minimum.
        var flags = System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        var rows = (double)typeof(BrowseGallerySurface)
            .GetField("_rowGap", flags)!.GetValue(gallery)!;
        if (Math.Abs(rows - 160) > 0.01)
            throw new Exception("Vertical spacing does not match the slider.");
        var sections = (System.Collections.IEnumerable)typeof(BrowseGallerySurface)
            .GetField("_sections", flags)!.GetValue(gallery)!;
        var firstSection = sections.Cast<object>().First();
        var columns = (int)firstSection.GetType().GetProperty("ColumnCount")!
            .GetValue(firstSection)!;
        if (columns < 2)
            throw new Exception("Spacing regression cannot measure two columns.");
        var getLeft = typeof(BrowseGallerySurface)
            .GetMethod("ImageColumnLeft", flags)!;
        var left0 = (double)getLeft.Invoke(gallery, [firstSection, 0])!;
        var left1 = (double)getLeft.Invoke(gallery, [firstSection, 1])!;
        var horizontalGap = left1 - left0 - gallery.ThumbnailWidth;
        if (horizontalGap < 159.9)
            throw new Exception($"Horizontal minimum spacing lost: {horizontalGap:F1}px.");
        Console.WriteLine("PASS: Horizontal minimum and vertical spacing match slider.");

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
        // Fresh orphan files can still belong to an in-flight encoder.
        File.SetLastWriteTimeUtc(orphanThumb, DateTime.UtcNow.AddHours(-1));
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
        try { Task.WhenAll(concurrentScan, exclusion).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { /* Old scan was invalidated. */ }
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

    private sealed class GatedObservedPaths : IReadOnlySet<string>
    {
        private readonly HashSet<string> _paths;
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public GatedObservedPaths(IEnumerable<string> paths,
            ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _paths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            _entered = entered;
            _release = release;
        }

        public int Count => _paths.Count;
        public bool Contains(string item) => _paths.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<string> other) => _paths.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => _paths.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => _paths.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => _paths.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => _paths.Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => _paths.SetEquals(other);

        public IEnumerator<string> GetEnumerator()
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Reconciliation gate was never released.");
            return _paths.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
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
