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
        if (!_folderManagerOpen && !_initialScanPumpRunning && !Dispatcher.HasShutdownStarted)
            _ = ProcessPendingInitialScansAsync();
    }

    private async Task ProcessPendingInitialScansAsync()
    {
        if (_initialScanPumpRunning) return;
        _initialScanPumpRunning = true;
        try
        {
            while (!Dispatcher.HasShutdownStarted && !_folderRemovalPending && !_folderManagerOpen)
            {
                // A manual scan already in progress owns the foreground job.
                // Its finally block will resume pending initial imports.
                if (_manualScanRunning || (_isScanning && !_backgroundWorkerRunning))
                    return;

                var next = _folderRepository.GetPendingInitialScans()
                    .FirstOrDefault(folder => !_initialScanAttempted.Contains(folder.Id));
                if (next is null) break;
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
        _backgroundCurrentScanCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        _cacheMaintenanceCancellation?.Cancel();
        ScanAllMenuItem.IsEnabled = false;
        TrackFolderActivity(folder.LocalPath, "Pending", recursive: true);
        RefreshFolderStatusVisuals();

        BrowseCacheProgressWindow? progressWindow = null;
        var ownsWindow = false;
        try
        {
            // The pending node remains visible even if its NAS is offline.
            // Network availability is checked inside the scheduler job, never
            // by a second unscheduled probe or on the WPF dispatcher.
            TrackFolderActivity(folder.LocalPath, "Working", recursive: true);
            RefreshFolderStatusVisuals();
            ownsWindow = _browseProgressWindow is null;
            progressWindow = _browseProgressWindow ??
                new BrowseCacheProgressWindow { Owner = this };
            if (ownsWindow)
            {
                _browseProgressWindow = progressWindow;
                progressWindow.Show();
            }
            var shownWindow = progressWindow;
            var progress = new Progress<ScanProgress>(scan =>
            {
                shownWindow.UpdateScan(scan);
                ShowScannedPhotoFolder(scan);
                StatusText.Text =
                    $"Ordner einlesen: {folder.LocalPath} · {scan.ImageFilesSeen:N0} geprüft · " +
                    $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · {scan.Failed:N0} Fehler";
            });
            StatusText.Text = $"Ordner wird eingelesen: {folder.LocalPath}";

            // A user add overrides old watcher work for exactly this source.
            // The scanner itself owns cache writes and generation-safe commits;
            // this is NOT a second implementation of the import pipeline.
            ScanResult? result = null;
            await _scheduler.EnqueueAsync(
                $"initial-scan:{folder.Id}:{folder.CanonicalPath}",
                SchedulerPriority.UserAction,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    // The same source path may have been removed and re-added.
                    // Its persisted folder ID must still belong to this job.
                    if (!_folderRepository.GetAll().Any(root => root.Id == folder.Id &&
                        string.Equals(root.CanonicalPath, folder.CanonicalPath,
                            StringComparison.OrdinalIgnoreCase)))
                        throw new OperationCanceledException("Outdated first import.");
                    if (!Directory.Exists(folder.LocalPath))
                        throw new DirectoryNotFoundException(
                            $"Überwachter Ordner momentan nicht erreichbar: {folder.LocalPath}");

                    // Source-generation advance, enumeration, decoding and
                    // guarded commits form ONE scheduler-owned operation.
                    _folderRepository.BeginManualScan(folder.CanonicalPath);
                    result = _folderScanner.Scan(folder, progress, token);
                    return Task.CompletedTask;
                },
                () => _folderRepository.GetPendingInitialScans().Any(root =>
                    root.Id == folder.Id),
                cancellation.Token);
            if (result is null) throw new OperationCanceledException();

            TrackFolderActivity(folder.LocalPath,
                result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();
            if (!_folderManagerOpen)
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
            StatusText.Text = result.Failed == 0
                ? $"Ordner eingelesen: {folder.LocalPath} · {result.Updated:N0} aktualisiert"
                : $"Ordner unvollständig: {folder.LocalPath} · {result.Failed:N0} Fehler";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Einlesen unterbrochen: {folder.LocalPath} · wird später fortgesetzt";
            TrackFolderActivity(folder.LocalPath, "Pending", recursive: true);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Catalog.InitialScan", ex);
            StatusText.Text = $"Ordner nicht eingelesen: {folder.LocalPath} · {ex.Message}";
            TrackFolderActivity(folder.LocalPath, "Failed", recursive: true);
        }
        finally
        {
            if (ownsWindow && progressWindow is not null)
            {
                progressWindow.Finish();
                if (ReferenceEquals(_browseProgressWindow, progressWindow))
                    _browseProgressWindow = null;
            }
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            _initialScanActive = false;
            _manualScanRunning = _manualContextScanActive;
            _manualFolderScanWaiting = _manualContextScanActive;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            ScanAllMenuItem.IsEnabled = !_isScanning;
            RefreshFolderStatusVisuals();
            if (_backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }
    }
}
