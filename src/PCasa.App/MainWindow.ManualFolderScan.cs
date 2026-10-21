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

    private async void BrowseScanCurrentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning && !_backgroundWorkerRunning)
        {
            return;
        }

        if (FoldersTree.SelectedItem is not TreeViewItem item ||
            item.Tag is not FolderTreeTag tag)
        {
            return;
        }

        var folder = ResolveMonitoredFolder(tag.FullPath);
        if (folder is null)
        {
            MessageBox.Show(
                this,
                "Dieser Struktureintrag liegt oberhalb der überwachten Ordner und kann nicht direkt eingelesen werden.",
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var directory = Path.GetFullPath(tag.FullPath).Replace('/', '\\');
        if (FolderExclusionRules.IsExcluded(
                directory, _folderRepository.GetExcludedPaths(folder.Id)))
        {
            MessageBox.Show(this,
                "Dieser Unterordner ist im Ordnermanager ausgeschlossen.",
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A missing VPN/SMB root can block Directory.Exists. Never probe the
        // network synchronously from the folder-tree context menu.
        if (IsPathKnownOffline(directory) ||
            !await Task.Run(() => Directory.Exists(directory)))
        {
            MessageBox.Show(
                this,
                $"Der Ordner ist nicht erreichbar:\n\n{directory}",
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_folderRemovalPending) return;

        // Physical pack compaction is low priority. An explicit folder
        // refresh must preempt it rather than block on the shared cache lock.
        if (_cacheMaintenanceRunning)
        {
            _cacheMaintenanceCancellation?.Cancel();
            while (_cacheMaintenanceRunning && !Dispatcher.HasShutdownStarted)
                await Task.Delay(40);
        }
        if (_folderRemovalPending) return;

        // Explicit user work takes precedence over automatic SMB checks.
        if (_backgroundWorkerRunning)
        {
            _manualFolderScanWaiting = true;
            try
            {
                _backgroundCurrentScanCancellation?.Cancel();
                while (_backgroundWorkerRunning && !Dispatcher.HasShutdownStarted)
                    await Task.Delay(40);
            }
            finally
            {
                _manualFolderScanWaiting = false;
            }
        }
        // Interrupted cache jobs remain durable and resume later. An explicit
        // manual folder update must not wait for the global recovery pass.
        if (_browseCacheBuilding && !_backgroundWorkerRunning)
        {
            _browseCacheCancellation?.Cancel();
            while (_browseCacheBuilding && !Dispatcher.HasShutdownStarted)
                await Task.Delay(40);
        }
        if (_isScanning || _browseCacheBuilding) return;

        _isScanning = true;
        ScanAllMenuItem.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        _activeScanCancellation = cancellation;
        _activeScanFolderId = folder.Id;

        TrackFolderActivity(directory, "Working", recursive: true);
        RefreshFolderStatusVisuals();

        var window = new BrowseCacheProgressWindow { Owner = this };
        _browseProgressWindow = window;
        window.Show();
        window.UpdateScan(new ScanProgress(0, 0, 0, 0, directory));

        var progress = new Progress<ScanProgress>(scan =>
        {
            window.UpdateScan(scan);
            QueueImportedPreview(scan);
            StatusText.Text =
                $"Ordner aktualisieren: {scan.ImageFilesSeen:N0} geprüft · " +
                $"{scan.Added:N0} neu · {scan.Changed:N0} geändert · " +
                $"{scan.CacheRepaired:N0} Cache · {scan.Failed:N0} Fehler";
        });

        try
        {
            StatusText.Text = $"Einlesen: {directory}";
            // Manual scans enter the shared scheduler rather than starting
            // an independent Task.Run worker. The source generation fences
            // work that was queued before removal/re-inclusion.
            var sourceState = _folderRepository.GetSourceState(folder.CanonicalPath);
            if (sourceState is not { DesiredPresent: true })
                throw new OperationCanceledException("Source is no longer monitored.");
            ScanResult? scanResult = null;
            await _scheduler.EnqueueAsync(
                $"manual-scan:{folder.Id}:{directory}",
                SchedulerPriority.UserAction,
                token =>
                {
                    scanResult = _folderScanner.ScanDirectory(
                        folder, directory, recursive: true, progress, token);
                    return Task.CompletedTask;
                },
                () => _folderRepository.IsCurrentSourceGeneration(
                    folder.CanonicalPath, sourceState.Value.Generation, true),
                cancellation.Token);
            var result = scanResult ?? throw new OperationCanceledException();
            TrackFolderActivity(directory, result.Failed > 0 ? "Failed" : "Ready", recursive: true);
            RefreshFolderStatusVisuals();

            // The scanner has already built every configured tier for changed
            // images. Do not stall a high-priority manual refresh behind other
            // unrelated pending cache jobs.
            await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: true);
        }
        catch (OperationCanceledException)
        {
            TrackFolderActivity(directory, "Pending", recursive: true);
            RefreshFolderStatusVisuals();
        }
        catch (Exception ex)
        {
            TrackFolderActivity(directory, "Failed", recursive: true);
            RefreshFolderStatusVisuals();
            MessageBox.Show(
                this,
                $"Der Ordner konnte nicht eingelesen werden.\n\n{ex.Message}",
                "PCasa",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            window.Finish();
            if (ReferenceEquals(_browseProgressWindow, window))
                _browseProgressWindow = null;
            if (ReferenceEquals(_activeScanCancellation, cancellation))
            {
                _activeScanCancellation = null;
                _activeScanFolderId = null;
            }

            _isScanning = false;
            ScanAllMenuItem.IsEnabled = true;

            // Cache repair preempted by the manual action is resumed silently,
            // without forcing another directory scan.
            if (_backgroundScanSettings?.Enabled == true)
                QueueBackgroundChange(new BackgroundFolderChange(
                    folder.Id, folder.LocalPath,
                    BackgroundFolderChangeKind.RecoverCache, false));
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
