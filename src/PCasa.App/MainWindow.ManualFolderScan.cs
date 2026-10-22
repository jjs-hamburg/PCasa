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

    private async void BrowseScanCurrentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_manualContextScanActive)
        {
            StatusText.Text = "Der angeforderte manuelle Ordnerscan läuft bereits.";
            return;
        }

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
        _manualContextScanActive = true;
        _manualScanRunning = true;
        _isScanning = true;
        ScanAllMenuItem.IsEnabled = false;

        using var cancellation = new CancellationTokenSource();
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
            window.UpdateScan(scan);
            ShowScannedPhotoFolder(scan);
            StatusText.Text =
                $"Ordner aktualisieren: {scan.ImageFilesSeen:N0} geprüft · " +
                $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · " +
                $"{scan.CacheRepaired:N0} Cache · {scan.Failed:N0} Fehler";
        });

        try
        {
            ScanResult? scanResult = null;
            await _scheduler.EnqueueAsync(
                $"manual-scan:{Guid.NewGuid():N}:{directory}",
                SchedulerPriority.ManualAction,
                token =>
                {
                    // Validation, generation fencing and the complete
                    // filesystem/database reconciliation have one scheduler
                    // owner, with no separate Task.Run or scan code path.
                    var folder = ResolveMonitoredFolder(directory)
                        ?? throw new InvalidOperationException(
                            "Dieser Struktureintrag liegt außerhalb der PCasa-Überwachung.");
                    if (FolderExclusionRules.IsExcluded(directory,
                        _folderRepository.GetExcludedPaths(folder.Id)))
                        throw new InvalidOperationException(
                            "Dieser Ordner ist im Ordnermanager ausgeschlossen.");
                    token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(directory))
                        throw new DirectoryNotFoundException(
                            $"Der Ordner ist nicht erreichbar: {directory}");
                    _folderRepository.BeginManualScan(folder.CanonicalPath);
                    scanResult = _folderScanner.ScanDirectory(
                        folder, directory, recursive: true, progress, token);
                    return Task.CompletedTask;
                },
                cancellationToken: cancellation.Token);

            var result = scanResult ?? throw new OperationCanceledException();
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
            MessageBox.Show(this, $"Der Ordner konnte nicht eingelesen werden.\\n\\n{ex.Message}",
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
            _manualContextScanActive = false;
            _manualScanRunning = _initialScanActive;
            _isScanning = _backgroundWorkerRunning || _manualScanRunning;
            ScanAllMenuItem.IsEnabled = !_isScanning;
            QueuePendingInitialScans();

            if (_backgroundScanSettings?.Enabled == true)
            {
                // Scheduler-controlled cache recovery, not a second scan.
                var monitored = ResolveMonitoredFolder(directory);
                if (monitored is not null)
                    QueueBackgroundChange(new BackgroundFolderChange(
                        monitored.Id, monitored.LocalPath,
                        BackgroundFolderChangeKind.RecoverCache, false));
            }
        }
    }

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
