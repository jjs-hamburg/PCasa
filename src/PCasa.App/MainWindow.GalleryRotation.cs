using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
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
    private const string RotationRecipeType = "gallery-rotation-v1";
    private sealed record RotationPreparation(string Path, long Size, long ModifiedTicks,
        int? TargetOrientation, string? OriginalHash, string? CandidateHash);
    private sealed record RotationRecipe(long[] ImageIds, int[] Turns, RotationPreparation?[] Prepared);

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

    private bool FlushGalleryRotations()
    {
        _galleryRotationTimer?.Stop();
        if (_galleryRotationDraft.Count == 0) return true;
        var items = _galleryRotationDraft.ToArray();
        _galleryRotationDraft.Clear();
        foreach (var (id, angle) in items)
        {
            _galleryRotationWriting[id] = angle;
            _pendingEditImageIds.Add(id);
        }
        UpdateGalleryRotationButtons();
        var key = "image:gallery-rotation:" + Guid.NewGuid().ToString("N");
        var title = SchedulerTaskView.GalleryRotationCaption;
        _rotationJobImages[key] = items.Select(item => item.Key).ToArray();
        BeginVisibleManualOperation(key, title, items.Length);
        try
        {
            var recipe = new RotationRecipe(items.Select(x => x.Key).ToArray(),
                items.Select(x => x.Value).ToArray(), new RotationPreparation?[items.Length]);
            var payload = JsonSerializer.Serialize(recipe);
            var task = _scheduler.EnqueueDurableRecipeAsync(key, RotationRecipeType, payload,
                    SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            _ = ObserveGalleryRotationAsync(task, key, items.Select(item => item.Key).ToArray(), title);
            return true;
        }
        catch (Exception ex)
        {
            foreach (var (id, _) in items)
            {
                _galleryRotationWriting.Remove(id);
                _pendingEditImageIds.Remove(id);
                _galleryRotationDraft[id] = items.First(item => item.Key == id).Value;
            }
            UpdateGalleryRotationPreview();
            UpdateGalleryRotationButtons();
            _rotationJobImages.Remove(key);
            EndVisibleManualOperation(key);
            PCasaDiagnostics.Write("Image.GalleryRotation.Enqueue", ex);
            PcasaDialog.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private SchedulerTaskPackage BuildGalleryRotationPackage(string key, string payload)
    {
        var recipe = JsonSerializer.Deserialize<RotationRecipe>(payload)
            ?? throw new InvalidDataException("Invalid rotation recipe.");
        if (recipe.ImageIds.Length != recipe.Turns.Length ||
            recipe.ImageIds.Length != recipe.Prepared.Length)
            throw new InvalidDataException("Invalid rotation recipe length.");
        var package = new SchedulerTaskPackage(key);
        for (int i = 0; i < recipe.ImageIds.Length; i++)
        {
            int index = i;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var id = recipe.ImageIds[index];
                string path; long folderId;
                using (var db = _database.OpenConnection())
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT local_path,folder_id FROM images WHERE id=$id;";
                    command.Parameters.AddWithValue("$id", id);
                    using var row = command.ExecuteReader();
                    if (!row.Read()) throw new IOException("Image no longer in catalog.");
                    path = row.GetString(0); folderId = row.GetInt64(1);
                }
                var title = SchedulerTaskView.GalleryRotationCaption;
                _scheduler.ReportProgress(key, title, index, recipe.ImageIds.Length, path);
                using var lease = SourceFileLocks.Enter(path, token);
                var before = new FileInfo(path);
                if (!before.Exists) throw new FileNotFoundException("Rotation source missing.", path);
                bool jpeg = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg";
                using var reader = new ImageExifReader();
                ImageExifData? current = jpeg ? reader.Read(path, token) : null;
                string Hash(string file)
                {
                    using var stream = File.OpenRead(file);
                    return Convert.ToHexString(SHA256.HashData(stream));
                }
                void Persist(RotationPreparation prepared)
                {
                    recipe.Prepared[index] = prepared;
                    // The immutable acceptance identity and cursor remain the
                    // same. Record the absolute target BEFORE touching pixels.
                    using var db = _database.OpenConnection();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        UPDATE scheduler_packages SET payload_json=$payload
                         WHERE package_key=$key AND state IN ('queued','running','paused','deferred');
                        """;
                    cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(recipe));
                    cmd.Parameters.AddWithValue("$key", key);
                    if (cmd.ExecuteNonQuery() != 1)
                        throw new IOException("Rotation intent could not be recorded.");
                }
                var prepared = recipe.Prepared[index];
                if (prepared is null)
                {
                    prepared = new RotationPreparation(path, before.Length, before.LastWriteTimeUtc.Ticks,
                        jpeg ? ExifOrientation.RotateQuarterTurns(MetadataFileWriter.Orientation(current!),
                            recipe.Turns[index]) : null, jpeg ? null : Hash(path), null);
                    Persist(prepared);
                }
                if (!string.Equals(prepared.Path, path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Rotation source changed after preparation.");
                bool applied = jpeg
                    ? MetadataFileWriter.Orientation(current!) == prepared.TargetOrientation
                    : prepared.CandidateHash is not null && Hash(path) == prepared.CandidateHash;
                if (!applied)
                {
                    if (before.Length != prepared.Size || before.LastWriteTimeUtc.Ticks != prepared.ModifiedTicks ||
                        !jpeg && Hash(path) != prepared.OriginalHash)
                        throw new IOException("Rotation source changed after preparation.");
                    if (jpeg && prepared.TargetOrientation is int target)
                        JpegCropWriter.SetGalleryOrientation(path, target, token, current);
                    else
                        JpegCropWriter.RotateGalleryImage(path, recipe.Turns[index], token,
                            candidate => Persist(prepared with { CandidateHash = Hash(candidate) }));
                }
                // Reconcile even after a crash between replacement and the
                // package checkpoint. Never repeat a relative rotation.
                new BrowseCacheRepository(_database).InvalidateImage(id);
                var root = _folderRepository.GetAll().First(f => f.Id == folderId);
                var result = _folderScanner.ScanFile(root, path,
                    publishCatalogChange: false, forceExifRefresh: true);
                if (result.Failed > 0 || result.ImageFilesSeen == 0)
                    throw new IOException("Image catalog reconciliation failed: " + path);
                _scheduler.ReportProgress(key, title, index+1, recipe.ImageIds.Length, path);
                return Task.CompletedTask;
            });
        }
        return package;
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
