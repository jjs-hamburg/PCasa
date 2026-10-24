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
            _visibleManualWindow?.UpdateManualOperation(latest);
    }

    private void VisibleManualProgressChanged(SchedulerJobSnapshot status)
    {
        if (Dispatcher.HasShutdownStarted) return;
        // ProgressChanged is raised by the scheduler worker. Presentation
        // ownership belongs exclusively to the WPF dispatcher; never read
        // the UI-owned job dictionary on a background thread.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_visibleManualOperations.ContainsKey(status.Key) &&
                status.Key == _visibleManualActiveKey)
                _visibleManualWindow?.UpdateManualOperation(status);
        }));
    }

    private void EndVisibleManualOperation(string key)
    {
        if (!_visibleManualOperations.Remove(key)) return;
        _visibleManualOrder.Remove(key);
        if (_visibleManualOrder.Count != 0)
        {
            if (_visibleManualActiveKey == key)
                DisplayManualOperation(_visibleManualOrder[^1]);
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
        // Every valid manual drop must reach the scheduler, even if an
        // earlier manual transfer is still running or awaiting a source lock.
        string[] requested;
        string targetPath;
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
            targetPath = Path.GetFullPath(targetDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            ShowFileActionError(ex);
            return;
        }
        var key = $"gallery:file-transfer:{Guid.NewGuid():N}";
        var transferPhase = move ? "Bilder verschieben" : "Bilder kopieren";
        var completed = new List<string>();
        var retainedIds = new HashSet<long>();
        var failed = new List<string>();
        var userCanceled = false;
        var serverRenames = 0;
        var crossVolumeCopies = 0;

        _outstandingGalleryMutations++;
        _outstandingFileTransfers++;
        // The SCHEDULER alone preempts the running scan through its yield
        // token and resumes that same request afterward. Directly canceling
        // _activeScanCancellation here would turn safe preemption into a
        // permanent cancellation of a user-initiated or initial import.
        _backgroundDebounceTimer?.Stop();
        try
        {
            BeginVisibleManualOperation(key, transferPhase, requested.Length);
            // Build a single operation from reusable steps. The first step
            // validates the destination; each requested file becomes one
            // independently resumable step and may discover an additional
            // image-import step. No work is scheduled at lower priority.
            var package = new SchedulerTaskPackage(key);
            var catalog = _imageCatalog;
            MonitoredFolder? destinationRoot = null;
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(targetPath) ||
                    !IsMonitoredGalleryDestination(targetPath))
                    throw new IOException(
                        "Ablage nur in einem erreichbaren überwachten Ordner möglich.");
                destinationRoot = _folderRepository.GetAll()
                    .Where(root => FolderExclusionRules.IsAtOrBelow(
                        targetPath, root.LocalPath))
                    .OrderByDescending(root => root.LocalPath.Length)
                    .FirstOrDefault() ?? throw new IOException(
                        "Der Zielordner ist nicht überwacht.");
                _scheduler.ReportProgress(key, transferPhase, 0, requested.Length);
                return Task.CompletedTask;
            });

            foreach (var sourcePath in requested)
            {
                package.AddStep(async token =>
                {
                    if (userCanceled) return;
                    foreach (var source in new[] { sourcePath })
                    {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(source))
                        {
                            failed.Add(Path.GetFileName(source) +
                                ": Quelldatei nicht erreichbar");
                            _scheduler.ReportProgress(key, transferPhase,
                                completed.Count + failed.Count, requested.Length, source);
                            continue;
                        }
                        var destination = Path.Combine(targetPath,
                            Path.GetFileName(source));
                        var samePath = string.Equals(source, destination,
                            StringComparison.OrdinalIgnoreCase);
                        if (samePath && move) continue;
                        try
                        {
                            _scheduler.ReportProgress(key, transferPhase,
                                completed.Count + failed.Count,
                                requested.Length, source);
                            var replace = false;
                            if (reserved.Contains(destination))
                                destination = UniqueCopyPath(destination, reserved);
                            else if (samePath || File.Exists(destination))
                            {
                                var conflictPath = destination;
                                var choice = samePath ? NameConflict.KeepBoth :
                                    await Dispatcher.InvokeAsync(() =>
                                        ResolveFileConflict(conflictPath));
                                if (choice == NameConflict.Cancel)
                                {
                                    userCanceled = true;
                                    break;
                                }
                                if (choice == NameConflict.Skip) continue;
                                if (choice == NameConflict.Replace && !samePath)
                                    replace = true;
                                else destination = UniqueCopyPath(
                                    destination, reserved);
                            }
                            // A preempted step must not reserve a name it has
                            // NOT yet materialized. Only a successfully
                            // completed file mutation can claim this target.
                            var sourceId = move ? CatalogImageIdAtPath(source) : null;
                            var replacedId = replace
                                ? CatalogImageIdAtPath(destination) : null;
                            lock (SourceFileLocks.ForPath(source))
                            {
                                token.ThrowIfCancellationRequested();

                                // Durable intent BEFORE File.Move. Its removal
                                // is atomic with the catalog location update.
                                // A crash between the two is recoverable without
                                // ever treating an offline NAS as a deletion.
                                string? intentId = null;
                                if (move && sourceId is long indexed)
                                {
                                    intentId = Guid.NewGuid().ToString("N");
                                    catalog.BeginRelocationIntent(intentId,
                                        indexed, source, destination,
                                        destinationRoot!.Id);
                                }

                                if (move)
                                {
                                    // One physical move path. Try a server-side
                                    // rename first, even for overwrite or mapped
                                    // aliases. Only ERROR_NOT_SAME_DEVICE permits
                                    // copying a file across the VPN.
                                    var method = ServerSideFileMover.Move(
                                        source, destination, replace);
                                    if (method == ServerSideFileMover.MoveMethod.Rename)
                                        serverRenames++;
                                    else
                                        crossVolumeCopies++;
                                }
                                else if (replace)
                                {
                                    // Cross-volume-copy semantics for explicit
                                    // COPY+Replace: stage completely before
                                    // replacing the target file.
                                    var stage = destination + ".pcasa-" +
                                        Guid.NewGuid().ToString("N") + ".tmp";
                                    try
                                    {
                                        File.Copy(source, stage, overwrite: false);
                                        if (new FileInfo(source).Length !=
                                            new FileInfo(stage).Length)
                                            throw new IOException("Kopie unvollständig.");
                                        File.Move(stage, destination, overwrite: true);
                                    }
                                    finally
                                    {
                                        if (File.Exists(stage)) File.Delete(stage);
                                    }
                                }
                                else File.Copy(source, destination, overwrite: false);

                                // No scan or re-decode for an unchanged image.
                                // Failure is NOT treated as permission to
                                // delete the original catalog row. The durable
                                // intent remains for conservative recovery.
                                if (move && sourceId is long imageId)
                                {
                                    if (!catalog.RelocateImage(imageId, source,
                                        destination, destinationRoot!, CancellationToken.None,
                                        intentId))
                                        throw new InvalidOperationException(
                                            "Verschiebung erfolgt, Katalogzustand " +
                                            "nicht bestätigt; Wiederherstellung vorgemerkt.");
                                    retainedIds.Add(imageId);
                                    CatalogChanges.PublishImageCommitted();
                                }
                                else
                                {
                                    if (replace && replacedId is long oldId)
                                        new CatalogRemovalService(_database)
                                            .DetachImage(oldId, destination);
                                    // Attach only the work this file actually needs.
                                    // The package's cursor survives preemption.
                                    package.InsertNext(indexToken =>
                                    {
                                        indexToken.ThrowIfCancellationRequested();
                                        _scheduler.ReportProgress(key, "Bild katalogisieren",
                                            completed.Count, requested.Length, destination);
                                        try
                                        {
                                            if (!File.Exists(destination))
                                                throw new FileNotFoundException(
                                                    "Übertragene Datei ist nicht mehr vorhanden.", destination);
                                            var result = _folderScanner.ScanFile(
                                                destinationRoot!, destination,
                                                cancellationToken: indexToken);
                                            if (result.Failed > 0)
                                                throw new IOException(
                                                    "Katalogaufnahme fehlgeschlagen: " + destination);
                                        }
                                        catch (Exception ex) when (ex is IOException or
                                            UnauthorizedAccessException or InvalidOperationException)
                                        {
                                            failed.Add(Path.GetFileName(destination) +
                                                ": Katalogaufnahme: " + ex.Message);
                                            PCasaDiagnostics.Write("Gallery.TransferImport", ex);
                                        }
                                        return Task.CompletedTask;
                                    });
                                }
                            }
                            completed.Add(destination);
                            reserved.Add(destination);
                        }
                        catch (Exception ex) when (ex is IOException or
                            UnauthorizedAccessException or InvalidOperationException)
                        {
                            failed.Add(Path.GetFileName(source) + ": " + ex.Message);
                            PCasaDiagnostics.Write("Gallery.Transfer", ex);
                        }
                        _scheduler.ReportProgress(key, transferPhase,
                            completed.Count + failed.Count, requested.Length, source);
                    }
                    // A copy/relocation and its DB identity update are now
                    // one safe step. Scanner ingestion, when necessary, is
                    // inserted immediately AFTER it in this package.
                });
            }

            package.AddStep(async token =>
            {
                token.ThrowIfCancellationRequested();
                    _scheduler.ReportProgress(key, "Galerie aktualisieren",
                        completed.Count, Math.Max(1, requested.Length));

                    // Final publication belongs to the SAME package as all
                    // file changes and image ingestion; no orphaned followup.
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await RefreshBrowseGalleryAsync(
                            forceIndexReload: true, warmViewport: false);

                        // Existing image IDs remain stable. Copies/new
                        // sources will appear as their normal scanner commits.
                        var newSelection = new HashSet<long>(retainedIds);
                        _selectedImageIds.Clear();
                        _selectedImageIds.UnionWith(newSelection);
                        if (_selectedImageIds.Count == 0)
                            _gallerySelection.Reset();
                        ApplyGallerySelection();
                        var first = _displayedBrowseIndex.FirstOrDefault(
                            entry => _selectedImageIds.Contains(entry.ImageId));
                        if (first is not null)
                            ScrollGalleryImageIntoView(first.ImageId);
                        StatusText.Text = $"{completed.Count:N0} Datei(en) " +
                            (move ? "verschoben" : "kopiert") +
                            (move ? $" · {serverRenames:N0} direkt, " +
                                $"{crossVolumeCopies:N0} per Kopie" : "") +
                            (failed.Count > 0 ? $" · {failed.Count:N0} Fehler" : "");
                    }, System.Windows.Threading.DispatcherPriority.Normal)
                        .Task.Unwrap();

                    _scheduler.ReportProgress(key, "Galerie aktualisiert",
                        completed.Count, Math.Max(1, requested.Length));


                    // Scheduler state reflects the outcome, not merely that
                    // this delegate returned: partial failure is FAILED and
                    // deliberate cancellation is CANCELED, even if individual
                    // preceding files were already moved and published.
                    if (failed.Count > 0)
                        throw new InvalidOperationException(
                            $"{completed.Count:N0} Datei(en) übertragen, " +
                            $"{failed.Count:N0} fehlgeschlagen:\n" +
                            string.Join("\n", failed.Take(15)));
                    if (userCanceled)
                        throw new OperationCanceledException(
                            "Dateiübertragung vom Benutzer abgebrochen.");

            });
            await _scheduler.EnqueuePackageAsync(
                package, SchedulerPriority.ManualAction);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{completed.Count:N0} Datei(en) übertragen · " +
                "weitere Übertragung abgebrochen";
        }
        catch (Exception ex) { ShowFileActionError(ex); }
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

    private async Task DeleteSelectedImagesAsync()
    {
        if (_selectedImageIds.Count == 0) return;
        if (_galleryFileOperationRunning)
        {
            StatusText.Text =
                "Löschen erst nach Abschluss der laufenden Dateiübertragung möglich";
            return;
        }
        (long Id, string Path)[] selected;
        bool anyNetwork;
        try
        {
            // Resolve the selection and network classification through the
            // same scheduler-owned readers used by drag/drop. Never query
            // SQLite or a drive provider synchronously on the WPF thread.
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
        _outstandingGalleryMutations++;
        var succeeded = new List<long>();
        var errors = new List<string>();
        try
        {
            await _scheduler.EnqueueAsync(
                $"gallery:delete:{Guid.NewGuid():N}", SchedulerPriority.ManualAction,
                token =>
                {
                    foreach (var (id, path) in selected)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            lock (SourceFileLocks.ForPath(path))
                            {
                                if (PathClassifier.Classify(path).IsNetwork)
                                    File.Delete(path);
                                else
                                    FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                                        RecycleOption.SendToRecycleBin);
                                new CatalogRemovalService(_database).DetachImage(id, path);
                            }
                            succeeded.Add(id);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                            or InvalidOperationException)
                        {
                            errors.Add(Path.GetFileName(path) + ": " + ex.Message);
                        }
                    }
                    return Task.CompletedTask;
                });
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
        catch (Exception ex) { ShowFileActionError(ex); }
        finally { _outstandingGalleryMutations--; }
    }
}
