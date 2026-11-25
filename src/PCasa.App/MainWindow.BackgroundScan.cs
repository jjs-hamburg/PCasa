using System.IO;
using System.Windows;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private readonly Dictionary<string, PendingBackgroundScan> _backgroundPending =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _backgroundOrderChanged;
    // These are references into the already existing one-batch scan dispatcher;
    // they expose its real waiting tasks without spawning extra operations.
    private PendingBackgroundScan[]? _backgroundInFlight;
    private int _backgroundInFlightIndex;
    private readonly HashSet<string> _backgroundSuppressed =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _backgroundLiveTaskKey;
    private int _backgroundLiveChecked;
    private readonly HashSet<long> _backgroundKnownFolderIds = new();
    private readonly HashSet<long> _backgroundDeferredInventoryRoots = new();
    private readonly HashSet<long> _gpsSourceAuditedRoots = new();
    private bool _backgroundFirstMonitorSnapshot = true;
    // One idle verification selects the least recently checked reachable
    // root. Only a source failure needs retry backoff; no time interval
    // or mandatory round completion limits successful checks.
    private static readonly TimeSpan SourceRetryInterval = TimeSpan.FromSeconds(10);
    private BackgroundFolderChangeKind? _backgroundActiveScanKind;
    private string? _backgroundActiveRootReviewPath;
    private readonly Dictionary<long, IReadOnlyList<string>> _backgroundExcludedPaths = new();
    private readonly Dictionary<long, IReadOnlyList<string>> _backgroundIncludedPaths = new();

    private BackgroundFolderMonitor? _backgroundFolderMonitor;
    private BackgroundScanSettings? _backgroundScanSettings;
    private CatalogRemovalService? _catalogRemovalService;
    private DispatcherTimer? _backgroundDebounceTimer;
    private DispatcherTimer? _backgroundMonitorRefreshTimer;
    private DispatcherTimer? _cacheMaintenanceTimer;
    private CancellationTokenSource? _cacheMaintenanceCancellation;
    private bool _cacheMaintenanceRunning;
    private CancellationTokenSource? _backgroundLifetime;
    private CancellationTokenSource? _backgroundCurrentScanCancellation;
    private bool _backgroundScanInitialized;
    private bool _backgroundWorkerRunning;
    private bool _manualFolderScanWaiting;
    private bool _backgroundMonitorRefreshRunning;
    private bool _backgroundMonitorRefreshRequested;
    private bool _backgroundMonitorClosing;
    // A single external edit owns a P0 admission lease. Automatic ingestion
    // is canceled/yielded but gallery/viewer reads remain independent.
    private bool _externalEditPauseActive;

    private void PauseBackgroundForExternalEdit()
    {
        _externalEditPauseActive = true;
        _backgroundDebounceTimer?.Stop();
        _backgroundCurrentScanCancellation?.Cancel();
        // The first-import pump is distinct from background reconciliation.
        // Stop its current file step too; its durable cursor survives.
        _initialScanCancellation?.Cancel();
        _cacheMaintenanceCancellation?.Cancel();
        _scheduler.NotifyTaskViewChanged();
    }

    private void ResumeBackgroundAfterExternalEdit()
    {
        if (!_externalEditPauseActive) return;
        _externalEditPauseActive = false;
        if (!_backgroundMonitorClosing && _backgroundPending.Count > 0 &&
            _backgroundScanSettings?.Enabled == true)
            _backgroundDebounceTimer?.Start();
        // A yielded first import must be re-admitted only AFTER the lease
        // has been released. Do not silently strand its saved file cursor.
        if (!Dispatcher.HasShutdownStarted)
        {
            if (_initialScanPumpRunning)
                _initialScanResumeAfterExternalEdit = true;
            else
            {
                _initialScanResumeAfterExternalEdit = false;
                QueuePendingInitialScans();
            }
        }
        _scheduler.NotifyTaskViewChanged();
    }
    // Human-readable reason when Taskmanager has no actionable work.
    // Updated only by the existing 30-second source/eligibility probe.
    private string _backgroundIdleStatus =
        "Status der automatischen Bestandsprüfung wird ermittelt";
    private string[] _backgroundOfflineRootPaths = Array.Empty<string>();

    private void BackgroundScan_WindowLoaded(object sender, RoutedEventArgs e)
    {
        // Window.Loaded can occur before the async gallery finishes. Starting
        // monitor reconciliation now would compete with the first thumbnails.
        // BrowseGallery releases this gate only after first paint AND durable
        // user-package recovery, preserving existing P0-before-P4 ordering.
        if (!_startupGalleryPresented || !_startupRecoveryComplete)
            return;
        if (_backgroundScanInitialized)
        {
            return;
        }

        _backgroundScanInitialized = true;
        _backgroundScanSettings = BackgroundScanSettings.Load();
        _catalogRemovalService = new CatalogRemovalService(_database);
        _backgroundLifetime = new CancellationTokenSource();
        _backgroundFolderMonitor = new BackgroundFolderMonitor(
            sources: _scheduler.SourceAvailability);
        _backgroundFolderMonitor.Changed += BackgroundFolderMonitor_Changed;
        BackgroundScanSettings.Changed += BackgroundScanSettings_Changed;

        _backgroundDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _backgroundDebounceTimer.Tick += BackgroundDebounceTimer_Tick;

        _backgroundMonitorRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Probe network availability periodically, never on Dispatcher.
            Interval = TimeSpan.FromSeconds(30)
        };
        _backgroundMonitorRefreshTimer.Tick += (_, _) =>
        {
            // A dead SMB share can stall an OS probe for longer than the
            // timer interval. Never stack probes while one is unfinished.
            // Explicit settings changes are still coalesced separately.
            if (!_externalEditPauseActive && !_backgroundMonitorRefreshRunning)
                RefreshBackgroundMonitor();
        };
        _backgroundMonitorRefreshTimer.Start();

        // Physical cache reclamation never delays removing a folder. The
        // persistent maintenance marker is picked up during idle periods,
        // including after restart if the previous session was interrupted.
        _cacheMaintenanceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _cacheMaintenanceTimer.Tick += (_, _) => _ = RunDeferredCacheMaintenanceAsync();
        _cacheMaintenanceTimer.Start();

        // A recovered session also suspends background ingestion. The
        // scheduler lease is established by the external editing owner.
        if (!_externalEditPauseActive) RefreshBackgroundMonitor();
    }

    private async Task RunDeferredCacheMaintenanceAsync()
    {
        if (_externalEditPauseActive) return;
        if (_updateDownloadActive || _cacheMaintenanceRunning || _folderRemovalPending ||
            _backgroundMonitorClosing || _folderManagerOpen ||
            _isScanning || _backgroundWorkerRunning || _browseCacheBuilding ||
            _backgroundPending.Count > 0 || _catalogRemovalService is null)
            return;

        _cacheMaintenanceRunning = true;
        using var cancellation = new CancellationTokenSource();
        _cacheMaintenanceCancellation = cancellation;
        var started = false;
        try
        {
            var service = _catalogRemovalService;
            await _scheduler.EnqueueAsync(
                "cache-maintenance",
                SchedulerPriority.Idle,
                token =>
                {
                    // Compaction commits each pack separately. A later cancel
                    // can leave already committed index offsets changed.
                    started = true;
                    service.RunPendingMaintenance(token);
                    return Task.CompletedTask;
                },
                cancellationToken: cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Interrupted maintenance remains durable; even a canceled run
            // can have committed pack-index changes before it was interrupted.
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Cache.DeferredMaintenance", ex);
        }
        finally
        {
            // Always re-read the index after an ATTEMPTED compaction,
            // including cancellation/failure after the first pack commit.
            // A folder-removal action already owns its final gallery refresh.
            if (started && !_backgroundMonitorClosing && !_folderRemovalPending &&
                _browseInitialized && !Dispatcher.HasShutdownStarted)
            {
                try
                {
                    await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false,
                    fromScanner: true);
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Cache.DeferredIndexRefresh", ex);
                }
            }
            if (ReferenceEquals(_cacheMaintenanceCancellation, cancellation))
                _cacheMaintenanceCancellation = null;
            _cacheMaintenanceRunning = false;
        }
    }

    private void BackgroundScanSettings_Changed(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            var nextSettings = BackgroundScanSettings.Load();
            var wasEnabled = _backgroundScanSettings?.Enabled == true;
            _backgroundScanSettings = nextSettings;

            // BackgroundScanSettings.Save() raises Changed even when an
            // unrelated settings dialog saves the SAME Enabled=true value.
            // Previously every such save cleared the known-root set and
            // manufactured another complete RescanRoot of every catalogued
            // folder. Only an actual enabled-state transition may do that.
            if (wasEnabled == nextSettings.Enabled)
                return;

            if (nextSettings.Enabled)
            {
                _backgroundKnownFolderIds.Clear();
                _gpsSourceAuditedRoots.Clear();
                // This is a REAL disabled->enabled transition: changes made
                // while the watcher was absent require one reconciliation.
                _backgroundFirstMonitorSnapshot = false;
            }
            else
            {
                DisposeQueuedScanCursors();
                _backgroundPending.Clear();
                _backgroundCurrentScanCancellation?.Cancel();
            }

            RefreshBackgroundMonitor();
        });
    }

    private long _automaticDeletionVersion;

    private async void RefreshBackgroundMonitor()
    {
        if (_updateDownloadActive || !_backgroundScanInitialized ||
            _backgroundMonitorClosing ||
            _backgroundFolderMonitor is null || _backgroundScanSettings is null)
            return;
        if (_backgroundMonitorRefreshRunning)
        {
            // A settings change while an older probe is in flight MUST NOT
            // wait for the next 30-second timer tick. Coalesce one refresh
            // request and execute it once the current scheduled probe ends.
            _backgroundMonitorRefreshRequested = true;
            return;
        }

        _backgroundMonitorRefreshRunning = true;
        var monitor = _backgroundFolderMonitor;
        var enabled = _backgroundScanSettings.Enabled;
        // Capture UI snapshot before entering the scheduled source worker.
        // Only this single monitor owner detects offline -> available.
        var wasOffline = _backgroundOfflineRootPaths.ToArray();
        var initialAvailabilitySnapshot = _backgroundFirstMonitorSnapshot;
        var deletionVersion = _automaticDeletionVersion;

        try
        {
            // Both Directory.Exists on a dead UNC share and FileSystemWatcher
            // construction can block in the OS. Never do either on Dispatcher.
            var (folders, availability, exclusions, inclusions, pendingInitialIds,
                interruptedInventories, durableChanges, dueRootReviews,
                reconnectedFolderIds) = await _scheduler.RunAsync(
                $"background:monitor-probe:{Guid.NewGuid():N}",
                SchedulerPriority.Background,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var roots = _folderRepository.GetAll();
                    var rules = roots.ToDictionary(
                        folder => folder.Id,
                        folder => _folderRepository.GetExcludedPaths(folder.Id));
                    var includedRules = roots.ToDictionary(
                        folder => folder.Id,
                        folder => _folderRepository.GetIncludedPaths(folder.Id));
                    var states = monitor.Refresh(roots, enabled);
                    // An SMB share may return before an old six-hour retry
                    // window expires. Clear that deadline immediately after
                    // confirming the ACTUAL monitored root exists; never
                    // touch any scan receipt or manufacture a new scan key.
                    var reconnected = roots.Where(folder =>
                        states.TryGetValue(folder.Id, out var available) &&
                        available && (initialAvailabilitySnapshot ||
                            wasOffline.Any(path => string.Equals(path,
                                folder.LocalPath,
                                StringComparison.OrdinalIgnoreCase))))
                        .Select(folder => folder.Id).ToArray();
                    foreach (var recoveredId in reconnected)
                        _folderRepository.ReleaseRootReviewRetryOnReconnect(recoveredId);
                    // Capture pending first imports in this SAME scheduled
                    // monitor snapshot, not by a second SQLite read on WPF.
                    var pending = _folderRepository.GetPendingInitialScans()
                        .Select(root => root.Id).ToHashSet();
                    // Read the already committed inventory identities in
                    // the SAME scheduler snapshot. A process restart must
                    // continue an unfinished automatic scan, not manufacture
                    // another root scan with a different checkpoint key.
                    var inventories = _folderRepository.GetPendingAutomaticInventories();
                    // Read dates while already on the scheduled probe lane.
                    // Never schedule a new review from a WPF timer alone.
                    var due = _folderRepository.GetDueRootReviewIds(DateTime.UtcNow);
                    var changes = _folderRepository.GetPendingAutomaticChanges();
                    return (roots, states, rules, includedRules, pending, inventories, changes, due,
                        reconnected);
                }, cancellationToken: _backgroundLifetime?.Token ?? default,
                workKind: SchedulerWorkKind.SourceProbe);

            if (_backgroundMonitorClosing || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            // A paused watcher request may have been queued but not yet
            // started when PCasa exited. Rebuild its SAME accepted identity
            // from the scheduler's persisted hold rather than losing the
            // user's pause or inventing a second scanner.
            foreach (var pausedKey in _scheduler.RecoverableExternalTaskKeys())
            {
                var request = ParsePausedAutomaticTask(pausedKey);
                if (request is null ||
                    _backgroundPending.ContainsKey(pausedKey) ||
                    _backgroundInFlight?.Any(inflight =>
                        string.Equals(AutomaticTaskKey(inflight), pausedKey,
                            StringComparison.Ordinal)) == true)
                    continue;
                var owner = folders.FirstOrDefault(root =>
                    root.Id == request.MonitoredFolderId);
                if (owner is null ||
                    !FolderExclusionRules.IsAtOrBelow(
                        request.Path, owner.LocalPath) ||
                    exclusions.TryGetValue(owner.Id, out var excluded) &&
                    FolderExclusionRules.IsExcluded(request.Path, excluded,
                        inclusions.GetValueOrDefault(owner.Id) ?? Array.Empty<string>()) &&
                    !FolderExclusionRules.MayContainIncludedDescendant(request.Path,
                        inclusions.GetValueOrDefault(owner.Id) ?? Array.Empty<string>()))
                    continue;
                QueueBackgroundChange(request);
                _scheduler.AcknowledgeRecoveredExternalTask(pausedKey);
            }
            // The initial-import retry guard is session-only. After a
            // confirmed reconnect it may be retried immediately; a
            // deliberately paused first import stays paused in SQLite.
            var retriedFirstImport = false;
            foreach (var id in reconnectedFolderIds)
                if (_initialScanAttempted.Remove(id))
                    retriedFirstImport = true;
            if (retriedFirstImport)
                QueuePendingInitialScans();

            _backgroundExcludedPaths.Clear();
            _backgroundIncludedPaths.Clear();
            foreach (var (folderId, excluded) in exclusions)
            {
                _backgroundExcludedPaths[folderId] = excluded;
                _backgroundIncludedPaths[folderId] = inclusions.GetValueOrDefault(folderId)
                    ?? Array.Empty<string>();
            }

            var offlineRoots = folders
                .Where(folder => availability.TryGetValue(folder.Id, out var reachable) &&
                                 !reachable)
                .Select(folder => folder.LocalPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!_backgroundOfflineRootPaths.SequenceEqual(
                    offlineRoots, StringComparer.OrdinalIgnoreCase))
            {
                _backgroundOfflineRootPaths = offlineRoots;
                // Probe offline roots frequently but leave healthy roots at
                // the lower-overhead normal cadence. Both checks run on the
                // existing scheduler's SourceProbe lane, never on Dispatcher.
                if (_backgroundMonitorRefreshTimer is not null)
                    _backgroundMonitorRefreshTimer.Interval =
                        TimeSpan.FromSeconds(offlineRoots.Length > 0 ? 10 : 30);
                // Only touch WPF TreeViewItems when reachability actually changes.
                // Iterating a large folder tree every 30 seconds would stutter.
                ApplyFolderAvailabilityToTree();
                RefreshFolderStatusVisuals();
            }

            if (!enabled || _backgroundScanSettings?.Enabled != true)
            {
                _backgroundIdleStatus =
                    "Automatische Überwachung deaktiviert – keine Leerlaufprüfung";
                _backgroundKnownFolderIds.Clear();
                return;
            }

            // The durable queue is restored even when an individual root is
            // offline. Dispatch, not acceptance, depends on reachability.
            if (deletionVersion == _automaticDeletionVersion)
                foreach (var saved in durableChanges)
                {
                    if (_backgroundPending.ContainsKey(saved.Key) ||
                        _backgroundInFlight?.Any(item =>
                            AutomaticTaskKey(item) == saved.Key) == true)
                        continue;
                    QueueBackgroundChange(saved.Change, persisted: saved);
                }
            if (HasRunnableBackgroundRequests())
                _backgroundDebounceTimer?.Start();

            var currentIds = folders.Select(folder => folder.Id).ToHashSet();
            _backgroundKnownFolderIds.RemoveWhere(id => !currentIds.Contains(id));
            _backgroundDeferredInventoryRoots.RemoveWhere(id => !currentIds.Contains(id));
            _gpsSourceAuditedRoots.RemoveWhere(id => !currentIds.Contains(id));

            // Installing a watcher on startup does NOT prove the directory
            // changed. The old code manufactured a full RescanRoot on every
            // process launch, ahead of unfinished first imports.
            // Concrete watcher events or an explicit monitoring re-enable
            // justify reconciliation. Offline/online transitions DO NOT.
            var firstSnapshot = _backgroundFirstMonitorSnapshot;
            // Pick exactly one eligible root in persisted oldest-first order.
            // No backlog of thousands of independent repeat root scans.
            // An unfinished import on S: must NOT block a P4 check of G:
            // forever when S: is unreachable. The scan itself runs through
            // the SAME scheduler, and an actually active first import still
            // has precedence. A different pending root is not a global lock.
            var idleSlotFree = !_backgroundWorkerRunning &&
                !HasRunnableBackgroundRequests() &&
                !_initialScanActive && !_initialScanPumpRunning &&
                !_manualContextScanActive && !_manualScanAllActive &&
                !_manualFileTransferActive;
            var reachableDue = dueRootReviews.Count(id =>
                availability.TryGetValue(id, out var reachable) && reachable);
            // Only one permanent review can run. Its next root comes
            // exclusively from the persisted catalog sequence.
            var reviewPaused = _scheduler.IsExternalTaskPaused(
                PCasaScheduler.ContinuousReviewKey);
            var idleCandidate = !reviewPaused && idleSlotFree
                ? dueRootReviews.FirstOrDefault(id =>
                    availability.TryGetValue(id, out var reachable) && reachable)
                : 0L;
            _backgroundIdleStatus = reviewPaused ? "Angehalten" :
                dueRootReviews.Count == 0
                ? "Keine abgeschlossenen, prüfbaren Ordner verfügbar"
                : reachableDue == 0
                    ? "Keine erreichbaren Ordner zur Bestandsprüfung"
                    : !idleSlotFree
                        ? "Bestandsprüfung wartet auf andere Aufträge"
                        : "Nächster Ordner wird geprüft";
            foreach (var folder in folders)
            {
                if (!availability.TryGetValue(folder.Id, out var reachable) || !reachable)
                {
                    // A temporarily absent NAS is not a source change. Keep
                    // its monitor identity across reconnect so the next
                    // availability probe cannot manufacture RescanRoot.
                    _backgroundKnownFolderIds.Add(folder.Id);
                    continue;
                }

                var firstKnown = _backgroundKnownFolderIds.Add(folder.Id);
                if (pendingInitialIds.Contains(folder.Id))
                    continue; // The durable initial-import scanner owns this root.

                // This is a catalog-only read. ONLY mismatched source
                // identities enter the existing per-file scanner, never a
                // new whole-folder scan or a second EXIF writer.
                if (_gpsSourceAuditedRoots.Add(folder.Id))
                    _ = AuditGpsSourceIdentityAsync(folder.Id);

                var deferred = _backgroundDeferredInventoryRoots.Contains(folder.Id);
                var idleReviewDue = folder.Id == idleCandidate;
                // Reconnect can happen long after the first availability
                // snapshot. Recover the accepted inventory identity on any
                // available probe, not only the first time a root is seen.

                // Resume real interrupted inventory receipts without inventing
                // a second scan key, but defer them until all first imports
                // have finished. Live file watcher events are not deferred.
                var restored = interruptedInventories
                    .Where(receipt => deletionVersion == _automaticDeletionVersion &&
                        receipt.MonitoredFolderId == folder.Id)
                    .Select(TryRecoverAutomaticInventory)
                    .Where(request => request is not null &&
                        request.Kind != BackgroundFolderChangeKind.RescanRoot)
                    .Cast<BackgroundFolderChange>()
                    .Where(request =>
                    {
                        var key = AutomaticTaskKey(new PendingBackgroundScan(
                            request.MonitoredFolderId, request.Path,
                            request.Kind, request.Recursive));
                        return !_backgroundPending.ContainsKey(key) &&
                            !(_backgroundInFlight?.Any(item =>
                                string.Equals(AutomaticTaskKey(item), key,
                                    StringComparison.OrdinalIgnoreCase)) ?? false);
                    })
                    .ToArray();

                var reconcile = idleReviewDue;
                if (pendingInitialIds.Count > 0 &&
                    (restored.Length > 0 || reconcile) &&
                    // A new eligible P4 cycle on ANOTHER root may run when
                    // the first-import executor is idle. Keep the existing
                    // deferral for restored/explicit re-enable inventories
                    // until the original import batch is no longer active.
                    !idleReviewDue)
                {
                    _backgroundDeferredInventoryRoots.Add(folder.Id);
                    continue;
                }

                _backgroundDeferredInventoryRoots.Remove(folder.Id);
                if (restored.Length > 0)
                {
                    foreach (var request in restored)
                        QueueBackgroundChange(request);
                }
                else if (reconcile)
                {
                    QueueBackgroundChange(new BackgroundFolderChange(
                        folder.Id, folder.LocalPath,
                        BackgroundFolderChangeKind.RescanRoot,
                        Recursive: true));
                    // A newly queued cycle is not followed by a second one
                    // before the first has completed. Watcher events retain
                    // their own higher placement in the same scheduler.
                    if (idleReviewDue) idleCandidate = 0;
                }
            }
            _backgroundFirstMonitorSnapshot = false;
            _scheduler.NotifyTaskViewChanged();
            if (idleCandidate == 0L && dueRootReviews.Count > 0 &&
                reachableDue > 0 && idleSlotFree &&
                _backgroundPending.Count == 0)
                PCasaDiagnostics.WriteMessage("Scan.IdleQueue",
                    "Eligible idle root did not enter the scan queue. " +
                    $"Due={dueRootReviews.Count}; Reachable={reachableDue}; " +
                    $"PendingInitial={pendingInitialIds.Count}.");
        }
        catch (Exception ex)
        {
            _backgroundIdleStatus = "Leerlaufprüfung derzeit nicht ermittelbar";
            // Errors from an unavailable source must never crash Dispatcher.
            // Preserve a diagnostic without continually showing popup dialogs.
            PCasaDiagnostics.Write("RefreshBackgroundMonitor", ex);
        }
        finally
        {
            _backgroundMonitorRefreshRunning = false;
            if (_backgroundMonitorClosing)
                monitor.Dispose();
            else if (_backgroundMonitorRefreshRequested)
            {
                _backgroundMonitorRefreshRequested = false;
                RefreshBackgroundMonitor();
            }
        }
    }

    // Held or unreachable work remains accepted, but does not reserve the
    // idle slot. Use the same readiness predicate for admission and dispatch.
    private bool IsRunnableBackgroundRequest(PendingBackgroundScan request) =>
        !_scheduler.IsExternalTaskPaused(request.Kind == BackgroundFolderChangeKind.RescanRoot
            ? PCasaScheduler.ContinuousReviewKey : AutomaticTaskKey(request)) &&
        !IsPathKnownOffline(request.Path);

    private bool HasRunnableBackgroundRequests() =>
        _backgroundPending.Values.Any(IsRunnableBackgroundRequest);

    private bool IsPathKnownOffline(string path)
        => _backgroundOfflineRootPaths.Any(root => PathIsAtOrUnderRoot(path, root));

    private static bool PathIsAtOrUnderRoot(string path, string root)
    {
        var normalized = root.TrimEnd('\\', '/');
        return string.Equals(path.TrimEnd('\\', '/'), normalized,
                   StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalized + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFolderAvailabilityToTree()
    {
        foreach (var root in FoldersTree.Items.OfType<System.Windows.Controls.TreeViewItem>())
        {
            SetFolderTreeAvailability(root, inheritedOffline: false);
        }
    }

    private void SetFolderTreeAvailability(
        System.Windows.Controls.TreeViewItem item, bool inheritedOffline)
    {
        var unavailable = item.Tag is FolderTreeTag tag &&
                          IsPathKnownOffline(tag.FullPath);
        // Opacity is multiplicative in WPF. Apply it just once at the first
        // offline node, or deeper children would fade nearly to invisibility.
        item.Opacity = unavailable && !inheritedOffline ? 0.50 : 1.0;

        foreach (var child in item.Items.OfType<System.Windows.Controls.TreeViewItem>())
        {
            SetFolderTreeAvailability(child, inheritedOffline || unavailable);
        }
    }

    // Validate previously stored EXIF-to-image identities once per available
    // root and process launch. The catalog itself is the durable checkpoint:
    // successfully repaired rows no longer appear as mismatches after restart.
    // Scheduler P4 owns the read, and the existing scanner P3 owns rereading
    // the corresponding ORIGINAL file. No directory walking or thumbnail
    // rebuild is initiated for a valid catalog row.
    private async Task AuditGpsSourceIdentityAsync(long rootId)
    {
        var finished = false;
        var afterId = 0L;
        var mismatches = 0;
        var unverifiedGps = 0;
        try
        {
            var audit = new GpsSourceIntegrityAudit(_database);
            while (_backgroundLifetime is { IsCancellationRequested: false } lifetime &&
                   _backgroundScanSettings?.Enabled == true)
            {
                var page = await _scheduler.RunAsync(
                    $"gps:source-audit:{rootId}:{afterId}",
                    SchedulerPriority.Idle,
                    token => audit.ReadPage(rootId, afterId, 1000, token),
                    cancellationToken: lifetime.Token,
                    workKind: SchedulerWorkKind.CatalogRead);
                if (page.Checked == 0) break;
                afterId = page.LastImageId;
                unverifiedGps += page.UnverifiedGpsRows;
                foreach (var path in page.MismatchedPaths)
                {
                    if (_backgroundMonitorClosing || lifetime.IsCancellationRequested)
                        throw new OperationCanceledException();
                    QueueBackgroundChange(new BackgroundFolderChange(
                        rootId, path, BackgroundFolderChangeKind.ScanFile,
                        Recursive: false, ForceExifRefresh: true));
                    mismatches++;
                }
            }
            finished = true;
            if (mismatches > 0 || unverifiedGps > 0)
                PCasaDiagnostics.WriteMessage("GPS.SourceIdentity.Audit",
                    $"Root={rootId}; ProvenMismatches={mismatches}; " +
                    $"UnverifiedLegacyGps={unverifiedGps}; " +
                    "Only proven mismatches queued for native EXIF repair.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.SourceIdentity.Audit", ex);
        }
        finally
        {
            // A canceled/failed read is retryable; no separate scheduler or
            // orphaned journal owns the work. Successfully repaired rows are
            // no longer candidates on the next pass.
            if (!finished) _gpsSourceAuditedRoots.Remove(rootId);
        }
    }

    private static BackgroundFolderChange? TryRecoverAutomaticInventory(
        FolderRepository.PendingAutomaticInventory receipt)
    {
        var prefix = $"background:inventory:{receipt.MonitoredFolderId}:" +
                     $"{receipt.SourceGeneration}:";
        if (!receipt.ScanKey.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var parts = receipt.ScanKey[prefix.Length..]
            .Split(new[] { ':' }, 3);
        if (parts.Length != 3 ||
            !Enum.TryParse<BackgroundFolderChangeKind>(parts[0], out var kind) ||
            kind is not (BackgroundFolderChangeKind.RescanRoot or
                         BackgroundFolderChangeKind.ScanDirectory) ||
            !bool.TryParse(parts[1], out var recursive))
            return null;
        try
        {
            var path = Path.GetFullPath(parts[2]).Replace('/', '\\');
            if (!string.Equals(path, receipt.ScanRoot,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            return new BackgroundFolderChange(
                receipt.MonitoredFolderId, path, kind, recursive);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return null;
        }
    }

    private void BackgroundFolderMonitor_Changed(object? sender, BackgroundFolderChange change)
    {
        if (_backgroundMonitorClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_backgroundMonitorClosing && !Dispatcher.HasShutdownStarted)
                {
                    QueueBackgroundChange(change);
                }
            });
        }
        catch (Exception ex)
        {
            // FileSystemWatcher raises callbacks on thread-pool threads.
            // An exception here could otherwise terminate the process.
            PCasaDiagnostics.Write("BackgroundFolderMonitor_Changed", ex);
        }
    }

    // Identity is stable across watcher coalescing, scanner preemption and
    // taskmanager refresh; it is unrelated to a volatile WPF row index.
    private static string AutomaticTaskKey(PendingBackgroundScan task)
        => $"automatic:{task.MonitoredFolderId}:{task.Kind}:{task.Path}";

    private static BackgroundFolderChange? ParsePausedAutomaticTask(string key)
    {
        // Only parse our own stable key format, never accept arbitrary paths
        // as a new source. The calling monitor also validates root ownership.
        var parts = key.Split(':', 4);
        if (parts.Length != 4 || parts[0] != "automatic" ||
            !long.TryParse(parts[1], out var id) ||
            !Enum.TryParse<BackgroundFolderChangeKind>(parts[2], out var kind) ||
            kind is not (BackgroundFolderChangeKind.ScanFile or
                BackgroundFolderChangeKind.ScanDirectory or
                BackgroundFolderChangeKind.RemovePath or
                BackgroundFolderChangeKind.RescanRoot) ||
            string.IsNullOrWhiteSpace(parts[3]))
            return null;
        return new BackgroundFolderChange(id, parts[3], kind,
            Recursive: kind != BackgroundFolderChangeKind.ScanFile,
            ForceExifRefresh: kind == BackgroundFolderChangeKind.ScanFile);
    }

    private static string AutomaticTaskTitle(BackgroundFolderChangeKind kind)
        => kind switch
        {
            BackgroundFolderChangeKind.ScanFile => "Datei einlesen",
            BackgroundFolderChangeKind.ScanDirectory => "Neue Fotos einsammeln",
            BackgroundFolderChangeKind.RescanRoot => "Bestand überprüfen",
            BackgroundFolderChangeKind.RemovePath => "Gelöschte Dateien abgleichen",
            _ => "Vorschaubilder aktualisieren"
        };

    private static int AutomaticTaskPriority(BackgroundFolderChangeKind kind)
        => kind == BackgroundFolderChangeKind.RescanRoot ? 4 : 3;

    // Called only on the WPF dispatcher. The scheduler reports the actual
    // running state; staging/queued requests come from the one existing
    // FileSystemWatcher debounce and execution batch.
    private IReadOnlyList<AutomaticScanTask> SnapshotAutomaticScans()
    {
        // Initial imports are automatic even when the optional watcher is
        // disabled. NEVER let the watcher preference hide a running first
        // import from Taskmanager while its progress window is advancing.
        var snapshots = _scheduler.Snapshot();
        var latest = snapshots
            .Where(job => job.Key.StartsWith("automatic:", StringComparison.Ordinal))
            .GroupBy(job => job.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(job => job.Id).First(),
                StringComparer.Ordinal);
        var rows = new List<AutomaticScanTask>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long rank = 0;

        void Add(PendingBackgroundScan task, bool current)
        {
            // Cache rebuilds are technical work; watcher removals are
            // actionable catalog jobs with their own cancellation token.
            if (task.Kind is BackgroundFolderChangeKind.RecoverCache or
                BackgroundFolderChangeKind.RescanRoot) return;
            var key = AutomaticTaskKey(task);
            var stopping = _backgroundSuppressed.Contains(key);
            // A non-interruptible SMB operation may continue briefly after
            // Stop was requested. Keep that live task visible until it
            // actually exits, rather than recreating the empty-manager bug.
            if ((stopping && !current) || !keys.Add(key)) return;
            var path = task.Kind == BackgroundFolderChangeKind.ScanFile
                ? Path.GetDirectoryName(task.Path) ?? task.Path : task.Path;
            var paused = _scheduler.IsExternalTaskPaused(key);
            // The in-flight scheduler snapshot, not merely a queued WPF
            // request, decides whether a scanner is really running.
            var runningSnapshot = current && key == _backgroundLiveTaskKey
                && latest.TryGetValue(key, out var snapshot) &&
                    snapshot.State is SchedulerJobState.Running or SchedulerJobState.YieldRequested
                ? snapshot : null;
            // Show the directory actually being visited, rather than
            // merely the monitored root. During a scan of G:\Bilder the
            // user needs to see that Camera Roll is currently being checked.
            if (runningSnapshot?.CurrentPath is { Length: > 0 } currentPath)
            {
                var suffix = Path.GetExtension(currentPath);
                path = suffix.Length is > 1 and <= 8 &&
                    suffix.Skip(1).All(char.IsLetterOrDigit)
                    ? Path.GetDirectoryName(currentPath) ?? path
                    : currentPath;
            }
            var checkedFiles = current && key == _backgroundLiveTaskKey
                ? Math.Max(_backgroundLiveChecked,
                    runningSnapshot?.Processed ?? 0)
                : task.Resume?.CommittedFiles ?? 0;
            // Cursor receipts remain durable after a restart even when the
            // current in-memory enumerator has not produced its first file.
            var progress = stopping
                ? UiLanguage.T("Abbruch angefordert")
                : checkedFiles > 0
                    ? UiLanguage.Format("{0:N0} geprüft", checkedFiles)
                    : "–";
            rows.Add(new AutomaticScanTask(key,
                UiLanguage.T(AutomaticTaskTitle(task.Kind)), path,
                progress,
                paused ? "Angehalten" :
                    runningSnapshot is not null ? "Läuft" :
                    IsPathKnownOffline(task.Path) ? "Nicht erreichbar" : "Wartet",
                AutomaticTaskPriority(task.Kind),
                task.QueueOrder == long.MaxValue ? rank++ : task.QueueOrder,
                CanStop: !stopping,
                CatalogDirectory: task.Kind == BackgroundFolderChangeKind.ScanDirectory
                    ? task.Path : null));
        }

        if (_backgroundScanInitialized &&
            _backgroundScanSettings?.Enabled == true)
        {
            if (_backgroundInFlight is { } inFlight)
                for (var i = _backgroundInFlightIndex; i < inFlight.Length; i++)
                    Add(inFlight[i], i == _backgroundInFlightIndex);
            foreach (var task in _backgroundPending.Values
                         .OrderBy(row => AutomaticTaskPriority(row.Kind))
                         .ThenBy(row => row.QueueOrder)
                         .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
                Add(task, false);
        }

        // One permanent review row, even if no root is reachable.
        var review = snapshots.LastOrDefault(job =>
            job.Key == PCasaScheduler.ContinuousReviewKey);
        var reviewing = review?.State is SchedulerJobState.Running or SchedulerJobState.YieldRequested;
        var pausedReview = _scheduler.IsExternalTaskPaused(
            PCasaScheduler.ContinuousReviewKey);
        rows.Add(new AutomaticScanTask(PCasaScheduler.ContinuousReviewKey,
            UiLanguage.T("Bestand überprüfen"),
            reviewing ? review?.CurrentPath ?? _backgroundActiveRootReviewPath ?? "–" : "–",
            reviewing ? UiLanguage.Format("{0:N0} geprüft", _backgroundLiveChecked)
                : UiLanguage.T(_backgroundIdleStatus),
            pausedReview ? "Angehalten" : reviewing ? "Läuft" : "Wartet",
            (int)SchedulerPriority.Idle, long.MaxValue, CanStop: false));

        // A pending first import is not a watcher event. Its persistent
        // identity is also the scheduler key. Retain its root for display
        // even when the transient progress snapshot is unavailable.
        if (_initialScanActive &&
            _initialScanTaskKey is { } initialKey &&
            _initialScanTaskDirectory is { } initialRoot)
        {
            var job = snapshots.LastOrDefault(snapshot =>
                snapshot.Key == initialKey &&
                snapshot.WorkKind == SchedulerWorkKind.InitialImport);
            var active = job?.State is SchedulerJobState.Running or SchedulerJobState.YieldRequested ||
                (job is null && _initialScanWorkerStarted);
            var current = job?.CurrentPath is { Length: > 0 } file
                ? file : _initialScanTaskCurrentFile;
            var directory = initialRoot;
            if (!string.IsNullOrWhiteSpace(current) &&
                !string.Equals(current, initialRoot,
                    StringComparison.OrdinalIgnoreCase))
                directory = Path.GetDirectoryName(current) ?? initialRoot;
            var checkedCount = Math.Max(_initialScanTaskChecked,
                job?.Processed ?? 0);
            var stopping = _initialScanStopRequested;
            rows.Add(new AutomaticScanTask(
                initialKey,
                UiLanguage.T("Erstimport"), directory,
                stopping
                    ? UiLanguage.T("Abbruch angefordert")
                    : UiLanguage.Format("{0:N0} geprüft", checkedCount),
                active ? "Läuft" : "Wartet",
                (int)SchedulerPriority.ManualAction, _initialScanTaskQueueOrder,
                CanStop: !stopping));
        }
        return rows;
    }

    // Stop removes the pending request and its restart receipt, never a
    // catalogued photo. The permanent background check cannot be deleted.
    private async Task<bool> StopAutomaticScanAsync(string key)
    {
        if (key == PCasaScheduler.ContinuousReviewKey) return false;
        // Delete applies to BOTH waiting and explicitly held imports.
        // It removes the persistent queue intent, never the catalog or
        // committed first-pass receipts. A later explicit scan can resume
        // those saved file/directory frontiers without recreating this task.
        if (TryInitialTaskId(key, out var initialFolderId))
        {
            _initialScanAttempted.Add(initialFolderId);
            if (_initialScanActive &&
                _initialScanTaskKey == key &&
                _initialScanCancellation is
                    { IsCancellationRequested: false } activeImport)
            {
                // Request cancellation BEFORE dropping the persistent marker:
                // the running worker must not commit further initial-import
                // receipts after its intent is removed.
                _initialScanStopRequested = true;
                try { await activeImport.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }
            var deleted = await _scheduler.RunAsync(
                $"taskmanager:first-import-delete:{Guid.NewGuid():N}",
                SchedulerPriority.UserAction,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return _folderRepository.DeletePendingFirstImport(initialFolderId);
                }, workKind: SchedulerWorkKind.CatalogRead);
            if (deleted)
            {
                // Release any other pending work through the same scheduler
                // owners. The deleted root is no longer a queued import.
                QueuePendingInitialScans();
                if (_backgroundScanInitialized && !_backgroundMonitorClosing)
                    RefreshBackgroundMonitor();
            }
            return deleted;
        }
        if (!key.StartsWith("automatic:", StringComparison.Ordinal))
            return false;

        // Prefer the currently executing request over a duplicate follow-up
        // watcher event. One user action stops BOTH; otherwise an ongoing
        // scan could appear to ignore the Taskmanager's Delete button.
        if (_backgroundInFlight is { } inFlight)
            for (var index = _backgroundInFlightIndex;
                 index < inFlight.Length; index++)
            {
                var task = inFlight[index];
                if (!string.Equals(AutomaticTaskKey(task), key,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                _automaticDeletionVersion++;
                _scheduler.AcknowledgeRecoveredExternalTask(key);
                _scheduler.SetExternalTaskPaused(key, false);
                _scheduler.CompleteTaskPromotion(key);
                if (index == _backgroundInFlightIndex)
                {
                    _backgroundSuppressed.Add(key);
                    if (_backgroundPending.Remove(key, out var followup))
                        followup.Resume?.Dispose();
                    // The current source may still be in its network
                    // reachability probe. In that case the worker will
                    // acknowledge the stop BEFORE starting the scanner.
                    if (key == _backgroundLiveTaskKey &&
                        _backgroundCurrentScanCancellation is
                            { IsCancellationRequested: false } active)
                    {
                        try { await active.CancelAsync(); }
                        catch (ObjectDisposedException) { }
                    }
                    _folderRepository.RetireAutomaticChange(key);
                    DeleteAutomaticInventoryReceipt(task);
                    _scheduler.NotifyTaskViewChanged();
                    return true;
                }

                _backgroundSuppressed.Add(key);
                if (_backgroundPending.Remove(key, out var pendingFollowup))
                    pendingFollowup.Resume?.Dispose();
                _folderRepository.RetireAutomaticChange(key);
                DeleteAutomaticInventoryReceipt(task);
                _scheduler.NotifyTaskViewChanged();
                return true;
            }

        if (_backgroundPending.Remove(key, out var queued))
        {
            queued.Resume?.Dispose();
            _automaticDeletionVersion++;
            _scheduler.AcknowledgeRecoveredExternalTask(key);
            _scheduler.SetExternalTaskPaused(key, false);
            _scheduler.CompleteTaskPromotion(key);
            _folderRepository.RetireAutomaticChange(key);
            DeleteAutomaticInventoryReceipt(queued);
            RefreshFolderStatusVisuals();
            _scheduler.NotifyTaskViewChanged();
            return true;
        }
        return false;
    }

    private void CompleteAutomaticChange(PendingBackgroundScan task)
    {
        if (task.Revision is not null)
        {
            var key = AutomaticTaskKey(task);
            var retired = _folderRepository.RetireAutomaticChange(key, task.Revision);
            // A newer accepted revision is still the same logical task.
            // Preserve its user placement even if its publication to the
            // Dispatcher has not happened yet.
            if (retired && !_backgroundPending.ContainsKey(key))
                _scheduler.CompleteTaskPromotion(key);
            // Discard monitor snapshots made before this acknowledgement.
            _automaticDeletionVersion++;
        }
    }

    private void DeleteAutomaticInventoryReceipt(PendingBackgroundScan task)
    {
        if (task.Kind == BackgroundFolderChangeKind.ScanDirectory)
            _folderRepository.DeleteAutomaticDirectoryInventory(
                task.MonitoredFolderId, task.Path);
    }

    private void DeferStoppedRootReview(PendingBackgroundScan task)
    {
        if (task.Kind != BackgroundFolderChangeKind.RescanRoot) return;
        var folder = _folderRepository.GetAll()
            .FirstOrDefault(row => row.Id == task.MonitoredFolderId);
        if (folder is null) return;
        var source = _folderRepository.GetSourceState(folder.CanonicalPath);
        if (source is { DesiredPresent: true })
            _folderRepository.DeferRootReview(folder.Id,
                source.Value.Generation, DateTime.UtcNow + SourceRetryInterval);
    }

    private void RequeueBackgroundRequest(PendingBackgroundScan request,
        FolderScanner.ScanResumeState? cursor, long? generation)
    {
        FolderRepository.AutomaticChangeJob? receipt = null;
        if (request.Revision is not null)
        {
            receipt = _folderRepository.GetPendingAutomaticChange(AutomaticTaskKey(request));
            if (receipt is null)
            {
                cursor?.Dispose();
                return; // Explicitly retired; do not recreate the job.
            }
            if (receipt.Revision != request.Revision)
            {
                // A newer notification requires a fresh comparison. The
                // catalog retains all already committed image work.
                cursor?.Dispose();
                cursor = null;
                generation = null;
            }
        }
        QueueBackgroundChange(new BackgroundFolderChange(request.MonitoredFolderId,
            request.Path, request.Kind, request.Recursive, request.ForceExifRefresh),
            cursor, generation, receipt);
    }

    private void QueueBackgroundChange(BackgroundFolderChange change,
        FolderScanner.ScanResumeState? interruptedCursor = null,
        long? cursorGeneration = null,
        FolderRepository.AutomaticChangeJob? persisted = null)
    {
        if (!_backgroundScanInitialized ||
            _backgroundScanSettings?.Enabled != true)
        {
            interruptedCursor?.Dispose();
            return;
        }

        if (change.Kind == BackgroundFolderChangeKind.RescanRoot &&
            _scheduler.IsExternalTaskPaused(PCasaScheduler.ContinuousReviewKey))
        {
            interruptedCursor?.Dispose();
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(change.Path).Replace('/', '\\');
        }
        catch
        {
            return;
        }

        if (_backgroundExcludedPaths.TryGetValue(
                change.MonitoredFolderId, out var excludedPaths) &&
            FolderExclusionRules.IsExcluded(normalized, excludedPaths,
                _backgroundIncludedPaths.GetValueOrDefault(change.MonitoredFolderId)
                    ?? Array.Empty<string>()) &&
            !FolderExclusionRules.MayContainIncludedDescendant(normalized,
                _backgroundIncludedPaths.GetValueOrDefault(change.MonitoredFolderId)
                    ?? Array.Empty<string>()))
        {
            return;
        }

        var key = AutomaticTaskKey(new PendingBackgroundScan(
            change.MonitoredFolderId, normalized, change.Kind, change.Recursive));
        if (change.Kind is BackgroundFolderChangeKind.ScanFile or
            BackgroundFolderChangeKind.ScanDirectory or BackgroundFolderChangeKind.RemovePath)
        {
            // Persist at acceptance, not when a scanner eventually starts.
            // Only new events allocate a new revision.
            if (persisted is null)
            {
                try
                {
                    persisted = _folderRepository.AcceptAutomaticChange(key,
                        change with { Path = normalized });
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Background.AcceptChange", ex);
                    interruptedCursor?.Dispose();
                    return;
                }
            }
            else
            {
                // A review result or a restored snapshot may have been
                // superseded by a watcher callback before UI publication.
                var latest = _folderRepository.GetPendingAutomaticChange(key);
                if (latest is null)
                {
                    interruptedCursor?.Dispose();
                    return;
                }
                if (latest.Revision != persisted.Revision)
                {
                    interruptedCursor?.Dispose();
                    interruptedCursor = null;
                    cursorGeneration = null;
                }
                persisted = latest;
            }
            change = persisted.Change;
        }
        if (_backgroundPending.TryGetValue(key, out var existing))
        {
            var revisionChanged = persisted is not null &&
                existing.Revision != persisted.Revision;
            var retained = interruptedCursor ?? (revisionChanged ? null : existing.Resume);
            if (revisionChanged && existing.Resume is not null &&
                !ReferenceEquals(existing.Resume, retained))
                existing.Resume.Dispose();
            if (!revisionChanged && interruptedCursor is not null &&
                existing.Resume is not null &&
                !ReferenceEquals(interruptedCursor, existing.Resume))
                existing.Resume.Dispose();
            _backgroundPending[key] = existing with
            {
                Recursive = existing.Recursive || change.Recursive,
                QueueOrder = persisted?.Order ?? existing.QueueOrder,
                Revision = persisted?.Revision ?? existing.Revision,
                Resume = retained,
                ResumeGeneration = interruptedCursor is not null
                    ? cursorGeneration : revisionChanged ? null : existing.ResumeGeneration,
                ForceExifRefresh = existing.ForceExifRefresh ||
                    change.ForceExifRefresh
            };
        }
        else
        {
            _backgroundPending[key] = new PendingBackgroundScan(
                change.MonitoredFolderId,
                normalized,
                change.Kind,
                change.Recursive,
                interruptedCursor,
                cursorGeneration,
                ForceExifRefresh: change.ForceExifRefresh,
                QueueOrder: persisted?.Order ?? long.MaxValue,
                Revision: persisted?.Revision);
        }

        if (change.Kind is not (BackgroundFolderChangeKind.RemovePath or BackgroundFolderChangeKind.RecoverCache))
        {
            TrackFolderActivity(
                change.Kind == BackgroundFolderChangeKind.ScanFile
                    ? Path.GetDirectoryName(normalized) ?? normalized
                    : normalized, "Pending", change.Recursive);
        }

        // Any higher-priority watcher event yields the review.
        if (_backgroundActiveScanKind == BackgroundFolderChangeKind.RescanRoot &&
            (change.Kind is BackgroundFolderChangeKind.ScanFile or
                BackgroundFolderChangeKind.ScanDirectory or
                BackgroundFolderChangeKind.RemovePath) &&
            _backgroundCurrentScanCancellation is { IsCancellationRequested: false } running)
            _ = running.CancelAsync();
        _cacheMaintenanceCancellation?.Cancel();
        _backgroundDebounceTimer?.Stop();
        _backgroundDebounceTimer?.Start();
        _scheduler.NotifyTaskViewChanged();
    }

    private async void BackgroundDebounceTimer_Tick(object? sender, EventArgs e)
    {
        if (_externalEditPauseActive || _shutdownStarted || _backgroundMonitorClosing)
        {
            _backgroundDebounceTimer?.Stop();
            return;
        }
        _backgroundDebounceTimer?.Stop();

        // Accepted watcher jobs enter the same scheduler arbitration as
        // manual scans and first imports. Only actual foreground/exclusive
        // interactions, not the presence of another queued scan, gate admission.
        if (_shutdownStarted || _backgroundMonitorClosing ||
            _updateDownloadActive || _backgroundWorkerRunning ||
            _folderRemovalPending || _manualFileTransferActive ||
            _backgroundScanSettings?.Enabled != true || !_browseInitialized)
        {
            if (!_folderRemovalPending && _backgroundScanSettings?.Enabled == true &&
                HasRunnableBackgroundRequests())
            {
                _backgroundDebounceTimer?.Start();
            }

            return;
        }

        if (_backgroundPending.Count == 0 ||
            _backgroundLifetime is null ||
            _catalogRemovalService is null)
        {
            return;
        }

        // Keep paused requests with their original cursor and queue identity.
        // A paused-only queue must not spin the debounce timer.
        if (!HasRunnableBackgroundRequests())
            return;
        var pausedRequests = _backgroundPending
            .Where(entry => !IsRunnableBackgroundRequest(entry.Value))
            .ToArray();
        var requests = _backgroundPending.Values
            .Where(IsRunnableBackgroundRequest)
            .Select(request =>
            {
                var defaultPriority = request.Kind ==
                    BackgroundFolderChangeKind.RescanRoot
                    ? SchedulerPriority.Idle : SchedulerPriority.Background;
                var defaultOrder = request.QueueOrder;
                return (Request: request, Placement:
                    _scheduler.EffectiveTaskPlacement(
                        AutomaticTaskKey(request), defaultPriority, defaultOrder));
            })
            .OrderBy(item => item.Placement.Priority)
            .ThenBy(item => item.Placement.QueueOrder)
            .ThenBy(item => item.Request.Path,
                StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Request)
            .ToArray();
        // Retain the existing publication batch to avoid a gallery rebuild
        // per watcher event. Every individual request enters the scheduler
        // separately, and new ranks are reconsidered at every boundary.
        _backgroundPending.Clear();
        foreach (var pausedRequest in pausedRequests)
            _backgroundPending[pausedRequest.Key] = pausedRequest.Value;
        _backgroundInFlight = requests;
        _backgroundInFlightIndex = 0;
        RefreshFolderStatusVisuals();

        _backgroundWorkerRunning = true;
        _isScanning = true;
        UpdateManualScanControls();
        var catalogChanged = false;
        var reviewCompleted = false;
        BrowseCacheProgressWindow? activityWindow = null;
        var backgroundPresenterOwner = new object();
        var scanOwner = new object();
        var nextRequestIndex = 0;

        try
        {
            for (; nextRequestIndex < requests.Length; nextRequestIndex++)
            {
                _backgroundInFlightIndex = nextRequestIndex;
                if (_externalEditPauseActive)
                    throw new OperationCanceledException(
                        "External image editing has foreground priority.");
                // A user can change queued P4 order while an earlier
                // automatic request is blocked behind a manual task.
                // Rebuild from the same persisted ranks, not the old array.
                if (_backgroundOrderChanged)
                    throw new OperationCanceledException(
                        "Taskmanager changed the pending root-review order.");
                var request = requests[nextRequestIndex];
                var taskKey = AutomaticTaskKey(request);
                // A newly accepted/promoted watcher job must not wait
                // behind the remainder of an older in-flight array.
                var placement = _scheduler.EffectiveTaskPlacement(taskKey,
                    (SchedulerPriority)AutomaticTaskPriority(request.Kind), request.QueueOrder);
                if (_backgroundPending.Values.Where(IsRunnableBackgroundRequest)
                    .Select(pending => _scheduler.EffectiveTaskPlacement(
                        AutomaticTaskKey(pending),
                        (SchedulerPriority)AutomaticTaskPriority(pending.Kind), pending.QueueOrder))
                    .Any(other => other.Priority < placement.Priority ||
                        (other.Priority == placement.Priority &&
                         other.QueueOrder < placement.QueueOrder)))
                {
                    _backgroundOrderChanged = true;
                    throw new OperationCanceledException();
                }
                // A newer revision may have arrived while this request was
                // waiting in the array. Do not execute an obsolete receipt.
                if (request.Revision is not null)
                {
                    var latestReceipt = _folderRepository.GetPendingAutomaticChange(taskKey);
                    if (latestReceipt?.Revision != request.Revision)
                    {
                        RequeueBackgroundRequest(request, request.Resume, request.ResumeGeneration);
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        continue;
                    }
                }
                if (request.Kind == BackgroundFolderChangeKind.RescanRoot &&
                    _scheduler.IsExternalTaskPaused(PCasaScheduler.ContinuousReviewKey))
                {
                    request.Resume?.Dispose();
                    continue;
                }
                if (_scheduler.IsExternalTaskPaused(taskKey))
                {
                    _backgroundOrderChanged = true;
                    throw new OperationCanceledException(
                        "Automatic scan paused by the scheduler controller.");
                }
                if (_backgroundSuppressed.Contains(taskKey))
                {
                    request.Resume?.Dispose();
                    DeleteAutomaticInventoryReceipt(request);
                    continue; // stopped before the operation began
                }
                // The resume state belongs to this queued scan request, not
                // to an individual worker attempt. Manual preemption may
                // requeue this SAME request without revisiting old files.
                FolderScanner.ScanResumeState? scanResume = request.Resume;
                var resumeGeneration = request.ResumeGeneration;
                _backgroundLifetime.Token.ThrowIfCancellationRequested();
                if (_manualFileTransferActive || _folderRemovalPending)
                    throw new OperationCanceledException("Exclusive folder operation has priority.");

                var folder = _folderRepository.GetAll()
                    .FirstOrDefault(candidate => candidate.Id == request.MonitoredFolderId);
                if (folder is null)
                {
                    CompleteAutomaticChange(request);
                    scanResume?.Dispose();
                    continue;
                }

                // Watcher queue admission uses a cached rule snapshot; execution
                // must use the current committed rules after a folder edit.
                var currentExclusions = _folderRepository.GetExcludedPaths(folder.Id);
                var currentInclusions = _folderRepository.GetIncludedPaths(folder.Id);
                if (FolderExclusionRules.IsExcluded(request.Path, currentExclusions,
                        currentInclusions) &&
                    !FolderExclusionRules.MayContainIncludedDescendant(request.Path,
                        currentInclusions))
                {
                    CompleteAutomaticChange(request);
                    scanResume?.Dispose();
                    continue;
                }

                if (request.Kind == BackgroundFolderChangeKind.RecoverCache)
                {
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(
                        _backgroundLifetime.Token);
                    _backgroundCurrentScanCancellation = recovery;
                    try
                    {
                        if (_browseCacheRepository is not null &&
                            _browseProfileRepository is not null &&
                            _browseCacheRepository.CountMissing(_browseProfileRepository.Get()) > 0)
                            await RefreshBrowseAfterBackgroundScanAsync(recovery.Token, null);
                    }
                    finally
                    {
                        if (ReferenceEquals(_backgroundCurrentScanCancellation, recovery))
                            _backgroundCurrentScanCancellation = null;
                    }
                    continue;
                }

                if (request.Kind == BackgroundFolderChangeKind.RemovePath)
                {
                    // All reachability checks, confirmation and removal run
                    // under the accepted watcher identity and its effective
                    // priority. No unranked preflight and no SMB on WPF.
                    var deletionSource = _folderRepository.GetSourceState(folder.CanonicalPath);
                    if (deletionSource is not { DesiredPresent: true })
                        continue;
                    using var removalCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _backgroundLifetime.Token);
                    _backgroundCurrentScanCancellation = removalCancellation;
                    _backgroundLiveTaskKey = taskKey;
                    var removed = 0;
                    var verified = false;
                    try
                    {
                        await _scheduler.EnqueueAsync(
                            taskKey, SchedulerPriority.Background,
                            token =>
                            {
                                token.ThrowIfCancellationRequested();
                                if (!_scheduler.SourceAvailability.CanAccess(folder.LocalPath, token))
                                    return Task.CompletedTask;
                                var parent = Path.GetDirectoryName(request.Path);
                                if (string.IsNullOrWhiteSpace(parent) ||
                                    !Directory.Exists(folder.LocalPath) ||
                                    !Directory.Exists(parent))
                                {
                                    _scheduler.SourceAvailability.CheckAfterSourceError(
                                        folder.LocalPath, token);
                                    return Task.CompletedTask;
                                }
                                token.ThrowIfCancellationRequested();
                                if (!File.Exists(request.Path) && !Directory.Exists(request.Path))
                                    removed = _catalogRemovalService.RemovePath(
                                        folder.Id, request.Path, cancellationToken: token,
                                        sourceCanonicalPath: folder.CanonicalPath,
                                        expectedGeneration: deletionSource.Value.Generation,
                                        deferCleanup: true);
                                // Either a verified removal or a reappeared
                                // path acknowledges this revision, never an
                                // unreachable source.
                                verified = true;
                                return Task.CompletedTask;
                            },
                            () => _folderRepository.IsCurrentSourceGeneration(
                                folder.CanonicalPath, deletionSource.Value.Generation, true),
                            removalCancellation.Token,
                            workKind: SchedulerWorkKind.CatalogScan,
                            queueOrder: request.QueueOrder == long.MaxValue
                                ? null : request.QueueOrder);
                    }
                    catch (OperationCanceledException)
                    {
                        if (_backgroundSuppressed.Contains(taskKey)) continue;
                        if (_backgroundOrderChanged || _externalEditPauseActive ||
                            _folderRemovalPending || _backgroundLifetime.IsCancellationRequested)
                            throw;
                        RequeueBackgroundRequest(request, null, null);
                        continue;
                    }
                    finally
                    {
                        if (ReferenceEquals(_backgroundCurrentScanCancellation, removalCancellation))
                            _backgroundCurrentScanCancellation = null;
                        _backgroundLiveTaskKey = null;
                    }
                    if (verified)
                        CompleteAutomaticChange(request);
                    catalogChanged |= removed > 0;
                    if (removed > 0)
                    {
                        ForgetFolderActivity(request.Path);
                        TrackFolderActivity(
                            Path.GetDirectoryName(request.Path) ?? folder.LocalPath,
                            "Ready");
                        RefreshFolderStatusVisuals();
                    }
                    continue;
                }

                var scanPath = request.Kind == BackgroundFolderChangeKind.RescanRoot
                    ? folder.LocalPath
                    : request.Path;
                if (IsPathKnownOffline(scanPath))
                {
                    scanResume?.Dispose();
                    continue;
                }
                // Reachability and existence checks are part of the
                // accepted scan's own execution, not separate P1 probes.
                if (request.Kind == BackgroundFolderChangeKind.RescanRoot &&
                    _scheduler.IsExternalTaskPaused(PCasaScheduler.ContinuousReviewKey))
                {
                    scanResume?.Dispose();
                    continue;
                }
                if (_backgroundOrderChanged)
                    throw new OperationCanceledException(
                        "Taskmanager changed the pending root-review order.");
                if (_manualFileTransferActive || _folderRemovalPending)
                    throw new OperationCanceledException("Exclusive folder operation has priority.");

                TrackFolderActivity(
                    request.Kind == BackgroundFolderChangeKind.ScanFile
                        ? Path.GetDirectoryName(scanPath) ?? scanPath
                        : scanPath, "Working", request.Recursive);
                RefreshFolderStatusVisuals();

                using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    _backgroundLifetime.Token);
                _backgroundCurrentScanCancellation = scanCancellation;
                _activeScanCancellation = scanCancellation;
                _backgroundLiveTaskKey = taskKey;
                _backgroundLiveChecked = scanResume?.CommittedFiles ?? 0;
                _activeScanFolderId = folder.Id;

                var acceptsScanProgress = true;
                _backgroundActiveScanKind = request.Kind;
                _backgroundActiveRootReviewPath =
                    request.Kind == BackgroundFolderChangeKind.RescanRoot
                        ? scanPath : null;
                try
                {
                    // A Stop request may arrive during the preceding SMB
                    // source probe. Honor it here before touching an image;
                    // the scanner's committed SQLite cursor remains intact.
                    if (_backgroundSuppressed.Contains(taskKey))
                    {
                        scanResume?.Dispose();
                        scanResume = null;
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        DeleteAutomaticInventoryReceipt(request);
                        continue;
                    }
                    // Every active NAS scan is visible, even if it only
                    // checks existing photos or finds zero new files.
                    // The scanner is already executing when this callback
                    // arrives, so it is not a speculative queued indicator.
                    string? schedulerScanKey = null;
                    IProgress<ScanProgress> scanProgress = new Progress<ScanProgress>(value =>
                    {
                        // Progress<T> posts to the Dispatcher. A callback
                        // queued before Close can execute after the WPF owner
                        // is already closed (including during a new session).
                        if (!acceptsScanProgress || _manualFileTransferActive ||
                            _shutdownStarted || _backgroundMonitorClosing ||
                            Dispatcher.HasShutdownStarted || !IsLoaded) return;
                        _backgroundLiveChecked = value.ImageFilesSeen;
                        if (schedulerScanKey is not null)
                            _scheduler.ReportProgress(schedulerScanKey,
                                AutomaticTaskTitle(request.Kind), value.CatalogIndexed ?? value.ImageFilesSeen,
                                0, value.CurrentFile);
                        // Continuous checks are passive, never foreground
                        // import presentations. In particular, do not steal
                        // search status or gallery focus every idle pass.
                        if (request.Kind == BackgroundFolderChangeKind.RescanRoot)
                            return;
                        // The same scheduler snapshot drives both the small
                        // progress window and the taskmanager live row.
                        activityWindow ??= AcquireProgressPresenter(backgroundPresenterOwner);
                        activityWindow.UpdateScan(value,
                            showWhileManualWaiting: ScanMayReplaceWaitingManualPresentation(),
                            scanOwner: scanOwner);
                        ShowScannedPhotoFolder(value);
                        // Do not overwrite the shared Tag/scan presenter's
                        // authoritative status-bar projection.
                        if (_visibleManualOperations.Count == 0 &&
                            string.IsNullOrWhiteSpace(TagSearchBox?.Text))
                            StatusText.Text =
                                $"Hintergrund: {value.Added:N0} neu · {value.Changed:N0} geändert · " +
                                $"{value.CacheRepaired:N0} Cache-Reparaturen · {value.Unchanged:N0} unverändert" +
                                (value.Failed > 0 ? $" · {value.Failed:N0} Fehler" : string.Empty);
                    });
                    var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
                    if (sourceState is not { DesiredPresent: true })
                    {
                        scanResume?.Dispose();
                        continue;
                    }
                    ScanResult? scanResult = null;
                    bool unavailablePath = false;
                    CatalogInventoryCheckResult? inventoryResult = null;
                    IReadOnlyList<FolderRepository.AutomaticChangeJob>? acceptedReviewChanges = null;
                    var reviewEpoch = _folderRepository.GetScanEpoch(folder.CanonicalPath);
                    // One confirmed count belongs to the original
                    // directory-inventory receipt, not to a newly allocated
                    // WPF progress object or a new scheduler attempt.
                    var inventoryKey =
                        $"background:inventory:{folder.Id}:{sourceState.Value.Generation}:{request.Kind}:{request.Recursive}:{scanPath}";
                    // Discard only a genuinely obsolete cursor (removed
                    // source or changed inclusion/exclusion generation).
                    // Preemption itself never invalidates the iterator.
                    if (scanResume is not null &&
                        resumeGeneration != sourceState.Value.Generation)
                    {
                        scanResume.Dispose();
                        scanResume = null;
                    }
                    scanResume ??= new FolderScanner.ScanResumeState();
                    _backgroundLiveChecked = scanResume.ImageFilesSeen;
                    resumeGeneration = sourceState.Value.Generation;
                    request = request with
                    {
                        Resume = scanResume,
                        ResumeGeneration = resumeGeneration
                    };
                    // The outer finally requeues the original array on a
                    // manual interruption: keep its receipt up to date.
                    requests[nextRequestIndex] = request;
                    if (request.Kind == BackgroundFolderChangeKind.RescanRoot)
                        PCasaDiagnostics.WriteMessage("Scan.RootStart",
                            $"Folder={folder.LocalPath}; Generation={sourceState.Value.Generation}; " +
                            $"ResumeInMemory={scanResume.ImageFilesSeen}; Kind={request.Kind}");
                    schedulerScanKey = request.Kind == BackgroundFolderChangeKind.RescanRoot
                        ? PCasaScheduler.ContinuousReviewKey
                        : taskKey;
                    // Use the accepted task identity directly. Generation and
                    // scan receipts belong to the source, not the scheduler key.
                    await _scheduler.EnqueueAsync(
                        schedulerScanKey,
                        // A startup root verification is idle work (P4),
                        // never a P2 user action. Concrete watcher changes
                        // outrank it at P3; dedicated initial imports and
                        // all manual P0/P1 packages keep their existing lanes.
                        request.Kind == BackgroundFolderChangeKind.RescanRoot
                            ? SchedulerPriority.Idle : SchedulerPriority.Background,
                        token =>
                        {
                            token.ThrowIfCancellationRequested();
                            unavailablePath = false;
                            if (!_scheduler.SourceAvailability.CanAccess(scanPath, token))
                            {
                                unavailablePath = true;
                                return Task.CompletedTask;
                            }
                            var exists = request.Kind == BackgroundFolderChangeKind.ScanFile
                                ? File.Exists(scanPath) : Directory.Exists(scanPath);
                            token.ThrowIfCancellationRequested();
                            if (!exists)
                            {
                                // A delayed create/change event for a file already
                                // removed or renamed is complete, not an offline
                                // retry. Require explicit positive missing evidence.
                                if (request.Kind == BackgroundFolderChangeKind.ScanFile &&
                                    SourceFilePresence.Probe(scanPath) == FilePresence.ConfirmedMissing)
                                {
                                    var removed = _catalogRemovalService.RemovePath(
                                        folder.Id, scanPath, cancellationToken: token,
                                        sourceCanonicalPath: folder.CanonicalPath,
                                        expectedGeneration: sourceState.Value.Generation,
                                        deferCleanup: true);
                                    scanResult = new ScanResult(0, 0, 0, 0, removed);
                                    return Task.CompletedTask;
                                }
                                _scheduler.SourceAvailability.CheckAfterSourceError(scanPath, token);
                                unavailablePath = true;
                                return Task.CompletedTask;
                            }
                            // Running (not queued) scan: make progress visible
                            // even before a NAS directory yields its first file.
                            scanProgress.Report(new ScanProgress(
                                scanResume.ImageFilesSeen, scanResume.Updated,
                                scanResume.Unchanged, scanResume.Failed, scanPath,
                                scanResume.Added, scanResume.Changed,
                                scanResume.CacheRepaired,
                                CatalogIndexed: request.Kind == BackgroundFolderChangeKind.RescanRoot
                                    ? null : new ImageCatalogRepository(_database).CountIndexedImages(
                                        folder.Id, request.Kind == BackgroundFolderChangeKind.ScanFile
                                            ? Path.GetDirectoryName(scanPath)! : scanPath, request.Recursive)));
                            if (request.Kind == BackgroundFolderChangeKind.RescanRoot)
                            {
                                inventoryResult = new CatalogInventoryCheck(_database).Check(
                                    folder,
                                    _folderRepository.GetExcludedPaths(folder.Id),
                                    _folderRepository.GetIncludedPaths(folder.Id), token,
                                    (count, path) => scanProgress.Report(
                                        new ScanProgress(count, 0, count, 0, path)));
                                token.ThrowIfCancellationRequested();
                                acceptedReviewChanges = _folderRepository.CompleteRootCheck(
                                    folder.Id, sourceState.Value.Generation, reviewEpoch,
                                    inventoryResult.Changes);
                                if (acceptedReviewChanges is null)
                                    throw new OperationCanceledException("Review source changed.");
                                scanResult = new ScanResult(inventoryResult.CheckedFiles, 0,
                                    inventoryResult.CheckedFiles, 0);
                            }
                            else
                                scanResult = request.Kind == BackgroundFolderChangeKind.ScanFile
                                    ? _folderScanner.ScanFile(folder, scanPath, scanProgress, token,
                                        forceExifRefresh: request.ForceExifRefresh)
                                    : _folderScanner.ScanDirectory(
                                        folder, scanPath, request.Recursive,
                                        progress: scanProgress, token, scanResume,
                                        durableScanKey: inventoryKey,
                                        catalogOwnedProgress: true);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, sourceState.Value.Generation, true),
                        scanCancellation.Token,
                        workKind: SchedulerWorkKind.CatalogScan,
                        queueOrder: request.QueueOrder == long.MaxValue
                            ? null : request.QueueOrder);
                    if (unavailablePath)
                    {
                        // Keep accepted watcher receipts for the normal
                        // monitor retry. A failed check cannot advance order.
                        if (request.Kind == BackgroundFolderChangeKind.RescanRoot)
                            _folderRepository.DeferRootReview(folder.Id,
                                sourceState.Value.Generation, DateTime.UtcNow + SourceRetryInterval);
                        scanResume.Dispose();
                        scanResume = null;
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        TrackFolderActivity(scanPath, "Pending", request.Recursive);
                        RefreshFolderStatusVisuals();
                        continue;
                    }
                    var result = scanResult ?? throw new OperationCanceledException();
                    if (inventoryResult is not null)
                    {
                        _backgroundActiveScanKind = null;
                        reviewCompleted = true;
                        foreach (var accepted in acceptedReviewChanges!)
                            QueueBackgroundChange(accepted.Change, persisted: accepted);
                    }
                    CompleteAutomaticChange(request);

                    scanResume.Dispose();
                    scanResume = null;
                    request = request with
                    {
                        Resume = null,
                        ResumeGeneration = null
                    };
                    requests[nextRequestIndex] = request;
                    catalogChanged |= result.CatalogChanged;
                    TrackFolderActivity(
                        request.Kind == BackgroundFolderChangeKind.ScanFile
                            ? Path.GetDirectoryName(scanPath) ?? scanPath
                            : scanPath, result.Failed > 0 ? "Failed" : "Ready",
                        request.Recursive);
                    RefreshFolderStatusVisuals();
                }
                catch (OperationCanceledException)
                {
                    if (_backgroundOrderChanged)
                    {
                        // Requeue the SAME unfinished scanner + receipts
                        // in the outer finally, then sort by durable P4 rank.
                        requests[nextRequestIndex] = request with
                        {
                            Resume = scanResume,
                            ResumeGeneration = resumeGeneration
                        };
                        scanResume = null;
                        throw;
                    }
                    // An explicit deletion retires the durable request,
                    // but leaves the committed catalog untouched.
                    if (_backgroundSuppressed.Contains(taskKey))
                    {
                        scanResume?.Dispose();
                        scanResume = null;
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        DeleteAutomaticInventoryReceipt(request);
                        TrackFolderActivity(scanPath, "Ready");
                        RefreshFolderStatusVisuals();
                        continue;
                    }
                    // Preserve a scanner's live iterator on scheduler/manual
                    // preemption. Shutdown and disabled monitoring release it.
                    if (_backgroundLifetime.IsCancellationRequested ||
                        _backgroundMonitorClosing ||
                        _backgroundScanSettings?.Enabled != true)
                    {
                        scanResume?.Dispose();
                        scanResume = null;
                        request = request with { Resume = null, ResumeGeneration = null };
                        requests[nextRequestIndex] = request;
                    }
                    // A preempted scan is NOT running. The old code left
                    // "Working" in the tree indefinitely and recursively
                    // painted unrelated descendants with spinning arrows.
                    // Preserve pending work; only the scheduler may run it.
                    TrackFolderActivity(
                        request.Kind == BackgroundFolderChangeKind.ScanFile
                            ? Path.GetDirectoryName(scanPath) ?? scanPath
                            : scanPath, "Pending", request.Recursive);
                    RefreshFolderStatusVisuals();
                    if (_backgroundLifetime.IsCancellationRequested ||
                        _manualFileTransferActive || _folderRemovalPending ||
                        _externalEditPauseActive)
                    {
                        throw;
                    }
                    // Unexpected cancellation still needs a future attempt.
                    RequeueBackgroundRequest(request, scanResume, resumeGeneration);
                    scanResume = null;
                }
                catch (Exception scanError)
                {
                    // A failed check never advances catalog order. Retry
                    // on the existing source-availability cadence, not a
                    // separate multi-hour background-review cooldown.
                    if (request.Kind == BackgroundFolderChangeKind.RescanRoot &&
                        resumeGeneration is long failedGeneration)
                    {
                        _folderRepository.DeferRootReview(folder.Id,
                            failedGeneration, DateTime.UtcNow + SourceRetryInterval);
                        PCasaDiagnostics.Write("Background.RootCheck", scanError);
                    }
                    scanResume?.Dispose();
                    scanResume = null;
                    request = request with { Resume = null, ResumeGeneration = null };
                    requests[nextRequestIndex] = request;
                    TrackFolderActivity(
                        request.Kind == BackgroundFolderChangeKind.ScanFile
                            ? Path.GetDirectoryName(scanPath) ?? scanPath
                            : scanPath,
                        "Failed", request.Recursive);
                    RefreshFolderStatusVisuals();
                    // A temporarily unavailable path must not interrupt the UI.
                    // FileSystemWatcher or the next startup reconciliation will retry it.
                }
                finally
                {
                    _backgroundActiveScanKind = null;
                    _backgroundActiveRootReviewPath = null;
                    _backgroundLiveTaskKey = null;
                    _backgroundLiveChecked = 0;
                    acceptsScanProgress = false;
                    // A passive check has no presenter ownership. Likewise
                    // a scan that never published cannot clear someone else's
                    // preview or restore their waiting tag caption.
                    if (activityWindow?.EndScanPhase(scanOwner) == true)
                        RestoreWaitingManualPresentationAfterScan();
                    if (ReferenceEquals(_activeScanCancellation, scanCancellation))
                    {
                        _activeScanCancellation = null;
                        _activeScanFolderId = null;
                    }

                    if (ReferenceEquals(_backgroundCurrentScanCancellation, scanCancellation))
                    {
                        _backgroundCurrentScanCancellation = null;
                    }
                }
            }

            if (catalogChanged && !_manualContextScanActive &&
                !_manualScanAllActive && !_manualFileTransferActive)
            {
                using var recovery = CancellationTokenSource.CreateLinkedTokenSource(
                    _backgroundLifetime.Token);
                _backgroundCurrentScanCancellation = recovery;
                try
                {
                    await RefreshBrowseAfterBackgroundScanAsync(recovery.Token, activityWindow);
                }
                finally
                {
                    if (ReferenceEquals(_backgroundCurrentScanCancellation, recovery))
                        _backgroundCurrentScanCancellation = null;
                }
            }
            else
            {
                // The catalog summary already owns this count. Never open a new
                // SQLite connection to update a cosmetic WPF status label.
                if (!reviewCompleted && _visibleManualOperations.Count == 0 &&
                    string.IsNullOrWhiteSpace(TagSearchBox?.Text))
                    StatusText.Text = $"{_lastCatalogImageCount:N0} Bilder";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Network loss during a scan/cache rebuild must never escape
            // this async void Dispatcher event handler and terminate PCasa.
            PCasaDiagnostics.Write("BackgroundDebounceTimer_Tick", ex);
            if (string.IsNullOrWhiteSpace(TagSearchBox?.Text) &&
                _visibleManualOperations.Count == 0)
                StatusText.Text = "Hintergrundprüfung unterbrochen – erneuter Versuch später";
        }
        finally
        {
            if (activityWindow is not null)
            {
                activityWindow.EndScanPhase(scanOwner);
                activityWindow.EndCachePhase();
                ReleaseProgressPresenter(backgroundPresenterOwner);
            }
            // Do not clear a manual scan's token or progress state if the
            // background worker finishes after manual work has already started.
            if (!_manualScanRunning)
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _backgroundCurrentScanCancellation = null;
            var reordered = _backgroundOrderChanged;
            _backgroundOrderChanged = false;
            _backgroundInFlight = null;
            _backgroundInFlightIndex = 0;
            // A cancellation can arrive before scanner receipt creation.
            // Retire any late receipt only after the worker has unwound.
            var deleted = requests.Where(item =>
                _backgroundSuppressed.Contains(AutomaticTaskKey(item))).ToArray();
            foreach (var item in deleted)
                DeleteAutomaticInventoryReceipt(item);
            var deletedKeys = _backgroundSuppressed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _backgroundSuppressed.Clear();
            _backgroundWorkerRunning = false;
            _isScanning = _manualScanRunning;
            UpdateManualScanControls();
            _scheduler.NotifyTaskViewChanged();

            // A manual scan preempts background work at a cancellation boundary.
            // Never lose deferred SMB notifications when yielding priority.
            if (reordered || _manualFileTransferActive ||
                _folderRemovalPending || _externalEditPauseActive)
            {
                foreach (var remaining in requests.Skip(nextRequestIndex))
                {
                    if (deletedKeys.Contains(AutomaticTaskKey(remaining)))
                    {
                        remaining.Resume?.Dispose();
                        continue;
                    }
                    RequeueBackgroundRequest(remaining,
                        remaining.Resume, remaining.ResumeGeneration);
                }

                // Scans already completed before preemption can leave durable
                // cache jobs. Resume that work later without rescanning images.
                if (catalogChanged)
                {
                    var firstRoot = _dropTargetProjection
                        .Select(root => root.Folder).FirstOrDefault();
                    if (firstRoot is not null)
                        QueueBackgroundChange(new BackgroundFolderChange(
                            firstRoot.Id, firstRoot.LocalPath,
                            BackgroundFolderChangeKind.RecoverCache, false));
                }
            }

            if (!_updateDownloadActive && !_folderRemovalPending &&
                !_externalEditPauseActive && _backgroundScanSettings?.Enabled == true)
            {
                if (HasRunnableBackgroundRequests())
                    _backgroundDebounceTimer?.Start();
                else if (!_backgroundMonitorClosing)
                    // Immediately re-evaluate the next eligible root after
                    // any batch, even if offline/paused requests remain.
                    RefreshBackgroundMonitor();
            }
        }
    }

    private async Task RefreshBrowseAfterBackgroundScanAsync(
        CancellationToken cancellationToken, BrowseCacheProgressWindow? activityWindow)
    {
        if (!_browseInitialized ||
            _browseProfileRepository is null ||
            _browseCacheRepository is null ||
            _browseCacheBuilder is null)
        {
            return;
        }

        var profile = _browseProfileRepository.Get();
        var missing = _browseCacheRepository.CountMissing(profile);

        if (missing > 0)
        {
            _browseCacheBuilding = true;
            var cacheOwner = new object();
            try
            {
                if (string.IsNullOrWhiteSpace(TagSearchBox?.Text) &&
                    _visibleManualOperations.Count == 0)
                    StatusText.Text = $"Hintergrund: {missing:N0} Vorschaudaten vorbereiten";
                activityWindow?.BeginCachePhase(cacheOwner);
                var currentWindow = activityWindow;
                var cacheProgress = currentWindow is null ? null
                    : new Progress<BrowseCacheBuildProgress>(value =>
                        currentWindow.Update(value, cacheOwner));
                await _scheduler.EnqueueAsync(
                    "browse-cache:background",
                    SchedulerPriority.Background,
                    token =>
                    {
                        _browseCacheBuilder.BuildMissing(
                            progress: cacheProgress, token);
                        return Task.CompletedTask;
                    },
                    cancellationToken: cancellationToken);
            }
            finally
            {
                _browseCacheBuilding = false;
                activityWindow?.EndCachePhase(cacheOwner);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false,
            fromScanner: true);
    }

    private void BackgroundScan_WindowClosed(object? sender, EventArgs e)
    {
        if (!_backgroundScanInitialized)
        {
            return;
        }

        _backgroundMonitorClosing = true;
        BackgroundScanSettings.Changed -= BackgroundScanSettings_Changed;

        if (_backgroundFolderMonitor is not null)
        {
            _backgroundFolderMonitor.Changed -= BackgroundFolderMonitor_Changed;
            // The worker owns the monitor until its in-flight SMB check ends.
            if (!_backgroundMonitorRefreshRunning)
            {
                _backgroundFolderMonitor.Dispose();
            }

            _backgroundFolderMonitor = null;
        }

        _backgroundDebounceTimer?.Stop();
        _backgroundMonitorRefreshTimer?.Stop();
        _cacheMaintenanceTimer?.Stop();
        _cacheMaintenanceCancellation?.Cancel();
        _backgroundCurrentScanCancellation?.Cancel();
        _backgroundLifetime?.Cancel();
        _backgroundCurrentScanCancellation?.Dispose();
        _backgroundLifetime?.Dispose();
        _backgroundCurrentScanCancellation = null;
        _backgroundLifetime = null;
        DisposeQueuedScanCursors();
        _backgroundPending.Clear();
    }

    private void DisposeQueuedScanCursors()
    {
        foreach (var request in _backgroundPending.Values)
            request.Resume?.Dispose();
    }

    private sealed record PendingBackgroundScan(
        long MonitoredFolderId,
        string Path,
        BackgroundFolderChangeKind Kind,
        bool Recursive,
        FolderScanner.ScanResumeState? Resume = null,
        long? ResumeGeneration = null,
        bool ForceExifRefresh = false,
        long QueueOrder = long.MaxValue,
        string? Revision = null);
}
