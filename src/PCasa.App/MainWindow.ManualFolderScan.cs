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
        // A new valid manual request supersedes its predecessor. Scheduler
        // cancellation and source-generation fences own the transition.
        _manualContextCancellation?.Cancel();
        _manualContextScanActive = true;
        _manualScanRunning = true;
        _manualFolderScanWaiting = true;
        _backgroundCurrentScanCancellation?.Cancel();
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

        var ownsWindow = _browseProgressWindow is null;
        var window = _browseProgressWindow ??
            new BrowseCacheProgressWindow { Owner = this };
        if (ownsWindow)
        {
            _browseProgressWindow = window;
            window.Show();
        }
        window.UpdateScan(new ScanProgress(0, 0, 0, 0, directory));
        StatusText.Text = $"Manueller Scan angefordert (höchste Priorität): {directory}";
        var progress = new Progress<ScanProgress>(scan =>
        {
            if (cancellation.IsCancellationRequested) return;
            window.UpdateScan(scan);
            ShowScannedPhotoFolder(scan);
            StatusText.Text =
                $"Ordner aktualisieren: {scan.ImageFilesSeen:N0} geprüft · " +
                $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · " +
                $"{scan.CacheRepaired:N0} Cache · {scan.Failed:N0} Fehler";
        });

        try
        {
            var result = await ScheduleCompleteFolderScanAsync(
                directory, SchedulerPriority.ManualAction, progress, cancellation.Token);
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
            if (ownsWindow)
            {
                window.Finish();
                if (ReferenceEquals(_browseProgressWindow, window))
                    _browseProgressWindow = null;
            }
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }
            if (ReferenceEquals(_manualContextCancellation, cancellation))
            {
                _manualContextCancellation = null;
                _manualContextScanActive = false;
                _manualScanRunning = _initialScanActive || _manualScanAllActive;
                _manualFolderScanWaiting = _initialScanActive || _manualScanAllActive;
                _isScanning = _backgroundWorkerRunning || _manualScanRunning;
                UpdateManualScanControls();
                QueuePendingInitialScans();
            }
        }
    }

    // One shared entrance for every complete user-initiated directory scan.
    // The scheduler owns validation, generation advancement, scan, thumbnail
    // indexing and reconciliation; the entry points only present UI progress.
    private async Task<ScanResult> ScheduleCompleteFolderScanAsync(
        string directory, SchedulerPriority priority,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken,
        long? expectedMonitoredFolderId = null,
        bool requirePendingInitialImport = false)
    {
        ScanResult? completed = null;
        await _scheduler.EnqueueAsync(
            $"catalog:complete-scan:{Guid.NewGuid():N}:{directory}", priority,
            token =>
            {
                token.ThrowIfCancellationRequested();
                var folder = ResolveMonitoredFolder(directory)
                    ?? throw new InvalidOperationException(
                        "Dieser Ordner liegt außerhalb der PCasa-Überwachung.");
                if (expectedMonitoredFolderId is long expected && folder.Id != expected)
                    throw new OperationCanceledException(
                        "Ein inzwischen ersetzter Überwachungsordner darf nicht gescannt werden.");
                if (requirePendingInitialImport &&
                    !_folderRepository.GetPendingInitialScans().Any(candidate =>
                        candidate.Id == folder.Id))
                    throw new OperationCanceledException(
                        "Der Erstimport wurde bereits durch einen neueren Scan erledigt.");
                if (FolderExclusionRules.IsExcluded(directory,
                    _folderRepository.GetExcludedPaths(folder.Id)))
                    throw new InvalidOperationException(
                        "Dieser Ordner ist im Ordnermanager ausgeschlossen.");
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException(
                        $"Der Ordner ist nicht erreichbar: {directory}");
                // The scanner and the generation-safe RemoveMissing operation
                // are shared by context scan, Scan All, initial import and
                // background directory scans. No alternative SQL pruning.
                _folderRepository.BeginManualScan(folder.CanonicalPath);
                completed = _folderScanner.ScanDirectory(
                    folder, directory, recursive: true, progress, token);
                return Task.CompletedTask;
            },
            cancellationToken: cancellationToken,
            workKind: SchedulerWorkKind.CatalogScan);
        return completed ?? throw new OperationCanceledException();
    }

    private void UpdateManualScanControls()
        => ScanAllMenuItem.IsEnabled = !_manualScanAllActive;

    private MonitoredFolder? ResolveMonitoredFolder(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace('/', '\\')
            .TrimEnd('\\');

        return _folderRepository.GetAll()
            .Where(folder => IsInside(normalized, folder.LocalPath))
            .OrderByDescending(folder => Path.GetFullPath(folder.LocalPath).Length)
            .FirstOrDefault();
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
