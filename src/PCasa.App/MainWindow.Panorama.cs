using System.Diagnostics;
using System.IO;
using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private PanoramaSession? _panoramaSession;
    private bool _panoramaImportRunning;
    private bool _panoramaLaunchPending;
    private bool _panoramaActivationHooked;
    private DateTime _panoramaLastResultDialogUtc = DateTime.MinValue;

    private async Task StartPanoramaSelectionAsync(long[] imageIds)
    {
        if (imageIds.Length < 2) return;
        var programs = _externalEditors.ForPanorama();
        if (programs.Count == 0) return;
        var dialog = new PanoramaProgramWindow(programs) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProgram is not { } program)
            return;
        await StartPanoramaAsync(imageIds, program);
    }

    private void HookPanoramaActivation()
    {
        if (_panoramaActivationHooked) return;
        _panoramaActivationHooked = true;
        Activated += async (_, _) =>
        {
            if (_panoramaSession is { } session && !_panoramaImportRunning)
            {
                try { await TryImportPanoramaAsync(session); }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Panorama.Result", ex);
                    if (!Dispatcher.HasShutdownStarted)
                        ShowFileActionError(ex);
                }
            }
        };
    }

    private async Task StartPanoramaAsync(
        long[] imageIds, ExternalEditorProgram program)
    {
        if (_panoramaLaunchPending || _panoramaSession is not null ||
            _externalEditReview is not null || _externalEditLaunchPending)
        {
            ShowFileActionError(new InvalidOperationException(
                "Bitte zuerst die laufende externe Bearbeitung abschließen."));
            return;
        }
        _panoramaLaunchPending = true;
        bool suspended = false;
        try
        {
            // Snapshot every selected identity in visible gallery order.
            var sources = await SelectedGalleryImagePathsAsync(imageIds,
                SchedulerPriority.ManualAction);
            if (sources.Length != imageIds.Length)
                throw new IOException("Nicht alle ausgewählten Panoramabilder sind erreichbar.");
            var selectedById = sources.ToDictionary(x => x.Id, x => x.Path);
            var inputs = imageIds.Select(id => (Id: id, Path: selectedById[id])).ToArray();
            if (inputs.Any(input => VideoFormats.IsVideo(input.Path)))
                throw new NotSupportedException("Panorama nur für Bilder.");

            var key = "panorama:stage:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, UiLanguage.T("Panorama erstellen"), 100);
            SuspendSchedulerForExternalEdit();
            suspended = true;
            PanoramaSession session;
            try
            {
                session = await _scheduler.RunAsync(key,
                    SchedulerPriority.ManualAction, token =>
                    {
                        var (tags, lat, lon) = ReadPanoramaMetadata(imageIds, token);
                        return PanoramaTransaction.Begin(inputs, tags, lat, lon,
                            token, (completed, total) => _scheduler.ReportProgress(
                                key, UiLanguage.T("Panorama erstellen"),
                                total > 0 ? (int)(100.0 * completed / total) : 100,
                                100, inputs[0].Path));
                    }, workKind: SchedulerWorkKind.Catalog);
            }
            finally { EndVisibleManualOperation(key); }

            _panoramaSession = session;
            HookPanoramaActivation();
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = program.ExecutablePath,
                    WorkingDirectory = session.Directory,
                    UseShellExecute = false
                };
                // PanoramaStudio documents passing multiple input file paths.
                // Autopano receives the same explicit local source list; the
                // output must be exported into this isolated working folder.
                foreach (var path in session.Inputs)
                    start.ArgumentList.Add(path);
                await Task.Run(() =>
                {
                    using var launched = Process.Start(start)
                        ?? throw new IOException("Panoramaprogramm konnte nicht gestartet werden.");
                });
                StatusText.Text = UiLanguage.T("Panorama erstellen") + " · " +
                    program.Name;
            }
            catch
            {
                _panoramaSession = null;
                PanoramaTransaction.Cleanup(session);
                throw;
            }
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Panorama.Launch", ex);
            ShowFileActionError(ex);
        }
        finally
        {
            _panoramaLaunchPending = false;
            if (suspended && _panoramaSession is null)
                ResumeSchedulerAfterExternalEdit();
        }
    }

    private (string[] Tags, double? Lat, double? Lon) ReadPanoramaMetadata(
        long[] imageIds, CancellationToken token)
    {
        using var db = _database.OpenConnection();
        string[] tags = [];
        double? latitude = null, longitude = null;
        foreach (var id in imageIds)
        {
            token.ThrowIfCancellationRequested();
            using var command = db.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(t.keywords, ''), x.gps_latitude, x.gps_longitude
                  FROM images i
                  LEFT JOIN image_tags t ON t.image_id=i.id
                  LEFT JOIN image_exif x ON x.image_id=i.id
                 WHERE i.id=$id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var result = command.ExecuteReader();
            if (!result.Read()) continue;
            if (tags.Length == 0 && result.GetString(0) is { Length: > 0 } words)
                tags = words.Split(['\n', '\r'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (latitude is null && !result.IsDBNull(1) && !result.IsDBNull(2))
            {
                latitude = result.GetDouble(1);
                longitude = result.GetDouble(2);
            }
        }
        return (tags, latitude, longitude);
    }

    private async Task TryImportPanoramaAsync(PanoramaSession session)
    {
        if (_panoramaImportRunning || !ReferenceEquals(_panoramaSession, session))
            return;
        _panoramaImportRunning = true;
        try
        {
            var resultPath = await Task.Run(() => PanoramaTransaction.FindResult(session));
            if (!ReferenceEquals(_panoramaSession, session)) return;
            if (resultPath is null)
            {
                // Autopano's output directory is user-controlled. Do not
                // search arbitrary folders or invent a destination. On
                // returning to PCasa, choose the actual rendered result.
                // A canceled chooser leaves the session intact for the next
                // user return, but must not immediately reopen on activation.
                if (DateTime.UtcNow - _panoramaLastResultDialogUtc <
                    TimeSpan.FromSeconds(2)) return;
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = UiLanguage.T("Panoramaergebnis auswählen"),
                    Filter = "Bilder|*.jpg;*.jpeg;*.tif;*.tiff;*.png;*.bmp;*.webp;*.heic;*.heif;*.avif;*.jxl;*.psd|Alle Dateien|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                try
                {
                    if (picker.ShowDialog(this) != true) return;
                    resultPath = picker.FileName;
                }
                finally { _panoramaLastResultDialogUtc = DateTime.UtcNow; }
            }
            var key = "panorama:import:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, UiLanguage.T("Panorama erstellen"), 100);
            string target;
            try
            {
                target = await _scheduler.RunAsync(key,
                    SchedulerPriority.ManualAction, token =>
                    {
                        var actualResult = Path.GetDirectoryName(resultPath)!.Equals(
                                session.Directory, StringComparison.OrdinalIgnoreCase)
                            ? resultPath
                            : PanoramaTransaction.StageExternalResult(
                                session, resultPath, token);
                        var file = PanoramaTransaction.Import(session, actualResult, token,
                            (completed, total) => _scheduler.ReportProgress(key,
                                UiLanguage.T("Panorama erstellen"),
                                total > 0 ? (int)(100.0 * completed / total) : 100,
                                100, actualResult));
                        var monitored = _folderRepository.GetAll().FirstOrDefault(root =>
                            session.Sources[0].StartsWith(
                                Path.TrimEndingDirectorySeparator(root.LocalPath) +
                                Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase));
                        if (monitored is null)
                            throw new IOException("Panorama-Zielordner nicht im Katalog.");
                        var indexed = _folderScanner.ScanFile(monitored, file,
                            cancellationToken: token, publishCatalogChange: false,
                            forceExifRefresh: true);
                        if (indexed.Failed > 0 || indexed.ImageFilesSeen == 0)
                            throw new IOException("Panorama gespeichert, aber Katalogisierung fehlgeschlagen: " + file);
                        return file;
                    }, workKind: SchedulerWorkKind.Catalog);
            }
            finally { EndVisibleManualOperation(key); }
            _panoramaSession = null;
            ResumeSchedulerAfterExternalEdit();
            PanoramaTransaction.Cleanup(session);
            await RefreshBrowseGalleryAsync(forceIndexReload: true);
            StatusText.Text = UiLanguage.T("Panorama erstellen") + ": " +
                Path.GetFileName(target);
        }
        finally { _panoramaImportRunning = false; }
    }
}
