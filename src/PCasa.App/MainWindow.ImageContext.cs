using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;
using PCasa.App.Controls;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private void BrowseSurface_ImageContextRequested(
        object? sender, BrowseImageClickedEventArgs e)
    {
        if (_viewerOpen) return;
        if (!_selectedImageIds.Contains(e.ImageId) || _selectedImageIds.Count != 1)
            BrowseSurface_ImageClicked(sender, new BrowseImageClickedEventArgs(e.ImageId, ModifierKeys.None));

        var id = e.ImageId;
        var menu = new ContextMenu { PlacementTarget = BrowseSurface, Placement = PlacementMode.MousePoint };
        AddContextAction(menu, "Im Explorer anzeigen", () => ShowFileInExplorer(id));
        AddContextAction(menu, "Dateipfad kopieren", () =>
        {
            if (GetImagePath(id) is { } path) Clipboard.SetText(path);
        });
        AddContextAction(menu, "Eigenschaften", () => ShowFileProperties(id));
        menu.Items.Add(new Separator());
        AddContextAction(menu, "Duplizieren", () => DuplicateImage(id));
        AddContextAction(menu, "Löschen", () => RecycleImage(id));
        menu.IsOpen = true;
    }

    private static void AddContextAction(ContextMenu menu, string title, Action action)
    {
        var item = new MenuItem { Header = title };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ShowFileInExplorer(long id)
    {
        var path = GetImagePath(id);
        if (path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private void ShowFileProperties(long id)
    {
        var path = GetImagePath(id);
        if (path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path, Verb = "properties", UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async void DuplicateImage(long id)
    {
        var path = GetImagePath(id);
        if (path is null) return;
        try
        {
            var folderId = GetContextMonitoredFolderId(id);
            var monitored = folderId is long monitoredId
                ? _folderRepository.GetAll().FirstOrDefault(item => item.Id == monitoredId)
                : null;
            var source = monitored is null
                ? null : _folderRepository.GetSourceState(monitored.CanonicalPath);
            string? target = null;
            StatusText.Text = "Duplikat wird erstellt …";
            await _scheduler.EnqueueAsync(
                $"image-duplicate:{id}:{path}", SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    if (monitored is not null &&
                        (source is not { DesiredPresent: true } ||
                         !_folderRepository.IsCurrentSourceGeneration(
                             monitored.CanonicalPath, source.Value.Generation, true)))
                        throw new OperationCanceledException("Die überwachte Quelle wurde geändert.");
                    if (!File.Exists(path))
                        throw new FileNotFoundException("Das Original ist nicht mehr vorhanden.", path);
                    var directory = Path.GetDirectoryName(path)!;
                    var stem = Path.GetFileNameWithoutExtension(path);
                    var ext = Path.GetExtension(path);
                    var copy = Path.Combine(directory, stem + " - Kopie" + ext);
                    var suffix = 2;
                    while (File.Exists(copy))
                        copy = Path.Combine(directory, stem + $" - Kopie ({suffix++})" + ext);
                    File.Copy(path, copy);
                    target = copy;
                    if (monitored is not null)
                        _folderScanner.ScanFile(monitored, copy, cancellationToken: token);
                    return Task.CompletedTask;
                });
            if (monitored is not null)
                await RefreshBrowseGalleryAsync(forceIndexReload: true, warmViewport: true);
            StatusText.Text = "Dupliziert: " + Path.GetFileName(target);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async void RecycleImage(long id)
    {
        var path = GetImagePath(id);
        if (path is null) return;
        try
        {
            var network = await Task.Run(() => PathClassifier.Classify(path).IsNetwork);
            var question = network
                ? $"Netzlaufwerk: Datei ENDGÜLTIG löschen?\n\n" +
                  $"Dieser Vorgang kann nicht rückgängig gemacht werden.\n\n{path}"
                : $"Datei in den Windows-Papierkorb verschieben?\n\n{path}";
            if (MessageBox.Show(this, question, "Bild löschen",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var monitoredId = GetContextMonitoredFolderId(id);
            StatusText.Text = "Bild löschen …";
            await _scheduler.EnqueueAsync(
                $"image-delete:{id}:{path}", SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    // Physical file operation and catalog detachment occupy one
                    // scheduler job. A canceled queued request cannot delete.
                    if (network)
                        File.Delete(path);
                    else
                        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
                            RecycleOption.SendToRecycleBin);
                    if (monitoredId is long folderId)
                        new CatalogRemovalService(_database).DetachImage(id, path);
                    // No cancel between physical deletion and catalog update:
                    // if the file moved, always finish the short DB cleanup.
                    return Task.CompletedTask;
                });
            if (monitoredId is long)
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
            StatusText.Text = "Gelöscht: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            ShowFileActionError(ex);
        }
    }

    private long? GetContextMonitoredFolderId(long imageId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pf.monitored_folder_id FROM images i
            JOIN photo_folders pf ON pf.id = i.photo_folder_id
            WHERE i.id = $id
            """;
        command.Parameters.AddWithValue("$id", imageId);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private void QueueContextFolderRefresh(long imageId, string folder)
    {
        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT pf.monitored_folder_id FROM images i
                JOIN photo_folders pf ON pf.id = i.photo_folder_id
                WHERE i.id = $id
                """;
            command.Parameters.AddWithValue("$id", imageId);
            var value = command.ExecuteScalar();
            if (value is not null)
                QueueBackgroundChange(new BackgroundFolderChange(
                    Convert.ToInt64(value), folder,
                    BackgroundFolderChangeKind.ScanDirectory, Recursive: false));
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Context.Refresh", ex); }
    }

    private void ShowFileActionError(Exception ex)
    {
        PCasaDiagnostics.Write("Context.FileAction", ex);
        MessageBox.Show(this, ex.Message, "Dateiaktion fehlgeschlagen",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
