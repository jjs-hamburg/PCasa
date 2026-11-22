using System.Windows.Threading;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Catalog COUNTS are a tiny, independent read-only projection. A scanner
    // may defer the 500k-photo gallery index to preserve the user's viewport;
    // that must never freeze the numbers shown beside the folder names.
    private readonly HashSet<long> _pendingFolderCountIds = new();
    private readonly Dictionary<long, PhotoFolderSummary?> _liveFolderCountSummaries = new();
    private DispatcherTimer? _folderCountRefreshTimer;
    private bool _folderCountRefreshRunning;
    private int? _liveCatalogImageCount;

    private void QueueLiveFolderCountUpdate(long photoFolderId)
    {
        if (photoFolderId <= 0 || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_browseInitialized || _shutdownStarted) return;
            _pendingFolderCountIds.Add(photoFolderId);
            _folderCountRefreshTimer ??= CreateFolderCountRefreshTimer();
            if (!_folderCountRefreshTimer.IsEnabled)
                _folderCountRefreshTimer.Start();
        }), DispatcherPriority.Background);
    }

    private DispatcherTimer CreateFolderCountRefreshTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += FolderCountRefreshTimer_Tick;
        return timer;
    }

    private async void FolderCountRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _folderCountRefreshTimer?.Stop();
        if (_folderCountRefreshRunning || _pendingFolderCountIds.Count == 0)
        {
            if (_pendingFolderCountIds.Count != 0)
                _folderCountRefreshTimer?.Start();
            return;
        }
        var ids = _pendingFolderCountIds.ToArray();
        _pendingFolderCountIds.Clear();
        _folderCountRefreshRunning = true;
        try
        {
            // The same bounded foreground catalog reader as the gallery.
            // The read never opens NAS photos or waits on P3/P4 scanning.
            var result = await _galleryCatalogReader.ReadAsync(token =>
            {
                var counts = new Dictionary<long, PhotoFolderSummary?>(ids.Length);
                foreach (var id in ids)
                {
                    token.ThrowIfCancellationRequested();
                    counts[id] = _imageCatalog.GetPhotoFolderSummary(id);
                }
                token.ThrowIfCancellationRequested();
                return (Folders: counts, Total: _imageCatalog.CountAll());
            });
            if (Dispatcher.HasShutdownStarted || _shutdownStarted) return;
            foreach (var (id, folder) in result.Folders)
            {
                _liveFolderCountSummaries[id] = folder;
                if (folder is not null && !_folderTreeItems.ContainsKey(id))
                    ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                        DiscoveredPhotoFolderId: id,
                        DiscoveredPhotoFolderPath: folder.LocalPath));
            }
            _liveCatalogImageCount = result.Total;
            RefreshVisibleFolderCounts();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.LiveFolderCounts", ex);
        }
        finally
        {
            _folderCountRefreshRunning = false;
            if (_pendingFolderCountIds.Count > 0 &&
                !Dispatcher.HasShutdownStarted && !_shutdownStarted)
                _folderCountRefreshTimer?.Start();
        }
    }
}
