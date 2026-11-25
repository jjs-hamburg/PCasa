using System.IO;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // A durable scan request lives in monitored_folders.initial_scan_pending.
    // This set only avoids repeating failed attempts endlessly in one session.
    private readonly HashSet<long> _initialScanAttempted = new();
    private bool _initialScanPumpRunning;
    // Dispatcher-owned revision of the accepted import order. A read already
    // in flight must not start a root selected before the last reorder.
    private long _initialScanOrderVersion;
    private bool _initialScanRankCheckRunning;
    private bool _initialScanActive;
    // The source's pending-first-import marker lives in SQLite. These are
    // only live presentation/cancellation details of its ONE scanner job;
    // no independent task queue is introduced.
    private string? _initialScanTaskKey;
    private string? _initialScanTaskDirectory;
    private long _initialScanTaskQueueOrder;
    private string? _initialScanTaskCurrentFile;
    private int _initialScanTaskChecked;
    private bool _initialScanWorkerStarted;
    private bool _initialScanStopRequested;
    private bool _initialScanResumeAfterExternalEdit;
    private CancellationTokenSource? _initialScanCancellation;

    private void ReconsiderInitialImportOrder()
    {
        _initialScanOrderVersion++;
        if (_initialScanActive && !_initialScanStopRequested &&
            _initialScanCancellation is { IsCancellationRequested: false } active)
            RequestTaskManagerYield(active);
        QueuePendingInitialScans();
    }

    private void QueuePendingInitialScans()
    {
        _scheduler.NotifyTaskViewChanged();
        if (_shutdownStarted || _updateDownloadActive || _folderManagerOpen ||
            _externalEditPauseActive || _manualFileTransferActive ||
            Dispatcher.HasShutdownStarted) return;
        if (_initialScanPumpRunning)
        {
            // New roots can be accepted while another import is in flight.
            // Only yield if a real, runnable higher-ranked import exists.
            if (_initialScanActive && !_initialScanRankCheckRunning)
                _ = CheckInitialImportRankAsync();
            else if (!_initialScanActive)
                _initialScanOrderVersion++;
            return;
        }
        _ = ProcessPendingInitialScansAsync();
    }

    private async Task CheckInitialImportRankAsync()
    {
        if (_initialScanTaskKey is not { } key ||
            _initialScanCancellation is not { IsCancellationRequested: false } active ||
            _initialScanStopRequested) return;
        _initialScanRankCheckRunning = true;
        try
        {
            var attempted = _initialScanAttempted.ToHashSet();
            var order = _initialScanTaskQueueOrder;
            var higher = await Task.Run(() =>
            {
                var current = _scheduler.EffectiveTaskPlacement(key,
                    SchedulerPriority.ManualAction, order);
                return _folderRepository.GetPendingFirstImportQueue()
                    .Where(row => !row.Paused && !attempted.Contains(row.FolderId))
                    .Select(row => _scheduler.EffectiveTaskPlacement(
                        $"initial:{row.FolderId}", SchedulerPriority.ManualAction, row.QueueOrder))
                    .Any(place => place.Priority < current.Priority ||
                        (place.Priority == current.Priority &&
                         place.QueueOrder < current.QueueOrder));
            });
            if (higher && _initialScanTaskKey == key &&
                ReferenceEquals(_initialScanCancellation, active) &&
                !active.IsCancellationRequested && !_initialScanStopRequested)
                ReconsiderInitialImportOrder();
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Catalog.InitialImportRank", ex); }
        finally { _initialScanRankCheckRunning = false; }
    }

    private async Task ProcessPendingInitialScansAsync()
    {
        if (_initialScanPumpRunning) return;
        _initialScanPumpRunning = true;
        var processedAny = false;
        try
        {
            while (!_shutdownStarted && !_updateDownloadActive && !Dispatcher.HasShutdownStarted && !_folderRemovalPending &&
                   !_externalEditPauseActive && !_folderManagerOpen && !_manualFileTransferActive)
            {
                // Existing manual scans are scheduler owners, not an
                // admission barrier. The scheduler decides which P0 wins.

                // A pending initial import is a durable catalog decision.
                // Read it through the scheduler, never synchronously on the
                // UI dispatcher while another operation holds SQLite.
                var attempted = _initialScanAttempted.ToHashSet();
                var orderVersion = _initialScanOrderVersion;
                // This is admission/control-plane work, not a competing
                // catalog job. Never queue the lookup behind the P0 whose
                // execution the newly accepted import must supersede.
                var next = await Task.Run(
                    () =>
                    {
                        var folders = _folderRepository.GetAll()
                            .ToDictionary(folder => folder.Id);
                        return _folderRepository.GetPendingFirstImportQueue()
                            .Where(row => !row.Paused &&
                                !attempted.Contains(row.FolderId) &&
                                folders.ContainsKey(row.FolderId))
                            .Select(row => new
                            {
                                Folder = folders[row.FolderId],
                                row.QueueOrder,
                                Place = _scheduler.EffectiveTaskPlacement(
                                    $"initial:{row.FolderId}",
                                    SchedulerPriority.ManualAction, row.QueueOrder)
                            })
                            .OrderBy(x => x.Place.Priority)
                            .ThenBy(x => x.Place.QueueOrder)
                            .ThenBy(x => x.Folder.Id)
                            .FirstOrDefault();
                    });
                if (_shutdownStarted || _folderRemovalPending || _folderManagerOpen ||
                    _manualFileTransferActive || _externalEditPauseActive ||
                    Dispatcher.HasShutdownStarted)
                    return;
                if (orderVersion != _initialScanOrderVersion)
                    continue; // select again from the newly committed global order
                if (next is null) break;
                processedAny = true;
                _initialScanAttempted.Add(next.Folder.Id);
                await RunPendingInitialScanAsync(next.Folder, next.QueueOrder);
            }
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.InitialScanQueue", ex);
            StatusText.Text = "Erstimport unterbrochen; beim nächsten Start erneut versuchen";
        }
        finally
        {
            _initialScanPumpRunning = false;
            // If external editing ended while this pump was still unwinding,
            // a resume request must not be lost behind its running flag.
            if (_initialScanResumeAfterExternalEdit &&
                !_externalEditPauseActive && !Dispatcher.HasShutdownStarted)
            {
                _initialScanResumeAfterExternalEdit = false;
                QueuePendingInitialScans();
            }
            // Re-evaluate the ordinary monitor once after an import batch.
            // Failure or temporary unreachability must not strand other,
            // already-imported folders until a restart or watcher event.
            // This remains the ONE existing monitor/scheduler entry point.
            if (processedAny && _backgroundScanInitialized &&
                _backgroundScanSettings?.Enabled == true &&
                !Dispatcher.HasShutdownStarted)
                RefreshBackgroundMonitor();
        }
    }

    private async Task RunPendingInitialScanAsync(MonitoredFolder folder, long queueOrder)
    {
        using var cancellation = new CancellationTokenSource();
        _initialScanCancellation = cancellation;
        // Reuse the job identity as the Scheduler's actual InitialImport
        // operation. The Taskmanager and the progress presenter describe the
        // SAME job, not two artificially synchronized representations.
        var scanKey = $"initial:{folder.Id}";
        _initialScanTaskKey = scanKey;
        _initialScanTaskDirectory = folder.LocalPath;
        _initialScanTaskQueueOrder = queueOrder;
        _initialScanTaskCurrentFile = null;
        _initialScanTaskChecked = 0;
        _initialScanWorkerStarted = false;
        _initialScanStopRequested = false;
        _manualFolderScanWaiting = true;
        _initialScanActive = true;
        _scheduler.NotifyTaskViewChanged();
        _manualScanRunning = true;
        _isScanning = true;
        _activeScanCancellation = cancellation;
        _activeScanFolderId = folder.Id;
        // Retain the running NAS scan cursor; scheduler yield is reversible.
        _browseCacheCancellation?.Cancel();
        _cacheMaintenanceCancellation?.Cancel();
        UpdateManualScanControls();
        TrackFolderActivity(folder.LocalPath, "Pending", recursive: true);
        RefreshFolderStatusVisuals();

        BrowseCacheProgressWindow? progressWindow = null;
        var scanOwner = new object();
        var scanActive = false;
        try
        {
            // The pending node remains visible even if its NAS is offline.
            // Network availability is checked inside the scheduler job, never
            // by a second unscheduled probe or on the WPF dispatcher.
            // Starting the UI pump is not proof of a running scheduler worker.
            // Its queued source remains Pending until actual scan progress.
            progressWindow = AcquireProgressPresenter(scanOwner);
            // Queued NAS source checks are not active scan progress.
            // The first real scanner callback makes the presenter visible.
            var shownWindow = progressWindow;
            var workerStarted = false;
            var progress = new Progress<ScanProgress>(scan =>
            {
                if (_manualFileTransferActive || !scanActive ||
                    _initialScanTaskKey != scanKey) return;
                _initialScanTaskChecked = Math.Max(
                    _initialScanTaskChecked, scan.ImageFilesSeen);
                _initialScanTaskCurrentFile = scan.CurrentFile;
                _initialScanWorkerStarted = true;
                if (scan.VerifiedDirectoryPath is { Length: > 0 } verifiedDirectory)
                {
                    TrackFolderActivity(verifiedDirectory, "Ready");
                    RefreshFolderStatusVisuals();
                }
                if (!workerStarted)
                {
                    workerStarted = true;
                    TrackFolderActivity(folder.LocalPath, "Working", recursive: true);
                    RefreshFolderStatusVisuals();
                }
                shownWindow.UpdateScan(scan,
                    showWhileManualWaiting: ScanMayReplaceWaitingManualPresentation(),
                    scanOwner: scanOwner,
                    phaseOverride: "Erstimport – Dateien einlesen");
                ShowScannedPhotoFolder(scan);
                // While a queued Tag series shares this presenter, the
                // dialog itself owns the status-bar text as well.
                if (!shownWindow.IsManualPresentationActive)
                    StatusText.Text =
                        $"Ordner einlesen: {folder.LocalPath} · {scan.ImageFilesSeen:N0} geprüft · " +
                        $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · {scan.Failed:N0} Fehler";
            });
            StatusText.Text = $"Ordner wird eingelesen: {folder.LocalPath}";

            // A user add overrides old watcher work for exactly this source.
            // The scanner itself owns cache writes and generation-safe commits;
            // this is NOT a second implementation of the import pipeline.
            // Initial imports and explicit scans share one scheduler-owned
            // scan / thumbnail / reconciliation code path.
            scanActive = true;
            PCasaDiagnostics.WriteMessage("Scan.InitialImport",
                $"Resume pending first-import for monitored folder: {folder.LocalPath}");
            var result = await ScheduleCompleteFolderScanAsync(
                folder.LocalPath, SchedulerPriority.ManualAction,
                progress, cancellation.Token, folder.Id,
                requirePendingInitialImport: true, operationKey: scanKey,
                queueOrder: queueOrder);

            scanActive = false;
            TrackFolderActivity(folder.LocalPath,
                result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();
            if (!_folderManagerOpen)
            {
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false,
                    fromScanner: true);
                if (_allFolderProjectionRootPath is not null)
                    RefreshExplorerIndexedFolders();
            }
            StatusText.Text = result.Failed == 0
                ? $"Ordner eingelesen: {folder.LocalPath} · {result.Updated:N0} aktualisiert"
                : $"Ordner unvollständig: {folder.LocalPath} · {result.Failed:N0} Fehler";
        }
        catch (OperationCanceledException)
        {
            scanActive = false;
            // Any newer manual change (move, exclude, remove, rescan) can
            // supersede the old first-import inventory. Re-arm this request
            // for the existing pump if its durable pending marker survives;
            // GetPendingInitialScans prevents retries of removed/completed roots.
            if (_initialScanStopRequested)
            {
                // Explicit Stop: do not spin up the same root again in this
                // session. The persisted initial_scan_pending marker and all
                // confirmed file receipts remain available at next restart
                // or for an explicit user-requested scan.
                _initialScanAttempted.Add(folder.Id);
                StatusText.Text = $"Erstimport gestoppt: {folder.LocalPath}";
            }
            else
            {
                _initialScanAttempted.Remove(folder.Id);
                StatusText.Text = $"Einlesen unterbrochen: {folder.LocalPath} · wird später fortgesetzt";
            }
            TrackFolderActivity(folder.LocalPath, "Pending", recursive: true);
        }
        catch (Exception ex)
        {
            scanActive = false;
            PCasaDiagnostics.Write("Catalog.InitialScan", ex);
            StatusText.Text = $"Ordner nicht eingelesen: {folder.LocalPath} · {ex.Message}";
            TrackFolderActivity(folder.LocalPath, "Failed", recursive: true);
        }
        finally
        {
            if (progressWindow is not null)
            {
                if (progressWindow.EndScanPhase(scanOwner))
                    RestoreWaitingManualPresentationAfterScan();
                ReleaseProgressPresenter(scanOwner);
            }
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _initialScanActive = false;
            _scheduler.NotifyTaskViewChanged();
            _initialScanTaskKey = null;
            _initialScanTaskDirectory = null;
            _initialScanTaskQueueOrder = 0;
            _initialScanTaskCurrentFile = null;
            _initialScanTaskChecked = 0;
            _initialScanWorkerStarted = false;
            _initialScanStopRequested = false;
            if (ReferenceEquals(_initialScanCancellation, cancellation))
                _initialScanCancellation = null;
            _manualScanRunning = _manualContextScanActive || _manualScanAllActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            RefreshFolderStatusVisuals();
            if (!_externalEditPauseActive && _backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }
    }
}
