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
    // Only one complete P4 sweep is admitted at a time. A completed root
    // becomes due again after a minimum interval, never immediately after
    // its last image. Concrete watcher events remain higher priority.
    private static readonly TimeSpan IdleRootReviewInterval = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailedRootReviewBackoff = TimeSpan.FromHours(6);
    private BackgroundFolderChangeKind? _backgroundActiveScanKind;
    private string? _backgroundActiveRootReviewPath;
    private readonly Dictionary<string, int> _backgroundRootReviewYields =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxNewFileYieldsPerRootReview = 3;
    private readonly Dictionary<long, IReadOnlyList<string>> _backgroundExcludedPaths = new();

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
    private string[] _backgroundOfflineRootPaths = Array.Empty<string>();

    private void BackgroundScan_WindowLoaded(object sender, RoutedEventArgs e)
    {
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
        _backgroundMonitorRefreshTimer.Tick += (_, _) => RefreshBackgroundMonitor();
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

        RefreshBackgroundMonitor();
    }

    private async Task RunDeferredCacheMaintenanceAsync()
    {
        if (_cacheMaintenanceRunning || _folderRemovalPending ||
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

    private async void RefreshBackgroundMonitor()
    {
        if (!_backgroundScanInitialized || _backgroundMonitorClosing ||
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

        try
        {
            // Both Directory.Exists on a dead UNC share and FileSystemWatcher
            // construction can block in the OS. Never do either on Dispatcher.
            var (folders, availability, exclusions, pendingInitialIds,
                interruptedInventories, dueRootReviews) = await _scheduler.RunAsync(
                $"background:monitor-probe:{Guid.NewGuid():N}",
                SchedulerPriority.Background,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var roots = _folderRepository.GetAll();
                    var rules = roots.ToDictionary(
                        folder => folder.Id,
                        folder => _folderRepository.GetExcludedPaths(folder.Id));
                    var states = monitor.Refresh(roots, enabled);
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
                    var due = _folderRepository.GetDueRootReviewIds(
                        DateTime.UtcNow, IdleRootReviewInterval);
                    return (roots, states, rules, pending, inventories, due);
                }, cancellationToken: _backgroundLifetime?.Token ?? default,
                workKind: SchedulerWorkKind.SourceProbe);

            if (_backgroundMonitorClosing || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            _backgroundExcludedPaths.Clear();
            foreach (var (folderId, excluded) in exclusions)
            {
                _backgroundExcludedPaths[folderId] = excluded;
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
                // Only touch WPF TreeViewItems when reachability actually changes.
                // Iterating a large folder tree every 30 seconds would stutter.
                ApplyFolderAvailabilityToTree();
                RefreshFolderStatusVisuals();
            }

            if (!enabled || _backgroundScanSettings?.Enabled != true)
            {
                _backgroundKnownFolderIds.Clear();
                return;
            }

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
            var idleCandidate = !_backgroundWorkerRunning &&
                _backgroundPending.Count == 0 && pendingInitialIds.Count == 0 &&
                !_manualContextScanActive && !_manualScanAllActive &&
                !_manualFileTransferActive
                ? dueRootReviews.FirstOrDefault(id =>
                    availability.TryGetValue(id, out var reachable) && reachable)
                : 0L;
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
                if (!firstKnown && !deferred && !idleReviewDue)
                    continue;

                // Resume real interrupted inventory receipts without inventing
                // a second scan key, but defer them until all first imports
                // have finished. Live file watcher events are not deferred.
                var restored = interruptedInventories
                    .Where(receipt => receipt.MonitoredFolderId == folder.Id)
                    .Select(TryRecoverAutomaticInventory)
                    .Where(request => request is not null)
                    .Cast<BackgroundFolderChange>()
                    .ToArray();

                var reconcile = (firstKnown && !firstSnapshot) ||
                    deferred || idleReviewDue;
                if (pendingInitialIds.Count > 0 &&
                    (restored.Length > 0 || reconcile))
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
        }
        catch (Exception ex)
        {
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
        if (!_backgroundScanInitialized ||
            _backgroundScanSettings?.Enabled != true)
            return Array.Empty<AutomaticScanTask>();

        var latest = _scheduler.Snapshot()
            .Where(job => job.Key.StartsWith("background-scan:", StringComparison.Ordinal))
            .GroupBy(job => job.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.OrderByDescending(job => job.Id).First(),
                StringComparer.Ordinal);
        var rows = new List<AutomaticScanTask>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long rank = 0;

        void Add(PendingBackgroundScan task, bool current)
        {
            // Cache rebuilds and catalogue-only removals have no scanner
            // cursor and are not directly interruptible. They are not
            // actionable scan tasks and must not be misleading UI rows.
            if (task.Kind is BackgroundFolderChangeKind.RecoverCache or
                BackgroundFolderChangeKind.RemovePath) return;
            var key = AutomaticTaskKey(task);
            if (_backgroundSuppressed.Contains(key) || !keys.Add(key)) return;
            var path = task.Kind == BackgroundFolderChangeKind.ScanFile
                ? Path.GetDirectoryName(task.Path) ?? task.Path : task.Path;
            // The in-flight scheduler snapshot, not merely a queued WPF
            // request, decides whether a scanner is really running.
            var runningSnapshot = current && key == _backgroundLiveTaskKey
                ? latest.Values.FirstOrDefault(job =>
                    job.Key.StartsWith(
                        $"background-scan:{task.MonitoredFolderId}:",
                        StringComparison.Ordinal) &&
                    job.State == SchedulerJobState.Running)
                : null;
            var checkedFiles = current && key == _backgroundLiveTaskKey
                ? Math.Max(_backgroundLiveChecked,
                    runningSnapshot?.Processed ?? 0)
                : task.Resume?.CommittedFiles ?? 0;
            // Cursor receipts remain durable after a restart even when the
            // current in-memory enumerator has not produced its first file.
            var progress = checkedFiles > 0
                ? UiLanguage.Format("{0:N0} geprüft", checkedFiles)
                : "–";
            rows.Add(new AutomaticScanTask(key,
                UiLanguage.T(AutomaticTaskTitle(task.Kind)), path,
                progress,
                runningSnapshot is not null ? "Läuft" : "Wartet",
                AutomaticTaskPriority(task.Kind), rank++));
        }

        if (_backgroundInFlight is { } inFlight)
            for (var i = _backgroundInFlightIndex; i < inFlight.Length; i++)
                Add(inFlight[i], i == _backgroundInFlightIndex);
        foreach (var task in _backgroundPending.Values
                     .OrderBy(row => AutomaticTaskPriority(row.Kind))
                     .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase))
            Add(task, false);
        return rows;
    }

    // Stop means remove the queued event or cooperatively stop its current
    // attempt. It NEVER removes an indexed photo or a committed file cursor.
    // Full-root checks get a retry backoff, preventing a 30-second restart.
    private async Task<bool> StopAutomaticScanAsync(string key)
    {
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
                    return true;
                }

                _backgroundSuppressed.Add(key);
                if (_backgroundPending.Remove(key, out var pendingFollowup))
                    pendingFollowup.Resume?.Dispose();
                DeferStoppedRootReview(task);
                return true;
            }

        if (_backgroundPending.Remove(key, out var queued))
        {
            queued.Resume?.Dispose();
            DeferStoppedRootReview(queued);
            RefreshFolderStatusVisuals();
            return true;
        }
        return false;
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
                source.Value.Generation, DateTime.UtcNow.AddHours(6));
    }

    private void QueueBackgroundChange(BackgroundFolderChange change,
        FolderScanner.ScanResumeState? interruptedCursor = null,
        long? cursorGeneration = null)
    {
        if (!_backgroundScanInitialized ||
            _backgroundScanSettings?.Enabled != true)
        {
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
            FolderExclusionRules.IsExcluded(normalized, excludedPaths))
        {
            return;
        }

        var key = AutomaticTaskKey(new PendingBackgroundScan(
            change.MonitoredFolderId, normalized, change.Kind, change.Recursive));
        if (_backgroundPending.TryGetValue(key, out var existing))
        {
            // A watcher may add more work while a suspended request is
            // waiting. Preserve its one authoritative scanner cursor.
            var retained = interruptedCursor ?? existing.Resume;
            if (interruptedCursor is not null &&
                existing.Resume is not null &&
                !ReferenceEquals(interruptedCursor, existing.Resume))
                existing.Resume.Dispose();
            _backgroundPending[key] = existing with
            {
                Recursive = existing.Recursive || change.Recursive,
                Resume = retained,
                ResumeGeneration = interruptedCursor is not null
                    ? cursorGeneration : existing.ResumeGeneration,
                ForceExifRefresh = existing.ForceExifRefresh ||
                    change.ForceExifRefresh,
                // Never coalesce a later watcher event into an older partial
                // scan without preserving the need for a final fresh pass.
                NeedsFollowup = existing.NeedsFollowup ||
                    (existing.Resume is not null && interruptedCursor is null) ||
                    (existing.Resume is null && interruptedCursor is not null)
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
                ForceExifRefresh: change.ForceExifRefresh);
        }

        if (change.Kind is not (BackgroundFolderChangeKind.RemovePath or BackgroundFolderChangeKind.RecoverCache))
        {
            TrackFolderActivity(
                change.Kind == BackgroundFolderChangeKind.ScanFile
                    ? Path.GetDirectoryName(normalized) ?? normalized
                    : normalized, "Pending", change.Recursive);
        }

        // A newly copied file/directory must not wait days for a full P4
        // inventory to finish. Yield only the idle root review; the existing
        // scanner retains its live enumerator and persisted directory cursor
        // and is placed back behind the newly discovered P3 work.
        if (_backgroundActiveScanKind == BackgroundFolderChangeKind.RescanRoot &&
            _backgroundActiveRootReviewPath is string rootReview &&
            change.Kind is BackgroundFolderChangeKind.ScanFile or
                BackgroundFolderChangeKind.ScanDirectory)
        {
            // Fairness: a steady stream of new photos must not keep
            // interrupting the SAME P4 root forever. Yield to the first
            // three concrete discovery bursts, then finish this root before
            // processing further queued watcher events.
            var yields = _backgroundRootReviewYields.GetValueOrDefault(rootReview);
            if (yields < MaxNewFileYieldsPerRootReview &&
                _backgroundCurrentScanCancellation is
                    { IsCancellationRequested: false } running)
            {
                _backgroundRootReviewYields[rootReview] = yields + 1;
                _ = running.CancelAsync();
            }
        }
        _cacheMaintenanceCancellation?.Cancel();
        _backgroundDebounceTimer?.Stop();
        _backgroundDebounceTimer?.Start();
    }

    private async void BackgroundDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _backgroundDebounceTimer?.Stop();

        if (_backgroundWorkerRunning ||
            _folderRemovalPending ||
            _backgroundScanSettings?.Enabled != true ||
            !_browseInitialized ||
            _browseCacheBuilding ||
            _manualFileTransferActive ||
            _manualContextScanActive ||
            _manualScanAllActive)
        {
            if (!_folderRemovalPending && _backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
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

        var requests = _backgroundPending.Values
            // A concrete file discovery must never queue behind the
            // startup full-root verification. Both use this same scanner.
            .OrderBy(request => request.Kind switch
            {
                BackgroundFolderChangeKind.RemovePath => 0,
                BackgroundFolderChangeKind.ScanFile => 1,
                BackgroundFolderChangeKind.ScanDirectory => 2,
                BackgroundFolderChangeKind.RescanRoot => 3,
                _ => 4
            })
            .ThenBy(request => request.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _backgroundPending.Clear();
        _backgroundInFlight = requests;
        _backgroundInFlightIndex = 0;
        RefreshFolderStatusVisuals();

        _backgroundWorkerRunning = true;
        _isScanning = true;
        UpdateManualScanControls();
        var catalogChanged = false;
        BrowseCacheProgressWindow? activityWindow = null;
        var backgroundPresenterOwner = new object();
        var scanOwner = new object();
        var nextRequestIndex = 0;

        try
        {
            for (; nextRequestIndex < requests.Length; nextRequestIndex++)
            {
                _backgroundInFlightIndex = nextRequestIndex;
                var request = requests[nextRequestIndex];
                var taskKey = AutomaticTaskKey(request);
                if (_backgroundSuppressed.Remove(taskKey))
                {
                    request.Resume?.Dispose();
                    continue; // stopped before the operation began
                }
                // The resume state belongs to this queued scan request, not
                // to an individual worker attempt. Manual preemption may
                // requeue this SAME request without revisiting old files.
                FolderScanner.ScanResumeState? scanResume = request.Resume;
                var resumeGeneration = request.ResumeGeneration;
                _backgroundLifetime.Token.ThrowIfCancellationRequested();
                if (_manualContextScanActive || _manualScanAllActive ||
                    _manualFileTransferActive || _folderRemovalPending)
                    throw new OperationCanceledException("User-requested folder operation has priority.");

                var folder = _folderRepository.GetAll()
                    .FirstOrDefault(candidate => candidate.Id == request.MonitoredFolderId);
                if (folder is null)
                {
                    continue;
                }

                if (_backgroundExcludedPaths.TryGetValue(
                        folder.Id, out var currentExclusions) &&
                    FolderExclusionRules.IsExcluded(request.Path, currentExclusions))
                {
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
                    if (!_scheduler.SourceAvailability.CanAccess(folder.LocalPath,
                            _backgroundLifetime.Token)) continue;
                    // A disconnected SMB share must NEVER be interpreted
                    // as a user deleting its entire photo library.
                    if (IsPathKnownOffline(folder.LocalPath))
                    {
                        continue;
                    }

                    var rootReachable = await _scheduler.RunAsync(
                        $"background:root-reachable:{folder.Id}:{Guid.NewGuid():N}",
                        SchedulerPriority.Background,
                        token =>
                        {
                            token.ThrowIfCancellationRequested();
                            var exists = Directory.Exists(folder.LocalPath);
                            if (!exists)
                                _scheduler.SourceAvailability.CheckAfterSourceError(
                                    folder.LocalPath, token);
                            return exists;
                        },
                        cancellationToken: _backgroundLifetime.Token,
                        workKind: SchedulerWorkKind.SourceProbe);
                    if (!rootReachable)
                        continue;

                    // SMB watcher notifications can arrive late. Never prune
                    // an image that has reappeared, or when the parent cannot
                    // be reached reliably enough to verify the deletion.
                    var verifiedGone = await _scheduler.RunAsync(
                        $"background:confirm-missing:{Guid.NewGuid():N}",
                        SchedulerPriority.Background,
                        token =>
                        {
                            token.ThrowIfCancellationRequested();
                            var parent = Path.GetDirectoryName(request.Path);
                            return !string.IsNullOrWhiteSpace(parent) &&
                                   Directory.Exists(parent) &&
                                   !File.Exists(request.Path) &&
                                   !Directory.Exists(request.Path);
                        }, cancellationToken: _backgroundLifetime.Token,
                        workKind: SchedulerWorkKind.SourceProbe);
                    if (!verifiedGone)
                        continue;

                    // Deletion and scanning must share the same scheduler lane.
                    // Recheck the path at execution time: a delayed watcher
                    // event must not delete a file that has since returned.
                    var deletionSource = _folderRepository.GetSourceState(folder.CanonicalPath);
                    if (deletionSource is not { DesiredPresent: true })
                        continue;
                    var removed = 0;
                    await _scheduler.EnqueueAsync(
                        $"background-delete:{folder.Id}:{deletionSource.Value.Generation}:{request.Path}",
                        SchedulerPriority.Background,
                        token =>
                        {
                            token.ThrowIfCancellationRequested();
                            var parent = Path.GetDirectoryName(request.Path);
                            if (!string.IsNullOrWhiteSpace(parent) &&
                                Directory.Exists(folder.LocalPath) &&
                                Directory.Exists(parent) &&
                                !File.Exists(request.Path) &&
                                !Directory.Exists(request.Path))
                            {
                                removed = _catalogRemovalService.RemovePath(
                                    folder.Id, request.Path, cancellationToken: token,
                                    sourceCanonicalPath: folder.CanonicalPath,
                                    expectedGeneration: deletionSource.Value.Generation,
                                    deferCleanup: true);
                            }
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, deletionSource.Value.Generation, true),
                        _backgroundLifetime.Token);
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
                if (!_scheduler.SourceAvailability.CanAccess(scanPath,
                        _backgroundLifetime.Token) || IsPathKnownOffline(scanPath))
                {
                    // A missing network mount is a normal dormant state.
                    // Never manufacture a new inventory upon reconnection.
                    scanResume?.Dispose();
                    continue;
                }

                // A network path can stall the OS for a long time. The
                // existence check must run on a worker, never on Dispatcher.
                var scanPathExists = await _scheduler.RunAsync(
                    $"background:scan-path-probe:{Guid.NewGuid():N}",
                    SchedulerPriority.Background,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        var exists = request.Kind == BackgroundFolderChangeKind.ScanFile
                            ? File.Exists(scanPath)
                            : Directory.Exists(scanPath);
                        if (!exists)
                            _scheduler.SourceAvailability.CheckAfterSourceError(
                                scanPath, token);
                        return exists;
                    }, cancellationToken: _backgroundLifetime.Token,
                    workKind: SchedulerWorkKind.SourceProbe);
                if (!scanPathExists)
                {
                    scanResume?.Dispose();
                    continue;
                }
                // Directory.Exists can outlive cancellation on a dead NAS.
                // A manual request that arrived during the probe must still
                // supersede this background request before it gets scheduled.
                if (_manualContextScanActive || _manualScanAllActive ||
                    _manualFileTransferActive || _folderRemovalPending)
                    throw new OperationCanceledException("Manual folder action has priority.");

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
                    if (_backgroundSuppressed.Remove(taskKey))
                    {
                        scanResume?.Dispose();
                        scanResume = null;
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        DeferStoppedRootReview(request);
                        continue;
                    }
                    // Every active NAS scan is visible, even if it only
                    // checks existing photos or finds zero new files.
                    // The scanner is already executing when this callback
                    // arrives, so it is not a speculative queued indicator.
                    string? schedulerScanKey = null;
                    IProgress<ScanProgress> scanProgress = new Progress<ScanProgress>(value =>
                    {
                        if (!acceptsScanProgress || _manualFileTransferActive) return;
                        _backgroundLiveChecked = value.ImageFilesSeen;
                        if (schedulerScanKey is not null)
                            _scheduler.ReportProgress(schedulerScanKey,
                                AutomaticTaskTitle(request.Kind), value.ImageFilesSeen,
                                0, value.CurrentFile);
                        // The same scheduler snapshot drives both the small
                        // progress window and the taskmanager live row.
                        activityWindow ??= AcquireProgressPresenter(backgroundPresenterOwner);
                        activityWindow.UpdateScan(value,
                            showWhileManualWaiting: ScanMayReplaceWaitingManualPresentation(),
                            scanOwner: scanOwner);
                        ShowScannedPhotoFolder(value);
                        // Do not overwrite the shared Tag/scan presenter's
                        // authoritative status-bar projection.
                        if (_visibleManualOperations.Count == 0)
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
                    if (request.Kind != BackgroundFolderChangeKind.ScanFile &&
                        scanResume.CommittedFiles == 0)
                        scanResume.ImageFilesSeen =
                            _folderRepository.GetConfirmedRegularScanCount(inventoryKey);
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
                    schedulerScanKey =
                        $"background-scan:{folder.Id}:{sourceState.Value.Generation}:{scanPath}";
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
                            // Running (not queued) scan: make progress visible
                            // even before a NAS directory yields its first file.
                            scanProgress.Report(new ScanProgress(
                                scanResume.ImageFilesSeen, scanResume.Updated,
                                scanResume.Unchanged, scanResume.Failed, scanPath,
                                scanResume.Added, scanResume.Changed,
                                scanResume.CacheRepaired));
                            scanResult = request.Kind == BackgroundFolderChangeKind.ScanFile
                                ? _folderScanner.ScanFile(folder, scanPath, scanProgress, token,
                                    forceExifRefresh: request.ForceExifRefresh)
                                : _folderScanner.ScanDirectory(
                                    folder, scanPath, request.Recursive,
                                    progress: scanProgress, token, scanResume,
                                    durableScanKey: inventoryKey);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, sourceState.Value.Generation, true),
                        scanCancellation.Token,
                        workKind: SchedulerWorkKind.CatalogScan);
                    var result = scanResult ?? throw new OperationCanceledException();
                    if (request.Kind == BackgroundFolderChangeKind.RescanRoot)
                        _backgroundRootReviewYields.Remove(scanPath);
                    scanResume.Dispose();
                    scanResume = null;
                    var followup = request.NeedsFollowup;
                    request = request with
                    {
                        Resume = null,
                        ResumeGeneration = null,
                        NeedsFollowup = false
                    };
                    requests[nextRequestIndex] = request;
                    catalogChanged |= result.CatalogChanged;
                    if (followup)
                        QueueBackgroundChange(new BackgroundFolderChange(
                            request.MonitoredFolderId, request.Path,
                            request.Kind, request.Recursive,
                            ForceExifRefresh: request.ForceExifRefresh));
                    TrackFolderActivity(
                        request.Kind == BackgroundFolderChangeKind.ScanFile
                            ? Path.GetDirectoryName(scanPath) ?? scanPath
                            : scanPath, result.Failed > 0 ? "Failed" : "Ready",
                        request.Recursive);
                    RefreshFolderStatusVisuals();
                }
                catch (OperationCanceledException)
                {
                    // A user explicitly stopped this automatic task. Keep
                    // committed SQLite scan receipts but do not requeue
                    // the same request immediately after cancellation.
                    if (_backgroundSuppressed.Remove(taskKey))
                    {
                        scanResume?.Dispose();
                        scanResume = null;
                        requests[nextRequestIndex] = request with
                        {
                            Resume = null, ResumeGeneration = null
                        };
                        DeferStoppedRootReview(request);
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
                        _manualContextScanActive || _manualScanAllActive ||
                        _manualFileTransferActive || _folderRemovalPending)
                    {
                        throw;
                    }
                    // Unexpected cancellation still needs a future attempt.
                    QueueBackgroundChange(new BackgroundFolderChange(
                        request.MonitoredFolderId, request.Path,
                        request.Kind, request.Recursive), scanResume, resumeGeneration);
                    scanResume = null;
                }
                catch
                {
                    // Filesystem or source errors never erase unfinished
                    // directory receipts. Back off the whole-root P4 cycle
                    // rather than restarting a failed NAS scan every 30 sec.
                    if (request.Kind == BackgroundFolderChangeKind.RescanRoot &&
                        resumeGeneration is long failedGeneration)
                        _folderRepository.DeferRootReview(folder.Id,
                            failedGeneration, DateTime.UtcNow + FailedRootReviewBackoff);
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
                    activityWindow?.EndScanPhase(scanOwner);
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
            _backgroundInFlight = null;
            _backgroundInFlightIndex = 0;
            _backgroundSuppressed.Clear();
            _backgroundWorkerRunning = false;
            _isScanning = _manualScanRunning;
            UpdateManualScanControls();

            // A manual scan preempts background work at a cancellation boundary.
            // Never lose deferred SMB notifications when yielding priority.
            if (_manualContextScanActive || _manualScanAllActive ||
                _manualFileTransferActive || _folderRemovalPending)
            {
                foreach (var remaining in requests.Skip(nextRequestIndex))
                    QueueBackgroundChange(new BackgroundFolderChange(
                        remaining.MonitoredFolderId, remaining.Path,
                        remaining.Kind, remaining.Recursive,
                        ForceExifRefresh: remaining.ForceExifRefresh),
                        remaining.Resume, remaining.ResumeGeneration);

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

            if (!_folderRemovalPending && _backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
            {
                _backgroundDebounceTimer?.Start();
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
        bool NeedsFollowup = false,
        bool ForceExifRefresh = false);
}
