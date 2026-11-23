using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private BrowseThumbnailProfileRepository? _browseProfileRepository;
    private BrowseCacheRepository? _browseCacheRepository;
    private BrowseCacheBuilder? _browseCacheBuilder;
    private BrowseThumbnailPageCache? _browsePageCache;
    private HashSet<long> _restoreBadgeImageIds = new();
    private bool _restoreBadgeChecking;
    private DateTime _restoreArchiveStamp = DateTime.MinValue;

    private IReadOnlyList<BrowseThumbnailIndexEntry> _browseIndex =
        Array.Empty<BrowseThumbnailIndexEntry>();
    private CancellationTokenSource? _browseCacheCancellation;
    private CancellationTokenSource? _thumbnailSizeCancellation;
    private int? _activeBrowseTier;
    private readonly LatestViewRequest _browseRefreshRequests = new();
    private bool _browseInitialized;
    // The first catalog publication must never paint the first folder
    // before the user's saved thumbnail anchor has been restored.
    private bool _startupGalleryPublished;
    private bool _startupGalleryPresented;
    private bool _startupRecoveryComplete;
    private readonly System.Diagnostics.Stopwatch _startupClock =
        System.Diagnostics.Stopwatch.StartNew();

    private void LogStartupStage(string stage)
        => PCasaDiagnostics.WriteMessage("Gallery.StartupTiming",
            $"{stage}: {_startupClock.ElapsedMilliseconds} ms");

    // Both conditions are required: the correct gallery must have been
    // painted and durable P0 recovery must be registered before P3/P4 work.
    private void BeginDeferredBackgroundMonitoring()
    {
        if (_startupGalleryPresented && _startupRecoveryComplete &&
            IsLoaded && !_backgroundScanInitialized &&
            !Dispatcher.HasShutdownStarted && !_shutdownStarted)
            BackgroundScan_WindowLoaded(this, new RoutedEventArgs());
    }
    // The first catalog pass already read these immutable folder summaries
    // and total before the first gallery projection. Reuse them ONCE rather
    // than repeating the same grouping/count query on a 500K-photo SQLite DB.
    private (IReadOnlyList<PhotoFolderSummary> Folders, int ImageCount)?
        _startupFolderSnapshot;
    private bool _browseCacheBuilding;
    private bool _folderManagerOpen;
    private bool _folderManagerCommitPending;
    private BrowseCacheProgressWindow? _browseProgressWindow;
    // One user choice across all present/future progress windows in this session.
    private bool _progressHiddenByUser;

    // The ONE progress presenter is shared by scanner, cache and manual
    // callers. Its lifetime belongs to this owner, not to whichever caller
    // happened to allocate it first. A different job finishing must never
    // close another job's progress window.
    private readonly HashSet<object> _progressPresenterOwners = new();
    private readonly object _manualProgressOwner = new();

    private BrowseCacheProgressWindow AcquireProgressPresenter(object owner)
    {
        _progressPresenterOwners.Add(owner);
        if (_browseProgressWindow is null)
        {
            _browseProgressWindow = new BrowseCacheProgressWindow { Owner = this };
            _browseProgressWindow.SetUserHidden(_progressHiddenByUser);
            _browseProgressWindow.UserHiddenChanged += OnProgressUserHiddenChanged;
        }
        var presenter = _browseProgressWindow;
        AttachProgressStatus(presenter);
        return presenter;
    }

    private void OnProgressUserHiddenChanged(bool hidden)
        => _progressHiddenByUser = hidden;

    private void ReleaseProgressPresenter(object owner)
    {
        if (!_progressPresenterOwners.Remove(owner) ||
            _progressPresenterOwners.Count != 0) return;
        var presenter = _browseProgressWindow;
        _browseProgressWindow = null;
        if (ReferenceEquals(_attachedProgressPresenter, presenter) &&
            presenter is not null)
        {
            presenter.ProgressPresentationChanged -= VisibleProgressStatusChanged;
            _attachedProgressPresenter = null;
        }
        if (presenter is not null)
        {
            presenter.UserHiddenChanged -= OnProgressUserHiddenChanged;
            presenter.Finish();
        }
    }

    private async void BrowseSurface_Loaded(object sender, RoutedEventArgs e)
    {
        if (_browseInitialized)
        {
            return;
        }

        _browseInitialized = true;
        LogStartupStage("gallery-loaded");
        // The first layout has a temporary scroll position (usually 0).
        // Suppress ALL page requests from that provisional viewport; the
        // explicit warm after restore will load only the actual visible pages.
        BrowseSurface.DeferViewportPageRequests(true);
        _browseProfileRepository = new BrowseThumbnailProfileRepository(_database);
        _browseCacheRepository = new BrowseCacheRepository(_database);
        _browseCacheBuilder = new BrowseCacheBuilder(
            _browseCacheRepository,
            _browseProfileRepository);
        _browsePageCache = new BrowseThumbnailPageCache();
        BrowseSurface.ImageClicked += BrowseSurface_ImageClicked;
        BrowseSurface.EmptyAreaClicked += BrowseSurface_EmptyAreaClicked;
        BrowseSurface.ImageOpened += BrowseSurface_ImageOpened;
        BrowseSurface.ImageContextRequested += BrowseSurface_ImageContextRequested;
        BrowseSurface.ImagesDragRequested += BrowseSurface_ImagesDragRequested;
        BrowseSurface.LayoutReflowed += BrowseSurface_LayoutReflowed;
        CatalogChanges.ImageCommitted += OnCatalogImageCommitted;
        CatalogChanges.ImageIndexed += OnCatalogImageIndexed;
        Closed += BrowseWindow_Closed;
        // Publish the already-committed local catalog BEFORE any durable
        // recovery or first-import admission. Loading a view is not P0-P4
        // work; no NAS filesystem probe is required for this projection.
        await EnsureBrowseCacheAndRefreshAsync(initialProjectionOnly: true);
        if (Dispatcher.HasShutdownStarted || _shutdownStarted) return;
        LogStartupStage("correct-viewport-warmed");
        // A ContextIdle continuation runs after WPF Render. Even recovered
        // high-priority jobs may not consume CPU before the first gallery frame.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (Dispatcher.HasShutdownStarted || _shutdownStarted) return;

        // Recovery is a scheduler operation, not an alternative scanner.
        // It may perform slow SMB probes without blocking WPF startup.
        // If any durable move is recovered, the SAME gallery publication
        // owner will bring the new catalog state into the current view.
        // Reconciliation is queued after registering and restoring durable P0 packages.

        // Durable user imports enter the scheduler BEFORE background legacy
        // repair. Both use the SAME catalog and generation guards.
        // Recovered user edits must be queued BEFORE background folder scans.
        // Upgrade legacy recovery before reconstructing ANY worker.
        // The old fixed-list cursor stopped on the first offline NAS image
        // and could starve reachable C: photos indefinitely. This migration
        // retains the journal identity/order and does not touch pending tags.
        var packageJournal = new DurablePackageJournal(_database);
        // Alpha.58: outstanding original-file writes must retain the SAME
        // identity, recipe, priority, order and confirmed step on restart.
        // RecoverDurablePackages below is the single replay authority.
        packageJournal.UpgradeLegacyTagRecovery();
        // Legacy one-intent checks are not transfers. An unresolved attempt
        // is NOT a reason to create two new P0 jobs on every application
        // start. Retire only these obsolete probes, leaving all relocation
        // intents and original-file identities in SQLite for manual review.
        packageJournal.RetireLegacyTransferReconciliationPackages();
        // Restore the read-only view of already journaled P0 tag writes
        // BEFORE the scheduler starts replaying them. This is the same
        // progress presenter used by ordinary manual file operations.
        RestoreVisiblePendingTagPackages();
        // Recovered manual scans are real P0 work, too. Reconstruct their
        // read-only progress projection before the scheduler starts replay.
        // Otherwise an older pending Tag package appears to be stuck on
        // "Warte auf Scheduler" while a newer P0 scan actually owns the lane.
        RestoreVisiblePendingManualScans();
        var unresolvedPackages = _scheduler.RecoverDurablePackages(
            ObserveRecoveredDurablePackage);
        // A recipe or journal cursor that cannot be reconstructed is NOT a
        // queued scheduler operation. Keep it durable for inspection, but do
        // not leave a fake "Warte auf Scheduler" progress dialog forever.
        foreach (var unresolved in unresolvedPackages)
        {
            if (_recoveredTagPresentationKeys.Remove(unresolved.Key) ||
                _recoveredManualScanPresentationKeys.Remove(unresolved.Key))
                EndVisibleManualOperation(unresolved.Key);
            PCasaDiagnostics.WriteMessage("Scheduler.UnresolvedPackage",
                $"Key={unresolved.Key}; Type={unresolved.Type}; " +
                $"Priority={unresolved.Priority}; Step={unresolved.CompletedSteps}; " +
                $"Error={unresolved.Error ?? "(no error recorded)"}");
        }
        // Recoverable file writes resume automatically; intentionally
        // deferred failures remain available for explicit Taskmanager retry.
        // Unresolved legacy file-transfer intents remain stored, but must
        // not preempt imports with fresh P0 reconciliation packages. No
        // filesystem probe or transfer is performed merely by app startup.
        _ = ReportPendingLegacyTransfersAsync();
        // Unreadable or unsupported recipes remain durable and are retried
        // centrally, but cannot permanently prevent all catalog work.
        QueuePendingInitialScans();
        if (unresolvedPackages.Count > 0)
            StatusText.Text = $"{unresolvedPackages.Count} Paket(e) für spätere Wiederherstellung vorgemerkt";
        _ = ReconcilePersistedExclusionsAsync();
        _ = CompleteBrowseStartupMaintenanceAsync();
        StartTagBackfill();
        _startupRecoveryComplete = true;
        LogStartupStage("durable-recovery-registered");
        BeginDeferredBackgroundMonitoring();
    }


    // This callback is also used by the scheduler's central periodic retry
    // worker. It must return to WPF before changing any presentation state.
    private void ObserveRecoveredDurablePackage(string key, Task completion)
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
                ObserveRecoveredDurablePackage(key, completion)));
            return;
        }
        if (key.StartsWith("tags:", StringComparison.Ordinal))
        {
            RestoreVisiblePendingTagPackage(key);
            if (_recoveredTagPresentationKeys.Contains(key))
                _ = ObserveRecoveredTagOperationAsync(completion, key);
        }
        else if (key.StartsWith("gps:edit:", StringComparison.Ordinal))
        {
            // Recovered GPS edits use the original manual scheduler job.
            BeginVisibleManualOperation(key, "GPS in Originaldateien schreiben", 0);
            _ = ObserveGpsEditAsync(completion, key);
        }
        else if (key.StartsWith("catalog:complete-scan:", StringComparison.Ordinal) ||
                 key.StartsWith("catalog:scan-all:", StringComparison.Ordinal))
        {
            RestoreVisiblePendingManualScans();
            if (_recoveredManualScanPresentationKeys.Contains(key))
                _ = ObserveRecoveredManualScanAsync(completion, key);
        }
    }

    // Same presenter and scheduler identity as regular user-started scans.
    // Only actually runnable restored scans are shown as active jobs.
    private readonly HashSet<string> _recoveredManualScanPresentationKeys =
        new(StringComparer.Ordinal);

    private void RestoreVisiblePendingManualScans()
    {
        foreach (var record in new DurablePackageJournal(_database).Pending()
            .Where(p => p.State != "deferred" &&
                (p.Type is "manual-folder-scan-v1" or "manual-scan-all-v1")))
        {
            if (!_recoveredManualScanPresentationKeys.Add(record.Key)) continue;
            string? directory = null;
            if (record.Type == "manual-folder-scan-v1")
            {
                try
                {
                    directory = System.Text.Json.JsonSerializer
                        .Deserialize<ManualScanRecipe>(record.Payload)?.Directory;
                }
                catch (System.Text.Json.JsonException) { }
            }
            BeginVisibleManualOperationForDirectory(record.Key,
                record.Type == "manual-scan-all-v1"
                    ? "Alle Ordner einlesen" : "Ordner aktualisieren", 0,
                directory);
        }
    }

    private async Task ObserveRecoveredManualScanAsync(Task completion, string key)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (DurablePackageRetryException ex)
        {
            PCasaDiagnostics.Write("Catalog.RecoveredManualScanDeferred", ex);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.RecoveredManualScanFailed", ex);
        }
        finally
        {
            _recoveredManualScanPresentationKeys.Remove(key);
            if (!Dispatcher.HasShutdownStarted)
                EndVisibleManualOperation(key);
        }
    }

    private async Task ReconcilePersistedExclusionsAsync()
    {
        try
        {
            var removed = await _scheduler.RunAsync(
                "catalog:reconcile-persisted-exclusions",
                SchedulerPriority.Background,
                token => new FolderManagerSelectionService(_database)
                    .ReconcilePersistedExclusions(token),
                workKind: SchedulerWorkKind.Catalog);
            if (removed > 0 && !Dispatcher.HasShutdownStarted)
            {
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
                StatusText.Text =
                    $"{removed:N0} verwaiste Einträge ausgeschlossener Ordner bereinigt";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.ExclusionRecovery", ex);
        }
    }

    // Read-only diagnostics for old, ambiguous transfer intents. This
    // never probes SMB, starts a file operation, or takes P0 priority.
    // Intents owned by a durable gallery-transfer package are handled by
    // that package's original staged receipt and must not be duplicated.
    private async Task ReportPendingLegacyTransfersAsync()
    {
        try
        {
            var pending = await _scheduler.RunAsync(
                "catalog:pending-legacy-transfer-review",
                SchedulerPriority.Background,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return _imageCatalog.PendingRelocationDetails();
                }, workKind: SchedulerWorkKind.CatalogRead);
            foreach (var intent in pending)
            {
                if (_durableGalleryTransfers.OwnsRelocationIntent(intent.OperationId))
                    continue;
                PCasaDiagnostics.WriteMessage("Catalog.TransferNeedsReview",
                    $"Intent={intent.OperationId}; " +
                    $"From={intent.SourcePath}; To={intent.DestinationPath}; " +
                    "Kein automatischer Neustart: physischer Zustand ungeklärt.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.TransferReview", ex);
        }
    }

    private async Task CompleteBrowseStartupMaintenanceAsync()
    {
        try { await EnsureBrowseCacheAndRefreshAsync(maintenanceOnly: true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.StartupMaintenance", ex); }
    }

    private async Task EnsureBrowseCacheAndRefreshAsync(
        bool initialProjectionOnly = false, bool maintenanceOnly = false)
    {
        if (!_browseInitialized ||
            _browseProfileRepository is null ||
            _browseCacheRepository is null ||
            _browseCacheBuilder is null ||
            _browsePageCache is null ||
            _browseCacheBuilding ||
            _folderRemovalPending)
        {
            return;
        }

        var imageCount = _lastCatalogImageCount;
        if (!maintenanceOnly)
        {
        IReadOnlyList<PhotoFolderSummary> initialFolders = Array.Empty<PhotoFolderSummary>();
        IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths,
            IReadOnlyList<string> IncludedPaths)> initialConfiguration =
            Array.Empty<(MonitoredFolder, IReadOnlyList<string>, IReadOnlyList<string>)>();
        IReadOnlyList<string> createdDirectories = Array.Empty<string>();
        // Durable first-import completion is the only existing proof of a
        // previously FULL root inventory. Read it in the SAME catalog job.
        HashSet<long> pendingInitialRootIds = new();
        IReadOnlyList<string> verifiedInitialDirectories = Array.Empty<string>();
        imageCount = 0;
        long initialFolderSqlMs = 0, initialConfigurationSqlMs = 0;
        await _galleryCatalogReader.ReadAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                initialFolders = _imageCatalog.GetPhotoFolders();
                imageCount = _imageCatalog.CountAll();
                initialFolderSqlMs = watch.ElapsedMilliseconds;
                initialConfiguration = ReadFolderTreeConfigurationSnapshot();
                pendingInitialRootIds = _folderRepository.GetPendingInitialScans()
                    .Select(root => root.Id).ToHashSet();
                verifiedInitialDirectories = _folderRepository
                    .GetVerifiedPendingInitialDirectories();
                createdDirectories = _folderRepository.GetCreatedDirectories();
                initialConfigurationSqlMs =
                    watch.ElapsedMilliseconds - initialFolderSqlMs;
                return Task.CompletedTask;
            });
        if (!_browseInitialized || _folderRemovalPending) return;
        _photoFolders = initialFolders;
        if (initialProjectionOnly)
            LogStartupStage($"catalog-and-folder-snapshot: " +
                $"photo-summary={initialFolderSqlMs} ms; " +
                $"tree-config={initialConfigurationSqlMs} ms; " +
                $"folders={initialFolders.Count:N0}; photos={imageCount:N0}");
        if (initialProjectionOnly)
            _startupFolderSnapshot = (initialFolders, imageCount);
        _dropTargetProjection = initialConfiguration;
        foreach (var (root, _, _) in initialConfiguration)
        {
            if (!pendingInitialRootIds.Contains(root.Id))
                _folderScanStatuses.SeedCompletedInitialImport(root.LocalPath);
        }
        foreach (var directory in verifiedInitialDirectories)
            _folderScanStatuses.Set(directory, "Ready");
        _createdEmptyFolderPaths.Clear();
        _createdEmptyFolderPaths.UnionWith(createdDirectories);
        _dragTargetValidity.Clear();
        RefreshFolderTree(preserveGalleryAnchor: false);
        if (initialProjectionOnly) LogStartupStage("folder-tree-ready");
        _ = RefreshPendingTagDotsAsync();
        _lastCatalogImageCount = imageCount;
        UpdateFolderImageTotal();
        EmptyPanel.Visibility = imageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Publish the committed catalog BEFORE scheduling cache repair.
        // A P3/P4 missing-thumbnail rebuild can be held by a running NAS
        // scan indefinitely. Neither the folder tree nor the initial image
        // index may depend on that background job finishing.
        await RefreshBrowseGalleryAsync(forceIndexReload: true,
            // Warm only the restored initial viewport from the LOCAL packed
            // cache. No NAS probe, no scheduler admission, no global decode.
            warmViewport: initialProjectionOnly);
        if (!_browseInitialized || _folderRemovalPending || initialProjectionOnly) return;

        // A manual foreground scan already owns source/cache work. It must
        // not hide the gallery, and startup must never await another rebuild.
        if (_manualScanRunning) return;
        if (imageCount == 0)
        {
            StatusText.Text = UiLanguage.T("0 Bilder");
            return;
        }
        } // Initial tree and gallery projection already published on startup.

        if (_manualScanRunning || imageCount == 0) return;

        var profile = await _galleryCatalogReader.ReadAsync(
            _ => _browseProfileRepository.Get());
        var missing = await _galleryCatalogReader.ReadAsync(
            token =>
            {
                token.ThrowIfCancellationRequested();
                return _browseCacheRepository.CountMissing(profile);
            });
        BrowseCacheBuildResult? result = null;

        if (missing > 0)
        {
            _browseCacheBuilding = true;
            _browseCacheCancellation?.Cancel();
            _browseCacheCancellation?.Dispose();
            _browseCacheCancellation = new CancellationTokenSource();

            // ONE presenter owns visible progress. Background cache work
            // must not replace an active manual Tag/Transfer presenter with
            // another window showing the meaningless default "Einlesen 0/0".
            var cacheOwner = new object();
            var progressWindow = AcquireProgressPresenter(cacheOwner);
            progressWindow.BeginCachePhase(cacheOwner);
            // A queued background task has not started. Show the existing
            // presenter only when real cache-build progress arrives.
            StatusText.Text = UiLanguage.Format("Vorschaudaten vorgemerkt: {0:N0}", missing);

            var progress = new Progress<BrowseCacheBuildProgress>(value =>
            {
                // The shared presenter publishes exactly the same phase
                // and count to the status bar, including manual overlays.
                progressWindow.Update(value, cacheOwner);
            });

            try
            {
                await _scheduler.EnqueueAsync(
                    "browse-cache:foreground",
                    SchedulerPriority.Background,
                    token =>
                    {
                        result = _browseCacheBuilder.BuildMissing(progress, token);
                        return Task.CompletedTask;
                    },
                    cancellationToken: _browseCacheCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("BrowseCache.BuildMissing", ex);
                StatusText.Text = UiLanguage.T("Vorschaudaten: Konvertierung fehlgeschlagen – Details im PCasa-Fehlerprotokoll");
                return;
            }
            finally
            {
                _browseCacheBuilding = false;
                progressWindow.EndCachePhase(cacheOwner);
                ReleaseProgressPresenter(cacheOwner);
            }
        }

        await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: true);

        if (result is { FailedImages: > 0 })
        {
            StatusText.Text =
                UiLanguage.Format("{0:N0} Bilder · {1:N0} Vorschaudaten konnten nicht erzeugt werden", imageCount, result.FailedImages);
        }
        else
        {
            if (!_isScanning && !_manualScanRunning && _visibleManualWindow is null)
            StatusText.Text = UiLanguage.Format("{0:N0} Bilder", imageCount);
        }
    }

    // Exactly one publisher at a time. The pending bit AND the publisher
    // lifetime change under the SAME gate. A Task.IsCompleted check was
    // insufficient: a request between the last pending check and task
    // completion could disappear forever, leaving moved images on screen.
    private readonly object _galleryRefreshGate = new();
    private bool _galleryRefreshRunning;
    private TaskCompletionSource? _galleryRefreshCompletion;
    private bool _galleryRefreshPending;
    private bool _galleryRefreshForceIndex;
    private bool _galleryRefreshWarmViewport;
    private bool _galleryRefreshHasExplicitRequest;
    // Coalesced publication can have been started by another caller.
    // Awaiting P0 packages donate their rank to its technical reads.
    private string? _galleryRefreshOwningPackageKey;

    private Task RefreshBrowseGalleryAsync(
        bool forceIndexReload,
        bool warmViewport = false,
        bool fromScanner = false)
    {
        if (fromScanner && DeferScanGalleryPublication())
            return Task.CompletedTask;
        var owner = fromScanner ? null : _scheduler.CurrentPackageKey;
        Task completion;
        lock (_galleryRefreshGate)
        {
            _galleryRefreshPending = true;
            _galleryRefreshForceIndex |= forceIndexReload;
            _galleryRefreshWarmViewport |= warmViewport;
            _galleryRefreshHasExplicitRequest |= !fromScanner;
            if (owner is not null)
                _galleryRefreshOwningPackageKey = owner;
            if (!_galleryRefreshRunning)
            {
                _galleryRefreshRunning = true;
                _galleryRefreshCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = DrainGalleryRefreshAsync(_galleryRefreshCompletion);
            }
            completion = _galleryRefreshCompletion!.Task;
        }
        if (owner is not null)
            _scheduler.AttachDependentWorkToPackage(owner,
                "catalog:gallery-summary:", "catalog:gallery-index:",
                "catalog:selection:");
        return completion;
    }

    private async Task DrainGalleryRefreshAsync(TaskCompletionSource completion)
    {
        // Never execute publication inside the caller's gate.
        await Task.Yield();
        try
        {
            while (!Dispatcher.HasShutdownStarted)
            {
                bool force = false, warm = false, manual = false, finished = false;
                lock (_galleryRefreshGate)
                {
                    if (!_galleryRefreshPending)
                    {
                        // This is the only legal idle transition. A new
                        // request from now on creates its OWN publisher.
                        _galleryRefreshRunning = false;
                        _galleryRefreshCompletion = null;
                        _galleryRefreshOwningPackageKey = null;
                        finished = true;
                    }
                    else
                    {
                        force = _galleryRefreshForceIndex;
                        warm = _galleryRefreshWarmViewport;
                        manual = _galleryRefreshHasExplicitRequest;
                        _galleryRefreshPending = false;
                        _galleryRefreshForceIndex = false;
                        _galleryRefreshWarmViewport = false;
                        _galleryRefreshHasExplicitRequest = false;
                    }
                }
                if (finished)
                {
                    completion.TrySetResult();
                    return;
                }

                try
                {
                    await RefreshBrowseGalleryCoreAsync(force, warm,
                        fromScanner: !manual);
                }
                catch (OperationCanceledException) when (!Dispatcher.HasShutdownStarted)
                {
                    // Filter/input versions can cancel an individual
                    // projection; the latest confirmed catalog still needs
                    // a publication. Retry after newer UI changes settle.
                    lock (_galleryRefreshGate)
                    {
                        _galleryRefreshPending = true;
                        _galleryRefreshForceIndex = true;
                        _galleryRefreshHasExplicitRequest |= manual;
                    }
                    await Task.Yield();
                }
            }

            lock (_galleryRefreshGate)
            {
                if (ReferenceEquals(_galleryRefreshCompletion, completion))
                {
                    _galleryRefreshRunning = false;
                    _galleryRefreshCompletion = null;
                }
            }
            completion.TrySetResult(); // Owner window is shutting down.
        }
        catch (Exception ex)
        {
            lock (_galleryRefreshGate)
            {
                if (ReferenceEquals(_galleryRefreshCompletion, completion))
                {
                    _galleryRefreshRunning = false;
                    _galleryRefreshCompletion = null;
                }
            }
            completion.TrySetException(ex);
        }
    }

    // A worker may sort using a snapshot of WPF folder order, but only
    // the CURRENT tree and selected sort own publication. This check is
    // small (one item per folder, not one item per image), and gives the
    // same result for startup, later folder changes and manual resorting.
    private static bool CanReuseStartupOrdering(
        IReadOnlyDictionary<long, int>? beforeTree,
        IReadOnlyDictionary<long, int> publishedTree,
        GallerySortOptions beforeSort, GallerySortOptions publishedSort)
        => beforeTree is not null && beforeSort == publishedSort &&
            beforeTree.Count == publishedTree.Count &&
            publishedTree.All(pair =>
                beforeTree.TryGetValue(pair.Key, out var earlier) &&
                earlier == pair.Value);

    private async Task RefreshBrowseGalleryCoreAsync(
        bool forceIndexReload,
        bool warmViewport = false,
        bool fromScanner = false)
    {
        if (!_browseInitialized ||
            _browseProfileRepository is null ||
            _browseCacheRepository is null ||
            _browsePageCache is null)
        {
            return;
        }

        var refresh = _browseRefreshRequests.Begin();
        var countReadSerial = _liveFolderCountSerial;

        // Keep the old image visible until the new catalog index and layout
        // are applied. A tree reflow during the database await must not
        // consume this pending anchor.
        QueueGalleryAnchorRestore();
        IReadOnlyList<PhotoFolderSummary> freshFolders = Array.Empty<PhotoFolderSummary>();
        var imageCount = 0;
        var startupSnapshot = _startupFolderSnapshot;
        _startupFolderSnapshot = null;
        if (!_startupGalleryPublished && startupSnapshot is { } snapshot &&
            !fromScanner)
        {
            // This is the first projection immediately after loading the
            // initial tree. No scanner has started yet. Reusing the SAME
            // immutable read avoids the second expensive photo-folder SQL
            // aggregation while preserving all existing refresh semantics.
            freshFolders = snapshot.Folders;
            imageCount = snapshot.ImageCount;
        }
        else
        {
            try
            {
                await _galleryCatalogReader.ReadAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        freshFolders = _imageCatalog.GetPhotoFolders();
                        imageCount = _imageCatalog.CountAll();
                        return Task.CompletedTask;
                    }, () => _browseRefreshRequests.IsCurrent(refresh));
            }
            catch (OperationCanceledException) { return; }
        }
        if (!_browseRefreshRequests.IsCurrent(refresh)) return;
        // Do not publish a new folder summary while the corresponding
        // thumbnail index is still queued behind SQLite/network work.
        // The last fully published gallery remains available during a move.
        var nextFolders = freshFolders
            .Where(folder => !IsPendingFolderRemovalHidden(folder.LocalPath))
            .ToArray();

        if (imageCount == 0)
        {
            PublishPhotoFolderSnapshot(nextFolders, imageCount, countReadSerial);
            System.Threading.Volatile.Write(ref _galleryHasIndexedImages, 0);
            _browseIndex = Array.Empty<BrowseThumbnailIndexEntry>();
            RebuildDragPublishedIndex();
            _dragSelectionPathCache.Clear();
            _displayedBrowseIndex = _browseIndex;
            _browsePageCache.SetIndex(_browseIndex);
            BrowseSurface.SetData(_browseIndex, _photoFolders, _browsePageCache);
            _selectedImageIds.Clear();
            _activePhotoFolderId = null;
            BrowseSurface.SetSelection(_selectedImageIds);
            SelectionText.Text = "Auswahl: 0";
            UpdateComparisonButtons();
            _ = UpdateSidebarContentAsync();
            EmptyPanel.Visibility = Visibility.Visible;
            StatusText.Text = UiLanguage.T("0 Bilder");
            RestorePendingGalleryAnchor();
            PublishStartupGalleryOnce();
            return;
        }

        var profile = _browseProfileRepository.Get();
        var tier = profile.ChooseForDisplayWidth(ThumbnailSizeSlider.Value);
        IReadOnlyList<BrowseThumbnailIndexEntry>? nextIndex = null;

        // Only the initial 500k-scale index benefits from this one-time
        // off-UI sort. Capture WPF tree order on the dispatcher, then perform
        // BOTH SQLite materialization and the same canonical gallery sort
        // on the existing foreground catalog-read worker. Never create a
        // second index or run a separate file-system scan.
        IReadOnlyDictionary<long, int>? startupTreeOrder = null;
        var startupSort = GallerySortOptions.Default;
        long indexSqlMs = 0, initialSortMs = 0;
        if (!_startupGalleryPublished && !fromScanner)
        {
            startupTreeOrder = CaptureGalleryFolderOrder();
            startupSort = CurrentGallerySort;
        }

        if (forceIndexReload || _activeBrowseTier != tier || _browseIndex.Count == 0)
        {
            if (!_isScanning && !_manualScanRunning && _visibleManualWindow is null)
                StatusText.Text = $"Galerieindex {tier}px laden...";
            IReadOnlyList<BrowseThumbnailIndexEntry> index = Array.Empty<BrowseThumbnailIndexEntry>();
            try
            {
                await _galleryCatalogReader.ReadAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        index = _browseCacheRepository.GetGalleryIndex(tier);
                        indexSqlMs = watch.ElapsedMilliseconds;
                        // The initial sort previously ran synchronously on
                        // WPF immediately before displaying the first image.
                        // Keep it next to the one SQL read, on this worker.
                        if (startupTreeOrder is not null)
                        {
                            watch.Restart();
                            index = GalleryFolderOrdering.Order(index,
                                startupTreeOrder, startupSort);
                            initialSortMs = watch.ElapsedMilliseconds;
                        }
                        return Task.CompletedTask;
                    }, () => _browseRefreshRequests.IsCurrent(refresh));
            }
            catch (OperationCanceledException) { return; }
            if (!_browseRefreshRequests.IsCurrent(refresh))
            {
                return;
            }

            // Keep the newly read index PRIVATE until the last awaited
            // catalog read. Neither index nor folders may be published alone.
            nextIndex = index;
            if (!_startupGalleryPublished)
                LogStartupStage($"gallery-index-read: {index.Count:N0} photos; " +
                    $"sqlite={indexSqlMs} ms; background-sort={initialSortMs} ms");
        }

        if (_selectedImageIds.Count > 0)
        {
            // A removed/unchecked folder can leave deleted image IDs selected.
            // The catalog, not the currently available thumbnail tier, decides
            // whether a selection is still valid.
            var checkedSelection = _selectedImageIds.ToHashSet();
            HashSet<long> existing = new();
            try
            {
                await _galleryCatalogReader.ReadAsync(
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        existing = _imageCatalog.GetExistingImageIds(checkedSelection);
                        return Task.CompletedTask;
                    }, () => _browseRefreshRequests.IsCurrent(refresh));
            }
            catch (OperationCanceledException) { return; }
            if (!_browseRefreshRequests.IsCurrent(refresh))
                return;
            if (_selectedImageIds.RemoveWhere(id =>
                    checkedSelection.Contains(id) && !existing.Contains(id)) > 0)
            {
                UpdateComparisonButtons();
                _ = UpdateSidebarContentAsync();
            }
        }

        // One WPF publication boundary. If the user began scrolling while
        // SQLite was loading, do not publish this stale scan snapshot over
        // their moving viewport. A single refresh runs after the quiet period.
        if (!_browseRefreshRequests.IsCurrent(refresh)) return;
        if (fromScanner && DeferScanGalleryPublication()) return;
        // A user may have moved after the initial async catalog read began.
        // Anchor the CURRENT viewport, never the obsolete starting position.
        if (fromScanner) QueueGalleryAnchorRestore();
        var liveFolder = fromScanner ? CurrentGalleryLiveEdge() : null;
        (long FolderId, bool AtStart, bool AtEnd,
            double StartScreenY, double EndScreenY)? followFolder =
            liveFolder is { } liveEdge && (liveEdge.AtStart || liveEdge.AtEnd)
                ? liveEdge : null;
        var navigationVersion = _explicitGalleryNavigationVersion;
        PublishPhotoFolderSnapshot(nextFolders, imageCount, countReadSerial);
        if (nextIndex is null && _publishedGallerySort != CurrentGallerySort)
            nextIndex = _browseIndex;
        if (nextIndex is not null)
        {
            IReadOnlyList<BrowseThumbnailIndexEntry> ordered = nextIndex;
            if (_pendingFolderRemovalPath is not null)
            {
                var visibleIds = nextFolders.Select(folder => folder.Id).ToHashSet();
                ordered = ordered.Where(image =>
                    visibleIds.Contains(image.PhotoFolderId)).ToArray();
            }
            var displayOrder = CaptureGalleryFolderOrder();
            // Only reuse the background sort when the actual published tree
            // and sort option have not changed while SQLite was being read.
            // New roots or user changes always fall back to the existing
            // unique canonical sort, preserving gallery jump semantics.
            var sameOrder = CanReuseStartupOrdering(startupTreeOrder,
                displayOrder, startupSort, CurrentGallerySort);
            if (!sameOrder)
                ordered = GalleryFolderOrdering.Order(ordered,
                    displayOrder, CurrentGallerySort);
            if (_restoreBadgeImageIds.Count > 0)
                ordered = ordered.Select(entry =>
                    entry with { HasRestore = _restoreBadgeImageIds.Contains(entry.ImageId) })
                    .ToArray();
            if (_activeBrowseTier != tier ||
                _browseIndex.Count != ordered.Count ||
                !_browseIndex.SequenceEqual(ordered))
            {
                _browseIndex = ordered;
                RebuildDragPublishedIndex();
                _dragSelectionPathCache.Clear();
            }
            _activeBrowseTier = tier;
            _publishedGallerySort = CurrentGallerySort;
        }
        System.Threading.Volatile.Write(ref _galleryHasIndexedImages,
            _browseIndex.Count > 0 ? 1 : 0);

        await ApplyTagFilterAsync();
        if (!_startupGalleryPublished)
            LogStartupStage("filtered-gallery-projection-ready");
        if (!_restoreBadgeChecking) _ = RefreshRestoreBadgesAsync();
        if (!_browseRefreshRequests.IsCurrent(refresh))
            return;

        BrowseSurface.SetThumbnailWidth(ThumbnailSizeSlider.Value);
        BrowseSurface.SetSelection(_selectedImageIds);
        EmptyPanel.Visibility = _displayedBrowseIndex.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        await Dispatcher.InvokeAsync(
            () =>
            {
                if (!_browseRefreshRequests.IsCurrent(refresh))
                    return;
                BrowseScrollViewer.UpdateLayout();
                RestoreGalleryPositionOnce();
                RestorePendingGalleryAnchor();
                if (followFolder is { } current &&
                    navigationVersion == _explicitGalleryNavigationVersion)
                    FollowGalleryFolderEdge(current.FolderId, current.AtStart,
                        current.AtStart ? current.StartScreenY : current.EndScreenY);
                else
                    BrowseSurface.SetViewport(
                        BrowseScrollViewer.VerticalOffset,
                        Math.Max(1, BrowseScrollViewer.ViewportHeight));
            },
            DispatcherPriority.Loaded);

        if (!_browseRefreshRequests.IsCurrent(refresh))
            return;
        if (!_startupGalleryPublished)
            LogStartupStage("saved-viewport-positioned");

        if (warmViewport && !_startupGalleryPublished)
        {
            // A Loaded priority callback can still precede the final
            // ScrollViewer extent measurement. Run one layout/anchor pass
            // at Render before deciding which actual packed pages to warm.
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_browseRefreshRequests.IsCurrent(refresh)) return;
                BrowseScrollViewer.UpdateLayout();
                RestoreGalleryPositionOnce();
                BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                    Math.Max(1, BrowseScrollViewer.ViewportHeight));
            }, DispatcherPriority.Render);
            if (!_browseRefreshRequests.IsCurrent(refresh)) return;
        }

        if (warmViewport)
        {
            // The visible area can contain multiple disjoint index ranges
            // when intervening photo folders are collapsed.
            var visibleWarm = BrowseSurface.WarmVisibleAsync();
            if (!_startupGalleryPublished)
            {
                // All pages were previously awaited before showing ANY image.
                // A single slow 32-photo cache pack (especially on slower
                // storage) could stall the whole gallery. Give the correct
                // viewport a bounded warm-up, then publish it. The same
                // outstanding page loads continue and paint via PageReady.
                try
                {
                    await visibleWarm.WaitAsync(TimeSpan.FromMilliseconds(750));
                    LogStartupStage("visible-pages-decoded");
                }
                catch (TimeoutException)
                {
                    LogStartupStage("visible-pages-warming-after-first-frame");
                }
            }
            else
            {
                await visibleWarm;
            }
            if (!_browseRefreshRequests.IsCurrent(refresh))
                return;
            BrowseSurface.InvalidateVisual();
        }

        PublishStartupGalleryOnce();

        if (string.IsNullOrWhiteSpace(TagSearchBox.Text))
            if (!_isScanning && !_manualScanRunning && _visibleManualWindow is null)
            StatusText.Text = UiLanguage.Format("{0:N0} Bilder", imageCount);
        SelectionText.Text = UiLanguage.Format("Auswahl: {0:N0}", _selectedImageIds.Count);
    }

    private void PublishStartupGalleryOnce()
    {
        if (_startupGalleryPublished || Dispatcher.HasShutdownStarted) return;
        _startupGalleryPublished = true;
        BrowseScrollViewer.Opacity = 1;
        BrowseScrollViewer.IsHitTestVisible = true;
        // The correct viewport has been warmed; let WPF draw it before
        // speculative thumbnail reads or background-monitor jobs start.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted || _shutdownStarted) return;
            _startupGalleryPresented = true;
            LogStartupStage("first-gallery-frame");
            BrowseSurface.DeferViewportPageRequests(false);
            BeginDeferredBackgroundMonitoring();
        }), DispatcherPriority.ContextIdle);
    }

    // Called only on Dispatcher, with a complete paired gallery snapshot.
    // Numeric counts and discovered tree nodes are incremental; never rebuild
    // the entire TreeView merely because a photo moved.
    private void PublishPhotoFolderSnapshot(
        IReadOnlyList<PhotoFolderSummary> folders, int imageCount,
        long countReadSerial)
    {
        _photoFolders = folders;
        // Only an equally recent full read supersedes live tree-only
        // counters. An earlier gallery request may finish after a newer
        // scanner commit, and must NOT make that folder's number go backward.
        if (countReadSerial == _liveFolderCountSerial)
        {
            _liveFolderCountSummaries.Clear();
            _liveCatalogImageCount = null;
        }
        _dragPublishedFolders = folders.ToDictionary(folder => folder.Id,
            folder => folder.LocalPath);
        _lastCatalogImageCount = imageCount;
        foreach (var discovered in folders)
        {
            if (_folderTreeItems.ContainsKey(discovered.Id)) continue;
            ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                DiscoveredPhotoFolderId: discovered.Id,
                DiscoveredPhotoFolderPath: discovered.LocalPath));
        }
        RefreshVisibleFolderCounts();
        UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
    }

    // Every committed image (not just the FIRST image) may change a visible
    // folder's count and the selected gallery. Coalesce notifications rather
    // than rebuilding the index 500,000 times during a bulk scan.
    private int _catalogPublicationPending;
    private bool _catalogPublicationBusy;
    private DispatcherTimer? _catalogPublicationTimer;
    private int _galleryHasIndexedImages;

    private void OnCatalogImageCommitted()
    {
        // A scanner commit for the visible photo folder is published by
        // OnCatalogImageIndexed, without a full index rebuild. New photos
        // elsewhere at the live edges use the same coalesced publisher below.
        if (_isScanning &&
            System.Threading.Volatile.Read(ref _galleryHasIndexedImages) != 0)
            return;
        ScheduleCatalogGalleryPublication();
    }

    private void ScheduleCatalogGalleryPublication()
    {
        // Single existing 750ms coalesced catalog publication owner.
        // Both catalog commits and live-edge commits from another folder
        // enter here; never refresh the complete 500k gallery per image.
        if (Dispatcher.HasShutdownStarted) return;
        if (System.Threading.Interlocked.Exchange(
            ref _catalogPublicationPending, 1) != 0) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Dispatcher.HasShutdownStarted || !_browseInitialized) return;
            if (_catalogPublicationTimer is null)
            {
                _catalogPublicationTimer = new DispatcherTimer(
                    DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(750)
                };
                _catalogPublicationTimer.Tick += CatalogPublicationTimer_Tick;
            }
            if (!_catalogPublicationTimer.IsEnabled)
                _catalogPublicationTimer.Start();
        }), DispatcherPriority.Background);
    }

    private async void CatalogPublicationTimer_Tick(object? sender, EventArgs e)
    {
        _catalogPublicationTimer?.Stop();
        if (DeferScanGalleryPublication()) return;
        if (_catalogPublicationBusy)
        {
            _catalogPublicationTimer?.Start();
            return;
        }
        if (System.Threading.Interlocked.Exchange(
            ref _catalogPublicationPending, 0) == 0) return;

        _catalogPublicationBusy = true;
        try
        {
            if (_browseInitialized && !_folderManagerOpen &&
                !_folderRemovalPending && !Dispatcher.HasShutdownStarted)
                await RefreshBrowseGalleryAsync(
                    forceIndexReload: true, warmViewport: false, fromScanner: true);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.CatalogPublication", ex);
        }
        finally
        {
            _catalogPublicationBusy = false;
            if (System.Threading.Volatile.Read(ref _catalogPublicationPending) != 0 &&
                !Dispatcher.HasShutdownStarted)
                _catalogPublicationTimer?.Start();
        }
    }

    // A single asynchronous archive snapshot is projected onto catalog IDs.
    // No original JPEG or NAS source path is opened by thumbnail rendering.
    private async Task RefreshRestoreBadgesAsync()
    {
        if (_restoreBadgeChecking || Dispatcher.HasShutdownStarted) return;
        var options = UndoOptions.Load();
        var archive = Path.Combine(options.DirectoryPath, "Versions");
        DateTime stamp;
        try { stamp = Directory.GetLastWriteTimeUtc(archive); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PCasaDiagnostics.Write("Undo.Badges", ex); return; }
        if (stamp == _restoreArchiveStamp) return;
        _restoreBadgeChecking = true;
        try
        {
            var verifiedPaths = await Task.Run(() =>
                new UndoHistoryService(options).GetVerifiedRestorablePaths(
                    CancellationToken.None));
            HashSet<long> ids = new();
            if (verifiedPaths.Count > 0)
                await _galleryCatalogReader.ReadAsync(token =>
                {
                    using var db = _database.OpenConnection();
                    using var command = db.CreateCommand();
                    command.CommandText = "SELECT id, local_path FROM images";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        if (verifiedPaths.Contains(reader.GetString(1)))
                            ids.Add(reader.GetInt64(0));
                    }
                    return Task.CompletedTask;
                });
            if (Dispatcher.HasShutdownStarted) return;
            _restoreArchiveStamp = stamp;
            if (!_restoreBadgeImageIds.SetEquals(ids))
            {
                _restoreBadgeImageIds = ids;
                if (_browseInitialized)
                    await RefreshBrowseGalleryAsync(forceIndexReload: true,
                        warmViewport: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or OperationCanceledException or InvalidOperationException)
        {
            PCasaDiagnostics.Write("Undo.Badges", ex);
        }
        finally { _restoreBadgeChecking = false; }
    }

    private bool _manualScanAllActive;

    private sealed record DurableScanRoot(long Id, string Path);

    // One stable step per original root; a restart never rediscovers a
    // different root order and does not replay already completed roots.
    private SchedulerTaskPackage BuildScanAllPackage(string key, string payload)
        => BuildScanAllPackage(key, payload, null, null);

    private SchedulerTaskPackage BuildScanAllPackage(string key, string payload,
        List<CompleteFolderScanStep>? callerScans, List<string>? callerFailures)
    {
        var roots = System.Text.Json.JsonSerializer.Deserialize<DurableScanRoot[]>(payload)
            ?? throw new InvalidDataException("Ungültiger Gesamtscan-Auftrag.");
        var package = new SchedulerTaskPackage(key);
        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            var position = i + 1;
            var display = new Progress<ScanProgress>(scan =>
            {
                ShowScannedPhotoFolder(scan);
                if (_visibleManualActiveKey == key)
                    StatusText.Text = $"Einlesen {position}/{roots.Length}: " +
                        $"{root.Path} · {scan.ImageFilesSeen:N0} geprüft";
            });
            var step = new CompleteFolderScanStep(this, root.Path,
                new ScanJobProgress(_scheduler, key, display), root.Id);
            callerScans?.Add(step);
            package.AddStep(async token =>
            {
                if (!_folderRepository.GetAll().Any(folder => folder.Id == root.Id))
                {
                    step.Dispose();
                    return;
                }
                try
                {
                    await step.ExecuteAsync(token);
                    step.Dispose();
                }
                catch (OperationCanceledException) when (step.SourceChanged ||
                    !_folderRepository.GetAll().Any(folder => folder.Id == root.Id))
                {
                    step.Dispose();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    callerFailures?.Add($"{root.Path}: {ex.Message}");
                    PCasaDiagnostics.Write("Catalog.ScanAll", ex);
                    step.Dispose();
                }
            });
        }
        return package;
    }

    private async void BrowseScanAll_Click(object sender, RoutedEventArgs e)
    {
        if (_manualScanAllActive || _folderRemovalPending)
            return;

        var roots = _dropTargetProjection.Select(entry => entry.Folder)
            .DistinctBy(folder => folder.Id).ToArray();
        if (roots.Length == 0)
        {
            PcasaDialog.Show(
                this,
                UiLanguage.T("Im Ordnermanager sind noch keine überwachten Ordner definiert."),
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Scan All is ONE manual package, not a chain of independent priority
        // jobs. Every directory keeps its own resumable scanner checkpoint;
        // the package cursor advances only after that directory is complete.
        var key = $"catalog:scan-all:{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var scans = new List<CompleteFolderScanStep>();
        var failures = new List<string>();
        var payload = System.Text.Json.JsonSerializer.Serialize(
            roots.Select(root => new DurableScanRoot(root.Id, root.LocalPath)).ToArray());
        var package = BuildScanAllPackage(key, payload, scans, failures);
        _manualScanAllActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        _isScanning = true;
        _activeScanCancellation = cancellation;
        _activeScanFolderId = null;
        _cacheMaintenanceCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        UpdateManualScanControls();
        BeginVisibleManualOperation(key, UiLanguage.T("Alle Ordner einlesen"), 0);

        try
        {
            await _scheduler.EnqueueDurablePackageAsync(package,
                "manual-scan-all-v1", payload, SchedulerPriority.ManualAction,
                SchedulerWorkKind.CatalogScan, cancellation.Token);
        }
        catch (OperationCanceledException) when (_folderRemovalPending ||
            cancellation.IsCancellationRequested)
        {
            StatusText.Text = UiLanguage.T("Gesamtscan abgebrochen · abgeschlossene Ordner bleiben katalogisiert");
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.ScanAll", ex);
            failures.Add(ex.Message);
        }
        finally
        {
            foreach (var scan in scans)
                scan.Dispose();
            EndVisibleManualOperation(key);
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _manualScanAllActive = false;
            _manualScanRunning = _initialScanActive || _manualContextScanActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            QueuePendingInitialScans();
            if (_backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }

        if (_folderRemovalPending)
            return;
        await EnsureBrowseCacheAndRefreshAsync();

        if (failures.Count > 0)
        {
            PcasaDialog.Show(
                this,
                UiLanguage.T("Einige Ordner konnten nicht eingelesen werden:") + "\n\n" +
                    string.Join("\n", failures),
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void BrowseFolderManager_Click(object sender, RoutedEventArgs e)
    {
        if (_folderManagerOpen || _folderManagerCommitPending) return;
        _folderManagerOpen = true;
        FolderManagerSelection? selection = null;
        try
        {
            // The folder manager is a read-only foreground view until OK.
            // It must never wait behind a deferred or running durable P0
            // scan that was started by Explorer registration. The same
            // independent gallery reader already protects catalog snapshots.
            // One immutable catalog read: configuration and already-indexed
            // photo folders travel together. Opening the manager must not
            // enumerate SMB directories, and no catalog row is modified.
            var folderSnapshot = await _galleryCatalogReader.ReadAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return (
                        Configuration: ReadFolderTreeConfigurationSnapshot(),
                        CatalogFolders: _imageCatalog.GetPhotoFolders());
                });
            if (Dispatcher.HasShutdownStarted) return;
            var window = new FolderManagerWindow(
                folderSnapshot.Configuration.Select(root =>
                    (root.Folder, root.ExcludedPaths)).ToArray(),
                _foregroundReadService,
                serverDatabaseConfigured: false,
                catalogPhotoFolders: folderSnapshot.CatalogFolders,
                includedByRoot: folderSnapshot.Configuration.ToDictionary(
                    root => root.Folder.Id, root => root.IncludedPaths))
            {
                Owner = this
            };
            // No checkbox mutates SQLite. Cancel closes without side effects.
            if (window.ShowDialog() == true)
                selection = window.SelectedConfiguration;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderManager.Open", ex);
            PcasaDialog.Show(this, UiLanguage.T("Der Ordnermanager konnte nicht geöffnet werden.") + "\n\n" +
                ex.Message, "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _folderManagerOpen = false;
        }

        if (selection is null) return; // Abbruch / X: zero persisted changes.

        var durableCommitted = false;
        const string operationKey = "folder-manager:commit-selection";
        _folderManagerCommitPending = true;
        BeginVisibleManualOperation(operationKey, UiLanguage.T("Ordnerauswahl übernehmen"), 1);
        try
        {
            // One user operation owns the persisted decision, the updated
            // folder-tree snapshot AND publication of its catalog view.
            await _scheduler.EnqueueAsync(
                operationKey, SchedulerPriority.ManualAction,
                async token =>
                {
                    _scheduler.ReportProgress(operationKey,
                        UiLanguage.T("Ordnerauswahl übernehmen"), 0, 1);
                    var modified = new FolderManagerSelectionService(_database)
                        .Apply(selection, token);
                    durableCommitted = true;
                    IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths,
                        IReadOnlyList<string> IncludedPaths)> snapshot =
                        Array.Empty<(MonitoredFolder, IReadOnlyList<string>, IReadOnlyList<string>)>();
                    if (modified)
                        snapshot = ReadFolderTreeConfigurationSnapshot();

                    // Preferences persist only after the catalog decision.
                    // No I/O is performed on the WPF dispatcher.
                    if (BackgroundScanSettings.Load().Enabled != selection.BackgroundScanEnabled)
                        new BackgroundScanSettings
                        {
                            Enabled = selection.BackgroundScanEnabled
                        }.Save();

                    if (modified)
                    {
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            _initialScanAttempted.Clear();
                            _dropTargetProjection = snapshot;
                            _backgroundExcludedPaths.Clear();
                            _backgroundIncludedPaths.Clear();
                            foreach (var rule in snapshot)
                            {
                                _backgroundExcludedPaths[rule.Folder.Id] = rule.ExcludedPaths;
                                _backgroundIncludedPaths[rule.Folder.Id] = rule.IncludedPaths;
                            }
                            _dragTargetValidity.Clear();
                            RefreshFolderTree(preserveGalleryAnchor: true);
                            StatusText.Text =
                                UiLanguage.T("Ordnerauswahl übernommen · Einlesen mit Benutzerpriorität");
                            // Durable initial scans can enter their own
                            // scheduler lane, but cannot overtake this
                            // still-running manual catalog publication.
                            QueuePendingInitialScans();
                            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                                warmViewport: false);
                        }).Task.Unwrap();
                    }
                    _scheduler.ReportProgress(operationKey,
                        UiLanguage.T("Ordnerauswahl übernommen"), 1, 1);
                });
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderManager.Commit", ex);
            PcasaDialog.Show(this,
                durableCommitted
                    ? "Die Ordnerauswahl wurde gespeichert, aber die Aktualisierung " +
                      "der Ansicht oder Einstellungen ist fehlgeschlagen.\n\n" + ex.Message
                    : UiLanguage.T("Die Ordnerauswahl konnte nicht übernommen werden.") + "\n\n" +
                      ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(operationKey);
            _folderManagerCommitPending = false;
        }
    }

    private void BrowseStatus_Click(object sender, MouseButtonEventArgs e)
    {
        _browseProgressWindow?.ShowFromStatus();
    }

    private void BrowseCleanupStatusChanged(string message)
        => StatusText.Text = message;

    private void OnFolderExclusionCommitted(long folderId, string path,
        bool excluded, IReadOnlyList<string>? committedRules = null,
        IReadOnlyList<string>? committedInclusions = null)
    {
        // Stop any running scan of this monitored root before modifying its
        // stored index; without cancellation it could re-add excluded photos.
        if (_isScanning && _activeScanFolderId == folderId)
        {
            _activeScanCancellation?.Cancel();
        }

        // The scheduler has already read this committed snapshot. Do not
        // block WPF on a second SQLite read during publication.
        var rules = committedRules ?? _folderRepository.GetExcludedPaths(folderId);
        _backgroundExcludedPaths[folderId] = rules;
        var inclusions = committedInclusions ?? _dropTargetProjection
            .FirstOrDefault(entry => entry.Folder.Id == folderId).IncludedPaths
            ?? Array.Empty<string>();
        _backgroundIncludedPaths[folderId] = inclusions;
        _dropTargetProjection = _dropTargetProjection
            .Select(entry => entry.Folder.Id == folderId
                ? (entry.Folder, ExcludedPaths: rules, IncludedPaths: inclusions)
                : entry)
            .ToArray();
        _dragTargetValidity.Clear();
        if (excluded)
        {
            ForgetFolderActivity(path);
            // Drop any stale watcher events queued while the path was included.
            foreach (var key in _backgroundPending.Keys.ToArray())
            {
                if (FolderExclusionRules.IsAtOrBelow(
                    _backgroundPending[key].Path, path))
                {
                    _backgroundPending.Remove(key);
                }
            }
        }
        else
        {
            // Re-inclusion must actively repopulate the folder, even if the
            // parent has already been monitored for months.
            QueueBackgroundChange(new BackgroundFolderChange(
                folderId, path,
                BackgroundFolderChangeKind.ScanDirectory,
                Recursive: true));
        }
    }

    // Tree nodes represent both actual photo directories AND parents such
    // as S:\\ or an indexed root with pictures only in its descendants.
    // A path-based target is mandatory: parent nodes deliberately have no
    // photo-folder ID. Use only the already-published local catalog summary
    // and visible tree order; never enumerate the disk or wait for a scan.
    private long? ResolveTreePhotoFolder(FolderTreeTag tag)
    {
        if (tag.PhotoFolderId is long ownId) return ownId;
        // During a scan the tree counters can be newer than the deliberately
        // deferred gallery index. Include those already CONFIRMED SQLite
        // folder identities so S:\\ root clicks still find their descendants,
        // even before a full gallery refresh is permitted.
        IReadOnlyList<PhotoFolderSummary> known = _photoFolders;
        if (_liveFolderCountSummaries.Count > 0)
        {
            var latest = _photoFolders.ToDictionary(folder => folder.Id);
            foreach (var (id, folder) in _liveFolderCountSummaries)
            {
                if (folder is null) latest.Remove(id);
                else latest[id] = folder;
            }
            known = latest.Values.ToArray();
        }
        var descendants = PhotoFoldersBelowTreePath(tag.FullPath,
            known, CaptureGalleryFolderOrder());
        // Prefer an already-published child section. A single indexed
        // layout pass stays responsive even for an S:\\ root containing
        // thousands of photo folders; missing candidates use one fallback.
        if (descendants.Count == 0) return null;
        return BrowseSurface.GetFirstVisibleFolderId(descendants.ToHashSet())
            ?? descendants[0];
    }

    internal static IReadOnlyList<long> PhotoFoldersBelowTreePath(
        string path, IReadOnlyList<PhotoFolderSummary> folders,
        IReadOnlyDictionary<long, int> treeOrder)
        => folders.Where(folder =>
                FolderExclusionRules.IsAtOrBelow(folder.LocalPath, path))
            .OrderBy(folder => treeOrder.TryGetValue(folder.Id, out var position)
                ? position : int.MaxValue)
            .ThenBy(folder => folder.CanonicalPath,
                StringComparer.OrdinalIgnoreCase)
            .Select(folder => folder.Id).ToArray();

    private async void BrowseFoldersTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_syncingFolderSelection ||
            e.NewValue is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            return;
        }

        var clickStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var destinationId = ResolveTreePhotoFolder(tag);
        var resolutionMs = System.Diagnostics.Stopwatch
            .GetElapsedTime(clickStarted).TotalMilliseconds;
        if (destinationId is null || resolutionMs > 150)
            PCasaDiagnostics.WriteMessage("Gallery.FolderClick",
                $"path={tag.FullPath}; selected={destinationId?.ToString() ?? "none"}; " +
                $"catalogFolders={_photoFolders.Count}; " +
                $"visiblePhotos={_displayedBrowseIndex.Count}; " +
                $"resolveMs={resolutionMs:F0}");

        // Inversion is a folder-local toggle, never a global permanent
        // selection mode. Reset its visual state on a tree-folder change,
        // WITHOUT performing another inversion.
        if (_invertedGallerySelectionFolderId is long invertedId &&
            destinationId != invertedId)
            ResetGalleryInvertSelectionToggle();

        // Register the new folder BEFORE clearing the old GPS photo
        // selection. Clearing it starts asynchronous map/filmstrip UI work;
        // none of that work can subsequently restore the old photo anchor.
        MarkExplicitGalleryNavigation(destinationId);

        // Explicit navigation to a different folder ends the previous
        // thumbnail selection. Scrolling does not alter the selection.
        if (_selectedImageIds.Count > 0)
        {
            _selectedImageIds.Clear();
            _gallerySelection.Reset();
            ApplyGallerySelection();
        }
        _appearance.SelectedTreePath = tag.FullPath;
        SaveAppearance();

        // A deliberate click changes the active search root in the two
        // folder-scoped modes. The previous implementation captured it only
        // once, so after moving filtered pictures away ALL other tree clicks
        // appeared to do nothing because no folder had an offset in the
        // stale result set. The single existing search pipeline owns reflow.
        if (_searchWithinTree && !string.IsNullOrWhiteSpace(TagSearchBox?.Text))
        {
            CaptureTagSearchRoot();
            try { await ApplyTagFilterAsync(scrollToTop: true); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Search.FolderSelection", ex);
                StatusText.Text = "Ordnerfilter konnte nicht aktualisiert werden.";
                return;
            }
            // Folder-scoped search also accepts a parent or drive node.
            // Search results already contain the chosen subtree; never
            // start a second catalog scan merely to navigate it.
            if (destinationId is long filteredId)
            {
                if (BrowseSurface.GetFolderOffset(filteredId) is double filteredTop)
                    ScrollToExplicitGalleryFolder(filteredId, filteredTop);
                else
                    TryNavigateToFilteredEmptyFolder(filteredId);
            }
            return;
        }

        if (destinationId is long photoFolderId)
            await NavigateToPhotoFolderAsync(item, photoFolderId);
    }

    private void FoldersTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_syncingFolderSelection || _rebuildingFolderTree) return;
        var source = e.OriginalSource as DependencyObject;
        if (source is null ||
            FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null)
            return;
        var item = FindAncestor<TreeViewItem>(source);
        if (item?.IsSelected == true && item.Tag is FolderTreeTag tag &&
            ResolveTreePhotoFolder(tag) is long folderId)
        {
            MarkExplicitGalleryNavigation(folderId);
            _ = NavigateToPhotoFolderAsync(item, folderId);
        }
    }

    private async Task NavigateToPhotoFolderAsync(TreeViewItem item, long photoFolderId)
    {
        var navigationVersion = _explicitGalleryNavigationVersion;
        _activePhotoFolderId = photoFolderId;
        // A previous zero-result folder's temporary header must not displace
        // the next selected folder's real gallery coordinates.
        if (BrowseSurface.EmptyNavigationFolderId is long previous &&
            previous != photoFolderId)
        {
            BrowseSurface.SetEmptyNavigationFolder(null);
            EmptyPanel.Visibility = _displayedBrowseIndex.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // If "hide collapsed folders" omitted this indexed folder from the
        // virtual layout, first reveal its existing ancestors. Previously we
        // awaited one or even TWO complete catalog/index refreshes before
        // trying this zero-I/O solution. During an active scan that appeared
        // to make clicks on other folders stop working entirely.
        var offset = BrowseSurface.GetFolderOffset(photoFolderId);
        if (offset is null && _appearance.HideCollapsedFolders &&
            string.IsNullOrWhiteSpace(TagSearchBox.Text))
        {
            _bulkFolderExpansion = true;
            try
            {
                // A selected drive/parent is not necessarily the photo
                // directory. Expand the ACTUAL target's ancestors, not just
                // S:\\'s ancestors (there are none), before testing for a
                // visible gallery section.
                var visualTarget = _folderTreeItems.TryGetValue(
                    photoFolderId, out var photoItem) ? photoItem : item;
                FolderTreeNavigation.ExpandAncestors(FoldersTree, visualTarget);
            }
            finally { _bulkFolderExpansion = false; }
            UpdateGalleryFolderVisibility(preserveGalleryAnchor: false);
            offset = BrowseSurface.GetFolderOffset(photoFolderId);
        }

        if (offset is double cached)
        {
            // Even if a background scanner has fresher counts, the existing
            // thumbnail index must navigate IMMEDIATELY. Publishing those
            // deferred changes can happen after WPF has painted this target.
            ScrollToExplicitGalleryFolder(photoFolderId, cached);
            UpdateActiveFolderDisplay(photoFolderId,
                updateTreeSelection: false);
            // Do not immediately start a full index refresh after a
            // successful cached click. It previously reflowed S:\\ folders
            // back to their OLD viewport and made the selection look dead.
            // The existing live-edge publisher will reconcile scanner work
            // when the user reaches an actual gallery edge.
            return;
        }

        // A filtered folder with zero matching images still owns an exact
        // section in the gallery. Don't query SQLite or drop the click just
        // because the visible thumbnail index contains no matching entry.
        if (TryNavigateToFilteredEmptyFolder(photoFolderId))
            return;

        // This folder was committed after the last gallery projection, or
        // was omitted while its ancestors were collapsed. A complete SQLite
        // scan of hundreds of thousands of photos used to run here, could
        // remain pending through concurrent NAS indexing, and made S: appear
        // unresponsive. Load ONLY this folder's indexed local thumbnail rows.
        if (!string.IsNullOrWhiteSpace(TagSearchBox?.Text))
        {
            StatusText.Text = "Keine Filtertreffer in diesem Ordner. " +
                "Suchbereich ändern oder Filter löschen.";
            return;
        }
        if (!_browseInitialized || _folderRemovalPending) return;

        var published = await PublishSelectedFolderIndexAsync(
            photoFolderId, navigationVersion);
        if (!published || Dispatcher.HasShutdownStarted || _shutdownStarted ||
            navigationVersion != _explicitGalleryNavigationVersion ||
            (FoldersTree.SelectedItem as TreeViewItem)?.Tag is not
            FolderTreeTag selectedTag ||
            !_photoFolders.Any(folder => folder.Id == photoFolderId &&
                FolderExclusionRules.IsAtOrBelow(
                    folder.LocalPath, selectedTag.FullPath)))
            return;

        if (BrowseSurface.GetFolderOffset(photoFolderId) is double fresh)
        {
            ScrollToExplicitGalleryFolder(photoFolderId, fresh);
            UpdateActiveFolderDisplay(photoFolderId,
                updateTreeSelection: false);
        }
    }

    private bool TryNavigateToFilteredEmptyFolder(long photoFolderId)
    {
        if (!AttributeFilterActive &&
            string.IsNullOrWhiteSpace(TagSearchBox?.Text) &&
            _gpsAreaFilterIds is null)
            return false;
        if (BrowseSurface.HiddenFolderIds.Contains(photoFolderId) ||
            !_photoFolders.Any(folder => folder.Id == photoFolderId) ||
            _displayedBrowseIndex.Any(image =>
                image.PhotoFolderId == photoFolderId))
            return false;

        // Both indices have the same authoritative folder order. Find the
        // requested folder in the COMPLETE local index, then count only
        // visible images in preceding folders. No NAS reads, no second
        // gallery index, and no unfiltered thumbnails are ever displayed.
        var orderedFolders = new Dictionary<long, int>();
        foreach (var image in _browseIndex)
            if (!orderedFolders.ContainsKey(image.PhotoFolderId))
                orderedFolders.Add(image.PhotoFolderId, orderedFolders.Count);
        if (!orderedFolders.TryGetValue(photoFolderId, out var targetOrder))
            return false;

        var insertBefore = 0;
        foreach (var image in _displayedBrowseIndex)
        {
            if (!orderedFolders.TryGetValue(image.PhotoFolderId, out var order) ||
                order >= targetOrder) break;
            insertBefore++;
        }

        BrowseSurface.SetEmptyNavigationFolder(photoFolderId, insertBefore);
        // A completely empty filtered index normally displays a full-page
        // empty overlay. The selected folder now has a real gallery header
        // and its own localized no-results message, so reveal that section.
        EmptyPanel.Visibility = Visibility.Collapsed;
        BrowseScrollViewer.UpdateLayout();
        if (BrowseSurface.GetFolderOffset(photoFolderId) is not double top)
            return false;
        ScrollToExplicitGalleryFolder(photoFolderId, top);
        UpdateActiveFolderDisplay(photoFolderId, updateTreeSelection: false);
        return true;
    }

    private async Task<bool> PublishSelectedFolderIndexAsync(
        long photoFolderId, long navigationVersion)
    {
        if (_browseCacheRepository is null || _activeBrowseTier is not int tier)
            return false;
        try
        {
            // Read-only, bounded foreground lane; neither NAS file I/O nor
            // a queued background scan is allowed in this click path.
            var snapshot = await _galleryCatalogReader.ReadAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                var images = _browseCacheRepository.GetGalleryFolderIndex(
                    tier, photoFolderId);
                var folder = _imageCatalog.GetPhotoFolderSummary(photoFolderId);
                return (Images: images, Folder: folder);
            });
            if (Dispatcher.HasShutdownStarted || _shutdownStarted ||
                navigationVersion != _explicitGalleryNavigationVersion ||
                snapshot.Folder is null || snapshot.Images.Count == 0)
            {
                if (navigationVersion == _explicitGalleryNavigationVersion)
                    PCasaDiagnostics.WriteMessage("Gallery.ClickedFolderProjection",
                        $"folderId={photoFolderId}; no indexed SQLite photos; " +
                        $"catalogFolder={snapshot.Folder is not null}; " +
                        $"fetched={snapshot.Images.Count}");
                return false;
            }

            // The selected folder has changed since the older full-gallery
            // index was published. Replace its local section exactly once
            // without replacing every other photo in the gallery.
            var folder = snapshot.Folder;
            var order = CaptureGalleryFolderOrder();
            var options = CurrentGallerySort;
            var comparer = GalleryFolderOrdering.CreateComparer(order, options);
            var added = GalleryFolderOrdering.Order(snapshot.Images, order, options);
            var retained = _browseIndex
                .Where(image => image.PhotoFolderId != photoFolderId)
                .ToArray();
            if (_publishedGallerySort != options)
                retained = GalleryFolderOrdering.Order(retained, order, options)
                    .ToArray();
            var insert = Array.BinarySearch(retained, added[0], comparer);
            if (insert < 0) insert = ~insert;
            var result = new BrowseThumbnailIndexEntry[
                retained.Length + added.Count];
            Array.Copy(retained, 0, result, 0, insert);
            for (var i = 0; i < added.Count; i++)
                result[insert + i] = added[i];
            Array.Copy(retained, insert, result, insert + added.Count,
                retained.Length - insert);

            // Fence an older in-flight full-index projection. It must never
            // overwrite this explicit S: click with its stale folder set.
            _browseRefreshRequests.Invalidate();
            _browseIndex = result;
            _publishedGallerySort = options;
            _photoFolders = _photoFolders
                .Where(existing => existing.Id != photoFolderId)
                .Append(folder)
                .OrderBy(existing => existing.CanonicalPath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _dragPublishedFolders[folder.Id] = folder.LocalPath;
            if (!_folderTreeItems.ContainsKey(folder.Id))
                ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                    DiscoveredPhotoFolderId: folder.Id,
                    DiscoveredPhotoFolderPath: folder.LocalPath));
            await ApplyTagFilterAsync();
            if (navigationVersion != _explicitGalleryNavigationVersion)
                return false;
            BrowseScrollViewer.UpdateLayout();
            PCasaDiagnostics.WriteMessage("Gallery.ClickedFolderProjection",
                $"folderId={photoFolderId}; fetched={snapshot.Images.Count}; " +
                $"published={_browseIndex.Count}; " +
                $"offset={BrowseSurface.GetFolderOffset(photoFolderId)?.ToString("F0") ?? "none"}");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.ClickedFolderProjection", ex);
            return false;
        }
    }

    // One wheel event moves the gallery exactly once. No acceleration,
    // inertia, accumulated target position, timer, or delayed scroll work.
    private void BrowseScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            return;

        PicasaGalleryScrollBar.StopMotion();
        const double pixelsPerWheelNotch = 96.0;
        var movement = e.Delta / 120.0 * pixelsPerWheelNotch;
        var previous = BrowseScrollViewer.VerticalOffset;
        var target = Math.Clamp(
            BrowseScrollViewer.VerticalOffset - movement,
            0.0,
            BrowseScrollViewer.ScrollableHeight);
        BrowseScrollViewer.ScrollToVerticalOffset(target);
        if (Math.Abs(previous - target) > 0.5)
            RegisterGalleryUserScroll();
        e.Handled = true;
    }

    private void BrowseScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_browseInitialized)
        {
            return;
        }

        BrowseSurface.SetViewport(
            e.VerticalOffset,
            Math.Max(1, e.ViewportHeight));

        // A filtered search list and a sidebar resize must not change
        // the user's folder-tree focus. They change gallery geometry,
        // not the selected source folder.
        if (e.VerticalChange == 0 || _pendingGalleryAnchor is not null ||
            _restoringGalleryLayoutAnchor)
        {
            return;
        }

        // Startup layout changes fire ScrollChanged with a nonzero delta
        // even though the USER never touched the gallery. Previously this
        // registered explicit navigation and permanently canceled restoring
        // the persisted last image. Actual wheel/keyboard/folder input still
        // registers navigation through its existing explicit handlers.
        if (!_galleryPositionRestored && _appearance.RestoreGalleryPosition)
            return;

        // A tree click scrolls programmatically. Its own ScrollChanged
        // cannot erase the chosen folder intent and expose a stale GPS
        // reflow anchor. Real wheel input already registers navigation.
        if (_scrollingToExplicitGalleryFolder) return;
        var folderId = BrowseSurface.GetFolderIdAtOffset(e.VerticalOffset + 4);
        if (_explicitGalleryFolderTargetId is not long intended ||
            folderId != intended)
            RegisterGalleryUserScroll();
        else if (GalleryAtLiveEdge())
            PublishDeferredScanGallery();
        if (!string.IsNullOrWhiteSpace(TagSearchBox?.Text)) return;

        if (folderId is null || _activePhotoFolderId == folderId)
        {
            return;
        }

        _activePhotoFolderId = folderId;
        // Scrolling changes only which gallery section is visible. It must
        // NEVER select, expand or scroll the independent folder tree. Only a
        // deliberate thumbnail/folder click may change tree selection.
        UpdateActiveFolderDisplay(folderId.Value, updateTreeSelection: false);
    }

    private bool _thumbnailSizePreviewActive;
    private bool _thumbnailSizeFrameScheduled;

    private void ThumbnailSizeThumb_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (!_browseInitialized || _thumbnailSizePreviewActive) return;
        _thumbnailSizePreviewActive = true;
        _thumbnailSizeCancellation?.Cancel();
        ReflowSpacingPreservingScroll(BrowseSurface.BeginSizePreview);
    }

    private void ThumbnailSizeThumb_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_thumbnailSizePreviewActive)
        {
            _thumbnailSizePreviewActive = false;
            ReflowSpacingPreservingScroll(BrowseSurface.EndSizePreview);
        }
        SaveFooterSettingsNow();
        ScheduleThumbnailTierRefresh();
    }

    private void BrowseThumbnailSizeSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_browseInitialized) return;
        _appearance.ThumbnailWidth = Math.Clamp(e.NewValue, 72, 600);
        if (!_thumbnailSizePreviewActive) ScheduleFooterSettingsSave();

        // Immediate reflow from already decoded pages. The cache tier is
        // NOT reloaded during mouse dragging. Limit layout to one WPF frame.
        if (!_thumbnailSizeFrameScheduled)
        {
            _thumbnailSizeFrameScheduled = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _thumbnailSizeFrameScheduled = false;
                if (!_browseInitialized || Dispatcher.HasShutdownStarted) return;
                ReflowSpacingPreservingScroll(() =>
                    BrowseSurface.SetThumbnailWidth(ThumbnailSizeSlider.Value));
            }), DispatcherPriority.Render);
        }
        if (!_thumbnailSizePreviewActive) ScheduleThumbnailTierRefresh();
    }

    private void ScheduleThumbnailTierRefresh()
    {
        _thumbnailSizeCancellation?.Cancel();
        _thumbnailSizeCancellation?.Dispose();
        _thumbnailSizeCancellation = new CancellationTokenSource();
        if (_thumbnailSizePreviewActive || _browseCacheBuilding) return;
        _ = RefreshThumbnailTierAfterIdleAsync(_thumbnailSizeCancellation.Token);
    }

    private async Task RefreshThumbnailTierAfterIdleAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(260, cancellation);
            if (cancellation.IsCancellationRequested || _thumbnailSizePreviewActive ||
                !_browseInitialized || _browseCacheBuilding ||
                _browseProfileRepository is null)
                return;

            var profile = await _galleryCatalogReader.ReadAsync(
                token => { token.ThrowIfCancellationRequested(); return _browseProfileRepository.Get(); },
                cancellationToken: cancellation);

            if (cancellation.IsCancellationRequested || _thumbnailSizePreviewActive)
                return;
            var nextTier = profile.ChooseForDisplayWidth(ThumbnailSizeSlider.Value);
            if (_activeBrowseTier != nextTier)
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.ThumbnailTierAfterIdle", ex); }
    }

    private void BrowseGalleryViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_browseInitialized)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            RestorePendingGalleryAnchor();
            BrowseSurface.SetViewport(
                BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }, DispatcherPriority.Loaded);
    }

    private readonly GallerySelection _gallerySelection = new();

    private void BrowseSurface_EmptyAreaClicked(object? sender, EventArgs e)
    {
        if (_viewerOpen || _selectedImageIds.Count == 0) return;
        _selectedImageIds.Clear();
        _gallerySelection.Reset();
        ApplyGallerySelection();
    }

    private void BrowseSurface_ImageClicked(
        object? sender,
        BrowseImageClickedEventArgs e)
    {
        // A genuine thumbnail click is independent of GPS map focus; it
        // supersedes any earlier tree-folder anchor.
        MarkExplicitGalleryNavigation();
        _gallerySelection.Click(e.ImageId, e.Modifiers,
            _displayedBrowseIndex, _selectedImageIds, BrowseSurface.HiddenFolderIds);
        ApplyGallerySelection();

        // Explicit thumbnail selection identifies its source folder. Ordinary
        // scroll does NOT select a different folder in the independent tree.
        var selectedImage = _displayedBrowseIndex.FirstOrDefault(entry =>
            entry.ImageId == e.ImageId);
        if (selectedImage is not null)
        {
            _activePhotoFolderId = selectedImage.PhotoFolderId;
            UpdateActiveFolderDisplay(selectedImage.PhotoFolderId, updateTreeSelection: true);
        }
        Keyboard.Focus(BrowseSurface);
    }

    private void ApplyGallerySelection()
    {
        BrowseSurface.SetSelection(_selectedImageIds);
        SelectionText.Text = UiLanguage.Format("Auswahl: {0:N0}", _selectedImageIds.Count);
        UpdateComparisonButtons();
        _ = UpdateSidebarContentAsync();
    }

    private void BrowseWindow_Closed(object? sender, EventArgs e)
    {
        _appearance.ThumbnailWidth = ThumbnailSizeSlider.Value;
        // Normal PCasa shutdown saved the actual gallery/tree position in
        // Closing, BEFORE awaiting scheduler disposal. Do not overwrite that
        // snapshot with late scan-driven layout changes during shutdown.
        if (!_shutdownStarted)
            SaveGalleryPosition();
        SaveAppearance();
        CatalogChanges.ImageCommitted -= OnCatalogImageCommitted;
        CatalogChanges.ImageIndexed -= OnCatalogImageIndexed;
        _catalogPublicationTimer?.Stop();
        _browseProgressWindow?.Finish();
        _browseProgressWindow = null;
        _thumbnailSizeCancellation?.Cancel();
        _thumbnailSizeCancellation?.Dispose();
        _browseRefreshRequests.Invalidate();
        _browseCacheCancellation?.Cancel();
        _browseCacheCancellation?.Dispose();
        StopTagWork();
        _browsePageCache?.Dispose();
    }
}
