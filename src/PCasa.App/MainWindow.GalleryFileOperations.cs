using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // In-flight counts are UI-thread ownership markers, NOT a second queue.
    // Multiple manual transfers all enter the same scheduler manual lane;
    // none may be silently discarded by a local boolean guard.
    private int _outstandingGalleryMutations;
    private int _outstandingFileTransfers;
    private bool _galleryFileOperationRunning => _outstandingGalleryMutations != 0;
    private bool _manualFileTransferActive => _outstandingFileTransfers != 0;

    private sealed record GalleryFileOperation(string Source, string Destination,
        bool Move, bool Replace, long? CatalogImageId, long? ExistingTargetId);

    // Only a fallback for selected IDs absent from the current browse index.
    // This cache is populated by scheduler catalog reads before mouse dragging;
    // no await may ever interrupt an active WPF mouse gesture.
    private readonly Dictionary<long, string> _dragSelectionPathCache = new();

    private async Task<string[]> SelectedGalleryPathsAsync(long[] selectedIds)
        => (await SelectedGalleryImagePathsAsync(selectedIds))
            .Select(item => item.Path).ToArray();

    private async Task<(long Id, string Path)[]> SelectedGalleryImagePathsAsync(
        long[] selectedIds)
    {
        if (selectedIds.Length == 0) return [];

        // The gallery index is a disposable, read-only catalog projection.
        // Use it immediately without touching SQLite or the filesystem.
        var selected = selectedIds.ToHashSet();
        var folders = _photoFolders.ToDictionary(folder => folder.Id,
            folder => folder.LocalPath);
        var pathsById = new Dictionary<long, string>(selected.Count);
        foreach (var id in selected)
            if (_dragSelectionPathCache.TryGetValue(id, out var cachedPath))
                pathsById[id] = cachedPath;
        foreach (var image in _browseIndex)
        {
            if (!selected.Contains(image.ImageId) ||
                !folders.TryGetValue(image.PhotoFolderId, out var folderPath))
                continue;
            pathsById[image.ImageId] = Path.Combine(folderPath, image.FileName);
        }

        var missingIds = selected.Where(id => !pathsById.ContainsKey(id)).ToArray();
        if (missingIds.Length > 0)
        {
            // A selected image may have no prepared thumbnail tier. Read its
            // authoritative path on the catalog-read lane, NEVER on the WPF
            // dispatcher. The selection snapshot also prevents later clicks
            // from changing this particular drag or clipboard request.
            var uncachedPaths = await _scheduler.RunAsync(
                $"catalog:selected-gallery-paths:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    var result = new Dictionary<long, string>();
                    using var db = _database.OpenConnection();
                    foreach (var group in missingIds.Chunk(400))
                    {
                        token.ThrowIfCancellationRequested();
                        using var command = db.CreateCommand();
                        var placeholders = group.Select((_, i) => "$id" + i).ToArray();
                        for (var i = 0; i < group.Length; i++)
                            command.Parameters.AddWithValue(placeholders[i], group[i]);
                        command.CommandText = "SELECT id, local_path FROM images WHERE id IN (" +
                            string.Join(",", placeholders) + ");";
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                            result[reader.GetInt64(0)] = reader.GetString(1);
                    }
                    return result;
                }, workKind: SchedulerWorkKind.CatalogRead);
            foreach (var (id, path) in uncachedPaths)
            {
                pathsById[id] = path;
                _dragSelectionPathCache[id] = path;
            }
        }
        return pathsById
            .Select(item => (Id: item.Key, Path: item.Value))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Called SYNCHRONOUSLY from MouseMove. A native OLE drag can only start
    // while the initiating mouse button remains pressed. Waiting for SQLite
    // here silently loses the gesture, even if the read later succeeds.
    private string[]? PreparedDragFilesOrQueueLookup(long[] selectedIds)
    {
        var pending = SelectedGalleryPathsAsync(selectedIds);
        if (!pending.IsCompletedSuccessfully)
        {
            StatusText.Text = "Bildpfade werden vorbereitet – anschließend erneut ziehen";
            _ = CompleteDeferredDragLookupAsync(pending, selectedIds);
            return null;
        }
        var paths = pending.GetAwaiter().GetResult();
        if (paths.Length != selectedIds.Length)
        {
            StatusText.Text = "Nicht alle ausgewählten Dateien sind im Katalog vorhanden";
            return null;
        }
        return paths;
    }

    private async Task CompleteDeferredDragLookupAsync(
        Task<string[]> pending, long[] selectedIds)
    {
        try
        {
            var paths = await pending;
            if (Dispatcher.HasShutdownStarted ||
                !_selectedImageIds.SetEquals(selectedIds)) return;
            StatusText.Text = paths.Length == selectedIds.Length
                ? "Bildpfade vorbereitet – jetzt erneut ziehen"
                : "Nicht alle ausgewählten Dateien sind im Katalog vorhanden";
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.PrepareDrag", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = "Bildpfade konnten nicht geladen werden";
        }
    }

    private async Task CopyGalleryFilesToClipboardAsync(bool cut)
    {
        var selected = _selectedImageIds.ToArray();
        if (selected.Length == 0) return;
        try
        {
            var paths = await SelectedGalleryPathsAsync(selected);
            if (paths.Length == 0 || Dispatcher.HasShutdownStarted) return;
            var data = new DataObject(DataFormats.FileDrop, paths);
            data.SetData("Preferred DropEffect",
                new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
            Clipboard.SetDataObject(data, true);
            StatusText.Text = cut
                ? $"{paths.Length:N0} Datei(en) zum Verschieben vorgemerkt"
                : $"{paths.Length:N0} Datei(en) kopiert";
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.CopyToClipboard", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = "Ausgewählte Bildpfade konnten nicht gelesen werden";
        }
    }

    private async Task PasteGalleryFilesAsync()
    {
        if (!Clipboard.ContainsFileDropList()) return;
        if (SelectedGalleryDirectory() is not string target)
        {
            StatusText.Text = "Zum Einfügen einen Zielordner im Ordnerbaum auswählen";
            return;
        }
        var paths = Clipboard.GetFileDropList().Cast<string>().ToArray();
        var move = false;
        if (Clipboard.GetData("Preferred DropEffect") is MemoryStream stream &&
            stream.Length >= 4)
        {
            stream.Position = 0;
            move = (new BinaryReader(stream).ReadInt32() & 2) != 0;
        }
        await TransferGalleryFilesAsync(paths, target, move);
    }

    private string? SelectedGalleryDirectory()
    {
        if ((FoldersTree.SelectedItem as TreeViewItem)?.Tag is
            not FolderTreeTag tag) return null;
        // Only capture the selected tree path. NAS reachability and monitoring
        // validity are checked by the actual scheduler operation below.
        return tag.FullPath;
    }

    private bool IsMonitoredGalleryDestination(string path)
    {
        // The scanner and the file operation must resolve the SAME owning
        // root. A permissive outer root cannot override the exclusion of a
        // nested monitored root.
        var root = FolderExclusionRules.FindOwner(
            path, _folderRepository.GetAll());
        return root is not null &&
            !FolderExclusionRules.IsExcluded(path,
                _folderRepository.GetExcludedPaths(root.Id));
    }

    private long? CatalogImageIdAtPath(string path)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM images WHERE local_path = $path COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() is { } value ? Convert.ToInt64(value) : null;
    }

    private static string UniqueCopyPath(string target, IReadOnlySet<string> reserved)
    {
        var folder = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        var result = Path.Combine(folder, stem + " - Kopie" + ext);
        var suffix = 2;
        while (File.Exists(result) || reserved.Contains(result))
            result = Path.Combine(folder, stem + $" - Kopie ({suffix++})" + ext);
        return result;
    }

    private enum NameConflict { Replace, KeepBoth, Skip, Cancel }

    private NameConflict ResolveFileConflict(string path)
    {
        // Explicit four-way choice; never silently overwrite a source photo.
        var choice = NameConflict.Cancel;
        var dialog = new Window
        {
            Owner = this,
            Title = "Datei bereits vorhanden",
            Width = 520,
            Height = 184,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "PcasaPanelBrush");
        var panel = new StackPanel { Margin = new Thickness(18) };
        var message = new TextBlock
        {
            Text = "Am Ziel existiert bereits:\n" + Path.GetFileName(path),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        panel.Children.Add(message);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (title, value) in new[]
        {
            ("Ersetzen", NameConflict.Replace),
            ("Beide behalten", NameConflict.KeepBoth),
            ("Überspringen", NameConflict.Skip),
            ("Abbrechen", NameConflict.Cancel)
        })
        {
            var button = new Button
            {
                Content = title,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(3, 0, 3, 0),
                Style = (Style)FindResource("PcasaStatusButtonStyle")
            };
            button.Click += (_, _) => { choice = value; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return choice;
    }

    // A single UI projection of the scheduler owns manual progress. It is
    // not a second job queue. If a new manual action starts before an older
    // action ends, its status takes precedence; ending the older action must
    // never close the newer action's window.
    private readonly Dictionary<string, (string Title, int Total)>
        _visibleManualOperations = new(StringComparer.Ordinal);
    private readonly List<string> _visibleManualOrder = new();
    private string? _visibleManualActiveKey;
    private BrowseCacheProgressWindow? _visibleManualWindow;
    private bool _visibleManualOwnsWindow;

    // Read-only UI projection across a continuous burst of durable Tag jobs.
    // Entries remain visible in this projection after each package finishes,
    // until ALL tag packages of this burst finish. It is never an execution
    // queue and does not replace scheduler or pending_tag_edits state.
    private readonly Dictionary<string, (int Total, int Processed)>
        _visibleTagSeries = new(StringComparer.Ordinal);

    private static bool IsTagSeriesKey(string key)
        => key.StartsWith("tags:", StringComparison.Ordinal);

    private void SeedVisibleTagSeriesCheckpoint(string key, int confirmed)
    {
        if (!_visibleTagSeries.TryGetValue(key, out var record)) return;
        _visibleTagSeries[key] =
            (record.Total, Math.Clamp(confirmed, 0, record.Total));
        RenderVisibleTagSeries();
    }

    private void UpdateVisibleTagSeries(SchedulerJobSnapshot status)
    {
        if (!_visibleTagSeries.TryGetValue(status.Key, out var previous))
            return;
        var total = Math.Max(previous.Total, status.Total);
        var processed = status.State == SchedulerJobState.Completed
            ? total
            : Math.Clamp(Math.Max(previous.Processed, status.Processed), 0, total);
        _visibleTagSeries[status.Key] = (total, processed);
    }

    private void RenderVisibleTagSeries()
    {
        if (_visibleManualActiveKey is null ||
            !IsTagSeriesKey(_visibleManualActiveKey)) return;
        // Display just one truthful overall count for the tag work burst.
        // Total grows when another manual packet is submitted; completed
        // packets remain in the sum until the whole burst ends.
        var processed = _visibleTagSeries.Values.Sum(x => (long)x.Processed);
        var total = _visibleTagSeries.Values.Sum(x => (long)x.Total);
        _visibleManualWindow?.UpdateTagAggregateProgress(
            (int)Math.Min(int.MaxValue, processed),
            (int)Math.Min(int.MaxValue, total));
    }

    private void BeginVisibleManualOperation(string key, string title, int total)
    {
        if (_visibleManualOperations.ContainsKey(key)) return;
        if (_visibleManualOperations.Count == 0)
        {
            _visibleManualOwnsWindow = _browseProgressWindow is null;
            _visibleManualWindow = _browseProgressWindow ??
                new BrowseCacheProgressWindow { Owner = this };
            if (_visibleManualOwnsWindow)
                _browseProgressWindow = _visibleManualWindow;
            _scheduler.ProgressChanged += VisibleManualProgressChanged;
        }
        _visibleManualOperations.Add(key, (title, total));
        if (IsTagSeriesKey(key))
            _visibleTagSeries.TryAdd(key, (Math.Max(0, total), 0));
        _visibleManualOrder.Add(key);
        DisplayManualOperation(key);
    }

    private void DisplayManualOperation(string key)
    {
        if (!_visibleManualOperations.TryGetValue(key, out var item)) return;
        _visibleManualActiveKey = key;
        _visibleManualWindow?.BeginManualOperation(item.Title, item.Total);
        var latest = _scheduler.Snapshot().LastOrDefault(snapshot => snapshot.Key == key);
        if (latest is not null)
        {
            if (IsTagSeriesKey(key)) UpdateVisibleTagSeries(latest);
            _visibleManualWindow?.UpdateManualOperation(latest);
        }
        RenderVisibleTagSeries();
    }

    private bool _manualThumbnailBusy;
    private DateTime _lastManualThumbnailUtc = DateTime.MinValue;
    private string? _lastManualThumbnailPath;

    private void RequestVisibleTagThumbnail(SchedulerJobSnapshot status)
    {
        if (!status.Key.StartsWith("tags:", StringComparison.Ordinal) ||
            status.CurrentPath is not { Length: > 0 } source ||
            _manualThumbnailBusy ||
            string.Equals(_lastManualThumbnailPath, source,
                StringComparison.OrdinalIgnoreCase) ||
            DateTime.UtcNow - _lastManualThumbnailUtc < TimeSpan.FromMilliseconds(850))
            return;

        // A bounded local thumbnail lookup; never decode the NAS original
        // and never start a second progress/scheduler subsystem.
        _manualThumbnailBusy = true;
        _lastManualThumbnailPath = source;
        _lastManualThumbnailUtc = DateTime.UtcNow;
        _ = LoadVisibleTagThumbnailAsync(status.Key, source);
    }

    private async Task LoadVisibleTagThumbnailAsync(string jobKey, string source)
    {
        try
        {
            var bitmap = await _scheduler.RunAsync(
                "ui:tag-progress-preview:" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.Interactive, token =>
                {
                    using var connection = _database.OpenConnection();
                    using var lookup = connection.CreateCommand();
                    lookup.CommandText =
                        "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
                    lookup.Parameters.AddWithValue("$path", source);
                    var value = lookup.ExecuteScalar();
                    if (value is null) return (System.Windows.Media.Imaging.BitmapSource?)null;

                    var imageId = Convert.ToInt64(value);
                    var repository = new BrowseCacheRepository(_database);
                    var tiers = _browseProfileRepository.Get().Tiers;
                    using var reader = new PackedThumbnailReader();
                    foreach (var tier in tiers)
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = repository.GetIndexEntry(tier, imageId);
                        if (entry is null) continue;
                        return reader.Load(entry, token);
                    }
                    return null;
                }, workKind: SchedulerWorkKind.ThumbnailRead);
            if (!Dispatcher.HasShutdownStarted &&
                _visibleManualActiveKey == jobKey)
                _visibleManualWindow?.ShowManualThumbnail(source, bitmap);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A preview is nonessential. It must never fail or hold up the
            // original's durable metadata write.
            PCasaDiagnostics.Write("Tags.ProgressPreview", ex);
        }
        finally
        {
            _manualThumbnailBusy = false;
        }
    }

    private void VisibleManualProgressChanged(SchedulerJobSnapshot status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        // ProgressChanged is raised by the scheduler worker. Presentation
        // ownership belongs exclusively to the WPF dispatcher; never read
        // the UI-owned job dictionary on a background thread.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_visibleManualOperations.ContainsKey(status.Key)) return;
            if (IsTagSeriesKey(status.Key))
                UpdateVisibleTagSeries(status);
            if (status.Key == _visibleManualActiveKey)
            {
                _visibleManualWindow?.UpdateManualOperation(status);
                RequestVisibleTagThumbnail(status);
            }
            // A *different* older Tag package may have progressed in its
            // scheduler lane. Its completed images count toward the SAME
            // overall total even while the newest package is displayed.
            RenderVisibleTagSeries();
        }));
    }

    private void EndVisibleManualOperation(string key)
    {
        if (!_visibleManualOperations.Remove(key)) return;
        if (IsTagSeriesKey(key) &&
            _visibleTagSeries.TryGetValue(key, out var last))
        {
            var final = _scheduler.Snapshot().LastOrDefault(x => x.Key == key);
            if (final is not null) UpdateVisibleTagSeries(final);
            // Do not count failed originals as successful. The scheduler's
            // last confirmed cursor/progress remains authoritative.
        }
        _visibleManualOrder.Remove(key);
        if (!_visibleManualOperations.Keys.Any(IsTagSeriesKey))
            _visibleTagSeries.Clear();
        if (_visibleManualOrder.Count != 0)
        {
            if (_visibleManualActiveKey == key)
                DisplayManualOperation(_visibleManualOrder[^1]);
            else
                RenderVisibleTagSeries();
            return;
        }

        _scheduler.ProgressChanged -= VisibleManualProgressChanged;
        _visibleManualActiveKey = null;
        var window = _visibleManualWindow;
        var owned = _visibleManualOwnsWindow;
        _visibleManualWindow = null;
        _visibleManualOwnsWindow = false;
        window?.EndManualOperation();
        if (owned)
        {
            window?.Finish();
            if (ReferenceEquals(_browseProgressWindow, window))
                _browseProgressWindow = null;
        }
        else if (window is { IsLoaded: false } &&
                 ReferenceEquals(_browseProgressWindow, window))
            _browseProgressWindow = null;
    }

    private async Task TransferGalleryFilesAsync(IEnumerable<string> input,
        string targetDirectory, bool move)
    {
        string[] requested;
        string target;
        try
        {
            requested = input.Where(FolderScanner.IsSupportedImagePath)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (requested.Length == 0)
            {
                StatusText.Text = "Keine unterstützten Bilddateien für die Ablage";
                return;
            }
            target = Path.GetFullPath(targetDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            ShowFileActionError(ex);
            return;
        }

        var key = "gallery:file-transfer:" + Guid.NewGuid().ToString("N");
        var payload = System.Text.Json.JsonSerializer.Serialize(
            new DurableGalleryTransferRecipe(requested, target, move));
        _outstandingGalleryMutations++;
        _outstandingFileTransfers++;
        _backgroundDebounceTimer?.Stop();
        try
        {
            BeginVisibleManualOperation(key,
                move ? "Bilder verschieben" : "Bilder kopieren", requested.Length);
            var package = BuildDurableGalleryTransferPackage(
                key, payload, recovered: false);
            await _scheduler.EnqueueDurablePackageAsync(
                package, "gallery-transfer-v1", payload,
                SchedulerPriority.ManualAction);
            _durableGalleryTransfers.PruneCompleted();
        }
        catch (OperationCanceledException)
        {
            if (!Dispatcher.HasShutdownStarted)
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
            StatusText.Text = "Dateiübertragung abgebrochen";
        }
        catch (Exception ex)
        {
            if (!Dispatcher.HasShutdownStarted)
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
            ShowFileActionError(ex);
        }
        finally
        {
            try { EndVisibleManualOperation(key); }
            finally
            {
                _outstandingGalleryMutations--;
                _outstandingFileTransfers--;
                if (!_manualFileTransferActive)
                {
                    QueuePendingInitialScans();
                    if (_backgroundPending.Count > 0 &&
                        _backgroundScanSettings?.Enabled == true)
                        _backgroundDebounceTimer?.Start();
                }
            }
        }
    }

    private sealed record DurableDeleteItem(long Id, string Path);

    private SchedulerTaskPackage BuildDurableDeletePackage(string key, string payload)
        => BuildDurableDeletePackage(key, payload, null, null);

    private SchedulerTaskPackage BuildDurableDeletePackage(string key, string payload,
        List<long>? succeeded, List<string>? errors)
    {
        var selected = System.Text.Json.JsonSerializer.Deserialize<DurableDeleteItem[]>(payload)
            ?? throw new InvalidDataException("Ungültiger Löschauftrag.");
        var package = new SchedulerTaskPackage(key);
        for (var i = 0; i < selected.Length; i++)
        {
            var (id, path) = selected[i];
            var ordinal = i + 1;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (SourceFileLocks.Enter(path, token))
                    {
                        token.ThrowIfCancellationRequested();
                        var currentId = CatalogImageIdAtPath(path);
                        var presence = SourceFilePresence.Probe(path);
                        if (currentId == id && presence == FilePresence.Present)
                        {
                            if (PathClassifier.Classify(path).IsNetwork)
                                File.Delete(path);
                            else
                                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                                    RecycleOption.SendToRecycleBin);
                            // The operation may be interrupted between the
                            // physical deletion and SQLite reconciliation.
                            if (!new CatalogRemovalService(_database).DetachImage(id, path))
                                throw new InvalidOperationException(
                                    "Datei entfernt, Katalogeintrag nicht bestätigt.");
                        }
                        else if (currentId == id &&
                                 presence == FilePresence.ConfirmedMissing)
                        {
                            // Crash after the physical delete, before the
                            // catalogue commit. Resume only when missing is
                            // authoritative, never for an unreachable NAS.
                            if (!new CatalogRemovalService(_database).DetachImage(id, path))
                                throw new InvalidOperationException(
                                    "Bereits gelöschte Datei nicht aus dem Katalog entfernt.");
                        }
                        else if (currentId is null &&
                                 presence == FilePresence.ConfirmedMissing)
                        {
                            // Already fully committed before the crash.
                        }
                        else throw new InvalidOperationException(
                            "Unklarer oder inzwischen veränderter Dateizustand: " + path);
                    }
                    succeeded?.Add(id);
                }
                catch (Exception ex) when (ex is IOException or
                    UnauthorizedAccessException or InvalidOperationException)
                {
                    errors?.Add(Path.GetFileName(path) + ": " + ex.Message);
                    PCasaDiagnostics.Write("Gallery.Delete", ex);
                }
                _scheduler.ReportProgress(key, "Bilder löschen",
                    ordinal, selected.Length, path);
                return Task.CompletedTask;
            });
        }
        return package;
    }

    private async Task DeleteSelectedImagesAsync()
    {
        if (_selectedImageIds.Count == 0) return;
        (long Id, string Path)[] selected;
        bool anyNetwork;
        try
        {
            // Reads use scheduler lanes; capture the intended selection before
            // presenting the confirmation dialog.
            selected = await SelectedGalleryImagePathsAsync(_selectedImageIds.ToArray());
            if (selected.Length == 0) return;
            anyNetwork = await _scheduler.RunAsync(
                $"gallery:delete-network-probe:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return selected.Any(item => PathClassifier.Classify(item.Path).IsNetwork);
                }, workKind: SchedulerWorkKind.SourceProbe);
        }
        catch (Exception ex) { ShowFileActionError(ex); return; }

        var prompt = anyNetwork
            ? $"{selected.Length:N0} Bilddatei(en) löschen?\n\n" +
              "Mindestens eine liegt auf einem Netzlaufwerk und wird dort ENDGÜLTIG gelöscht."
            : $"{selected.Length:N0} Bilddatei(en) in den Windows-Papierkorb verschieben?";
        if (MessageBox.Show(this, prompt, "Bilder löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var key = $"gallery:delete:{Guid.NewGuid():N}";
        _outstandingGalleryMutations++;
        var succeeded = new List<long>();
        var errors = new List<string>();
        try
        {
            BeginVisibleManualOperation(key, "Bilder löschen", selected.Length);
            var payload = System.Text.Json.JsonSerializer.Serialize(
                selected.Select(image => new DurableDeleteItem(image.Id, image.Path)).ToArray());
            var package = BuildDurableDeletePackage(key, payload, succeeded, errors);
            await _scheduler.EnqueueDurablePackageAsync(package,
                "gallery-delete-v1", payload, SchedulerPriority.ManualAction);
            _selectedImageIds.ExceptWith(succeeded);
            if (_selectedImageIds.Count == 0) _gallerySelection.Reset();
            await RefreshBrowseGalleryAsync(forceIndexReload: true);
            ApplyGallerySelection();
            StatusText.Text = $"{succeeded.Count:N0} Datei(en) gelöscht" +
                (errors.Count == 0 ? "" : $" · {errors.Count:N0} Fehler");
            if (errors.Count > 0)
                MessageBox.Show(this, string.Join("\n", errors.Take(15)),
                    "Einige Dateien konnten nicht gelöscht werden",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{succeeded.Count:N0} Datei(en) gelöscht · weitere Löschung abgebrochen";
        }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally
        {
            EndVisibleManualOperation(key);
            _outstandingGalleryMutations--;
        }
    }

}
