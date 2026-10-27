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
    private static readonly Lazy<PCasaScheduler> ExclusionTestScheduler =
        new(() => new PCasaScheduler());

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--exif-probe"))
        {
            try { ExifCatalogRoundTrip(); Console.WriteLine("EXIF PROBE PASS"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("EXIF PROBE FAIL: " + ex); return 1; }
        }
        if (args.Contains("--tag-write-probe"))
        {
            try
            {
                JpegTagRoundTrip();
                JpegUnicodeTagPreservesPixels();
                JpegTagTemporaryOutputsAreCleaned();
                ExifToolOwnedWritersTerminate();
                Console.WriteLine("TAG WRITE PROBE PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TAG WRITE PROBE FAIL: " + ex);
                return 1;
            }
        }
        var failures = new List<string>();
        Run("Scheduler prioritizes queued user work", SchedulerPriorityOrdering, failures);
        Run("Durable scheduler journal preserves original priority and LIFO order", DurableSchedulerPrioritySurvivesRestart, failures);
        Run("Durable package resumes saved step after process restart", DurableSchedulerCursorSurvivesRestart, failures);
        Run("Durable gallery transfer receipt survives restart and rejects changed identity", DurableGalleryTransferReceiptSurvivesRestart, failures);
        Run("Temporary transfer failure remains recoverable at manual priority", DurableTransferRetryPreservesPriority, failures);
        Run("Durable packages reject non-reconstructable dynamic steps", DurablePackageRejectsDynamicSteps, failures);
        Run("Scheduler cancels stale source work", SchedulerRejectsStaleGeneration, failures);
        Run("Scheduler coalesces duplicate pending work", SchedulerCoalescesPendingWork, failures);
        Run("Scheduler cancels queued work on shutdown", SchedulerShutdownCancelsQueuedWork, failures);
        Run("Scheduler does not merge canceled queued work", SchedulerReplacesCanceledPendingWork, failures);
        Run("Scheduler rejects source invalidated during execution", SchedulerRejectsMidFlightInvalidation, failures);
        Run("Running background job yields to user work and resumes", SchedulerYieldsToUserAndResumes, failures);
        Run("Preempted manual scan waits for requested file mutation then resumes", ManualMutationOvertakesYieldingManualScan, failures);
        Run("Newest manual package runs first, older packages resume at their saved step", DynamicPackagesResumeLifo, failures);
        Run("Newer manual source probes hold older manual package resumption", NewerManualProbeSuspendsOlderPackage, failures);
        Run("Canceled scanner resumes enumeration instead of starting again", ScannerCheckpointRetainsEnumeration, failures);
        Run("Durable first-import checkpoints survive restart without unsafe pruning", DurableInitialImportRestartSafe, failures);
        Run("Restart resumes within unfinished directory after last committed image", DurableFileCursorRestartSafe, failures);
        Run("Changed monitoring invalidates old scan without implicit restart", SourceChangeRejectsOldScanCheckpoint, failures);
        Run("Yield while waiting for image lock does not count same file twice", ScannerYieldBeforeSourceLockDoesNotDoubleCount, failures);
        Run("Newer scans preserve older checkpoints without stale pruning", ScanEpochSupersessionRetainsCursor, failures);
        Run("Canceled source-lock waiter releases its scheduler lane", SourceLockWaitCanYield, failures);
        Run("Two-endpoint source locks serialize opposite transfers", PairedSourceLocksAreOrdered, failures);
        Run("Interactive work starts while noninterruptible background blocks", SchedulerInteractiveDoesNotWaitForBlockedBackground, failures);
        Run("Viewer work does not wait for blocked catalog", SchedulerViewerReadIsIndependent, failures);
        Run("Catalog reads do not wait behind blocked background scans", SchedulerCatalogReadIsIndependent, failures);
        Run("User scan can start despite uninterruptible background I/O", SchedulerManualScanDoesNotWaitForBlockedBackground, failures);
        Run("Explicit manual scan overtakes blocked initial import", SchedulerManualOvertakesBlockedInitialImport, failures);
        Run("Manual and interactive source probes bypass blocked background probes", SchedulerManualBypassesBlockedSourceProbe, failures);
        Run("Read-only folder-tree probe does not stop durable background image import", ReadOnlyFolderProjectionNeverBlocksInitialImport, failures);
        Run("Canceled all-folders toggle immediately retires queued scheduler probe", CanceledAllFoldersScopeRetiresQueuedProbe, failures);
        Run("Manual folder creation preempts and safely resumes network scan", SchedulerManualFolderCreateYieldsScan, failures);
        Run("Canceled queued thumbnail page is re-requestable", CanceledQueuedThumbnailPageCanBeReloaded, failures);
        Run("Root removal proceeds despite noninterruptible scan work", FolderRemovalBypassesBlockedBackground, failures);
        Run("Source locks serialize one photo without globally blocking other photos", SourceFileLocksAreScoped, failures);
        Run("Running user work yields to interactive work", SchedulerInteractivePreemptsUserWork, failures);
        Run("Enqueue does not synchronously check source validity on UI thread", SchedulerEnqueueDoesNotProbeSource, failures);
        Run("Rename reconciles the catalog", RenameMustReconcile, failures);
        Run("Failed image decoding does not block safe rename pruning", DecodeFailureStillReconcilesKnownMissingPaths, failures);
        Run("Excluded subtree has no catalog or gallery entries", ExcludedSubtreeIsRemoved, failures);
        Run("Re-inclusion after atomic exclusion restores original catalog through scan", ReincludeAfterExclusionRestoresCatalogOnScan, failures);
        Run("Source generation survives remove and re-add", SourceGenerationSurvivesReAdd, failures);
        Run("User-added empty root is immediately persisted and resumes initial import after restart", NewlyAddedFolderInitialImportSurvivesRestart, failures);
        Run("Folder manager changes are atomic on OK with no staged side effects", FolderManagerSelectionCommitsAtomically, failures);
        Run("Changed exclusion advances source generation", ExclusionAdvancesGeneration, failures);
        Run("Nested monitored root owns exclusion decision", NestedMonitoredRootOwnsExclusion, failures);
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
        Run("Per-folder gallery sorting preserves tree order for all six sort fields", PerFolderSortAllFields, failures);
        Run("Gallery sorting reads EXIF capture date, file dates, size and original dimensions from one index", GallerySortUsesCatalogMetadata, failures);
        Run("Moved images reuse their decoded pixels without blanking unrelated folders", ChangedIndexRetainsUnmodifiedImages, failures);
        Run("Targeted image lookup and folder counts match full catalog", SingleImageCatalogProjectionMatchesFullIndex, failures);
        Run("Exclusion service removes and re-includes folder", ExclusionServiceRoundTrip, failures);
        Run("Exclusion and catalog removal rollback atomically", ExclusionCommitRollback, failures);
        Run("Restart repairs legacy split-phase exclusions", LegacyExclusionRecovery, failures);
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
        if (ExclusionTestScheduler.IsValueCreated)
            ExclusionTestScheduler.Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine($"RESULT: catalog regression suite complete, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void DurablePackageRejectsDynamicSteps()
    {
        var package = new SchedulerTaskPackage("test:durable:dynamic");
        package.AddStep(_ => Task.CompletedTask);
        // Production owns this internal callback; bind it reflectively here
        // instead of widening the scheduler's public API just for a test.
        typeof(SchedulerTaskPackage).GetProperty("OnStepCompleted",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)!
            .SetValue(package, (Action<int>)(_ => { }));
        var rejected = false;
        try { package.InsertNext(_ => Task.CompletedTask); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || package.TotalSteps != 1)
            throw new Exception("Durable package accepted unjournaled dynamic steps.");
    }

    private static void DurableTransferRetryPreservesPriority()
    {
        static async Task Check()
        {
            using var sandbox = new Sandbox();
            var (database, _, _) = sandbox.Prepare();
            const string key = "test:durable-offline";
            await using (var scheduler = new PCasaScheduler())
            {
                scheduler.EnableDurablePackages(database);
                scheduler.RegisterDurablePackageType("test-offline-v1", (id, _) =>
                    new SchedulerTaskPackage(id));
                var package = new SchedulerTaskPackage(key);
                package.AddStep(_ => throw new DurablePackageRetryException("NAS offline"));
                var deferred = false;
                try
                {
                    await scheduler.EnqueueDurablePackageAsync(package,
                        "test-offline-v1", "{}", SchedulerPriority.ManualAction);
                }
                catch (DurablePackageRetryException) { deferred = true; }
                if (!deferred)
                    throw new Exception("Offline durable package was not deferred.");
                // The durable P0 marker survives even though its task has
                // unwound. Automatic NAS/import/scan work MUST remain held,
                // not proceed just because the in-memory queue is empty.
                var ranAutomatic = 0;
                var automaticBackground = scheduler.EnqueueAsync(
                    "test:background-behind-deferred-tag",
                    SchedulerPriority.Background,
                    _ => { Interlocked.Increment(ref ranAutomatic); return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.CatalogScan);
                var automaticInventory = scheduler.EnqueueAsync(
                    "test:inventory-behind-deferred-tag",
                    SchedulerPriority.UserAction,
                    _ => { Interlocked.Increment(ref ranAutomatic); return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.CatalogScan);
                var automaticInitialImport = scheduler.EnqueueAsync(
                    "test:first-import-behind-deferred-tag",
                    SchedulerPriority.Background,
                    _ => { Interlocked.Increment(ref ranAutomatic); return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.InitialImport);
                await Task.Delay(250);
                if (ranAutomatic != 0 || automaticBackground.IsCompleted ||
                    automaticInventory.IsCompleted || automaticInitialImport.IsCompleted)
                    throw new Exception(
                        "NAS scanning overtook a still-durable deferred manual P0 package.");
                var ranManual = false;
                await scheduler.EnqueueAsync("test:independent-manual",
                    SchedulerPriority.ManualAction,
                    _ => { ranManual = true; return Task.CompletedTask; })
                    .WaitAsync(TimeSpan.FromSeconds(10));
                if (!ranManual)
                    throw new Exception("Deferred P0 package blocked newer manual work.");
            }
            var saved = new DurablePackageJournal(database).Pending();
            if (saved.Count != 1 || saved[0].Key != key ||
                saved[0].Priority != SchedulerPriority.ManualAction ||
                saved[0].CompletedSteps != 0 || saved[0].State != "queued")
                throw new Exception("Deferred package lost its priority or resume position.");

            await using (var restarted = new PCasaScheduler())
            {
                restarted.EnableDurablePackages(database);
                var executed = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseManual = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                restarted.RegisterDurablePackageType("test-offline-v1", (id, _) =>
                {
                    var resumed = new SchedulerTaskPackage(id);
                    resumed.AddStep(async token =>
                    {
                        executed.TrySetResult();
                        await releaseManual.Task.WaitAsync(token);
                    });
                    return resumed;
                });
                if (restarted.RecoverDurablePackages().Count != 0)
                    throw new Exception("Offline durable package was not reconstructed.");
                await executed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var automaticRan = false;
                var automaticCompletion = restarted.EnqueueAsync(
                    "test:resume-after-p0-journal-completes",
                    SchedulerPriority.Background,
                    _ => { automaticRan = true; return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.CatalogScan);
                await Task.Delay(150);
                if (automaticRan || automaticCompletion.IsCompleted)
                    throw new Exception(
                        "Background scan started before resumed P0 finished.");
                releaseManual.TrySetResult();
                await automaticCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                if (!automaticRan)
                    throw new Exception(
                        "Background scan never resumed after P0 journal acknowledgement.");
                // Step execution precedes the final ledger acknowledgement.
                // Dispose is shutdown (a safe yield), not a completion barrier
                // for work that has not yet finished committing its result.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (new DurablePackageJournal(database).Pending().Count > 0 &&
                       DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                if (new DurablePackageJournal(database).Pending().Count != 0)
                    throw new Exception("Resumed offline package was not acknowledged.");
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void DurableGalleryTransferReceiptSurvivesRestart()
    {
        using var sandbox = new Sandbox();
        var (database, _, _) = sandbox.Prepare();
        const string key = "gallery:file-transfer:test-restart";
        var first = new DurableGalleryTransferJournal(database);
        var recipe = new GalleryTransferReceipt(
            @"C:\images\source.jpg", @"C:\target\destination.jpg",
            true, false, 42, 17, null, "test-intent-replay-1", "planned");
        first.Prepare(key, 0, recipe);
        first.EnsureRelocationIntent(recipe);
        first.EnsureRelocationIntent(recipe);
        first.Advance(key, 0, "planned", "executing");

        // Recreate both the journal and the caller as if the app crashed.
        var restarted = new DurableGalleryTransferJournal(database);
        var saved = restarted.Get(key, 0);
        if (saved is null || saved.Phase != "executing" ||
            saved.SourceImageId != 17 || saved.TargetRootId != 42 ||
            saved.RelocationId != recipe.RelocationId ||
            !restarted.OwnsRelocationIntent(recipe.RelocationId!))
            throw new Exception("Gallery transfer receipt was not restored.");
        if (!new ImageCatalogRepository(database).PendingRelocationIds()
            .Contains(recipe.RelocationId!))
            throw new Exception("Transfer relocation intent was not preserved.");

        var collisionRejected = false;
        try
        {
            restarted.Prepare(key, 0, recipe with
            {
                Destination = @"C:\target\different.jpg"
            });
        }
        catch (InvalidOperationException) { collisionRejected = true; }
        if (!collisionRejected)
            throw new Exception("Gallery transfer receipt allowed target reassignment.");

        restarted.Advance(key, 0, "executing", "materialized");
        restarted.Advance(key, 0, "materialized", "indexed");
        if (new DurableGalleryTransferJournal(database)
            .MovedImageIds(key).SetEquals(new[] { 17L }) == false)
            throw new Exception("Committed move identity was lost.");
    }

    private static void DurableSchedulerPrioritySurvivesRestart()
    {
        using var sandbox = new Sandbox();
        var (database, _, _) = sandbox.Prepare();
        var journal = new DurablePackageJournal(database);
        journal.Put("older", "test-case", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.Catalog);
        journal.Put("background", "test-case", "{}",
            SchedulerPriority.Background, SchedulerWorkKind.CatalogScan);
        journal.Put("newer", "test-case", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.Catalog);
        journal.SetState("older", "running", 2);
        journal.SetState("newer", "running", 1);

        // A delayed checkpoint write must never rewind already durable work.
        journal.SetState("older", "running", 1);
        if (journal.Pending().Single(x => x.Key == "older").CompletedSteps != 2)
            throw new Exception("Delayed checkpoint rewound the durable cursor.");

        // Late writes from a retired worker cannot revive a terminal record.
        journal.Put("terminal", "test-case", "{}",
            SchedulerPriority.Background, SchedulerWorkKind.CatalogScan);
        journal.SetState("terminal", "completed", 0);
        journal.SetState("terminal", "running", 1);
        if (journal.Put("terminal", "test-case", "{}",
                SchedulerPriority.Background, SchedulerWorkKind.CatalogScan).State != "completed")
            throw new Exception("Late checkpoint reopened a completed package.");

        // A new journal instance simulates application restart.
        var restored = new DurablePackageJournal(database).Pending();
        if (restored.Count != 3 ||
            restored[0].Key != "older" ||
            restored[1].Key != "newer" ||
            restored[2].Key != "background")
            throw new Exception("Durable package journal lost original sequence/priority.");
        if (restored[0].Priority != SchedulerPriority.ManualAction ||
            restored[0].CompletedSteps != 2 ||
            restored[1].CompletedSteps != 1)
            throw new Exception("Durable package journal lost manual priority or cursor.");
        // Idempotent re-submission must retain the exact immutable recipe.
        var repeated = journal.Put("older", "test-case", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
        if (repeated.Priority != SchedulerPriority.ManualAction ||
            repeated.CompletedSteps != 2)
            throw new Exception("Duplicate package lost original cursor.");
        foreach (var conflicting in new (string Type, string Payload,
            SchedulerPriority Priority, SchedulerWorkKind Kind)[]
        {
            ("different-type", "{}", SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog),
            ("test-case", "different-payload", SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog),
            ("test-case", "{}", SchedulerPriority.Background, SchedulerWorkKind.Catalog),
            ("test-case", "{}", SchedulerPriority.ManualAction, SchedulerWorkKind.SourceProbe)
        })
        {
            var rejected = false;
            try
            {
                journal.Put("older", conflicting.Type, conflicting.Payload,
                    conflicting.Priority, conflicting.Kind);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            if (!rejected)
                throw new Exception("Conflicting durable identity was accepted.");
        }
    }

    private static void DurableSchedulerCursorSurvivesRestart()
    {
        static async Task Check()
        {
            using var sandbox = new Sandbox();
            var (database, _, _) = sandbox.Prepare();
            var firstStepCalls = 0;
            var enteredSecondStep = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SchedulerTaskPackage Build(string key, bool blockSecond)
            {
                var package = new SchedulerTaskPackage(key);
                package.AddStep(_ =>
                {
                    Interlocked.Increment(ref firstStepCalls);
                    return Task.CompletedTask;
                });
                package.AddStep(async token =>
                {
                    if (blockSecond)
                    {
                        enteredSecondStep.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                });
                return package;
            }

            await using (var original = new PCasaScheduler())
            {
                original.EnableDurablePackages(database);
                original.RegisterDurablePackageType("resume-test",
                    (key, _) => Build(key, true));
                var task = original.EnqueueDurablePackageAsync(
                    Build("durable:resume-test", true),
                    "resume-test", "{}", SchedulerPriority.ManualAction);
                await enteredSecondStep.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var saved = new DurablePackageJournal(database).Pending();
                if (saved.Count != 1 || saved[0].CompletedSteps != 1)
                    throw new Exception("Finished first step was not checkpointed.");
                // Dispose simulates a stopped application while step 2 runs.
            }

            await using (var restarted = new PCasaScheduler())
            {
                restarted.EnableDurablePackages(database);
                var finished = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                restarted.RegisterDurablePackageType("resume-test",
                    (key, _) =>
                    {
                        var package = Build(key, false);
                        package.AddStep(_ =>
                        {
                            finished.TrySetResult();
                            return Task.CompletedTask;
                        });
                        return package;
                    });
                var unresolved = restarted.RecoverDurablePackages();
                if (unresolved.Count != 0)
                    throw new Exception("Registered durable package was not restored.");
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
                // The step's signal fires before the scheduler writes its
                // final durable completion record. Wait for that commit before
                // disposing the database-backed worker.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (new DurablePackageJournal(database).Pending().Count > 0 &&
                       DateTime.UtcNow < deadline)
                    await Task.Delay(30);
                if (firstStepCalls != 1)
                    throw new Exception("Completed step was repeated after restart.");
            }
            var remaining = new DurablePackageJournal(database).Pending();
            if (remaining.Count != 0)
                throw new Exception("Finished recovered package stayed in durable queue.");
        }
        Check().GetAwaiter().GetResult();
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

    private static void DynamicPackagesResumeLifo()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var trace = new List<string>();
            var firstRunning = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRunning = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAttempts = 0;
            var secondAttempts = 0;

            var oldest = new SchedulerTaskPackage("test:package:oldest");
            oldest.AddStep(_ =>
            {
                trace.Add("oldest:first-step");
                return Task.CompletedTask;
            });
            oldest.AddStep(async token =>
            {
                if (Interlocked.Increment(ref firstAttempts) == 1)
                {
                    firstRunning.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                trace.Add("oldest:resumed");
            });
            var first = scheduler.EnqueuePackageAsync(
                oldest, SchedulerPriority.ManualAction);
            await firstRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var second = new SchedulerTaskPackage("test:package:second");
            second.AddStep(async token =>
            {
                if (Interlocked.Increment(ref secondAttempts) == 1)
                {
                    secondRunning.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                trace.Add("second:resumed");
            });
            var middle = scheduler.EnqueuePackageAsync(
                second, SchedulerPriority.ManualAction);
            await secondRunning.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var latest = new SchedulerTaskPackage("test:package:latest");
            latest.AddStep(_ =>
            {
                trace.Add("latest");
                return Task.CompletedTask;
            });
            var last = scheduler.EnqueuePackageAsync(
                latest, SchedulerPriority.ManualAction);

            await Task.WhenAll(first, middle, last).WaitAsync(TimeSpan.FromSeconds(10));
            var expected = new[]
            {
                "oldest:first-step", "latest",
                "second:resumed", "oldest:resumed"
            };
            if (!trace.SequenceEqual(expected) ||
                oldest.CompletedSteps != 2 ||
                second.CompletedSteps != 1 || latest.CompletedSteps != 1)
                throw new Exception(
                    "Manual LIFO order or completed-step checkpoint violated: " +
                    string.Join(", ", trace));
        }
        Check().GetAwaiter().GetResult();
    }

    private static void NewerManualProbeSuspendsOlderPackage()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var olderStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var probeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProbe = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var trace = new List<string>();
            var attempts = 0;

            var older = new SchedulerTaskPackage("test:older-manual");
            older.AddStep(async token =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    olderStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                trace.Add("older resumed");
            });
            var olderTask = scheduler.EnqueuePackageAsync(
                older, SchedulerPriority.ManualAction);
            await olderStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var newerTask = scheduler.EnqueueAsync(
                "test:newer-manual-probe", SchedulerPriority.ManualAction,
                async token =>
                {
                    probeStarted.TrySetResult();
                    await releaseProbe.Task.WaitAsync(token);
                    trace.Add("newer completed");
                }, workKind: SchedulerWorkKind.SourceProbe);
            try
            {
                await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                if (trace.Count != 0 || olderTask.IsCompleted)
                    throw new Exception("Older manual package resumed during newer source probe.");
            }
            finally { releaseProbe.TrySetResult(); }

            await Task.WhenAll(newerTask, olderTask).WaitAsync(TimeSpan.FromSeconds(10));
            if (!trace.SequenceEqual(["newer completed", "older resumed"]) ||
                attempts != 2)
                throw new Exception("Manual LIFO order crossed scheduler lane boundaries.");
        }
        Check().GetAwaiter().GetResult();
    }

    private static void ScannerCheckpointRetainsEnumeration()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 3; i++)
        {
            var dir = Path.Combine(sandbox.Images, $"Folder-{i:D2}");
            Directory.CreateDirectory(dir);
            WriteJpeg(Path.Combine(dir, $"photo-{i:D2}.jpg"));
        }
        var (db, root, scanner) = sandbox.Prepare();
        using var resume = new FolderScanner.ScanResumeState();
        using var cancellation = new CancellationTokenSource();
        var interrupted = false;
        var callback = new InlineProgress<ScanProgress>(progress =>
        {
            if (interrupted || progress.DiscoveredPhotoFolderId is null) return;
            interrupted = true;
            cancellation.Cancel();
        });
        try
        {
            scanner.ScanDirectory(root, root.LocalPath, recursive: true,
                progress: callback, cancellationToken: cancellation.Token,
                resume: resume);
            throw new Exception("A scan ignored the explicit yield.");
        }
        catch (OperationCanceledException) when (interrupted) { }

        if (resume.CommittedFiles != 1 ||
            new ImageCatalogRepository(db).CountAll() != 1)
            throw new Exception("First image/scan cursor was not committed before yield.");

        var result = scanner.ScanDirectory(root, root.LocalPath, recursive: true,
            resume: resume);
        if (result.ImageFilesSeen != 3 ||
            new ImageCatalogRepository(db).CountAll() != 3)
            throw new Exception(
                "Resumed scan did not continue at its previous file; counted " +
                result.ImageFilesSeen);
    }


    private static void DurableFileCursorRestartSafe()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.Images, "LongFolder");
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 8; i++)
            WriteJpeg(Path.Combine(directory, $"image-{i:D3}.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        using var stop = new CancellationTokenSource();
        var first = new InlineProgress<ScanProgress>(p =>
        {
            if (p.DiscoveredPhotoFolderId is not null) stop.Cancel();
        });
        try
        {
            scanner.ScanDirectory(root, root.LocalPath, true, first,
                stop.Token, durableInitialImport: true);
            throw new Exception("Test scan did not interrupt mid-folder.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

        var repository = new FolderRepository(db);
        var state = repository.GetSourceState(root.CanonicalPath)!.Value;
        var epoch = repository.GetScanEpoch(root.CanonicalPath);
        var cursors = repository.GetInitialScanFileCursors(root.Id, state.Generation, epoch);
        if (!cursors.TryGetValue(directory, out var fileName) ||
            fileName != "image-000.jpg")
            throw new Exception("Last committed file cursor was not saved.");

        // The second scan must not read/index image zero again.
        // Removing an already processed file must NOT authorize its deletion
        // from a partial inventory that skipped it on restart.
        File.Delete(Path.Combine(directory, "image-000.jpg"));
        db.Initialize();
        var resumedScanner = new FolderScanner(new ImageCatalogRepository(db),
            new ThumbnailCache(), new FolderRepository(db),
            new BrowseThumbnailProfileRepository(db));
        var resumed = resumedScanner.ScanDirectory(root, root.LocalPath, true,
            durableInitialImport: true);
        if (resumed.ImageFilesSeen != 7 || resumed.Failed != 0)
            throw new Exception($"Restart unnecessarily reread images: {resumed.ImageFilesSeen}.");
        if (new ImageCatalogRepository(db).CountAll() != 8)
            throw new Exception("Partial inventory removed an unseen earlier image.");
        if (repository.GetPendingInitialScans().Any(f => f.Id == root.Id))
            throw new Exception("Restarted first import remained pending.");
        resumedScanner.Scan(root);
        if (new ImageCatalogRepository(db).CountAll() != 7)
            throw new Exception("Later full inventory did not reconcile deleted image.");
    }

    private static void DurableInitialImportRestartSafe()
    {
        using var sandbox = new Sandbox();
        var paths = Enumerable.Range(0, 3).Select(i =>
        {
            var dir = Path.Combine(sandbox.Images, $"Batch-{i:D2}");
            Directory.CreateDirectory(dir);
            var photo = Path.Combine(dir, $"photo-{i:D2}.jpg");
            WriteJpeg(photo);
            return photo;
        }).ToArray();
        var (db, root, scanner) = sandbox.Prepare();
        using var cancellation = new CancellationTokenSource();
        var discovered = 0;
        var progress = new InlineProgress<ScanProgress>(p =>
        {
            if (p.DiscoveredPhotoFolderId is null) return;
            if (++discovered == 2) cancellation.Cancel();
        });
        try
        {
            scanner.ScanDirectory(root, root.LocalPath, true, progress,
                cancellation.Token, durableInitialImport: true);
            throw new Exception("First-import interruption was ignored.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

        var repository = new FolderRepository(db);
        var state = repository.GetSourceState(root.CanonicalPath)!.Value;
        var done = repository.GetCompletedInitialScanDirectories(
            root.Id, state.Generation, repository.GetScanEpoch(root.CanonicalPath));
        if (!done.Contains(Path.GetDirectoryName(paths[0])!) ||
            done.Contains(Path.GetDirectoryName(paths[1])!))
            throw new Exception("Directory completion was not checkpointed at a safe boundary.");

        // The completed directory is not re-inventoried by a restarted first
        // import. A file removed while PCasa was closed must NOT be pruned by
        // an incomplete/mixed inventory. A later full scan owns reconciliation.
        File.Delete(paths[0]);
        db.Initialize();
        var afterRestart = new FolderScanner(new ImageCatalogRepository(db),
            new ThumbnailCache(), new FolderRepository(db),
            new BrowseThumbnailProfileRepository(db));
        var result = afterRestart.ScanDirectory(root, root.LocalPath, true,
            durableInitialImport: true);
        if (result.Failed != 0 ||
            repository.GetPendingInitialScans().Any(folder => folder.Id == root.Id))
            throw new Exception("Restarted first import did not complete.");
        if (new ImageCatalogRepository(db).CountAll() != 3)
            throw new Exception("Restart accidentally deleted a checkpointed image.");
        afterRestart.Scan(root);
        if (new ImageCatalogRepository(db).CountAll() != 2)
            throw new Exception("Subsequent complete scan failed to reconcile missing file.");
    }

    private static void ScanEpochSupersessionRetainsCursor()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 3; i++)
            WriteJpeg(Path.Combine(sandbox.Images, $"kept-{i}.jpg"));
        var obsolete = Path.Combine(sandbox.Images, "obsolete.jpg");
        WriteJpeg(obsolete);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 4);
        File.Delete(obsolete);

        using var resume = new FolderScanner.ScanResumeState();
        using var interruption = new CancellationTokenSource();
        var paused = false;
        var progress = new InlineProgress<ScanProgress>(scan =>
        {
            if (paused || scan.CurrentFile is null) return;
            paused = true;
            interruption.Cancel();
        });
        try
        {
            scanner.ScanDirectory(root, root.LocalPath, recursive: true,
                progress: progress, cancellationToken: interruption.Token, resume: resume);
            throw new Exception("Scanner ignored the requested checkpoint.");
        }
        catch (OperationCanceledException) when (paused) { }
        if (resume.CommittedFiles != 1)
            throw new Exception("Scanner checkpoint was not retained.");

        var folders = new FolderRepository(db);
        var sourceGeneration = folders.GetSourceState(root.CanonicalPath)!.Value.Generation;
        var previousEpoch = folders.GetScanEpoch(root.CanonicalPath);
        if (folders.BeginManualScan(root.CanonicalPath) != previousEpoch + 1 ||
            folders.GetSourceState(root.CanonicalPath)!.Value.Generation != sourceGeneration)
            throw new Exception("Manual scan incorrectly invalidated source membership.");

        var resumed = scanner.ScanDirectory(root, root.LocalPath,
            recursive: true, resume: resume);
        if (resumed.ImageFilesSeen != 3 || resumed.Removed != 0)
            throw new Exception("Superseded scan restarted files or pruned using stale inventory.");
        Require(db, 4);

        var latest = scanner.Scan(root);
        if (latest.Removed != 1) 
            throw new Exception("Newest complete inventory failed to reconcile missing paths.");
        Require(db, 3);
    }

    private static void SourceChangeRejectsOldScanCheckpoint()
    {
        using var sandbox = new Sandbox();
        for (var i = 0; i < 3; i++)
            WriteJpeg(Path.Combine(sandbox.Images, $"source-{i}.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        using var resume = new FolderScanner.ScanResumeState();
        using var cancellation = new CancellationTokenSource();
        var interrupted = false;
        var callback = new InlineProgress<ScanProgress>(scan =>
        {
            if (interrupted || scan.CurrentFile is null) return;
            interrupted = true;
            cancellation.Cancel();
        });
        try
        {
            scanner.ScanDirectory(root, root.LocalPath, true, callback,
                cancellation.Token, resume);
            throw new Exception("Scanner ignored the yield checkpoint.");
        }
        catch (OperationCanceledException) when (interrupted) { }
        if (resume.CommittedFiles != 1)
            throw new Exception("Expected one committed file before source change.");

        var excluded = Path.Combine(root.LocalPath, "excluded-later");
        Directory.CreateDirectory(excluded);
        new FolderRepository(db).SetExcluded(root.Id, excluded, true);
        var rejected = false;
        try { scanner.ScanDirectory(root, root.LocalPath, true, resume: resume); }
        catch (OperationCanceledException) { rejected = true; }
        if (!rejected || resume.CommittedFiles != 0)
            throw new Exception("Old source generation silently rescanned from zero.");
        scanner.Scan(root);
        Require(db, 3);
    }

    private static void ScannerYieldBeforeSourceLockDoesNotDoubleCount()
    {
        static async Task Check()
        {
            using var sandbox = new Sandbox();
            var photo = Path.Combine(sandbox.Images, "blocked-source.jpg");
            WriteJpeg(photo);
            var (db, root, scanner) = sandbox.Prepare();
            using var checkpoint = new FolderScanner.ScanResumeState();
            using var cancel = new CancellationTokenSource();
            var locked = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Run(() =>
            {
                lock (SourceFileLocks.ForPath(photo))
                {
                    locked.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            });
            try
            {
                await locked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var worker = Task.Run(() =>
                    scanner.ScanDirectory(root, root.LocalPath, recursive: true,
                        cancellationToken: cancel.Token, resume: checkpoint));
                // The worker may still be enumerating. Cancellation before or
                // during Enter must preserve CurrentFile without counting it.
                await Task.Delay(50);
                cancel.Cancel();
                try
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("Scanner ignored an interrupted source lock.");
                }
                catch (OperationCanceledException) { }
            }
            finally
            {
                release.TrySetResult();
                await holder.WaitAsync(TimeSpan.FromSeconds(5));
            }
            if (checkpoint.CommittedFiles != 0)
                throw new Exception("Interrupted source was counted before commit.");
            var result = scanner.ScanDirectory(root, root.LocalPath, true,
                resume: checkpoint);
            if (result.ImageFilesSeen != 1 || result.Updated != 1)
                throw new Exception("Resumed source was skipped or counted twice.");
            Require(db, 1);
        }
        Check().GetAwaiter().GetResult();
    }

    private static void PairedSourceLocksAreOrdered()
    {
        static async Task Check()
        {
            var source = Path.Combine(Path.GetTempPath(), "pcasa-pair-" +
                Guid.NewGuid().ToString("N") + "-source.jpg");
            var target = source + "-target.jpg";
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var firstReady = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(() =>
            {
                using (SourceFileLocks.EnterPair(source, target, token.Token))
                {
                    firstReady.TrySetResult();
                    releaseFirst.Task.GetAwaiter().GetResult();
                }
            });
            try
            {
                await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var secondStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var secondAcquired = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var second = Task.Run(() =>
                {
                    secondStarted.TrySetResult();
                    using (SourceFileLocks.EnterPair(target, source, token.Token))
                        secondAcquired.TrySetResult();
                });
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(100);
                if (secondAcquired.Task.IsCompleted)
                    throw new Exception("Destination was not locked with the source.");
                releaseFirst.TrySetResult();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
                if (!secondAcquired.Task.IsCompleted)
                    throw new Exception("Paired gate acquisition did not resume.");
            }
            finally
            {
                releaseFirst.TrySetResult();
                await first.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SourceLockWaitCanYield()
    {
        static async Task Check()
        {
            var path = Path.Combine(Path.GetTempPath(),
                "pcasa-source-lock-" + Guid.NewGuid().ToString("N") + ".jpg");
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = SourceFileLocks.ForPath(path);
            var holder = Task.Run(() =>
            {
                lock (gate)
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
            });
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var waiter = Task.Run(() =>
                {
                    waiting.TrySetResult();
                    using (SourceFileLocks.Enter(path, cancel.Token)) { }
                });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancel.Cancel();
                try
                {
                    await waiter.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("A canceled lock waiter entered a held source.");
                }
                catch (OperationCanceledException) { }
            }
            finally
            {
                release.TrySetResult();
                await holder.WaitAsync(TimeSpan.FromSeconds(5));
            }
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
            var bothStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            // Both automatic source-probe workers are held in an OS-like
            // noninterruptible call. Neither may consume foreground capacity.
            var blocked = Enumerable.Range(0, 2).Select(i => scheduler.RunAsync(
                $"blocked-smb-probe-{i}", SchedulerPriority.Background,
                _ =>
                {
                    if (Interlocked.Increment(ref count) == 2)
                        bothStarted.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    return true;
                }, workKind: SchedulerWorkKind.SourceProbe)).ToArray();
            try
            {
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var manualProbe = scheduler.RunAsync(
                    "manual-create-folder-probe", SchedulerPriority.ManualAction,
                    _ => true, workKind: SchedulerWorkKind.SourceProbe);
                var interactiveProbe = scheduler.RunAsync(
                    "interactive-expand-folder", SchedulerPriority.Interactive,
                    _ => true, workKind: SchedulerWorkKind.SourceProbe);
                var scan = scheduler.EnqueueAsync(
                    "manual-scan-during-probe", SchedulerPriority.ManualAction,
                    _ => Task.CompletedTask);
                await Task.WhenAll(manualProbe, interactiveProbe, scan)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (blocked.Any(task => task.IsCompleted))
                    throw new Exception(
                        "A foreground action completed only after background probe ended.");
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(blocked).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void ReadOnlyFolderProjectionNeverBlocksInitialImport()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var folderProbeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var finishFolderProbe = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            // Model the user's optional folder-tree projection enumerating a
            // slow NAS. SourceProbe remains scheduler-owned but has NO authority
            // to interrupt the durable background catalog scanner.
            var projection = scheduler.RunAsync(
                "folder-tree:scope:readonly",
                SchedulerPriority.Interactive,
                token =>
                {
                    folderProbeStarted.TrySetResult();
                    finishFolderProbe.Task.GetAwaiter().GetResult();
                    token.ThrowIfCancellationRequested();
                    return true;
                }, workKind: SchedulerWorkKind.SourceProbe);
            try
            {
                await folderProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var scanRan = false;
                await scheduler.EnqueueAsync(
                    "catalog:durable-first-import",
                    SchedulerPriority.Background,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        scanRan = true;
                        return Task.CompletedTask;
                    }, workKind: SchedulerWorkKind.CatalogScan)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!scanRan || projection.IsCompleted)
                    throw new Exception(
                        "Read-only folder visibility scan blocked catalog import.");
            }
            finally
            {
                finishFolderProbe.TrySetResult();
                await projection.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void CanceledAllFoldersScopeRetiresQueuedProbe()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            using var scopeCancellation = new CancellationTokenSource();
            var blockingProbeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseBlockingProbe = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var busy = scheduler.EnqueueAsync(
                "test:older-directory-probe", SchedulerPriority.Interactive,
                async _ =>
                {
                    blockingProbeStarted.TrySetResult();
                    await releaseBlockingProbe.Task;
                }, workKind: SchedulerWorkKind.SourceProbe);
            try
            {
                await blockingProbeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                const string scopedKey = "folder-tree:scope:disabled";
                var unexpectedlyExecuted = false;
                var queued = scheduler.EnqueueAsync(
                    scopedKey, SchedulerPriority.Interactive,
                    _ =>
                    {
                        unexpectedlyExecuted = true;
                        return Task.CompletedTask;
                    },
                    cancellationToken: scopeCancellation.Token,
                    workKind: SchedulerWorkKind.SourceProbe);
                if (!scheduler.Snapshot().Any(x =>
                    x.Key == scopedKey && x.State == SchedulerJobState.Queued))
                    throw new Exception("Dismissed folder probe was not queued.");

                scopeCancellation.Cancel();
                scheduler.CancelPending(scopedKey, SchedulerWorkKind.SourceProbe);
                try
                {
                    await queued.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new Exception("Canceled folder visibility job completed instead of being retired.");
                }
                catch (TaskCanceledException) { }

                if (unexpectedlyExecuted || scheduler.Snapshot().Any(x =>
                    x.Key == scopedKey && x.State != SchedulerJobState.Canceled))
                    throw new Exception("Dismissed folder projection stayed active in scheduler.");

                var importRan = false;
                await scheduler.EnqueueAsync(
                    "test:background-image-import", SchedulerPriority.Background,
                    _ => { importRan = true; return Task.CompletedTask; },
                    workKind: SchedulerWorkKind.CatalogScan)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                if (!importRan)
                    throw new Exception("Image import stalled behind abandoned folder visibility job.");
            }
            finally
            {
                releaseBlockingProbe.TrySetResult();
                await busy.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Check().GetAwaiter().GetResult();
    }

    private static void SchedulerManualFolderCreateYieldsScan()
    {
        static async Task Check()
        {
            await using var scheduler = new PCasaScheduler();
            var firstScanStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var scanHeld = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseManual = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var manualStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var scanPasses = 0;
            var scanResumedTooEarly = false;
            var scan = scheduler.EnqueueAsync("test:scan-before-new-folder",
                SchedulerPriority.Background, async token =>
                {
                    var pass = Interlocked.Increment(ref scanPasses);
                    if (pass == 1)
                    {
                        firstScanStarted.TrySetResult();
                        await scanHeld.Task.WaitAsync(token);
                    }
                    else if (!releaseManual.Task.IsCompleted)
                    {
                        scanResumedTooEarly = true;
                    }
                }, workKind: SchedulerWorkKind.CatalogScan);
            try
            {
                await firstScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var create = scheduler.EnqueueAsync("test:create-folder",
                    SchedulerPriority.ManualAction, async token =>
                    {
                        manualStarted.TrySetResult();
                        await releaseManual.Task.WaitAsync(token);
                    }, workKind: SchedulerWorkKind.SourceProbe);
                await manualStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (scan.IsCompleted)
                    throw new Exception("Background scan vanished during folder creation.");
                if (!scheduler.Snapshot().Any(job =>
                    job.Key == "test:scan-before-new-folder" &&
                    job.State is SchedulerJobState.YieldRequested or SchedulerJobState.Queued))
                    throw new Exception("Manual network operation did not yield the scan.");
                releaseManual.TrySetResult();
                await Task.WhenAll(create, scan).WaitAsync(TimeSpan.FromSeconds(5));
                if (scanPasses < 2 || scanResumedTooEarly)
                    throw new Exception("Background scan resumed before manual folder creation finished.");
            }
            finally
            {
                releaseManual.TrySetResult();
                scanHeld.TrySetResult();
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

    private static void GallerySortUsesCatalogMetadata()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "catalog-sort.jpg");
        WriteJpeg(path);
        RunExifTool("-overwrite_original",
            "-EXIF:DateTimeOriginal=2020:01:09 10:11:12", path);
        var created = new DateTime(2021, 4, 3, 5, 6, 7, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        var file = new FileInfo(path);
        var (db, root, scanner) = sandbox.Prepare();
        if (scanner.Scan(root).Failed != 0)
            throw new Exception("Sort metadata fixture failed initial scan.");

        var tier = new BrowseThumbnailProfileRepository(db).Get().Tiers[0];
        var entries = new BrowseCacheRepository(db).GetIndex(tier);
        if (entries.Count != 1) throw new Exception("Fixture image missing from pack index.");
        var entry = entries[0];
        if (entry.TakenUtcTicks !=
                new DateTime(2020, 1, 9, 10, 11, 12).Ticks ||
            entry.CreatedUtcTicks != file.CreationTimeUtc.Ticks ||
            entry.ModifiedUtcTicks != file.LastWriteTimeUtc.Ticks ||
            entry.FileSize != file.Length ||
            entry.OriginalWidth < 1 || entry.OriginalHeight < 1)
            throw new Exception(
                "Sort key not read from authoritative EXIF/file catalog metadata.");

        // An unchanged scan cannot reprocess a whole photo just to expose
        // an existing sort key. The image and thumbnail pack identities
        // remain unchanged across a no-op scan.
        var imageId = entry.ImageId;
        var pack = (entry.PackFile, entry.ByteOffset, entry.ByteLength);
        var second = scanner.Scan(root);
        var after = new BrowseCacheRepository(db).GetIndex(tier).Single();
        if (second.Failed != 0 || after.ImageId != imageId ||
            (after.PackFile, after.ByteOffset, after.ByteLength) != pack)
            throw new Exception("Sorting metadata created duplicate images or thumbnails.");
    }

    private static void PerFolderSortAllFields()
    {
        // Tree order intentionally differs from both numeric folder ID and
        // each image key: no sort mode may interleave photo-folder sections.
        var entries = new[]
        {
            new BrowseThumbnailIndexEntry(1, 30, 96, "test.pack", 0, 100, 96, 96,
                "Zed.jpg", FileSize: 100, ModifiedUtcTicks: 200,
                CreatedUtcTicks: 500, TakenUtcTicks: 900,
                OriginalWidth: 4000, OriginalHeight: 2000),
            new BrowseThumbnailIndexEntry(2, 30, 96, "test.pack", 100, 100, 96, 96,
                "Alpha.jpg", FileSize: 300, ModifiedUtcTicks: 300,
                CreatedUtcTicks: 100, TakenUtcTicks: null,
                OriginalWidth: 1000, OriginalHeight: 1000),
            new BrowseThumbnailIndexEntry(3, 30, 96, "test.pack", 200, 100, 96, 96,
                "Middle.jpg", FileSize: 200, ModifiedUtcTicks: 100,
                CreatedUtcTicks: 300, TakenUtcTicks: 800,
                OriginalWidth: 2000, OriginalHeight: 1500),
            new BrowseThumbnailIndexEntry(4, 20, 96, "test.pack", 300, 100, 96, 96,
                "Beta.jpg", FileSize: 200, ModifiedUtcTicks: 300,
                CreatedUtcTicks: 100, TakenUtcTicks: 600,
                OriginalWidth: 500, OriginalHeight: 500),
            new BrowseThumbnailIndexEntry(5, 20, 96, "test.pack", 400, 100, 96, 96,
                "Able.jpg", FileSize: 100, ModifiedUtcTicks: 100,
                CreatedUtcTicks: 300, TakenUtcTicks: null,
                OriginalWidth: 800, OriginalHeight: 800)
        };
        var positions = new Dictionary<long, int> { [20] = 0, [30] = 1 };
        static long[] Ids(IReadOnlyList<BrowseThumbnailIndexEntry> images)
            => images.Select(item => item.ImageId).ToArray();
        void Expect(GallerySortField field, bool descending, params long[] expected)
        {
            var actual = Ids(GalleryFolderOrdering.Order(entries, positions,
                new GallerySortOptions(field, descending)));
            if (!actual.SequenceEqual(expected))
                throw new Exception(
                    $"Sort {field} desc={descending}: expected " +
                    string.Join(",", expected) + " found " +
                    string.Join(",", actual));
        }
        // Folder 20 ALWAYS precedes folder 30, even when descending.
        Expect(GallerySortField.Name, false, 5, 4, 2, 3, 1);
        Expect(GallerySortField.Name, true, 4, 5, 1, 3, 2);
        Expect(GallerySortField.Size, false, 5, 4, 1, 3, 2);
        Expect(GallerySortField.Modified, false, 5, 4, 3, 1, 2);
        Expect(GallerySortField.Created, false, 4, 5, 2, 3, 1);
        // Missing EXIF capture time uses creation, not a fake epoch.
        Expect(GallerySortField.Captured, false, 5, 4, 2, 3, 1);
        Expect(GallerySortField.Dimensions, false, 4, 5, 2, 3, 1);
        Expect(GallerySortField.Size, true, 4, 5, 2, 3, 1);
        // Repeated sorting with the same rule must keep index identity.
        var preordered = GalleryFolderOrdering.Order(entries, positions,
            GallerySortOptions.Default);
        if (!ReferenceEquals(preordered, GalleryFolderOrdering.Order(
                preordered, positions, GallerySortOptions.Default)))
            throw new Exception("Identical gallery sort invalidated stable cached index.");
        // Two newly discovered folders with no tree projection yet must
        // still form separate photo sections.
        var unknownPositions = new Dictionary<long, int>();
        var unknown = GalleryFolderOrdering.Order(entries, unknownPositions,
            GallerySortOptions.Default);
        if (unknown.Select(x => x.PhotoFolderId).Distinct().Count() != 2 ||
            unknown.Select(x => x.PhotoFolderId).ToArray()
                .SequenceEqual(new long[] { 20, 20, 30, 30, 30 }) == false)
            throw new Exception("Unknown folder positions mixed image sections.");
    }

    private static void ChangedIndexRetainsUnmodifiedImages()
    {
        using var sandbox = new Sandbox();
        foreach (var name in new[] { "a.jpg", "b.jpg", "c.jpg" })
            WriteJpeg(Path.Combine(sandbox.Images, name));
        var (db, root, scanner) = sandbox.Prepare();
        if (scanner.Scan(root).Failed != 0)
            throw new Exception("Cannot prepare indexed image trio.");
        var tier = new BrowseThumbnailProfileRepository(db).Get().Tiers[0];
        var entries = new BrowseCacheRepository(db).GetIndex(tier).ToArray();
        if (entries.Length != 3)
            throw new Exception("Expected exactly three independent cache identities.");

        using var cache = new BrowseThumbnailPageCache();
        cache.SetIndex(entries);
        cache.WarmRangeAsync(0, 2).GetAwaiter().GetResult();
        if (!cache.TryGet(0, out var a) || a is null ||
            !cache.TryGet(1, out var b) || b is null ||
            !cache.TryGet(2, out var c) || c is null)
            throw new Exception("Initial thumbnail page did not load all images.");

        // Relocating one indexed photo changes folder identity and moves it
        // to another position, shifting unrelated photos' ordinal/page.
        // Neither the source index position nor an unchanged folder ID
        // can authorize displaying the WRONG cached bitmap.
        var relocated = entries[0] with
        {
            FileName = "relocated.jpg",
            PhotoFolderId = entries[0].PhotoFolderId + 1000
        };
        cache.SetIndex(new[] { entries[2], relocated, entries[1] });
        if (!cache.TryGet(0, out var retainedC) || !ReferenceEquals(c, retainedC) ||
            !cache.TryGet(2, out var retainedB) || !ReferenceEquals(b, retainedB))
            throw new Exception(
                "Changing one folder emptied unrelated gallery pictures before page reload.");
        if (!cache.TryGet(1, out var relocatedPixels) ||
            !ReferenceEquals(a, relocatedPixels))
            throw new Exception(
                "Moving an unchanged image unnecessarily discarded its decoded pixels.");

        // A changed pack span cannot reuse old pixels merely because the
        // image ID remains the same: cache bytes, not path, own identity.
        cache.SetIndex(new[]
        {
            entries[2],
            relocated with { ByteOffset = relocated.ByteOffset + 999 },
            entries[1]
        });
        if (cache.TryGet(1, out var wrongPixels) &&
            ReferenceEquals(a, wrongPixels))
            throw new Exception("Changed cache bytes reused stale decoded pixels.");

        cache.SetIndex(Array.Empty<BrowseThumbnailIndexEntry>());
        if (cache.TryGet(0, out _))
            throw new Exception("Empty gallery reused thumbnails from the prior index.");
    }

    private static void SingleImageCatalogProjectionMatchesFullIndex()
    {
        using var sandbox = new Sandbox();
        WriteJpeg(Path.Combine(sandbox.Images, "single-projection.jpg"));
        var (db, root, scanner) = sandbox.Prepare();
        if (scanner.Scan(root).Failed != 0)
            throw new Exception("Failed to prepare projection image.");
        var catalog = new ImageCatalogRepository(db);
        var tier = new BrowseThumbnailProfileRepository(db).Get().Tiers[0];
        var browse = new BrowseCacheRepository(db);
        var full = browse.GetIndex(tier).Single();
        var targeted = browse.GetIndexEntry(tier, full.ImageId);
        if (targeted != full ||
            browse.GetIndexEntry(tier, long.MaxValue) is not null)
            throw new Exception("Single-image gallery projection differed from full index.");

        var expectedFolder = catalog.GetPhotoFolders().Single();
        var sourceFolderId = catalog.GetImagePhotoFolderId(full.ImageId);
        if (sourceFolderId != expectedFolder.Id ||
            catalog.GetPhotoFolderSummary(expectedFolder.Id) != expectedFolder)
            throw new Exception("Targeted folder summary differed from full folder catalog.");
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
        var service = new FolderExclusionService(new FolderRepository(db), db,
            ExclusionTestScheduler.Value);
        service.ChangeAsync(root.Id, child, true,
            (_, _, excluded) =>
            {
                observed.Add(excluded);
                if (new ImageCatalogRepository(db).CountAll() != 0 ||
                    new FolderRepository(db).GetExcludedPaths(root.Id).Count == 0)
                    throw new Exception("Published exclusion exposed only half of the commit.");
            }).GetAwaiter().GetResult();
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

    // The manager's entire OK decision uses one SQLite commit with the
    // exact same exclusion transition used by the context-menu service.

    // A SQL failure AFTER new exclusion rules have been written must roll
    // back those rules, catalog rows, source generation and cleanup marker.
    private static void ExclusionCommitRollback()
    {
        using var sandbox = new Sandbox();
        var excluded = Path.Combine(sandbox.Images, "Rollback");
        Directory.CreateDirectory(excluded);
        var photo = Path.Combine(excluded, "original.jpg");
        WriteJpeg(photo);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var repository = new FolderRepository(db);
        var generation = repository.GetSourceState(root.CanonicalPath)!.Value.Generation;
        using (var connection = db.OpenConnection())
        using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER pcasa_test_exclusion_failure
                BEFORE DELETE ON images
                BEGIN SELECT RAISE(ABORT, 'simulate catalog delete failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }
        try
        {
            var failed = false;
            try
            {
                new FolderExclusionService(repository, db,
                    ExclusionTestScheduler.Value)
                    .ChangeAsync(root.Id, excluded, true).GetAwaiter().GetResult();
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { failed = true; }
            if (!failed)
                throw new Exception("Injected catalog failure did not abort exclusion.");
        }
        finally
        {
            using var connection = db.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TRIGGER IF EXISTS pcasa_test_exclusion_failure;";
            command.ExecuteNonQuery();
        }
        if (repository.GetExcludedPaths(root.Id).Count != 0 ||
            repository.GetSourceState(root.CanonicalPath)!.Value.Generation != generation)
            throw new Exception("Failed exclusion left a committed rule or generation.");
        Require(db, 1);
        if (!File.Exists(photo))
            throw new Exception("Failed catalog operation damaged an original.");
    }

    // This reproduces the pre-atomic crash gap without modifying originals:
    // exclusion metadata was persisted, but pruning never started.
    private static void LegacyExclusionRecovery()
    {
        using var sandbox = new Sandbox();
        var excluded = Path.Combine(sandbox.Images, "Legacy");
        Directory.CreateDirectory(excluded);
        var photo = Path.Combine(excluded, "original.jpg");
        WriteJpeg(photo);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);
        var folders = new FolderRepository(db);
        folders.SetExcluded(root.Id, excluded, true);
        Require(db, 1); // Simulates crash before legacy cleanup.
        db.Initialize(); // Simulates opening the persistent catalog after restart.
        var reconcile = new FolderManagerSelectionService(db);
        if (reconcile.ReconcilePersistedExclusions() != 1)
            throw new Exception("Restart did not reconcile the legacy exclusion.");
        Require(db, 0);
        if (folders.GetExcludedPaths(root.Id).Count != 1 ||
            !File.Exists(photo))
            throw new Exception("Legacy recovery changed desired rules or original.");
        if (!new CatalogRemovalService(db).HasPendingMaintenance())
            throw new Exception("Legacy cleanup was not deferred for idle compaction.");
        if (reconcile.ReconcilePersistedExclusions() != 0)
            throw new Exception("Repeated exclusion recovery was not idempotent.");
        folders.SetExcluded(root.Id, excluded, false);
        scanner.Scan(root);
        Require(db, 1);
    }

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

    private static void ReincludeAfterExclusionRestoresCatalogOnScan()
    {
        using var sandbox = new Sandbox();
        var child = Path.Combine(sandbox.Images, "child");
        Directory.CreateDirectory(child);
        var original = Path.Combine(child, "keep.jpg");
        WriteJpeg(original);
        var (db, root, scanner) = sandbox.Prepare();
        scanner.Scan(root);
        Require(db, 1);

        var service = new FolderExclusionService(new FolderRepository(db), db,
            ExclusionTestScheduler.Value);
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
        // The first exclusion and its catalog pruning are one commit.
        // Re-inclusion persists a new scan obligation; the original file
        // returns through the existing scanner, never a stale cleanup.
        Require(db, 0);
        scanner.Scan(root);
        Require(db, 1);
        if (!File.Exists(original))
            throw new Exception("The original file was unexpectedly removed.");
    }

    private static void NestedMonitoredRootOwnsExclusion()
    {
        using var sandbox = new Sandbox();
        var (db, outer, _) = sandbox.Prepare();
        var innerPath = Path.Combine(sandbox.Images, "Inner");
        var leaf = Path.Combine(innerPath, "Private");
        Directory.CreateDirectory(leaf);
        var folders = new FolderRepository(db);
        folders.Add(innerPath);
        var inner = folders.GetAll().Single(root =>
            string.Equals(root.LocalPath, innerPath, StringComparison.OrdinalIgnoreCase));
        if (FolderExclusionRules.FindOwner(leaf, folders.GetAll())?.Id != inner.Id)
            throw new Exception("Nested folder was incorrectly assigned to outer root.");
        folders.SetExcluded(inner.Id, leaf, true);
        var owner = FolderExclusionRules.FindOwner(leaf, folders.GetAll())
            ?? throw new Exception("Nested root disappeared.");
        if (!FolderExclusionRules.IsExcluded(
                leaf, folders.GetExcludedPaths(owner.Id)))
            throw new Exception(
                "A permissive outer root overrode the nested root's exclusion.");
        if (FolderExclusionRules.FindOwner(sandbox.Images, folders.GetAll())?.Id != outer.Id)
            throw new Exception("Non-nested paths stopped belonging to outer root.");
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
                var previousEpoch = folders.GetScanEpoch(root.CanonicalPath);
                var newerEpoch = folders.BeginManualScan(root.CanonicalPath);
                if (newerEpoch != previousEpoch + 1 ||
                    folders.GetSourceState(root.CanonicalPath)!.Value.Generation != previousGeneration)
                    throw new Exception("Manual scan did not advance its independent inventory epoch.");
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

            var oldResult = await old.WaitAsync(TimeSpan.FromSeconds(15));
            if (oldResult.Removed != 0)
                throw new Exception("Older background inventory was allowed to prune.");
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
        var service = new FolderExclusionService(new FolderRepository(db), db,
            ExclusionTestScheduler.Value);
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
        var originalWriteTime = File.GetLastWriteTimeUtc(path);
        void AssertMetadataOnlyWrite()
        {
            if (File.GetLastWriteTimeUtc(path) != originalWriteTime)
                throw new Exception("Tag write changed source mtime and will retrigger thumbnail import.");
            using var stateDb = db.OpenConnection();
            using var state = stateDb.CreateCommand();
            state.CommandText = """
                SELECT i.file_size, i.modified_utc_ticks,
                       x.source_file_size, x.source_modified_utc_ticks
                FROM images i LEFT JOIN image_exif x ON x.image_id = i.id
                WHERE i.id = $id;
                """;
            state.Parameters.AddWithValue("$id", id);
            using var row = state.ExecuteReader();
            if (!row.Read() ||
                row.GetInt64(0) != new FileInfo(path).Length ||
                row.GetInt64(1) != originalWriteTime.Ticks ||
                row.IsDBNull(2) || row.GetInt64(2) != row.GetInt64(0) ||
                row.GetInt64(3) != originalWriteTime.Ticks)
                throw new Exception("Tag-only edit invalidated catalog EXIF/cache fingerprint.");
            var scan = scanner.Scan(root);
            if (scan.Updated != 0 ||
                new BrowseCacheRepository(db).CountMissing(
                    new BrowseThumbnailProfileRepository(db).Get()) != 0)
                throw new Exception("Tag-only edit triggered an unnecessary image/cache re-import.");
        }
        tags.ChangeKeywords(id, "PCasaRegressionKeyword", remove: false);
        AssertMetadataOnlyWrite();
        var value = ImageTagRepository.ReadKeywords(path);
        if (value is null || !value.Contains("PCasaRegressionKeyword", StringComparison.Ordinal))
            throw new Exception("JPEG metadata tag was not saved in the original.");
        tags.ChangeKeywords(id, "PCasaRegressionKeyword", remove: true);
        AssertMetadataOnlyWrite();
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

    private static void ExifToolOwnedWritersTerminate()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "owned-exiftool-child.jpg");
        WriteJpeg(path);
        using var session = new ExifToolKeywordWriter.Session();
        session.Read(path);
        var field = typeof(ExifToolKeywordWriter.Session).GetField(
            "_process", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("ExifTool process owner field missing.");
        var child = (System.Diagnostics.Process?)field.GetValue(session)
            ?? throw new Exception("ExifTool session did not start its own process.");
        if (!ExifToolKeywordWriter.HasLiveSessions || child.HasExited)
            throw new Exception("Owned ExifTool process was not registered.");
        var childPid = child.Id;
        ExifToolKeywordWriter.StopAll();
        // Dispose releases Process handles; inspecting child.HasExited after
        // dispose raises InvalidOperationException even if it exited cleanly.
        static bool IsProcessAlive(int pid)
        {
            try
            {
                using var probe = System.Diagnostics.Process.GetProcessById(pid);
                return !probe.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
        if (IsProcessAlive(childPid) || ExifToolKeywordWriter.HasLiveSessions)
            throw new Exception("Shutdown left an owned ExifTool process running.");
        // The durable file writer must still work after an orderly shutdown
        // of the previous native session, without sharing a dead process.
        using var next = new ExifToolKeywordWriter.Session();
        next.Write(path, new[] { "After clean ExifTool shutdown" });
        if (!ExifToolKeywordWriter.HasLiveSessions)
            throw new Exception("Replacement ExifTool process was not tracked.");
        ExifToolKeywordWriter.StopAll();
        if (ExifToolKeywordWriter.HasLiveSessions ||
            !(ImageTagRepository.ReadKeywords(path) ?? "")
                .Contains("After clean ExifTool shutdown", StringComparison.Ordinal))
            throw new Exception("Tag write or ExifTool final shutdown did not finish cleanly.");
    }

    private static void JpegTagTemporaryOutputsAreCleaned()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Images, "metadata-temporary.jpg");
        WriteJpeg(path);
        var temporary = path + "_exiftool_tmp";

        // A previous failed native write left exactly ExifTool's own suffix
        // next to an intact original. It must not block a new metadata write
        // or grow an archive of temporary copies.
        File.WriteAllText(temporary, "abandoned-output");
        File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddHours(-2));
        using (var session = new ExifToolKeywordWriter.Session())
            session.Write(path, new[] { "No temp graveyard" });
        if (File.Exists(temporary) || !File.Exists(path) ||
            !ImageTagRepository.ReadKeywords(path)!.Contains("No temp graveyard"))
            throw new Exception("ExifTool left an abandoned temp or lost the original.");

        // A second PC may own a fresh native temp, or the process may still
        // hold the file. Neither case may be "cleaned" on assumption.
        var cleanup = typeof(ExifToolKeywordWriter.Session).GetMethod(
            "TryRemoveAbandonedTemporaryOutput",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static)
            ?? throw new Exception("Scoped ExifTool cleanup missing.");
        void Prune() => cleanup.Invoke(null, new object[]
        {
            path, TimeSpan.FromMinutes(30)
        });
        File.WriteAllText(temporary, "fresh-output");
        Prune();
        if (!File.Exists(temporary))
            throw new Exception("Cleanup deleted a live ExifTool temp.");
        File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddHours(-2));
        using (var open = new FileStream(temporary, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Prune();
            if (!File.Exists(temporary))
                throw new Exception("Cleanup deleted another writer's locked temp.");
        }
        Prune();
        if (File.Exists(temporary) || !File.Exists(path))
            throw new Exception("Stale temp was not safely removed.");

        // If the original is absent, a temp could be the only remaining
        // data; never remove it, even if stale.
        var movedOriginal = path + ".preserved";
        File.WriteAllText(temporary, "only-recoverable-data");
        File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddHours(-2));
        File.Move(path, movedOriginal);
        try
        {
            Prune();
            if (!File.Exists(temporary))
                throw new Exception("Cleanup erased the only surviving file.");
        }
        finally
        {
            File.Move(movedOriginal, path);
            File.Delete(temporary);
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
        var committedImages = new List<(string Path, long FolderId)>();
        void OnIndexed(string path, long folderId)
        {
            if (new ImageCatalogRepository(db).GetImageId(path) is null)
                throw new Exception("Targeted gallery notification preceded durable image commit.");
            committedImages.Add((path, folderId));
        }
        CatalogChanges.ImageIndexed += OnIndexed;
        try
        {
            TestScan();
        }
        finally
        {
            CatalogChanges.ImageIndexed -= OnIndexed;
        }
        if (committedImages.Count != 2 ||
            committedImages.Select(entry => entry.FolderId).Distinct().Count() != 2)
            throw new Exception("Targeted gallery notifications did not identify both committed images.");

        void TestScan()
        {
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
        // The historical test merely checked metadata width/height. A lazy
        // 90-degree libvips rotation may report correct dimensions but fail
        // at the first real PNG/JPEG encode with "out of order read".
        //
        // Use a JPEG-sized *embedded RAW preview*, not a tiny 96x48 icon,
        // because the sequential JPEG reader must cross several line-cache
        // boundaries before the backward/column-wise failure is observable.
        var original = Path.Combine(sandbox.Images, "embedded-preview.jpg");
        const int width = 1600, height = 900;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = (y * width + x) * 4;
            pixels[p] = (byte)(x % 255);
            pixels[p + 1] = (byte)(y % 255);
            pixels[p + 2] = 180;
            pixels[p + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(original)) encoder.Save(output);
        var sha = SHA256.HashData(File.ReadAllBytes(original));

        foreach (var size in new[] { 512, 1280 })
        foreach (var orientation in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            using var unrotated = NetVips.Image.Thumbnail(original, size,
                height: size, size: NetVips.Enums.Size.Down, noRotate: true);
            var expectedWidth = unrotated.Width;
            var expectedHeight = unrotated.Height;
            using var oriented = RawImageSource.ApplyOrientation(unrotated, orientation);
            var swapsAxes = orientation is 5 or 6 or 7 or 8;
            expectedWidth = swapsAxes ? unrotated.Height : unrotated.Width;
            expectedHeight = swapsAxes ? unrotated.Width : unrotated.Height;

            // Require actual pixel evaluation, not just lazy dimensions.
            // This is precisely the point where the former code failed.
            var png = oriented.PngsaveBuffer(compression: 1);
            using var decoded = NetVips.Image.NewFromBuffer(png);
            if (decoded.Width != expectedWidth ||
                decoded.Height != expectedHeight)
                throw new Exception(
                    $"RAW preview {size}px EXIF {orientation}: " +
                    $"{decoded.Width}x{decoded.Height} != {expectedWidth}x{expectedHeight}.");
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
        var service = new FolderExclusionService(new FolderRepository(db), db,
            ExclusionTestScheduler.Value);
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
