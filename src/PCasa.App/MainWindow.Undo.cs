using System.IO;
using System.Text.Json;
using System.Windows;
using PCasa.App.Services;
using PCasa.App.Data;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record UndoRestoreRecipe(string Path, Guid VersionId,
        long ImageId, long MonitoredFolderId);

    private void UndoOptions_Click(object sender, RoutedEventArgs e)
    {
        var options = UndoOptions.Load();
        var dialog = new UndoOptionsWindow(this, options);
        dialog.ShowDialog();
    }

    private long CurrentRestoreImageId =>
        _viewerOpen && _viewerSequence is not null
            ? _viewerSequence.At(_viewerCurrentIndex).ImageId
            : _selectedImageIds.Count == 1 ? _selectedImageIds.First() : 0;

    private bool HasRestorableVersion(long imageId)
    {
        if (imageId <= 0) return false;
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT local_path FROM images WHERE id=$id";
        command.Parameters.AddWithValue("$id", imageId);
        var path = command.ExecuteScalar() as string;
        return path is not null &&
            new UndoHistoryService(UndoOptions.Load()).HasRestorableVersion(path);
    }

    private async void EditMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        var imageId = CurrentRestoreImageId;
        EditRestoreMenuItem.IsEnabled = false;
        if (imageId <= 0) return;
        try
        {
            var available = await Task.Run(() => HasRestorableVersion(imageId));
            if (!Dispatcher.HasShutdownStarted && imageId == CurrentRestoreImageId)
                EditRestoreMenuItem.IsEnabled = available;
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Undo.Availability", ex); }
    }

    private void UndoRestore_Click(object sender, RoutedEventArgs e)
        => UndoRestoreImage(CurrentRestoreImageId);

    private void UndoRestoreImage(long imageId)
    {
        if (imageId <= 0)
        {
            PcasaDialog.Show(this, UiLanguage.T("Bitte genau ein Bild auswählen."),
                "Undo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string path; long monitoredId;
        using (var db = _database.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT local_path, folder_id FROM images WHERE id=$id";
            command.Parameters.AddWithValue("$id",imageId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return;
            path = reader.GetString(0);
            monitoredId = reader.GetInt64(1);
        }
        var service = new UndoHistoryService(UndoOptions.Load());
        var versions = service.GetVersions(path);
        if (versions.Count == 0)
        {
            PcasaDialog.Show(this, UiLanguage.T("Für dieses Bild sind noch keine Undo-Sicherungen vorhanden."),
                "Undo",MessageBoxButton.OK,MessageBoxImage.Information);
            return;
        }
        var dialog = new UndoCompareWindow(this,path,versions);
        if (dialog.ShowDialog() != true ||
            dialog.SelectedVersion is not { } selected) return;
        var request = new UndoRestoreRecipe(path,selected.Id,imageId,monitoredId);
        var key = "undo:restore:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key,UiLanguage.T("Bild wiederherstellen"),1);
        Task task;
        try
        {
            task = _scheduler.EnqueueDurableRecipeAsync(key,"undo-restore-v1",
                JsonSerializer.Serialize(request),
                SchedulerPriority.ManualAction,SchedulerWorkKind.Catalog);
            _taskManagerWindow?.RequestRefresh();
        }
        catch { EndVisibleManualOperation(key); throw; }
        _ = ObserveUndoRestoreAsync(task,key,request);
    }

    private SchedulerTaskPackage BuildUndoRestorePackage(string key,string payload)
    {
        var request = JsonSerializer.Deserialize<UndoRestoreRecipe>(payload)
            ?? throw new InvalidDataException("Ungültiger Wiederherstellungsauftrag.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            _scheduler.ReportProgress(key,UiLanguage.T("Bild wiederherstellen"),0,1,request.Path);
            new UndoHistoryService(UndoOptions.Load()).Restore(
                request.Path,request.VersionId,token);
            new BrowseCacheRepository(_database).InvalidateImage(request.ImageId);
            _scheduler.ReportProgress(key,UiLanguage.T("Bild wiederherstellen"),1,1,request.Path);
            return Task.CompletedTask;
        });
        return package;
    }

    private async Task ObserveUndoRestoreAsync(Task completion,string key,
        UndoRestoreRecipe request)
    {
        var succeeded = false;
        try { await completion; succeeded = true; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Undo.Restore",ex);
            if (!Dispatcher.HasShutdownStarted)
                PcasaDialog.Show(this,ex.Message,"Undo",
                    MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                if (succeeded)
                {
                    // The durable writer has restored the actual source and
                    // invalidated its packed cache. Also drop any decoded
                    // old pixels (including the post-edit preview bridge).
                    _browsePageCache?.InvalidateImage(request.ImageId);
                    _viewerOriginalCache.Remove(request.ImageId);
                    _ = RefreshRestoreBadgesAsync();
                    // Reuse the normal priority image reader for an immediate
                    // visible restored thumbnail. No filesystem access on the
                    // WPF dispatcher and no second thumbnail writer.
                    try
                    {
                        var restored = await _scheduler.RunAsync(
                            "undo:restored-preview:" + key,
                            SchedulerPriority.ManualAction,
                            token =>
                            {
                                var info = new PhotoViewerImageRepository(_database)
                                    .Get(request.ImageId);
                                return info is null ? null
                                    : PhotoViewerImageLoader.Load(info, 512, token);
                            },
                            workKind: SchedulerWorkKind.ViewerRead);
                        if (!Dispatcher.HasShutdownStarted && restored is not null)
                            _browsePageCache?.ShowEditedImage(request.ImageId, restored);
                    }
                    catch (Exception ex)
                    {
                        PCasaDiagnostics.Write("Undo.RestoredPreview", ex);
                    }
                    QueueBackgroundChange(new BackgroundFolderChange(
                        request.MonitoredFolderId,request.Path,
                        BackgroundFolderChangeKind.ScanFile,
                        Recursive:false,ForceExifRefresh:true));
                    if (_viewerOpen && _viewerImageInfo?.Id == request.ImageId)
                        ShowViewerImage(keepCurrentAsPlaceholder:true);
                    _ = RefreshBrowseGalleryAsync(forceIndexReload:true,
                        warmViewport:false);
                }
            }
        }
    }
}
