using System.IO;
using System.Windows;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private readonly Dictionary<long, int> _galleryRotationDraft = [];
    private readonly Dictionary<long, int> _galleryRotationWriting = [];
    private DispatcherTimer? _galleryRotationTimer;
    private readonly Dictionary<string, long[]> _rotationJobImages = new(StringComparer.Ordinal);

    private bool CanRotateGalleryImage(long id) =>
        !_pendingEditImageIds.Contains(id) &&
        _dragPublishedImages.TryGetValue(id, out var entry) &&
        !entry.HasImportError && EditableImageFormats.Supports(entry.FileName);

    private void UpdateGalleryRotationButtons()
    {
        if (GalleryRotationButtons is null) return;
        GalleryRotationButtons.Visibility = !_viewerOpen &&
            _selectedImageIds.Any(CanRotateGalleryImage)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GalleryRotateLeft_Click(object sender, RoutedEventArgs e)
        => RotateGallerySelection(-1);

    private void GalleryRotateRight_Click(object sender, RoutedEventArgs e)
        => RotateGallerySelection(1);

    private void RotateGallerySelection(int turns)
    {
        if (_viewerOpen || _shutdownStarted || _databaseMaintenancePending) return;
        foreach (var id in _selectedImageIds.Where(CanRotateGalleryImage))
        {
            var angle = (_galleryRotationDraft.GetValueOrDefault(id) + turns + 4) % 4;
            if (angle == 0) _galleryRotationDraft.Remove(id);
            else _galleryRotationDraft[id] = angle;
        }
        UpdateGalleryRotationPreview();
        if (_galleryRotationTimer is null)
        {
            _galleryRotationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _galleryRotationTimer.Tick += (_, _) => FlushGalleryRotations();
        }
        _galleryRotationTimer.Stop();
        if (_galleryRotationDraft.Count != 0) _galleryRotationTimer.Start();
    }

    private void UpdateGalleryRotationPreview()
    {
        var preview = new Dictionary<long, int>(_galleryRotationWriting);
        foreach (var (id, angle) in _galleryRotationDraft) preview[id] = angle;
        BrowseSurface.SetPreviewRotations(preview);
    }

    private void FlushGalleryRotations()
    {
        _galleryRotationTimer?.Stop();
        if (_galleryRotationDraft.Count == 0) return;
        var items = _galleryRotationDraft.ToArray();
        _galleryRotationDraft.Clear();
        foreach (var (id, angle) in items)
        {
            _galleryRotationWriting[id] = angle;
            _pendingEditImageIds.Add(id);
        }
        UpdateGalleryRotationButtons();
        var key = "image:gallery-rotation:" + Guid.NewGuid().ToString("N");
        var title = UiLanguage.T("Bildbearbeitung speichern");
        _rotationJobImages[key] = items.Select(item => item.Key).ToArray();
        BeginVisibleManualOperation(key, title, items.Length);
        try
        {
            var failures = new List<Exception>();
            var processed = 0;
            // Exactly one nonpersistent P0 package; file identities are resolved
            // at execution so a prior accepted rename cannot target a stale path.
            var task = _scheduler.EnqueueSessionWriteAsync(key, token =>
            {
                while (processed < items.Length)
                {
                    token.ThrowIfCancellationRequested();
                    var (id, angle) = items[processed];
                    try
                    {
                        string path; long folderId;
                        using (var db = _database.OpenConnection())
                        using (var command = db.CreateCommand())
                        {
                            command.CommandText = "SELECT local_path,folder_id FROM images WHERE id=$id;";
                            command.Parameters.AddWithValue("$id", id);
                            using var row = command.ExecuteReader();
                            if (!row.Read()) throw new IOException("Image no longer in catalog.");
                            path = row.GetString(0);
                            folderId = row.GetInt64(1);
                        }
                        _scheduler.ReportProgress(key, title, processed, items.Length, path);
                        JpegCropWriter.RotateGalleryImage(path, angle, token);
                        // After the original commit, reconcile before releasing P0.
                        new BrowseCacheRepository(_database).InvalidateImage(id);
                        var root = _folderRepository.GetAll().First(f => f.Id == folderId);
                        var result = _folderScanner.ScanFile(root, path,
                            publishCatalogChange: false, forceExifRefresh: true);
                        if (result.Failed > 0 || result.ImageFilesSeen == 0)
                            throw new IOException("Image catalog reconciliation failed: " + path);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                        PCasaDiagnostics.Write("Image.GalleryRotation", ex);
                    }
                    _scheduler.ReportProgress(key, title, ++processed, items.Length);
                    // Keep the same cursor even when the next file yields.
                }
                if (failures.Count > 0) throw new AggregateException(failures);
                return Task.CompletedTask;
            }, title, total: items.Length, resumable: true);
            _ = ObserveGalleryRotationAsync(task, key, items.Select(item => item.Key).ToArray(), title);
        }
        catch (Exception ex)
        {
            foreach (var (id, _) in items)
            {
                _galleryRotationWriting.Remove(id);
                _pendingEditImageIds.Remove(id);
            }
            UpdateGalleryRotationPreview();
            UpdateGalleryRotationButtons();
            _rotationJobImages.Remove(key);
            EndVisibleManualOperation(key);
            PCasaDiagnostics.Write("Image.GalleryRotation.Enqueue", ex);
            PcasaDialog.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ObserveGalleryRotationAsync(Task task, string key, long[] ids, string title)
    {
        Exception? failure = null;
        try { await task; }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _rotationJobImages.Remove(key);
            foreach (var id in ids)
            {
                _galleryRotationWriting.Remove(id);
                _pendingEditImageIds.Remove(id);
                _viewerOriginalCache.Remove(id);
            }
        }
        if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
        EndVisibleManualOperation(key);
        UpdateGalleryRotationPreview();
        UpdateGalleryRotationButtons();
        await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
        _ = RefreshRestoreBadgesAsync();
        if (_viewerOpen && _viewerImageInfo is { } info && ids.Contains(info.Id))
            ShowViewerImage(keepCurrentAsPlaceholder: true);
        if (failure is not null && failure is not OperationCanceledException)
            PcasaDialog.Show(this, failure.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
