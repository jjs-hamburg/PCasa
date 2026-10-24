using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _manualScanRunning;

    private void FoldersTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        if (current is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private bool _manualContextScanActive;
    private int _manualContextScanCount;
    private CancellationTokenSource? _manualContextCancellation;

    private async void BrowseScanCurrentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folderRemovalPending)
        {
            StatusText.Text = "Ordner wird gerade entfernt – Scan nicht gestartet.";
            return;
        }

        if (FoldersTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            StatusText.Text = "Bitte zuerst den einzulesenden Ordner auswählen.";
            return;
        }

        // No existence probe, source-generation update or scan is performed
        // outside the scheduler. Even an unreachable SMB path is checked by
        // the priority worker, not by the UI event handler.
        var directory = Path.GetFullPath(tag.FullPath).Replace('/', '\\');
        // Manual requests form a LIFO stack. Do NOT cancel the predecessor:
        // its scanner cursor and progress remain valid for later resumption.
        _manualContextScanCount++;
        _manualContextScanActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        // Manual work preempts through the scheduler yield token. Do not
        // permanently cancel and dispose the background scanner's cursor.
        _cacheMaintenanceCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        _isScanning = true;
        UpdateManualScanControls();

        using var cancellation = new CancellationTokenSource();
        _manualContextCancellation = cancellation;
        _activeScanCancellation = cancellation;
        _activeScanFolderId = null;
        TrackFolderActivity(directory, "Working", recursive: true);
        RefreshFolderStatusVisuals();

        var operationKey = $"catalog:complete-scan:{Guid.NewGuid():N}:{directory}";
        BeginVisibleManualOperation(operationKey, "Ordner aktualisieren", 0);
        StatusText.Text = $"Manueller Scan angefordert (höchste Priorität): {directory}";
        var progress = new Progress<ScanProgress>(scan =>
        {
            if (cancellation.IsCancellationRequested) return;
            ShowScannedPhotoFolder(scan);
            StatusText.Text =
                $"Ordner aktualisieren: {scan.ImageFilesSeen:N0} geprüft · " +
                $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · " +
                $"{scan.CacheRepaired:N0} Cache · {scan.Failed:N0} Fehler";
        });

        try
        {
            var result = await ScheduleCompleteFolderScanAsync(
                directory, SchedulerPriority.ManualAction, progress, cancellation.Token,
                operationKey: operationKey);
            TrackFolderActivity(directory,
                result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: true);
            RefreshFolderTree();
            StatusText.Text = result.Failed == 0
                ? $"Scan abgeschlossen: {result.Updated:N0} aktualisiert · " +
                  $"{result.Removed:N0} verwaiste Einträge entfernt"
                : $"Scan unvollständig: {result.Failed:N0} Fehler; " +
                  "verwaiste Einträge wurden aus Sicherheitsgründen nicht gelöscht.";
        }
        catch (OperationCanceledException)
        {
            TrackFolderActivity(directory, "Pending", recursive: true);
            RefreshFolderStatusVisuals();
            StatusText.Text = $"Manueller Scan abgebrochen: {directory}";
        }
        catch (Exception ex)
        {
            TrackFolderActivity(directory, "Failed", recursive: true);
            RefreshFolderStatusVisuals();
            PCasaDiagnostics.Write("Catalog.ManualFolderScan", ex);
            MessageBox.Show(this, $"Der Ordner konnte nicht eingelesen werden.\n\n{ex.Message}",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(operationKey);
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            // Only the last outstanding context scan may clear the busy
            // flags. A newer scan may finish while this older scan is paused.
            _manualContextScanCount--;
            if (ReferenceEquals(_manualContextCancellation, cancellation))
                _manualContextCancellation = null;
            _manualContextScanActive = _manualContextScanCount > 0;
            _manualScanRunning = _manualContextScanActive ||
                _initialScanActive || _manualScanAllActive;
            _manualFolderScanWaiting = _manualScanRunning;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            UpdateManualScanControls();
            QueuePendingInitialScans();
        }
    }

    // One shared entrance for every complete user-initiated directory scan.
    // The scheduler owns validation, generation advancement, scan, thumbnail
    // indexing and reconciliation; the entry points only present UI progress.
    private async Task<ScanResult> ScheduleCompleteFolderScanAsync(
        string directory, SchedulerPriority priority,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken,
        long? expectedMonitoredFolderId = null,
        bool requirePendingInitialImport = false,
        string? operationKey = null)
    {
        var key = operationKey ?? $"catalog:complete-scan:{Guid.NewGuid():N}:{directory}";
        var schedulerProgress = new ScanJobProgress(_scheduler, key, progress);
        using var step = new CompleteFolderScanStep(this, directory,
            schedulerProgress, expectedMonitoredFolderId, requirePendingInitialImport);
        await _scheduler.EnqueueAsync(key, priority, step.ExecuteAsync,
            cancellationToken: cancellationToken,
            workKind: SchedulerWorkKind.CatalogScan);
        return step.Result ?? throw new OperationCanceledException();
    }

    // All complete scans use this ONE scanner step: explicit one-folder,
    // Scan All, and first import. Its iterator and generation checkpoint
    // belong to the step for the entire lifetime of its parent package.
    private sealed class CompleteFolderScanStep(
        MainWindow owner, string directory, IProgress<ScanProgress>? progress,
        long? expectedMonitoredFolderId = null,
        bool requirePendingInitialImport = false) : IDisposable
    {
        private readonly FolderScanner.ScanResumeState _resume = new();
        private bool _manualGenerationStarted;

        public ScanResult? Result { get; private set; }

        public Task ExecuteAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var folder = owner.ResolveMonitoredFolder(directory)
                ?? throw new InvalidOperationException(
                    "Dieser Ordner liegt außerhalb der PCasa-Überwachung.");
            if (expectedMonitoredFolderId is long expected && folder.Id != expected)
                throw new OperationCanceledException(
                    "Ein inzwischen ersetzter Überwachungsordner darf nicht gescannt werden.");
            if (requirePendingInitialImport &&
                !owner._folderRepository.GetPendingInitialScans().Any(candidate =>
                    candidate.Id == folder.Id))
                throw new OperationCanceledException(
                    "Der Erstimport wurde bereits durch einen neueren Scan erledigt.");
            if (FolderExclusionRules.IsExcluded(directory,
                owner._folderRepository.GetExcludedPaths(folder.Id)))
                throw new InvalidOperationException(
                    "Dieser Ordner ist im Ordnermanager ausgeschlossen.");
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException(
                    $"Der Ordner ist nicht erreichbar: {directory}");
            if (!_manualGenerationStarted)
            {
                owner._folderRepository.BeginManualScan(folder.CanonicalPath);
                _manualGenerationStarted = true;
            }

            // No duplicate import or cleanup path. The scanner owns all EXIF,
            // cache, catalog, and safe reconciliation transitions.
            Result = owner._folderScanner.ScanDirectory(
                folder, directory, recursive: true, progress, token, _resume);
            return Task.CompletedTask;
        }

        public void Dispose() => _resume.Dispose();
    }

    // A synchronous progress relay: scheduler state is committed on the
    // worker before posting any optional UI presentation callback.
    private sealed class ScanJobProgress(
        PCasaScheduler scheduler, string key, IProgress<ScanProgress>? display)
        : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            scheduler.ReportProgress(key, "Bilder einlesen",
                value.ImageFilesSeen, 0, value.CurrentFile);
            display?.Report(value);
        }
    }

    private void UpdateManualScanControls()
        => ScanAllMenuItem.IsEnabled = !_manualScanAllActive;

    private MonitoredFolder? ResolveMonitoredFolder(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace('/', '\\')
            .TrimEnd('\\');

        return FolderExclusionRules.FindOwner(
            normalized, _folderRepository.GetAll());
    }

    private static bool IsInside(string normalizedPath, string rootPath)
    {
        var root = Path.GetFullPath(rootPath)
            .Replace('/', '\\')
            .TrimEnd('\\');

        return string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
