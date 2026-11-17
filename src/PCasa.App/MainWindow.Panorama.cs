using System.Diagnostics;
using System.IO;
using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private PanoramaSession? _panoramaSession;
    // Batch and panorama results use the same existing viewer footer as a
    // single external edit. No second result dialog or automatic import.
    private sealed class ExternalBatchReview(PanoramaSession session)
    {
        public PanoramaSession Session { get; } = session;
        public ExternalBatchOutput[] Outputs { get; set; } = [];
        public bool Busy { get; set; }
    }
    private ExternalBatchReview? _externalBatchReview;
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
            _externalBatchReview = new ExternalBatchReview(session);
            HookPanoramaActivation();
            UpdateEditControls();
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
                _externalBatchReview = null;
                PanoramaTransaction.Cleanup(session);
                UpdateEditControls();
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
        _externalBatchReview = null;
        ResumeSchedulerAfterExternalEdit();
        PanoramaTransaction.Cleanup(session);
        // Never leave a batch preview displayed under the first source's name.
        if (_viewerOpen && _viewerImageInfo?.Id == session.ImageIds[0])
            ShowViewerImage();
        UpdateEditControls();
        StatusText.Text = UiLanguage.T("Externe Bearbeitung abgebrochen.");
        return true;
    }

    private async Task PresentExternalBatchReviewAsync(
        PanoramaSession session, ExternalBatchOutput[] outputs)
    {
        if (!ReferenceEquals(_panoramaSession, session) ||
            _externalBatchReview is not { } review ||
            !ReferenceEquals(review.Session, session) || review.Busy)
            return;
        review.Outputs = outputs;
        // The same three buttons already used by single-image external
        // editing belong to the viewer. Open its first source, not a new
        // dialog. No file is committed until a footer choice is clicked.
        if (!_viewerOpen || _viewerImageInfo?.Id != session.ImageIds[0])
            BrowseSurface_ImageOpened(BrowseSurface,
                new PCasa.App.Controls.BrowseImageClickedEventArgs(
                    session.ImageIds[0], System.Windows.Input.ModifierKeys.None));
        StatusText.Text = UiLanguage.T(session.IsPanorama
            ? "Panorama erstellen" : "Mehrfachbearbeitung") + " · " +
            outputs.Length + " " + UiLanguage.T("Ergebnis bereit");
        UpdateEditControls();
        // A failed image preview must never prevent Save Copy or Cancel.
        if (outputs.Length == 0) return;
        var first = outputs[0].Path;
        if (RawImageSource.IsRawPath(first)) return;
        try
        {
            var size = GetViewerRequiredPixelSize();
            var bitmap = await Task.Run(() =>
            {
                var dimensions = EditableImageFormats.Dimensions(first);
                return PhotoViewerImageLoader.Load(
                    new PhotoViewerImageInfo(session.ImageIds[0], first,
                        Path.GetFileName(first), dimensions.Width, dimensions.Height),
                    size, CancellationToken.None);
            });
            if (!ReferenceEquals(_externalBatchReview, review)) return;
            _viewerDecodeCancellation?.Cancel();
            ++_viewerImageRequest;
            _photoEditSession = null;
            SetViewerBitmap(bitmap);
            ApplyViewerImageLayout();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalBatch.Preview", ex);
        }
    }

    private async Task TryImportPanoramaAsync(PanoramaSession session)
    {
        if (!session.IsPanorama)
        {
            await TryImportMultipleAsync(session);
            return;
        }
        if (_panoramaImportRunning || !ReferenceEquals(_panoramaSession, session) ||
            _externalBatchReview is { Outputs.Length: > 0 })
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
                    Multiselect = false,
                    InitialDirectory = session.Directory
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
            await PresentExternalBatchReviewAsync(session,
                [new ExternalBatchOutput(0, resultPath)]);
        }
        finally { _panoramaImportRunning = false; }
    }

    private async Task TryImportMultipleAsync(PanoramaSession session)
    {
        if (_panoramaImportRunning || !ReferenceEquals(_panoramaSession, session) ||
            _externalBatchReview is { Outputs.Length: > 0 })
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
                    Multiselect = true,
                    InitialDirectory = session.Directory
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
            await PresentExternalBatchReviewAsync(session, outputs);
        }
        finally { _panoramaImportRunning = false; }
    }

    private async Task<bool> CompleteExternalBatchReviewAsync(
        ExternalEditDecision decision)
    {
        var review = _externalBatchReview;
        if (review is null || review.Busy || _panoramaImportRunning)
            return false;
        var session = review.Session;
        if (!ReferenceEquals(_panoramaSession, session)) return false;
        if (decision != ExternalEditDecision.Cancel && review.Outputs.Length == 0)
            return false;
        if (decision == ExternalEditDecision.Save &&
            !PanoramaTransaction.CanSaveMultiple(session, review.Outputs))
            return false;

        review.Busy = true;
        _panoramaImportRunning = true;
        UpdateEditControls();
        try
        {
            if (decision == ExternalEditDecision.Cancel)
            {
                _externalBatchReview = null;
                _panoramaSession = null;
                ResumeSchedulerAfterExternalEdit();
                PanoramaTransaction.Cleanup(session);
                if (_viewerOpen && _viewerImageInfo?.Id == session.ImageIds[0])
                    ShowViewerImage();
                StatusText.Text = UiLanguage.T("Externe Bearbeitung abgebrochen.");
                UpdateEditControls();
                return true;
            }

            var label = decision == ExternalEditDecision.Save
                ? UiLanguage.T("Speichern") : UiLanguage.T("Als Kopie speichern");
            var key = "external:batch-commit:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(key, label, Math.Max(1, review.Outputs.Length));
            string[] saved;
            var catalogWarnings = new List<string>();
            try
            {
                saved = await _scheduler.RunAsync(key,
                    SchedulerPriority.ManualAction, token =>
                    {
                        var staged = review.Outputs.Select(output => output with
                        {
                            Path = Path.GetDirectoryName(Path.GetFullPath(output.Path))!
                                .Equals(session.Directory, StringComparison.OrdinalIgnoreCase)
                                ? output.Path
                                : PanoramaTransaction.StageExternalResult(
                                    session, output.Path, token)
                        }).ToArray();
                        string[] files;
                        if (session.IsPanorama)
                        {
                            var output = staged.Single();
                            files = [PanoramaTransaction.Import(session, output.Path,
                                token, (completed, total) =>
                                    _scheduler.ReportProgress(key, label,
                                        total == 0 ? 100 : (int)(completed * 100 / total),
                                        100, output.Path))];
                        }
                        else
                        {
                            files = PanoramaTransaction.ImportMultiple(session,
                                staged, token, (completed, total) =>
                                    _scheduler.ReportProgress(key, label,
                                        total == 0 ? 100 : (int)(completed * 100 / total),
                                        100, session.Sources[0]),
                                replaceOriginals: decision == ExternalEditDecision.Save);
                        }
                        // File publication is finished: a catalog failure must
                        // never cause a second physical import on retry.
                        foreach (var file in files)
                        {
                            try
                            {
                                token.ThrowIfCancellationRequested();
                                var root = FolderExclusionRules.FindOwner(file,
                                    _folderRepository.GetAll()) ??
                                    throw new IOException(
                                        "Ausgabeverzeichnis nicht im Katalog: " + file);
                                var indexed = _folderScanner.ScanFile(root, file,
                                    cancellationToken: token, publishCatalogChange: false,
                                    forceExifRefresh: true);
                                if (indexed.Failed > 0 || indexed.ImageFilesSeen == 0)
                                    throw new IOException(
                                        "Gespeichert, aber Katalogisierung fehlgeschlagen: " + file);
                                // The result's packed thumbnail belongs to this
                                // SAME P0 transaction, never a later P4 scan.
                                using var lookup = _database.OpenConnection();
                                using var command = lookup.CreateCommand();
                                command.CommandText =
                                    "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
                                command.Parameters.AddWithValue("$path", file);
                                if (command.ExecuteScalar() is { } found)
                                    new BrowseCacheBuilder(
                                        new BrowseCacheRepository(_database),
                                        new BrowseThumbnailProfileRepository(_database))
                                        .RebuildSingleImage(Convert.ToInt64(found), token);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                PCasaDiagnostics.Write("ExternalBatch.Catalog", ex);
                                catalogWarnings.Add(Path.GetFileName(file) + ": " +
                                    ex.Message);
                            }
                        }
                        return files;
                    }, workKind: SchedulerWorkKind.Catalog);
            }
            finally { EndVisibleManualOperation(key); }

            // A preview has the FIRST INPUT's identity until the result is
            // indexed. Keep edit tools disabled while switching to the real
            // panorama image ID; otherwise Crop could write the first source.
            if (session.IsPanorama && saved.Length == 1)
            {
                try
                {
                    await ActivateSavedCopyAsync(new EditWriteRecipe(
                        session.ImageIds[0], 0, session.Sources[0], [], saved[0]));
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Panorama.ActivateResult", ex);
                    catalogWarnings.Add(Path.GetFileName(saved[0]) + ": " +
                        UiLanguage.T("Ergebnis gespeichert, aber Viewerwechsel fehlgeschlagen: ") +
                        ex.Message);
                }
            }
            _externalBatchReview = null;
            _panoramaSession = null;
            ResumeSchedulerAfterExternalEdit();
            PanoramaTransaction.Cleanup(session);
            UpdateEditControls();
            // If the activation failed, discard the misleading preview.
            if (session.IsPanorama && _viewerOpen &&
                _viewerImageInfo?.Id == session.ImageIds[0])
                ShowViewerImage();
            await RefreshBrowseGalleryAsync(forceIndexReload: true);
            StatusText.Text = UiLanguage.T(session.IsPanorama
                ? "Panorama erstellen" : "Mehrfachbearbeitung") +
                ": " + saved.Length + " " + UiLanguage.T("Bilder gespeichert");
            if (catalogWarnings.Count > 0)
                PcasaDialog.Show(this,
                    string.Join(Environment.NewLine, catalogWarnings.Take(10)),
                    UiLanguage.T("Externe Bearbeitung"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalBatch.Commit", ex);
            PcasaDialog.Show(this, ex.Message, UiLanguage.T("Externe Bearbeitung"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            review.Busy = false;
            _panoramaImportRunning = false;
            if (!Dispatcher.HasShutdownStarted) UpdateEditControls();
        }
    }
}
