using System.IO;
using System.Text.Json;
using System.Windows;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record ExplicitExifDateRecipe(long ImageId, string Path,
        DateTime SourceWriteUtc, ExifDateMetadata.Fields Values);

    private long? _infoDatesImageId;
    private string? _infoDatesPath;
    private string? _infoDatesFilename;
    private DateTime? _infoDatesSourceWriteUtc;
    private ExifDateMetadata.Fields _infoDatesOriginal = new(null, null, null);
    private bool _infoDatesEditing;
    private bool _infoDatesSaving;

    private static bool CanManuallyEditExifDates(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".png";

    private void UpdateInfoDateEditor(long? imageId, string? path,
        string? filename, DateTime? sourceWriteUtc, ExifDateMetadata.Fields? dates)
    {
        _infoDatesImageId = imageId;
        _infoDatesPath = path;
        _infoDatesFilename = filename;
        _infoDatesSourceWriteUtc = sourceWriteUtc;
        _infoDatesOriginal = dates ?? new(null, null, null);
        InfoDatesPanel.Visibility = _sidebarMode == "Info" &&
            imageId is not null && path is not null
            ? Visibility.Visible : Visibility.Collapsed;
        InfoDateOriginal.Text =
            ExifDateMetadata.DisplayDate(_infoDatesOriginal.DateTimeOriginal);
        InfoDateCreated.Text =
            ExifDateMetadata.DisplayDate(_infoDatesOriginal.CreateDate);
        InfoDateModified.Text =
            ExifDateMetadata.DisplayDate(_infoDatesOriginal.ModifyDate);
        SetInfoDateEditing(false);
        InfoDatesEdit.Visibility = path is not null && CanManuallyEditExifDates(path) &&
            imageId is long id && _selectedImageIds.Count == 1 &&
            _selectedImageIds.Contains(id)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetInfoDateEditing(bool value)
    {
        _infoDatesEditing = value;
        InfoDateOriginal.IsReadOnly = !value;
        InfoDateCreated.IsReadOnly = !value;
        InfoDateModified.IsReadOnly = !value;
        InfoDatesActions.Visibility = value
            ? Visibility.Visible : Visibility.Collapsed;
        var videoFromName = !value && _infoDatesPath is { } videoPath &&
            FilenameCaptureDateWriter.IsMp4(videoPath) && _infoDatesImageId is long videoId &&
            _selectedImageIds.Count == 1 && _selectedImageIds.Contains(videoId);
        InfoDateFromName.Visibility = value || videoFromName
            ? Visibility.Visible : Visibility.Collapsed;
        InfoDatesEdit.Visibility = !value && _infoDatesPath is { } path &&
            _infoDatesImageId is long id && _selectedImageIds.Count == 1 &&
            _selectedImageIds.Contains(id) && CanManuallyEditExifDates(path)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InfoDatesEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_infoDatesSaving || _infoDatesImageId is null ||
            _infoDatesPath is not { } path || !CanManuallyEditExifDates(path) ||
            _selectedImageIds.Count != 1 ||
            !_selectedImageIds.Contains(_infoDatesImageId.Value))
            return;
        SetInfoDateEditing(true);
    }

    private void InfoDateFromFilename_Click(object sender, RoutedEventArgs e)
    {
        if (!_infoDatesEditing && _infoDatesPath is { } videoPath &&
            FilenameCaptureDateWriter.IsMp4(videoPath) && _infoDatesImageId is long videoId &&
            _selectedImageIds.Count == 1 && _selectedImageIds.Contains(videoId))
        {
            _ = EditCaptureDatesFromNamesAsync([videoId]);
            return;
        }
        if (!_infoDatesEditing || _infoDatesFilename is null) return;
        var found = PhotoDateFromFilename.Recognize(_infoDatesFilename);
        if (found is null)
        {
            MessageBox.Show(this,
                UiLanguage.T("Im Dateinamen wurde kein gültiges Aufnahmedatum gefunden."),
                UiLanguage.T("Bildinformationen"), MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (found.Time is null)
        {
            // WhatsApp encodes only the date. Never invent a clock time.
            InfoDateOriginal.Text = found.Date.ToString(
                "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture) + " ";
            InfoDateOriginal.Focus();
            InfoDateOriginal.CaretIndex = InfoDateOriginal.Text.Length;
            MessageBox.Show(this,
                UiLanguage.T("Der Dateiname enthält nur ein Datum. Bitte die Uhrzeit ergänzen."),
                UiLanguage.T("Bildinformationen"), MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        InfoDateOriginal.Text = ExifDateMetadata.DisplayDate(found.AsExifDateTime);
    }

    private void InfoDatesCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_infoDatesSaving) return;
        InfoDateOriginal.Text = ExifDateMetadata.DisplayDate(
            _infoDatesOriginal.DateTimeOriginal);
        InfoDateCreated.Text = ExifDateMetadata.DisplayDate(
            _infoDatesOriginal.CreateDate);
        InfoDateModified.Text = ExifDateMetadata.DisplayDate(
            _infoDatesOriginal.ModifyDate);
        SetInfoDateEditing(false);
    }

    private async void InfoDatesSave_Click(object sender, RoutedEventArgs e)
    {
        if (_infoDatesSaving || !_infoDatesEditing ||
            _infoDatesImageId is not long imageId ||
            _infoDatesPath is not { } path || _infoDatesSourceWriteUtc is not { } stamp ||
            _sidebarMode != "Info" || _selectedImageIds.Count != 1 ||
            !_selectedImageIds.Contains(imageId))
            return;

        ExifDateMetadata.Fields newValues;
        try
        {
            newValues = new ExifDateMetadata.Fields(
                ExifDateMetadata.ParseEditableDate(InfoDateOriginal.Text),
                ExifDateMetadata.ParseEditableDate(InfoDateCreated.Text),
                ExifDateMetadata.ParseEditableDate(InfoDateModified.Text));
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, UiLanguage.T(ex.Message),
                UiLanguage.T("Bildinformationen"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (newValues == _infoDatesOriginal)
        {
            SetInfoDateEditing(false);
            return;
        }
        var key = "info:exif-dates:" + Guid.NewGuid().ToString("N");
        var request = new ExplicitExifDateRecipe(imageId, path, stamp, newValues);
        _infoDatesSaving = true;
        InfoDatesSave.IsEnabled = false;
        InfoDatesCancel.IsEnabled = false;
        BeginVisibleManualOperation(key, UiLanguage.T("EXIF-Datumsfelder speichern"), 1);
        try
        {
            var task = _scheduler.EnqueueDurableRecipeAsync(key, "explicit-exif-dates-v1",
                JsonSerializer.Serialize(request), SchedulerPriority.ManualAction);
            SetInfoDateEditing(false);
            await task;
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            _viewerOriginalCache.Remove(imageId);
            SetInfoDateEditing(false);
            await UpdateSidebarContentAsync();
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ImageInfo.ExifDates.Save", ex);
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            MessageBox.Show(this, UiLanguage.T("Aufnahmedatum konnte nicht gespeichert werden.") +
                Environment.NewLine + UiLanguage.T(ex.Message),
                UiLanguage.T("Bildinformationen"), MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            EndVisibleManualOperation(key);
            _infoDatesSaving = false;
            InfoDatesSave.IsEnabled = true;
            InfoDatesCancel.IsEnabled = true;
        }
    }

    private SchedulerTaskPackage BuildExplicitExifDatePackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<ExplicitExifDateRecipe>(payload)
            ?? throw new InvalidDataException("Invalid EXIF date recipe.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            token.ThrowIfCancellationRequested();
            _scheduler.ReportProgress(key, UiLanguage.T("EXIF-Datumsfelder speichern"),
                0, 1, request.Path);
            using var lease = SourceFileLocks.Enter(request.Path, token);
            long folderId;
            using (var db = _database.OpenConnection())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT local_path,folder_id FROM images WHERE id=$id";
                command.Parameters.AddWithValue("$id", request.ImageId);
                using var row = command.ExecuteReader();
                if (!row.Read() || !string.Equals(row.GetString(0), request.Path,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("EXIF date source no longer matches catalog.");
                folderId = row.GetInt64(1);
            }
            var root = _folderRepository.GetAll().FirstOrDefault(folder => folder.Id == folderId)
                ?? throw new IOException("Katalogordner der Bilddatei fehlt.");
            var current = ExifDateMetadata.ReadFields(request.Path, token);
            if (current != request.Values)
            {
                if (File.GetLastWriteTimeUtc(request.Path) != request.SourceWriteUtc)
                    throw new IOException("Die Datei wurde seit dem Öffnen der Bildinformationen geändert.");
                ExifDateMetadata.SaveExplicit(request.Path, request.Values, token);
            }
            new BrowseCacheRepository(_database).InvalidateImage(request.ImageId);
            var result = _folderScanner.ScanFile(root, request.Path,
                cancellationToken: CancellationToken.None, publishCatalogChange: false,
                forceExifRefresh: true);
            if (result.Failed > 0 || result.ImageFilesSeen == 0)
                throw new IOException("Die neuen EXIF-Datumsfelder konnten nicht katalogisiert werden.");
            _scheduler.ReportProgress(key, UiLanguage.T("EXIF-Datumsfelder speichern"),
                1, 1, request.Path);
            return Task.CompletedTask;
        });
        return package;
    }

}
