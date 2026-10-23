using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _galleryFileOperationRunning;
    // This is a user-visible high-priority mutation, not an import operation.
    // UI-thread state fences background pumps while the scheduler owns the
    // filesystem actions. The scheduler still arbitrates all actual I/O.
    private bool _manualFileTransferActive;

    private sealed record GalleryFileOperation(string Source, string Destination,
        bool Move, bool Replace, long? CatalogImageId, long? ExistingTargetId);

    private async Task<string[]> SelectedGalleryPathsAsync(long[] selectedIds)
    {
        if (selectedIds.Length == 0) return [];

        // The gallery index is a disposable, read-only catalog projection.
        // Use it immediately without touching SQLite or the filesystem.
        var selected = selectedIds.ToHashSet();
        var folders = _photoFolders.ToDictionary(folder => folder.Id,
            folder => folder.LocalPath);
        var pathsById = new Dictionary<long, string>(selected.Count);
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
                pathsById[id] = path;
        }
        return pathsById.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        => _folderRepository.GetAll().Any(root =>
            FolderExclusionRules.IsAtOrBelow(path, root.LocalPath) &&
            !FolderExclusionRules.IsExcluded(path,
                _folderRepository.GetExcludedPaths(root.Id)));

    private long? CatalogImageIdAtPath(string path)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id FROM images WHERE local_path = $path COLLATE NOCASE LIMIT 1;";
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() is { } value ? Convert.ToInt64(value) : null;
    }

    private static string VolumeIdentity(string path)
    {
        var canonical = PathClassifier.Classify(path).CanonicalPath
            .Replace('/', '\\');
        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = canonical.TrimStart('\\').Split('\\');
            return parts.Length >= 2
                ? @"\\" + parts[0] + "\\" + parts[1]
                : canonical;
        }
        return Path.GetPathRoot(canonical) ?? canonical;
    }

    private static bool SameVolume(string first, string second)
        => string.Equals(VolumeIdentity(first), VolumeIdentity(second),
            StringComparison.OrdinalIgnoreCase);

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

    // Only the scheduler owns status/progress. The existing progress window
    // is merely a foreground projection and does not run an operation.
    private string? _visibleTransferJobKey;
    private BrowseCacheProgressWindow? _visibleTransferWindow;
    private bool _visibleTransferOwnsWindow;

    private void BeginVisibleTransfer(string key, bool move, int total)
    {
        _visibleTransferJobKey = key;
        _visibleTransferOwnsWindow = _browseProgressWindow is null;
        _visibleTransferWindow = _browseProgressWindow ??
            new BrowseCacheProgressWindow { Owner = this };
        if (_visibleTransferOwnsWindow)
            _browseProgressWindow = _visibleTransferWindow;
        _visibleTransferWindow.BeginManualOperation(
            move ? "Bilder verschieben" : "Bilder kopieren", total);
        _scheduler.ProgressChanged += TransferProgressChanged;
    }

    private void TransferProgressChanged(SchedulerJobSnapshot status)
    {
        if (status.Key != _visibleTransferJobKey ||
            Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (status.Key == _visibleTransferJobKey)
                _visibleTransferWindow?.UpdateManualOperation(status);
        }));
    }

    private void EndVisibleTransfer()
    {
        _scheduler.ProgressChanged -= TransferProgressChanged;
        _visibleTransferJobKey = null;
        var window = _visibleTransferWindow;
        var owned = _visibleTransferOwnsWindow;
        _visibleTransferWindow = null;
        _visibleTransferOwnsWindow = false;
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
        if (_galleryFileOperationRunning) return;

        var requested = input.Where(FolderScanner.IsSupportedImagePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) return;
        var targetPath = Path.GetFullPath(targetDirectory);
        var key = $"gallery:file-transfer:{Guid.NewGuid():N}";
        var transferPhase = move ? "Bilder verschieben" : "Bilder kopieren";
        var completed = new List<string>();
        var retainedIds = new HashSet<long>();
        var needsIndex = new List<string>();
        var failed = new List<string>();
        var userCanceled = false;

        _galleryFileOperationRunning = true;
        _manualFileTransferActive = true;
        // The SCHEDULER alone preempts the running scan through its yield
        // token and resumes that same request afterward. Directly canceling
        // _activeScanCancellation here would turn safe preemption into a
        // permanent cancellation of a user-initiated or initial import.
        _backgroundDebounceTimer?.Stop();
        BeginVisibleTransfer(key, move, requested.Length);
        try
        {
            // This ONE scheduler job owns physical operations, catalog
            // commits, and the subsequent visible gallery publication.
            // It must not become Completed before the view has caught up.
            await _scheduler.EnqueueAsync(
                key, SchedulerPriority.ManualAction, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(targetPath) ||
                        !IsMonitoredGalleryDestination(targetPath))
                        throw new IOException(
                            "Ablage nur in einem erreichbaren überwachten Ordner möglich.");

                    var roots = _folderRepository.GetAll();
                    var destinationRoot = roots
                        .Where(root => FolderExclusionRules.IsAtOrBelow(
                            targetPath, root.LocalPath))
                        .OrderByDescending(root => root.LocalPath.Length)
                        .FirstOrDefault() ?? throw new IOException(
                            "Der Zielordner ist nicht überwacht.");
                    var catalog = _imageCatalog;
                    var reserved = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    _scheduler.ReportProgress(key, transferPhase, 0, requested.Length);

                    foreach (var source in requested)
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
                            reserved.Add(destination);
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
                                        destinationRoot.Id);
                                }

                                if (replace)
                                {
                                    var stage = destination + ".pcasa-" +
                                        Guid.NewGuid().ToString("N") + ".tmp";
                                    try
                                    {
                                        File.Copy(source, stage, overwrite: false);
                                        File.Move(stage, destination, overwrite: true);
                                        if (move) File.Delete(source);
                                    }
                                    finally
                                    {
                                        if (File.Exists(stage)) File.Delete(stage);
                                    }
                                }
                                else if (move)
                                {
                                    if (SameVolume(source, destination))
                                        File.Move(source, destination);
                                    else
                                    {
                                        File.Copy(source, destination, overwrite: false);
                                        File.Delete(source);
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
                                        destination, destinationRoot, token,
                                        intentId))
                                        throw new InvalidOperationException(
                                            "Verschiebung erfolgt, Katalogzustand " +
                                            "nicht bestätigt; Wiederherstellung vorgemerkt.");
                                    retainedIds.Add(imageId);
                                }
                                else
                                {
                                    if (replace && replacedId is long oldId)
                                        new CatalogRemovalService(_database)
                                            .DetachImage(oldId, destination);
                                    needsIndex.Add(destination);
                                }
                            }
                            completed.Add(destination);
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

                    _scheduler.ReportProgress(key, "Galerie aktualisieren",
                        completed.Count, Math.Max(1, requested.Length));

                    // Only this UI publication decides when the manual job
                    // is allowed to report Completed. The coalescing gallery
                    // refresh does not discard committed catalog changes.
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
                            (failed.Count > 0 ? $" · {failed.Count:N0} Fehler" : "");
                    }, System.Windows.Threading.DispatcherPriority.Normal)
                        .Task.Unwrap();

                    _scheduler.ReportProgress(key, "Galerie aktualisiert",
                        completed.Count, Math.Max(1, requested.Length));

                    // New/copied, genuinely uncatalogued images are a separate
                    // import operation in the existing scanner pathway; they
                    // never delay completion of the physical transfer.
                    if (needsIndex.Count > 0)
                    {
                        var pathsToIndex = needsIndex.ToArray();
                        _ = _scheduler.EnqueueAsync(
                            $"catalog:transfer-import:{Guid.NewGuid():N}",
                            SchedulerPriority.UserAction,
                            async followupToken =>
                            {
                                foreach (var destination in pathsToIndex)
                                {
                                    followupToken.ThrowIfCancellationRequested();
                                    try
                                    {
                                        _folderScanner.ScanFile(
                                            destinationRoot, destination,
                                            cancellationToken: followupToken);
                                    }
                                    catch (Exception ex) when (ex is IOException or
                                        UnauthorizedAccessException or
                                        InvalidOperationException)
                                    {
                                        PCasaDiagnostics.Write(
                                            "Gallery.TransferImport", ex);
                                    }
                                }
                                await Dispatcher.InvokeAsync(async () =>
                                    await RefreshBrowseGalleryAsync(true))
                                    .Task.Unwrap();
                            }, workKind: SchedulerWorkKind.CatalogScan);
                    }

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
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"{completed.Count:N0} Datei(en) übertragen · " +
                "weitere Übertragung abgebrochen";
        }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally
        {
            EndVisibleTransfer();
            _galleryFileOperationRunning = false;
            _manualFileTransferActive = false;
            QueuePendingInitialScans();
            if (_backgroundPending.Count > 0 &&
                _backgroundScanSettings?.Enabled == true)
                _backgroundDebounceTimer?.Start();
        }
    }

    private async Task DeleteSelectedImagesAsync()
    {
        if (_galleryFileOperationRunning || _selectedImageIds.Count == 0) return;
        var selected = _selectedImageIds
            .Select(id => (Id: id, Path: GetImagePath(id)))
            .Where(item => item.Path is not null)
            .Select(item => (item.Id, Path: item.Path!)).ToArray();
        if (selected.Length == 0) return;
        var anyNetwork = selected.Any(item => PathClassifier.Classify(item.Path).IsNetwork);
        var prompt = anyNetwork
            ? $"{selected.Length:N0} Bilddatei(en) löschen?\n\n" +
              "Mindestens eine liegt auf einem Netzlaufwerk und wird dort ENDGÜLTIG gelöscht."
            : $"{selected.Length:N0} Bilddatei(en) in den Windows-Papierkorb verschieben?";
        if (MessageBox.Show(this, prompt, "Bilder löschen",
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _galleryFileOperationRunning = true;
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
        finally { _galleryFileOperationRunning = false; }
    }
}
