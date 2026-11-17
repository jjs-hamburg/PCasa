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
            await PresentExternalBatchReviewAsync(session,
                [new ExternalBatchOutput(0, resultPath)]);
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
            await PresentExternalBatchReviewAsync(session, outputs);
        }
        finally { _panoramaImportRunning = false; }
    }
}
