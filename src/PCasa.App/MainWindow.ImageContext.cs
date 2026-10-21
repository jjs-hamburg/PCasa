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
        AddContextAction(menu, "Löschen (Papierkorb)", () => RecycleImage(id));
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
        if (path is null || !File.Exists(path)) return;
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var target = Path.Combine(folder, stem + " - Kopie" + ext);
            var suffix = 2;
            while (File.Exists(target))
                target = Path.Combine(folder, stem + $" - Kopie ({suffix++})" + ext);
            StatusText.Text = "Duplikat wird erstellt …";
            await Task.Run(() => File.Copy(path, target));
            if (_backgroundScanSettings?.Enabled == true)
                QueueContextFolderRefresh(id, folder);
            else if (GetContextMonitoredFolderId(id) is long folderId &&
                     _folderRepository.GetAll().FirstOrDefault(item => item.Id == folderId) is { } monitored)
            {
                StatusText.Text = "Duplikat wird in den Katalog aufgenommen …";
                var source = _folderRepository.GetSourceState(monitored.CanonicalPath);
                if (source is { DesiredPresent: true })
                {
                    await _scheduler.EnqueueAsync(
                        $"image-duplicate-scan:{monitored.Id}:{source.Value.Generation}:{folder}",
                        SchedulerPriority.UserAction,
                        token =>
                        {
                            _folderScanner.ScanDirectory(monitored, folder,
                                recursive: false, cancellationToken: token);
                            return Task.CompletedTask;
                        },
                        () => _folderRepository.IsCurrentSourceGeneration(
                            monitored.CanonicalPath, source.Value.Generation, true));
                }
                await EnsureBrowseCacheAndRefreshAsync();
            }
            StatusText.Text = "Dupliziert: " + Path.GetFileName(target);
        }
        catch (Exception ex) { ShowFileActionError(ex); }
    }

    private async void RecycleImage(long id)
    {
        var path = GetImagePath(id);
        if (path is null || !File.Exists(path)) return;
        var answer = MessageBox.Show(this,
            $"Datei in den Windows-Papierkorb verschieben?\n\n{path}",
            "Bild löschen", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        var monitoredId = GetContextMonitoredFolderId(id);
        try
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            StatusText.Text = "In Papierkorb verschoben · Cache bereinigen …";
            if (monitoredId is long folderId)
            {
                var progress = new Progress<string>(message => StatusText.Text = message);
                await Task.Run(() => new CatalogRemovalService(_database).RemovePath(folderId, path, progress));
                await RefreshBrowseGalleryAsync(forceIndexReload: true);
            }
            else
                StatusText.Text = "In Papierkorb verschoben: " + Path.GetFileName(path);
        }
        catch (Exception ex)
        {
            // Never fall back to irreversible deletion on unsupported UNC shares.
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
