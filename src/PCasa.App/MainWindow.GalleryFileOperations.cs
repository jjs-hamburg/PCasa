using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _galleryFileOperationRunning;

    private sealed record GalleryFileOperation(string Source, string Destination,
        bool Move, bool Replace, long? CatalogImageId);

    private string[] SelectedGalleryPaths()
    {
        // Ctrl+A can select tens of thousands of images: one bounded IN query
        // per 400 IDs rather than one SQLite connection per selected file.
        var paths = new List<string>(_selectedImageIds.Count);
        using var db = _database.OpenConnection();
        foreach (var group in _selectedImageIds.Chunk(400))
        {
            using var command = db.CreateCommand();
            var placeholders = group.Select((_, i) => "$id" + i).ToArray();
            for (var i = 0; i < group.Length; i++)
                command.Parameters.AddWithValue(placeholders[i], group[i]);
            command.CommandText = "SELECT local_path FROM images WHERE id IN (" +
                string.Join(",", placeholders) + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void CopyGalleryFilesToClipboard(bool cut)
    {
        var paths = SelectedGalleryPaths();
        if (paths.Length == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData("Preferred DropEffect",
            new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
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
        var sources = input.Where(File.Exists)
            .Where(FolderScanner.IsSupportedImagePath)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (sources.Length == 0) return;
        targetDirectory = Path.GetFullPath(targetDirectory);
        if (!Directory.Exists(targetDirectory) ||
            !IsMonitoredGalleryDestination(targetDirectory))
        {
            StatusText.Text = "Ablage nur in einem erreichbaren überwachten Ordner möglich";
            return;
        }
        var plans = new List<GalleryFileOperation>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(source));
            var samePath = string.Equals(source, destination,
                StringComparison.OrdinalIgnoreCase);
            if (samePath && move) continue;
            var replace = false;
            if (samePath || File.Exists(destination) || !reserved.Add(destination))
            {
                var choice = samePath ? NameConflict.KeepBoth : ResolveFileConflict(destination);
                if (choice == NameConflict.Cancel) return;
                if (choice == NameConflict.Skip) continue;
                if (choice == NameConflict.Replace && !samePath) replace = true;
                else destination = UniqueCopyPath(destination, reserved);
                reserved.Add(destination);
            }
            plans.Add(new GalleryFileOperation(source, destination, move, replace,
                CatalogImageIdAtPath(source)));
        }
        if (plans.Count == 0) return;
        _galleryFileOperationRunning = true;
        var completed = new List<string>();
        var failed = new List<string>();
        try
        {
            await _scheduler.EnqueueAsync(
                $"gallery:file-transfer:{Guid.NewGuid():N}", SchedulerPriority.Interactive,
                token =>
                {
                    var roots = _folderRepository.GetAll();
                    foreach (var plan in plans)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var destination = plan.Destination;
                            // Staging a replacement in the target directory
                            // preserves the previous file if copying fails.
                            lock (SourceFileLocks.ForPath(plan.Source))
                            {
                                if (plan.Replace)
                                {
                                    var staged = destination + ".pcasa-" +
                                        Guid.NewGuid().ToString("N") + ".tmp";
                                    try
                                    {
                                        File.Copy(plan.Source, staged, overwrite: false);
                                        File.Move(staged, destination, overwrite: true);
                                        if (plan.Move) File.Delete(plan.Source);
                                    }
                                    finally
                                    {
                                        if (File.Exists(staged)) File.Delete(staged);
                                    }
                                }
                                else if (plan.Move)
                                {
                                    // MoveFileEx can fail across different volumes;
                                    // copy+delete is the correct EXPLORER fallback.
                                    if (SameVolume(plan.Source, destination))
                                        File.Move(plan.Source, destination);
                                    else
                                    {
                                        File.Copy(plan.Source, destination, overwrite: false);
                                        File.Delete(plan.Source);
                                    }
                                }
                                else File.Copy(plan.Source, destination, overwrite: false);
                                if (plan.Move && plan.CatalogImageId is long sourceId)
                                    new CatalogRemovalService(_database).DetachImage(
                                        sourceId, plan.Source);
                            }
                            // Scanner remains THE single index/cache writer for
                            // imported files, including external Explorer drops.
                            var root = roots.FirstOrDefault(candidate =>
                                FolderExclusionRules.IsAtOrBelow(destination, candidate.LocalPath));
                            if (root is not null)
                                _folderScanner.ScanFile(root, destination, cancellationToken: token);
                            completed.Add(destination);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                            or InvalidOperationException)
                        {
                            failed.Add(Path.GetFileName(plan.Source) + ": " + ex.Message);
                        }
                    }
                    return Task.CompletedTask;
                });
            await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: false);
            var targets = completed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _selectedImageIds.Clear();
            using (var db = _database.OpenConnection())
            {
                foreach (var group in completed.Chunk(400))
                {
                    using var cmd = db.CreateCommand();
                    var placeholders = group.Select((_, i) => "$p" + i).ToArray();
                    for (var i = 0; i < group.Length; i++)
                        cmd.Parameters.AddWithValue(placeholders[i], group[i]);
                    cmd.CommandText = "SELECT id FROM images WHERE local_path IN (" +
                        string.Join(",", placeholders) + ");";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) _selectedImageIds.Add(reader.GetInt64(0));
                }
            }
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
        finally { _galleryFileOperationRunning = false; }
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
                $"gallery:delete:{Guid.NewGuid():N}", SchedulerPriority.Interactive,
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
