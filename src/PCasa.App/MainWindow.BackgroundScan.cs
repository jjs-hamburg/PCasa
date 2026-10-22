using System.IO;
using System.Windows;
using System.Windows.Threading;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private readonly Dictionary<string, PendingBackgroundScan> _backgroundPending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _backgroundKnownFolderIds = new();
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
        _backgroundFolderMonitor = new BackgroundFolderMonitor();
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
                    await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
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
            _backgroundScanSettings = BackgroundScanSettings.Load();
            if (_backgroundScanSettings.Enabled)
            {
                _backgroundKnownFolderIds.Clear();
            }
            else
            {
                _backgroundPending.Clear();
                _backgroundCurrentScanCancellation?.Cancel();
            }

            RefreshBackgroundMonitor();
        });
    }

    private async void RefreshBackgroundMonitor()
    {
        if (!_backgroundScanInitialized ||
            _backgroundMonitorClosing ||
            _backgroundMonitorRefreshRunning ||
            _backgroundFolderMonitor is null ||
            _backgroundScanSettings is null)
        {
            return;
        }

        _backgroundMonitorRefreshRunning = true;
        var monitor = _backgroundFolderMonitor;
        var enabled = _backgroundScanSettings.Enabled;

        try
        {
            // Both Directory.Exists on a dead UNC share and FileSystemWatcher
            // construction can block in the OS. Never do either on Dispatcher.
            var (folders, availability, exclusions) = await Task.Run(() =>
            {
                var roots = _folderRepository.GetAll();
                var rules = roots.ToDictionary(
                    folder => folder.Id,
                    folder => _folderRepository.GetExcludedPaths(folder.Id));
                var states = monitor.Refresh(roots, enabled);
                return (roots, states, rules);
            });

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

            var userInitialScanIds = _folderRepository.GetPendingInitialScans()
                .Select(root => root.Id).ToHashSet();
            foreach (var folder in folders)
            {
                if (!availability.TryGetValue(folder.Id, out var reachable) || !reachable)
                {
                    // If the NAS reconnects later, it must receive a fresh scan.
                    _backgroundKnownFolderIds.Remove(folder.Id);
                    continue;
                }

                if (_backgroundKnownFolderIds.Add(folder.Id) &&
                    !userInitialScanIds.Contains(folder.Id))
                {
                    // User-selected roots are owned by the durable initial
                    // scan scheduler, never queued a second time by watcher.
                    QueueBackgroundChange(new BackgroundFolderChange(
                        folder.Id,
                        folder.LocalPath,
                        BackgroundFolderChangeKind.RescanRoot,
                        Recursive: true));
                }
            }
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
            {
                monitor.Dispose();
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

    private void QueueBackgroundChange(BackgroundFolderChange change)
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

        var key = $"{change.MonitoredFolderId}|{change.Kind}|{normalized}";
        if (_backgroundPending.TryGetValue(key, out var existing))
        {
            _backgroundPending[key] = existing with
            {
                Recursive = existing.Recursive || change.Recursive
            };
        }
        else
        {
            _backgroundPending[key] = new PendingBackgroundScan(
                change.MonitoredFolderId,
                normalized,
                change.Kind,
                change.Recursive);
        }

        if (change.Kind is not (BackgroundFolderChangeKind.RemovePath or BackgroundFolderChangeKind.RecoverCache))
        {
            TrackFolderActivity(
                change.Kind == BackgroundFolderChangeKind.ScanFile
                    ? Path.GetDirectoryName(normalized) ?? normalized
                    : normalized, "Pending", change.Recursive);
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
            _isScanning)
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
            .OrderBy(request => request.Kind == BackgroundFolderChangeKind.RemovePath ? 0 : 1)
            .ThenBy(request => request.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _backgroundPending.Clear();
        RefreshFolderStatusVisuals();

        _backgroundWorkerRunning = true;
        _isScanning = true;
        UpdateManualScanControls();
        var catalogChanged = false;
        BrowseCacheProgressWindow? activityWindow = null;
        var ownsActivityWindow = false;
        var nextRequestIndex = 0;

        try
        {
            for (; nextRequestIndex < requests.Length; nextRequestIndex++)
            {
                var request = requests[nextRequestIndex];
                _backgroundLifetime.Token.ThrowIfCancellationRequested();
                if (_manualFolderScanWaiting || _folderRemovalPending)
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
                    // A disconnected SMB share must NEVER be interpreted
                    // as a user deleting its entire photo library.
                    if (IsPathKnownOffline(folder.LocalPath))
                    {
                        continue;
                    }

                    var rootReachable = await Task.Run(
                        () => Directory.Exists(folder.LocalPath),
                        _backgroundLifetime.Token);
                    if (!rootReachable)
                    {
                        continue;
                    }

                    // SMB watcher notifications can arrive late. Never prune
                    // an image that has reappeared, or when the parent cannot
                    // be reached reliably enough to verify the deletion.
                    var verifiedGone = await Task.Run(() =>
                    {
                        var parent = Path.GetDirectoryName(request.Path);
                        return !string.IsNullOrWhiteSpace(parent) &&
                               Directory.Exists(parent) &&
                               !File.Exists(request.Path) &&
                               !Directory.Exists(request.Path);
                    }, _backgroundLifetime.Token);
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
                if (IsPathKnownOffline(scanPath))
                {
                    continue;
                }

                // A network path can stall the OS for a long time. The
                // existence check must run on a worker, never on Dispatcher.
                var scanPathExists = await Task.Run(
                    () => request.Kind == BackgroundFolderChangeKind.ScanFile
                        ? File.Exists(scanPath)
                        : Directory.Exists(scanPath),
                    _backgroundLifetime.Token);
                if (!scanPathExists)
                {
                    continue;
                }
                // Directory.Exists can outlive cancellation on a dead NAS.
                // A manual request that arrived during the probe must still
                // supersede this background request before it gets scheduled.
                if (_manualFolderScanWaiting || _folderRemovalPending)
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
                _activeScanFolderId = folder.Id;

                try
                {
                    // Ordinary background checks run silently. Show a dialog
                    // only when the scan actually discovers new images.
                    var scanProgress = new Progress<ScanProgress>(value =>
                    {
                        if (value.Added > 0 && activityWindow is null)
                        {
                            ownsActivityWindow = _browseProgressWindow is null;
                            activityWindow = _browseProgressWindow ??
                                new BrowseCacheProgressWindow { Owner = this };
                            if (ownsActivityWindow)
                            {
                                _browseProgressWindow = activityWindow;
                                activityWindow.Show();
                            }
                        }
                        activityWindow?.UpdateScan(value);
                        ShowScannedPhotoFolder(value);
                        StatusText.Text =
                            $"Hintergrund: {value.Added:N0} neu · {value.Changed:N0} geändert · " +
                            $"{value.CacheRepaired:N0} Cache-Reparaturen · {value.Unchanged:N0} unverändert" +
                            (value.Failed > 0 ? $" · {value.Failed:N0} Fehler" : string.Empty);
                    });
                    var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
                    if (sourceState is not { DesiredPresent: true })
                        continue;
                    ScanResult? scanResult = null;
                    await _scheduler.EnqueueAsync(
                        $"background-scan:{folder.Id}:{sourceState.Value.Generation}:{scanPath}",
                        SchedulerPriority.Background,
                        token =>
                        {
                            scanResult = request.Kind == BackgroundFolderChangeKind.ScanFile
                                ? _folderScanner.ScanFile(folder, scanPath, scanProgress, token)
                                : _folderScanner.ScanDirectory(
                                    folder, scanPath, request.Recursive,
                                    progress: scanProgress, token);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            folder.CanonicalPath, sourceState.Value.Generation, true),
                        scanCancellation.Token);
                    var result = scanResult ?? throw new OperationCanceledException();
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
                    if (_backgroundLifetime.IsCancellationRequested ||
                        _manualFolderScanWaiting || _folderRemovalPending)
                    {
                        throw;
                    }
                }
                catch
                {
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

            if (catalogChanged && !_manualFolderScanWaiting)
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
                StatusText.Text = $"{_imageCatalog.CountAll():N0} Bilder";
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
            if (ownsActivityWindow && activityWindow is not null)
            {
                activityWindow.Finish();
                if (ReferenceEquals(_browseProgressWindow, activityWindow))
                    _browseProgressWindow = null;
            }
            // Do not clear a manual scan's token or progress state if the
            // background worker finishes after manual work has already started.
            if (!_manualScanRunning)
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _backgroundCurrentScanCancellation = null;
            _backgroundWorkerRunning = false;
            _isScanning = _manualScanRunning;
            UpdateManualScanControls();

            // A manual scan preempts background work at a cancellation boundary.
            // Never lose deferred SMB notifications when yielding priority.
            if (_manualFolderScanWaiting || _folderRemovalPending)
            {
                foreach (var remaining in requests.Skip(nextRequestIndex))
                    QueueBackgroundChange(new BackgroundFolderChange(
                        remaining.MonitoredFolderId, remaining.Path,
                        remaining.Kind, remaining.Recursive));

                // Scans already completed before preemption can leave durable
                // cache jobs. Resume that work later without rescanning images.
                if (catalogChanged)
                {
                    var firstRoot = _folderRepository.GetAll().FirstOrDefault();
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
            try
            {
                StatusText.Text = $"Hintergrund: {missing:N0} Vorschaudaten vorbereiten";
                activityWindow?.BeginCachePhase();
                var currentWindow = activityWindow;
                var cacheProgress = currentWindow is null ? null
                    : new Progress<BrowseCacheBuildProgress>(value =>
                        currentWindow.Update(value));
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
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
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
        _backgroundPending.Clear();
    }

    private sealed record PendingBackgroundScan(
        long MonitoredFolderId,
        string Path,
        BackgroundFolderChangeKind Kind,
        bool Recursive);
}
