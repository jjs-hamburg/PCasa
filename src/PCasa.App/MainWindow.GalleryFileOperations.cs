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

    private string[] SelectedGalleryPaths()
    {
        if (_selectedImageIds.Count == 0) return [];

        // Browse index + folder summaries are already a read-only projection
        // of the catalog on the UI thread. On a drag gesture, never wait for
        // SQLite transactions from an unrelated running scan. Reconstruct
        // the same local paths for indexed images without probing the NAS.
        var folders = _photoFolders.ToDictionary(folder => folder.Id,
            folder => folder.LocalPath);
        var pathsById = new Dictionary<long, string>(_selectedImageIds.Count);
        foreach (var image in _browseIndex)
        {
            if (!_selectedImageIds.Contains(image.ImageId) ||
                !folders.TryGetValue(image.PhotoFolderId, out var folderPath))
                continue;
            pathsById[image.ImageId] = Path.Combine(folderPath, image.FileName);
        }

        // Uncached images still use the same catalog as the authority;
        // no additional path cache or divergent source of truth is created.
        if (pathsById.Count < _selectedImageIds.Count)
        {
            using var db = _database.OpenConnection();
            foreach (var group in _selectedImageIds
                .Where(id => !pathsById.ContainsKey(id)).Chunk(400))
            {
                using var command = db.CreateCommand();
                var placeholders = group.Select((_, i) => "$id" + i).ToArray();
                for (var i = 0; i < group.Length; i++)
                    command.Parameters.AddWithValue(placeholders[i], group[i]);
                command.CommandText = "SELECT id, local_path FROM images WHERE id IN (" +
                    string.Join(",", placeholders) + ");";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    pathsById[reader.GetInt64(0)] = reader.GetString(1);
            }
        }
        return pathsById.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void CopyGalleryFilesToClipboard(bool cut)
    {
        var paths = SelectedGalleryPaths();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData("Preferred DropEffect",
            new MemoryStream(BitConverter.GetBytes(cut ? 2 : 1)));
        Clipboard.SetDataObject(data, true);
        StatusText.Text = cut
            ? $"{paths.Length:N0} Datei(en) zum Verschieben vorgemerkt"
            : $"{paths.Length:N0} Datei(en) kopiert";
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
        return Directory.Exists(tag.FullPath) && IsMonitoredGalleryDestination(tag.FullPath)
            ? tag.FullPath : null;
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

    private async Task TransferGalleryFilesAsync(IEnumerable<string> input,
        string targetDirectory, bool move)
    {
        if (_galleryFileOperationRunning) return;

        // Only lexical path normalization happens on the UI thread.
        // All filesystem checks, conflict planning and catalog lookups must
        // enter the scheduler immediately at MANUAL priority, even on SMB.
        var requested = input.Where(FolderScanner.IsSupportedImagePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Length == 0) return;
        var targetPath = Path.GetFullPath(targetDirectory);
        _galleryFileOperationRunning = true;
        _manualFileTransferActive = true;
        _cacheMaintenanceCancellation?.Cancel();
        _browseCacheCancellation?.Cancel();
        StatusText.Text = move ? "Verschieben angefordert (höchste Priorität)"
                               : "Kopieren angefordert (höchste Priorität)";
        var completed = new List<string>();
        var retainedIds = new HashSet<long>();
        var needsIndex = new List<string>();
        var failed = new List<string>();

        try
        {
            await _scheduler.EnqueueAsync(
                $"gallery:file-transfer:{Guid.NewGuid():N}",
                SchedulerPriority.ManualAction, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(targetPath) ||
                        !IsMonitoredGalleryDestination(targetPath))
                        throw new IOException(
                            "Ablage nur in einem erreichbaren überwachten Ordner möglich.");
                    // Do not inspect EVERY source/destination before moving
                    // the first photo. NAS existence probes are expensive.
                    // Resolve conflicts per file and immediately move it;
                    // defer thumbnail regeneration until all moves are done.
                    var roots = _folderRepository.GetAll();
                    var destinationRoot = roots
                        .Where(root => FolderExclusionRules.IsAtOrBelow(
                            targetPath, root.LocalPath))
                        .OrderByDescending(root => root.LocalPath.Length)
                        .FirstOrDefault()
                        ?? throw new IOException("Der Zielordner ist nicht überwacht.");
                    var catalog = new PCasa.App.Data.ImageCatalogRepository(_database);
                    var reserved = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var source in requested)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!File.Exists(source)) continue;
                        var destination = Path.Combine(targetPath,
                            Path.GetFileName(source));
                        var samePath = string.Equals(source, destination,
                            StringComparison.OrdinalIgnoreCase);
                        if (samePath && move) continue;

                        try
                        {
                            var replace = false;
                            if (reserved.Contains(destination))
                                destination = UniqueCopyPath(destination, reserved);
                            else if (samePath || File.Exists(destination))
                            {
                                var choice = samePath ? NameConflict.KeepBoth :
                                    await Dispatcher.InvokeAsync(() =>
                                        ResolveFileConflict(destination));
                                if (choice == NameConflict.Cancel)
                                    break; // completed moves remain committed
                                if (choice == NameConflict.Skip) continue;
                                if (choice == NameConflict.Replace && !samePath)
                                    replace = true;
                                else
                                    destination = UniqueCopyPath(destination, reserved);
                            }
                            reserved.Add(destination);

                            var sourceId = move ? CatalogImageIdAtPath(source) : null;
                            var replacedId = replace ? CatalogImageIdAtPath(destination) : null;
                            // The same per-source lock as FolderScanner avoids
                            // moving a photo while its original is decoded.
                            lock (SourceFileLocks.ForPath(source))
                            {
                                token.ThrowIfCancellationRequested();
                                if (replace)
                                {
                                    var staged = destination + ".pcasa-" +
                                        Guid.NewGuid().ToString("N") + ".tmp";
                                    try
                                    {
                                        File.Copy(source, staged, overwrite: false);
                                        File.Move(staged, destination, overwrite: true);
                                        if (move) File.Delete(source);
                                    }
                                    finally
                                    {
                                        if (File.Exists(staged)) File.Delete(staged);
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

                                // Same image ID, same bytes, same EXIF/tags and
                                // packed thumbnails. Do NOT run ScanFile on a
                                // successful relocation of a catalogued photo.
                                if (move && sourceId is long imageId &&
                                    catalog.RelocateImage(
                                        imageId, source, destination,
                                        destinationRoot, token))
                                {
                                    retainedIds.Add(imageId);
                                }
                                else
                                {
                                    // An uncatalogued file or a COPY needs the
                                    // ordinary scanner after physical transfer.
                                    if (move && sourceId is long oldId)
                                        new CatalogRemovalService(_database)
                                            .DetachImage(oldId, source);
                                    if (replace && replacedId is long oldTarget)
                                        new CatalogRemovalService(_database)
                                            .DetachImage(oldTarget, destination);
                                    needsIndex.Add(destination);
                                }
                            }
                            completed.Add(destination);
                        }
                        catch (Exception ex) when (ex is IOException or
                            UnauthorizedAccessException or InvalidOperationException)
                        {
                            failed.Add(Path.GetFileName(source) + ": " + ex.Message);
                        }
                    }

                    // Only genuinely new/uncatalogued files require decoding.
                    // Relocated originals are already completely indexed.
                    if (needsIndex.Count > 0)
                        await Dispatcher.InvokeAsync(() =>
                            StatusText.Text = $"{completed.Count:N0} Dateien " +
                                (move ? "verschoben" : "kopiert") +
                                " · neue Vorschaudaten werden erzeugt");

                    foreach (var destination in needsIndex)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var root = roots.FirstOrDefault(candidate =>
                                FolderExclusionRules.IsAtOrBelow(
                                    destination, candidate.LocalPath));
                            if (root is not null)
                                _folderScanner.ScanFile(root, destination,
                                    cancellationToken: token);
                        }
                        catch (Exception ex) when (ex is IOException or
                            UnauthorizedAccessException or InvalidOperationException)
                        {
                            // Physical transfer succeeded. Background monitor
                            // can repair missing cache tiers; don't falsely
                            // report that the user's file move failed.
                            PCasaDiagnostics.Write("Gallery.TransferIndex", ex);
                        }
                    }
                });
            if (completed.Count == 0)
                return;
            await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
            // Catalog refresh registers newly populated destination folders
            // through the existing targeted node insertion, never a full
            // tree rebuild (which would disrupt tree focus and scroll).
            var movedIds = await _scheduler.RunAsync(
                $"catalog:transferred-selection:{Guid.NewGuid():N}",
                SchedulerPriority.ManualAction,
                token =>
                {
                    var result = new HashSet<long>(retainedIds);
                    using var db = _database.OpenConnection();
                    foreach (var group in completed.Chunk(400))
                    {
                        token.ThrowIfCancellationRequested();
                        using var cmd = db.CreateCommand();
                        var placeholders = group.Select((_, i) => "$p" + i).ToArray();
                        for (var i = 0; i < group.Length; i++)
                            cmd.Parameters.AddWithValue(placeholders[i], group[i]);
                        cmd.CommandText = "SELECT id FROM images WHERE local_path IN (" +
                            string.Join(",", placeholders) + ");";
                        using var reader = cmd.ExecuteReader();
                        while (reader.Read()) result.Add(reader.GetInt64(0));
                    }
                    return result;
                }, workKind: SchedulerWorkKind.CatalogRead);
            _selectedImageIds.Clear();
            _selectedImageIds.UnionWith(movedIds);
            ApplyGallerySelection();
            var first = _displayedBrowseIndex.FirstOrDefault(entry =>
                _selectedImageIds.Contains(entry.ImageId));
            if (first is not null) ScrollGalleryImageIntoView(first.ImageId);
            StatusText.Text = $"{completed.Count:N0} Datei(en) " +
                (move ? "verschoben" : "kopiert") +
                (failed.Count > 0 ? $" · {failed.Count:N0} Fehler" : "");
            if (failed.Count > 0)
                MessageBox.Show(this, string.Join("\n", failed.Take(15)),
                    "Dateiübertragung teilweise fehlgeschlagen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
        finally
        {
            _galleryFileOperationRunning = false;
            _manualFileTransferActive = false;
            QueuePendingInitialScans();
            if (_backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
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
