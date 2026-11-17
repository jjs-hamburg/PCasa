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

    // The same local batch transaction handles panorama assembly and ordinary
    // multiple-image editing. These are independent program capabilities.
    private Task StartMultipleEditorAsync(long[] imageIds, ExternalEditorProgram program)
    {
        if (imageIds.Length < 2 || !program.MultiEdit)
            return Task.CompletedTask;
        return StartPanoramaAsync(imageIds, program, isPanorama: false);
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
        long[] imageIds, ExternalEditorProgram program,
        bool isPanorama = true)
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
                throw new IOException("Nicht alle ausgewählten Bilder sind erreichbar.");
            var selectedById = sources.ToDictionary(x => x.Id, x => x.Path);
            var inputs = imageIds.Select(id => (Id: id, Path: selectedById[id])).ToArray();
            if (inputs.Any(input => VideoFormats.IsVideo(input.Path)))
                throw new NotSupportedException("Mehrfachbearbeitung nur für Bilder.");
            if (!isPanorama && !program.MatchesMultiple(inputs.Select(x => x.Path).ToArray()))
                throw new InvalidOperationException(
                    "Dieses Programm unterstützt nicht alle ausgewählten Bildformate.");

            var key = (isPanorama ? "panorama:stage:" : "external:multi-stage:") +
                Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, UiLanguage.T(isPanorama
                ? "Panorama erstellen" : "Mehrfachbearbeitung"), 100);
            SuspendSchedulerForExternalEdit();
            suspended = true;
            PanoramaSession session;
            try
            {
                session = await _scheduler.RunAsync(key,
                    SchedulerPriority.ManualAction, token =>
                    {
                        string[] tags = [];
                        double? lat = null, lon = null;
                        if (isPanorama)
                            (tags, lat, lon) = ReadPanoramaMetadata(imageIds, token);
                        return PanoramaTransaction.Begin(inputs, tags, lat, lon,
                            token, (completed, total) => _scheduler.ReportProgress(
                                key, UiLanguage.T(isPanorama
                                    ? "Panorama erstellen" : "Mehrfachbearbeitung"),
                                total > 0 ? (int)(100.0 * completed / total) : 100,
                                100, inputs[0].Path), isPanorama);
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
                StatusText.Text = UiLanguage.T(isPanorama
                    ? "Panorama erstellen" : "Mehrfachbearbeitung") + " · " +
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

    // A dismissed output chooser is not an implicit successful edit.
    // Let users explicitly cancel the whole session; stale local copies
    // are best-effort cleaned without touching any original or foreign export.
    private bool OfferCancelMultipleSession(PanoramaSession session)
    {
        var answer = MessageBox.Show(this,
            UiLanguage.T("Externe Mehrfachbearbeitung abbrechen? Originalbilder bleiben unverändert."),
            UiLanguage.T(session.IsPanorama ? "Panorama erstellen" : "Mehrfachbearbeitung"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        if (!ReferenceEquals(_panoramaSession, session)) return false;
        _panoramaSession = null;
        ResumeSchedulerAfterExternalEdit();
        PanoramaTransaction.Cleanup(session);
        StatusText.Text = UiLanguage.T("Externe Bearbeitung abgebrochen.");
        return true;
    }

    private async Task TryImportPanoramaAsync(PanoramaSession session)
    {
        if (!session.IsPanorama)
        {
            await TryImportMultipleAsync(session);
            return;
        }
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
                    Filter = UiLanguage.T("Bilder") +
                        "|*.jpg;*.jpeg;*.tif;*.tiff;*.png;*.bmp;*.webp;*.heic;*.heif;*.avif;*.jxl;*.psd|" +
                        UiLanguage.T("Alle Dateien") + "|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };
                try
                {
                    if (picker.ShowDialog(this) != true)
                    {
                        OfferCancelMultipleSession(session);
                        return;
                    }
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

    private async Task TryImportMultipleAsync(PanoramaSession session)
    {
        if (_panoramaImportRunning || !ReferenceEquals(_panoramaSession, session))
            return;
        _panoramaImportRunning = true;
        try
        {
            var outputs = await Task.Run(() =>
                PanoramaTransaction.FindMultipleResults(session));
            if (!ReferenceEquals(_panoramaSession, session)) return;
            if (outputs.Length == 0)
            {
                // Some editors export into a user-selected folder. Ask for
                // every output, but never assign files to inputs by list order.
                if (DateTime.UtcNow - _panoramaLastResultDialogUtc <
                    TimeSpan.FromSeconds(2)) return;
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = UiLanguage.T("Ergebnisse der Mehrfachbearbeitung auswählen"),
                    Filter = UiLanguage.T("Bilder") +
                        "|*.jpg;*.jpeg;*.tif;*.tiff;*.png;*.bmp;*.webp;*.heic;*.heif;*.avif;*.jxl;*.psd;*.dng|" +
                        UiLanguage.T("Alle Dateien") + "|*.*",
                    CheckFileExists = true,
                    Multiselect = true
                };
                try
                {
                    if (picker.ShowDialog(this) != true)
                    {
                        OfferCancelMultipleSession(session);
                        return;
                    }
                    outputs = picker.FileNames.Select(path =>
                        new ExternalBatchOutput(
                            PanoramaTransaction.MatchSourceIndex(session, path),
                            path)).ToArray();
                }
                finally { _panoramaLastResultDialogUtc = DateTime.UtcNow; }
            }
            var key = "external:multi-import:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, UiLanguage.T("Mehrfachbearbeitung"),
                Math.Max(1, outputs.Length));
            string[] saved;
            var catalogWarnings = new List<string>();
            try
            {
                saved = await _scheduler.RunAsync(key,
                    SchedulerPriority.ManualAction, token =>
                    {
                        // Exports outside PCasa's local session are read-only;
                        // stage them before metadata changes and publishing.
                        var staged = outputs.Select(output => output with
                        {
                            Path = Path.GetDirectoryName(output.Path)!.Equals(
                                    session.Directory, StringComparison.OrdinalIgnoreCase)
                                ? output.Path
                                : PanoramaTransaction.StageExternalResult(session,
                                    output.Path, token)
                        }).ToArray();
                        var files = PanoramaTransaction.ImportMultiple(
                            session, staged, token, (completed, total) =>
                                _scheduler.ReportProgress(key,
                                    UiLanguage.T("Mehrfachbearbeitung"),
                                    total == 0 ? 100 : (int)(completed * 100 / total),
                                    100, session.Sources[0]));
                        foreach (var file in files)
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                var root = FolderExclusionRules.FindOwner(file,
                                    _folderRepository.GetAll());
                                if (root is null)
                                    throw new IOException(
                                        "Ausgabeverzeichnis nicht im Katalog: " + file);
                                var indexed = _folderScanner.ScanFile(root, file,
                                    cancellationToken: token, publishCatalogChange: false,
                                    forceExifRefresh: true);
                                if (indexed.Failed > 0 || indexed.ImageFilesSeen == 0)
                                    throw new IOException(
                                        "Mehrfachergebnis gespeichert, aber nicht katalogisiert: " +
                                        file);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                // The files are already safely published.
                                // Do NOT retry the physical import and create
                                // extra -Kopie duplicates after a catalog error.
                                PCasaDiagnostics.Write("ExternalBatch.Catalog", ex);
                                catalogWarnings.Add(Path.GetFileName(file) + ": " +
                                    ex.Message);
                            }
                        }
                        return files;
                    }, workKind: SchedulerWorkKind.Catalog);
            }
            finally { EndVisibleManualOperation(key); }
            _panoramaSession = null;
            ResumeSchedulerAfterExternalEdit();
            PanoramaTransaction.Cleanup(session);
            await RefreshBrowseGalleryAsync(forceIndexReload: true);
            StatusText.Text = UiLanguage.T("Mehrfachbearbeitung") +
                ": " + saved.Length + " " + UiLanguage.T("Bilder gespeichert");
            if (catalogWarnings.Count > 0)
                PcasaDialog.Show(this,
                    string.Join(Environment.NewLine, catalogWarnings.Take(10)),
                    UiLanguage.T("Mehrfachbearbeitung"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _panoramaImportRunning = false; }
    }
}
