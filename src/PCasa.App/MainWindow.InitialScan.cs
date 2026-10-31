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
    private bool _initialScanActive;

    private void QueuePendingInitialScans()
    {
        if (!_folderManagerOpen && !_initialScanPumpRunning &&
            !_manualFileTransferActive && !Dispatcher.HasShutdownStarted)
            _ = ProcessPendingInitialScansAsync();
    }

    private async Task ProcessPendingInitialScansAsync()
    {
        if (_initialScanPumpRunning) return;
        _initialScanPumpRunning = true;
        var processedAny = false;
        try
        {
            while (!Dispatcher.HasShutdownStarted && !_folderRemovalPending &&
                   !_folderManagerOpen && !_manualFileTransferActive)
            {
                // A manual scan already in progress owns the foreground job.
                // Its finally block will resume pending initial imports.
                if (_manualFileTransferActive || _manualScanRunning ||
                    (_isScanning && !_backgroundWorkerRunning))
                    return;

                // A pending initial import is a durable catalog decision.
                // Read it through the scheduler, never synchronously on the
                // UI dispatcher while another operation holds SQLite.
                var attempted = _initialScanAttempted.ToHashSet();
                var next = await _scheduler.RunAsync(
                    $"catalog:pending-first-import:{Guid.NewGuid():N}",
                    SchedulerPriority.Background,
                    token =>
                    {
                        token.ThrowIfCancellationRequested();
                        return _folderRepository.GetPendingInitialScans()
                            .FirstOrDefault(folder => !attempted.Contains(folder.Id));
                    }, workKind: SchedulerWorkKind.CatalogRead);
                if (_folderRemovalPending || _folderManagerOpen ||
                    _manualFileTransferActive || Dispatcher.HasShutdownStarted)
                    return;
                if (next is null) break;
                processedAny = true;
                _initialScanAttempted.Add(next.Id);
                await RunPendingInitialScanAsync(next);
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

    private async Task RunPendingInitialScanAsync(MonitoredFolder folder)
    {
        using var cancellation = new CancellationTokenSource();
        _manualFolderScanWaiting = true;
        _initialScanActive = true;
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
                if (_manualFileTransferActive || !scanActive) return;
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
                    scanOwner: scanOwner);
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
                folder.LocalPath, SchedulerPriority.Background,
                progress, cancellation.Token, folder.Id,
                requirePendingInitialImport: true);

            scanActive = false;
            TrackFolderActivity(folder.LocalPath,
                result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();
            if (!_folderManagerOpen)
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false,
                    fromScanner: true);
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
            _initialScanAttempted.Remove(folder.Id);
            StatusText.Text = $"Einlesen unterbrochen: {folder.LocalPath} · wird später fortgesetzt";
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
                progressWindow.EndScanPhase(scanOwner);
                RestoreWaitingManualPresentationAfterScan();
                ReleaseProgressPresenter(scanOwner);
            }
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _initialScanActive = false;
            _manualScanRunning = _manualContextScanActive || _manualScanAllActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            RefreshFolderStatusVisuals();
            if (_backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }
    }
}
