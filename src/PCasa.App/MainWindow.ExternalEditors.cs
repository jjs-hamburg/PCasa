using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using System.ComponentModel;

using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private ExternalEditorSettings _externalEditors = ExternalEditorSettings.Load();
    private readonly Dictionary<string, ImageSource> _externalEditorIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private long _externalEditorDisplayedImageId = -1;
    private string? _externalEditorDisplayedFileName;

    // The cards match the exact geometry of Crop / Straighten tool buttons.
    // Only applications configured for this image format are shown.
    private void RefreshExternalEditorToolButtons(long imageId, string fileName)
    {
        _externalEditorDisplayedImageId = imageId;
        _externalEditorDisplayedFileName = fileName;
        if (!_cropToolsReady) return;
        ExternalEditorToolsList.Children.Clear();

        foreach (var configured in _externalEditors.ForFile(fileName))
        {
            var editor = configured; // Capture immutable program per card.
            var button = new Button
            {
                Width = 64,
                Height = 43,
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                ToolTip = editor.Name,
                Content = CreateExternalEditorIcon(editor.ExecutablePath)
            };
            button.Click += (_, _) => _ = StartExternalEditorAsync(imageId, editor);
            var caption = new TextBlock
            {
                Text = editor.Name,
                Width = 76,
                Height = 26,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = editor.Name
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
            var card = new StackPanel
            {
                Width = 80,
                Margin = new Thickness(1, 1, 1, 2)
            };
            card.Children.Add(button);
            card.Children.Add(caption);
            ExternalEditorToolsList.Children.Add(card);
        }
    }

    private UIElement CreateExternalEditorIcon(string executable)
    {
        // Every editor icon is captured at configuration (or once during
        // migration for an existing profile). Viewer navigation must NEVER
        // touch the executable; only a 32px PNG from PCasa's user profile.
        if (!_externalEditorIconCache.TryGetValue(executable, out var image))
        {
            image = ExternalEditorIconStore.Load(executable)!;
            if (image is not null)
                _externalEditorIconCache[executable] = image;
        }
        if (image is not null)
            return new Image { Source = image, Width = 30, Height = 30,
                Stretch = Stretch.Uniform, IsHitTestVisible = false };
        var fallback = new TextBlock {
            Text = "↗", FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        fallback.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        return fallback;
    }

    private async Task WarmExternalEditorIconsAsync()
    {
        // One-time migration of programs configured before Alpha.42.
        // Runs off the UI thread, never at every image/Editor dialog opening.
        var entries = _externalEditors.Programs.Select(p => p.ExecutablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await Task.Run(() =>
        {
            foreach (var executable in entries)
            {
                try { ExternalEditorIconStore.EnsureCaptured(executable); }
                catch (Exception ex)
                { PCasaDiagnostics.Write("ExternalEditor.IconMigration", ex); }
            }
        });
        if (Dispatcher.HasShutdownStarted) return;
        _externalEditorIconCache.Clear();
        if (_viewerOpen && _externalEditorDisplayedImageId > 0 &&
            _externalEditorDisplayedFileName is { } file)
            RefreshExternalEditorToolButtons(_externalEditorDisplayedImageId, file);
    }

    private string? CachedExternalEditorSource(long id)
    {
        if (_dragPublishedImages.TryGetValue(id, out var entry))
        {
            if (_dragPublishedFolders.TryGetValue(entry.PhotoFolderId, out var folder))
                return Path.Combine(folder, entry.FileName);
            return entry.FileName; // Extension is enough to filter programs.
        }
        return _dragSelectionPathCache.TryGetValue(id, out var path)
            ? path : null;
    }

    // Works for a nested gallery context menu AND for the viewer dropdown.
    // No SQLite or SMB calls when opening the menu.
    private void PopulateExternalEditorMenu(ItemsControl menu, long id,
        string? sourceName)
    {
        if (sourceName is null) return;
        IReadOnlyList<ExternalEditorProgram> programs = sourceName is null
            ? [] : _externalEditors.ForFile(sourceName);
        foreach (var program in programs)
        {
            var current = program;
            var item = new MenuItem { Header = current.Name };
            item.Click += (_, _) => _ = StartExternalEditorAsync(id, current);
            menu.Items.Add(item);
        }
        // Configuring arbitrary shell associations under Windows Open With
        // has no reliably trackable exit. It cannot support PCasa's 3-button
        // rollback decision. Offer only explicitly configured executables;
        // application configuration lives under Options.
    }

    private void ExternalEditorsOptions_Click(object sender, RoutedEventArgs e)
        => ConfigureExternalEditors();

    private void ConfigureExternalEditors()
    {
        var dialog = new ExternalEditorsWindow(_externalEditors) { Owner = this };
        if (dialog.ShowDialog() == true &&
            dialog.SavedSettings is { } saved)
        {
            _externalEditors = saved;
            _externalEditorIconCache.Clear();
            if (_externalEditorDisplayedImageId > 0 &&
                _externalEditorDisplayedFileName is { } fileName)
                RefreshExternalEditorToolButtons(_externalEditorDisplayedImageId,
                    fileName);
        }
    }

    private sealed class ExternalEditReview(ExternalEditSnapshot snapshot,
        long imageId, string name, bool video)
    {
        public ExternalEditSnapshot Snapshot { get; } = snapshot;
        public long ImageId { get; } = imageId;
        public string Name { get; } = name;
        public bool IsVideo { get; } = video;
        public string? ResultPath { get; set; }
        public DateTime ResultWriteUtc { get; set; }
        public long ResultSize { get; set; }
        public bool Ready { get; set; }
        public bool Busy { get; set; }
        // True ONLY after the actual external-result pixels are displayed.
        // A failed preview must never make tools edit the old source image.
        public bool PreviewReady { get; set; }
        // Still-image sessions have one cursor spanning external and internal steps.
        public bool Combined { get; set; }
        public bool AwaitingReturn { get; set; }
        // The shared full-resolution writer stages these steps only ONCE.
        // Retrying a failed outer file commit must not crop twice.
        public bool InternalEditsStaged { get; set; }
        public string? InternalResultPath { get; set; }
    }

    private ExternalEditReview? _externalEditReview;

    private bool CanEditPendingExternalResult(long imageId)
        => _externalEditReview is
            { Ready: true, Busy: false, PreviewReady: true,
              InternalEditsStaged: false, ResultPath: string path } review &&
           review.ImageId == imageId &&
           EditableImageFormats.Supports(path) ||
           _externalEditReview is { Combined: true, Ready: true, Busy: false,
               PreviewReady: true, AwaitingReturn: false } combined && combined.ImageId == imageId &&
           EditableImageFormats.Supports(_photoEditSession?.LatestExternalPath ?? combined.Snapshot.OriginalPath);
    private IDisposable? _externalEditSchedulerLease;
    private bool _externalEditLaunchPending;

    private void SuspendSchedulerForExternalEdit()
    {
        if (_externalEditSchedulerLease is not null) return;
        _externalEditSchedulerLease = _scheduler.BeginExternalEditInteraction();
        PauseBackgroundForExternalEdit();
    }

    private void ResumeSchedulerAfterExternalEdit()
    {
        var lease = _externalEditSchedulerLease;
        _externalEditSchedulerLease = null;
        lease?.Dispose();
        ResumeBackgroundAfterExternalEdit();
    }

    // Call at startup before the background monitor is allowed to enqueue
    // more ingestion work. A persisted single session owns the same lease
    // after a crash; it must never be accidentally overwritten.
    private void RecoverExternalEditSchedulerLease()
    {
        try
        {
            if (ExternalEditTransaction.FindPending() is not null)
                SuspendSchedulerForExternalEdit();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalEdit.Recovery", ex);
            // Treat an unreadable manifest as busy, never overwrite/delete.
            SuspendSchedulerForExternalEdit();
        }
    }

    // One review is the sole authoritative UI session. Its lifetime is NOT
    // inferred from Process.Exited, since launchers can hand off to another
    // process or to an already-running instance.
    private bool _externalReviewActivationHooked;
    private bool _externalReviewRefreshing;
    private ExternalVideoReviewWindow? _externalVideoReviewDialog;

    private void EnsureExternalReviewActivationHook()
    {
        if (_externalReviewActivationHooked) return;
        _externalReviewActivationHooked = true;
        Activated += async (_, _) =>
        {
            if (_externalEditReview is { Ready: true, Busy: false } review)
            {
                try { await PresentExternalResultAsync(review); }
                catch (Exception ex)
                {
                    // Never let an async-void focus callback crash PCasa.
                    PCasaDiagnostics.Write("ExternalEdit.Activation", ex);
                    StatusText.Text = UiLanguage.T("Externe Bearbeitung: ") + ex.Message;
                }
            }
        };
    }

    private async Task StartExternalEditorAsync(long id, ExternalEditorProgram? editor)
    {
        if (_externalEditLaunchPending) return;
        _externalEditLaunchPending = true;
        try
        {
            var previous = _externalEditReview;
            if (previous is { Ready: true, Busy: false, ResultPath: null })
            {
                // An export that arrived since activation belongs to the
                // existing cursor. Present it before allowing another launch.
                var output = await Task.Run(() =>
                    ExternalEditTransaction.DetectResult(previous.Snapshot));
                if (output is not null)
                {
                    await PresentExternalResultAsync(previous);
                    return;
                }
                if (!previous.Combined || _photoEditSession?.HasChanges != true)
                {
                    if (!await CompleteExternalReviewAsync(ExternalEditDecision.Cancel)) return;
                    previous = null;
                }
            }
            if ((previous is not null && (!previous.Combined || previous.ImageId != id ||
                    previous.Busy || !previous.PreviewReady || previous.AwaitingReturn)) ||
                _externalReviewRefreshing || _panoramaSession is not null || HasPendingExternalWrites())
                throw new InvalidOperationException(UiLanguage.T(
                    "Bitte die laufende externe Bearbeitung zuerst abschließen."));
            var source = previous?.Snapshot.OriginalPath ??
                (await SelectedGalleryPathsAsync([id])).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(source)) return;
            var edit = _photoEditSession?.ImageId == id ? _photoEditSession : null;
            if (_viewerOpen && (!_viewerHasOriginal || _viewerImageInfo?.Id != id)) return;
            // Only accepted steps are handed off. The user can Apply or
            // Cancel the active adjustment before choosing another editor.
            if (GetPendingLiveEditStep() is not null) return;
            if (editor is null || !editor.Matches(edit?.LatestExternalPath ?? source))
                throw new InvalidOperationException(UiLanguage.T(
                    "Dieses Programm ist für das Dateiformat nicht konfiguriert."));
            if (_cropSavePending || _pendingEditImageIds.Contains(id) ||
                await HasUnfinishedImageEditAsync(id))
                throw new InvalidOperationException(UiLanguage.T(
                    "Für dieses Bild ist ein Speicherauftrag noch nicht abgeschlossen. Bitte im Taskmanager prüfen."));
            if (_cropActive) EndCrop();
            if (_straightenActive) LeaveStraighten();
            if (_brightnessActive) LeaveBrightness();
            if (_advancedToneActive) LeaveAdvancedTone();
            var steps = edit?.AppliedSteps.ToArray() ?? [];
            // Freeze tools while the immutable input is prepared and launched.
            if (previous is not null) previous.Busy = true;
            UpdateEditControls();
            EnsureExternalReviewActivationHook();
            StatusText.Text = UiLanguage.T(
                "Externe Bearbeitung: automatische Einlesevorgänge anhalten …");
            SuspendSchedulerForExternalEdit();
            var pending = await Task.Run(() => ExternalEditTransaction.FindPending(source));
            if (pending is not null && previous is null)
            {
                var old = new ExternalEditReview(pending, id, editor.Name,
                    VideoFormats.IsVideo(source)) { Ready = true, Combined = !VideoFormats.IsVideo(source) };
                _externalEditReview = old;
                await PresentExternalResultAsync(old);
                return;
            }

            var stageKey = "external:stage:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(stageKey,
                UiLanguage.T("Lokale Arbeitskopie erstellen …"), 100);
            ExternalEditSnapshot snapshot;
            try
            {
                snapshot = await _scheduler.RunAsync(stageKey,
                    SchedulerPriority.ManualAction,
                    token => previous is not null
                        ? ExternalEditTransaction.PrepareNextInput(previous.Snapshot, steps,
                            editor.ExchangeDirectory, token)
                        : ExternalEditTransaction.Begin(source, token,
                        (completed, total) => _scheduler.ReportProgress(stageKey,
                            UiLanguage.T("Lokale Arbeitskopie erstellen …"),
                            total <= 0 ? 100 : (int)(completed * 100 / total),
                            100, source), editor.ExchangeDirectory, steps),
                    workKind: SchedulerWorkKind.Catalog);
            }
            finally { EndVisibleManualOperation(stageKey); }
            if (Dispatcher.HasShutdownStarted) return; // Manifest survives crash.

            var review = new ExternalEditReview(snapshot, id, editor.Name,
                VideoFormats.IsVideo(source)) { Ready = true,
                    Combined = !VideoFormats.IsVideo(source), AwaitingReturn = true };
            // A fresh external session always supersedes the earlier session's
            // before/after comparison for this viewer identity.
            if (_latestExternalComparison?.ImageId == id)
                _latestExternalComparison = null;
            if (_externalComparisonUnavailableImageId == id)
                _externalComparisonUnavailableImageId = null;
            _externalEditReview = review;
            UpdateEditControls();

            try
            {
                if (!File.Exists(editor.ExecutablePath))
                    throw new FileNotFoundException(UiLanguage.T(
                        "Programmdatei nicht gefunden: ") + editor.ExecutablePath);
                // The EXE receives ONLY the local fixed working image.
                // Never use the gallery folder as a process work directory.
                var launch = new ProcessStartInfo
                {
                    FileName = editor.ExecutablePath,
                    WorkingDirectory = snapshot.DirectoryPath,
                    UseShellExecute = false
                };
                launch.ArgumentList.Add(snapshot.BackupPath);
                await Task.Run(() =>
                {
                    using var launched = Process.Start(launch)
                        ?? throw new IOException(UiLanguage.T(
                            "Das externe Programm konnte nicht gestartet werden."));
                    // Deliberately NO WaitForExit: Topaz may use a launcher,
                    // a secondary process or a pre-existing instance.
                });
                if (Dispatcher.HasShutdownStarted) return;
                StatusText.Text = UiLanguage.T("Externe Bearbeitung: warte auf Ergebnis oder Entscheidung · ") +
                    Path.GetFileName(source);
                // The application's main process may keep running or be a
                // launcher. PCasa makes NO exit-time assumption. Returning
                // focus to PCasa will refresh the locally exported result.
                UpdateEditControls();
            }
            catch
            {
                // No external process was successfully launched: Cancel
                // removes only the local work directory; source is untouched.
                if (ReferenceEquals(_externalEditReview, review))
                {
                    if (previous is not null || steps.Length > 0)
                    {
                        // A failed launch may not discard earlier accepted edits.
                        review.AwaitingReturn = false;
                        review.PreviewReady = true;
                    }
                    else
                    {
                        await Task.Run(() => ExternalEditTransaction.Commit(
                            snapshot, null, ExternalEditDecision.Cancel));
                        _externalEditReview = null;
                        ResumeSchedulerAfterExternalEdit();
                    }
                    UpdateEditControls();
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            // No persisted transaction => no external editor still owns the
            // global scheduling pause. A persisted one is deliberately kept.
            try
            {
                if (_externalEditReview is null &&
                    ExternalEditTransaction.FindPending() is null)
                    ResumeSchedulerAfterExternalEdit();
            }
            catch (Exception recoveryError)
            {
                PCasaDiagnostics.Write("ExternalEdit.RecoveryCheck", recoveryError);
            }
            ShowFileActionError(ex);
        }
        finally
        {
            _externalEditLaunchPending = false;
            if (_externalEditReview is { } current && !HasPendingExternalWrites()) current.Busy = false;
            if (_cropToolsReady && !Dispatcher.HasShutdownStarted) UpdateEditControls();
        }
    }

    private bool _unchangedExternalReleasePending;

    private async Task ReleaseUnchangedExternalEditorAsync(
        ExternalEditReview review, Action continueAfterRelease)
    {
        if (review.Combined && _photoEditSession?.HasChanges == true) return;
        if (_unchangedExternalReleasePending ||
            !ReferenceEquals(_externalEditReview, review) ||
            !review.Ready || review.Busy || review.ResultPath is not null)
            return;

        _unchangedExternalReleasePending = true;
        try
        {
            // A focus change may race a late external export. Never release
            // an actual result without giving Save/Copy/Cancel priority.
            var output = await Task.Run(() =>
                ExternalEditTransaction.DetectResult(review.Snapshot));
            if (!ReferenceEquals(_externalEditReview, review)) return;
            if (output is not null)
            {
                await PresentExternalResultAsync(review);
                return;
            }

            if (await CompleteExternalReviewAsync(ExternalEditDecision.Cancel))
            {
                PCasaDiagnostics.WriteMessage("ExternalEdit.UnchangedRelease",
                    "Unchanged session released by viewer action.");
                // Resume the actual original click/key action, but only while
                // its source photo is still the active viewer identity.
                // Cancel starts a new asynchronous original decode and
                // clears _viewerImageInfo before this continuation resumes.
                // The sequence, not transient metadata, owns viewer identity.
                if (_viewerOpen && _viewerSequence is not null &&
                    _viewerCurrentIndex >= 0 &&
                    _viewerCurrentIndex < _viewerSequence.Count &&
                    _viewerSequence.At(_viewerCurrentIndex).ImageId == review.ImageId)
                    continueAfterRelease();
            }
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalEdit.UnchangedRelease", ex);
            StatusText.Text = UiLanguage.T("Externe Bearbeitung: ") + ex.Message;
        }
        finally
        {
            _unchangedExternalReleasePending = false;
        }
    }

    private async Task PresentExternalResultAsync(ExternalEditReview review)
    {
        if (!ReferenceEquals(_externalEditReview, review) ||
            review.Busy || _externalReviewRefreshing) return;
        _externalReviewRefreshing = true;
        // session.json, not the external process lifetime, owns the decision.
        // Resume a durably chosen Save/Copy/Cancel rather than offering an
        // invalid new Cancel after the commit has already started.
        ExternalEditSnapshot? persisted;
        try
        {
            persisted = await Task.Run(() =>
                ExternalEditTransaction.FindPending(review.Snapshot.OriginalPath));
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExternalEdit.Manifest", ex);
            StatusText.Text = UiLanguage.T("Externe Bearbeitung: ") + ex.Message;
            _externalReviewRefreshing = false;
            return;
        }
        if (!ReferenceEquals(_externalEditReview, review) || review.Busy)
        {
            _externalReviewRefreshing = false;
            return;
        }
        if (persisted is null)
        {
            _externalEditReview = null;
            ResumeSchedulerAfterExternalEdit();
            UpdateEditControls();
            _externalReviewRefreshing = false;
            return;
        }
        if (persisted.Finalizing is { } pendingDecision)
        {
            _externalReviewRefreshing = false;
            await CompleteExternalReviewAsync(pendingDecision);
            return;
        }
        try
        {
            string? result = null;
            try
            {
                result = await Task.Run(() =>
                    ExternalEditTransaction.DetectResult(review.Snapshot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                // The editor may still have its output open. Cancel remains
                // available and the next activation will recheck.
                PCasaDiagnostics.Write("ExternalEdit.Output", ex);
                result = review.ResultPath;
            }
            if (!ReferenceEquals(_externalEditReview, review)) return;
            var metadata = result is not null ? await Task.Run(() =>
            {
                var f = new FileInfo(result);
                return (Length: f.Length, Time: f.LastWriteTimeUtc);
            }) : (Length: 0L, Time: DateTime.MinValue);
            // An active tool owns its live preview. Delay newly arriving exports
            // until that tool has been accepted or canceled.
            if (review.Combined && GetPendingLiveEditStep() is not null) return;
            review.AwaitingReturn = false;
            var changed = !string.Equals(result, review.ResultPath,
                    StringComparison.OrdinalIgnoreCase) ||
                (result is not null && (metadata.Length != review.ResultSize ||
                    metadata.Time != review.ResultWriteUtc));
            if (changed)
            {
                review.PreviewReady = false;
                // A fresh Topaz export supersedes any local edits made to
                // the previous result. A committed external session never
                // shares the old edit-buffer identity with a new result.
                if (!review.Combined && _photoEditSession?.ImageId == review.ImageId)
                    _photoEditSession = null;
                // An editor re-export invalidates the private internal copy;
                // never import old straighten/crop steps over newer pixels.
                if (!review.Busy)
                {
                    review.InternalResultPath = null;
                    review.InternalEditsStaged = false;
                }
            }
            review.ResultPath = result;
            review.ResultSize = metadata.Length;
            review.ResultWriteUtc = metadata.Time;
            review.Ready = true;
            var previewFailed = false;

            if (review.IsVideo)
            {
                if (_externalVideoReviewDialog is null ||
                    !_externalVideoReviewDialog.IsVisible)
                {
                    var dialog = new ExternalVideoReviewWindow(
                        Path.GetFileName(review.Snapshot.OriginalPath),
                        choice => CompleteExternalReviewAsync(choice), result is not null)
                    { Owner = this };
                    _externalVideoReviewDialog = dialog;
                    dialog.Activated += async (_, _) =>
                    {
                        if (ReferenceEquals(_externalEditReview, review) &&
                            !_externalReviewRefreshing)
                            await PresentExternalResultAsync(review);
                    };
                    dialog.Closed += (_, _) =>
                    {
                        if (ReferenceEquals(_externalVideoReviewDialog, dialog))
                            _externalVideoReviewDialog = null;
                    };
                    dialog.Show();
                }
                else _externalVideoReviewDialog.SetHasResult(result is not null);
            }
            else
            {
                if (!_viewerOpen)
                    BrowseSurface_ImageOpened(BrowseSurface,
                        new PCasa.App.Controls.BrowseImageClickedEventArgs(
                            review.ImageId, System.Windows.Input.ModifierKeys.None));
                if ((changed || !review.PreviewReady) && result is not null && _viewerOpen &&
                    _viewerSequence is not null && _viewerCurrentIndex >= 0 &&
                    _viewerSequence.At(_viewerCurrentIndex).ImageId == review.ImageId)
                {
                    try
                    {
                        var size = GetViewerRequiredPixelSize();
                        var historyPath = review.Combined ? await Task.Run(() =>
                            ExternalEditTransaction.CaptureHistoryResult(review.Snapshot, result,
                                metadata.Length, metadata.Time)) : result;
                        var bitmap = await Task.Run(() =>
                        {
                            var (width, height) = EditableImageFormats.Dimensions(historyPath);
                            return PhotoViewerImageLoader.Load(
                                new PhotoViewerImageInfo(review.ImageId, historyPath,
                                    Path.GetFileName(historyPath), width, height),
                                size, CancellationToken.None);
                        });
                        BitmapSource? originalPixels = null;
                        if (review.Combined && _photoEditSession?.ImageId != review.ImageId)
                            originalPixels = await Task.Run(() =>
                            {
                                var originalPath = review.Snapshot.BeforePath;
                                var dimensions = EditableImageFormats.Dimensions(originalPath);
                                return PhotoViewerImageLoader.Load(new PhotoViewerImageInfo(review.ImageId,
                                    originalPath, Path.GetFileName(originalPath), dimensions.Width,
                                    dimensions.Height), size, CancellationToken.None);
                            });
                        var originalInfo = _viewerImageInfo ?? await Task.Run(() =>
                            new PhotoViewerImageRepository(_database).Get(review.ImageId));
                        if (!ReferenceEquals(_externalEditReview, review) || !_viewerOpen ||
                            _viewerSequence is null || _viewerCurrentIndex < 0 ||
                            _viewerSequence.At(_viewerCurrentIndex).ImageId != review.ImageId) return;
                        _viewerDecodeCancellation?.Cancel();
                        ++_viewerImageRequest;
                        _viewerPendingOriginalImageId = null;
                        _viewerPendingOriginalPixels = 0;
                        _viewerImageInfo = originalInfo;
                        if (review.Combined)
                        {
                            if (_photoEditSession?.ImageId != review.ImageId)
                                _photoEditSession = new PhotoEditSession(review.ImageId, originalPixels!);
                            _photoEditSession.Apply(new PhotoEditStep("external", ExternalPath: historyPath,
                                ExternalName: review.Name));
                        }
                        else _photoEditSession = null;
                        ResetEditComparison();
                        SetViewerBitmap(bitmap);
                        _viewerHasOriginal = true;
                        review.PreviewReady = true;
                        SetViewerQualityMessage(null);
                        ApplyViewerImageLayout();
                    }
                    catch (Exception ex)
                    {
                        // A decoder/preview failure is NOT evidence that the
                        // externally rendered file is missing or unsaveable.
                        // Preserve the detected result and the Save/Save Copy
                        // actions; commit will validate the file independently.
                        // Cancel remains available until finalization begins.
                        PCasaDiagnostics.Write("ExternalEdit.Preview", ex);
                        previewFailed = true;
                    }
                }
            }
            if (review.Combined && result is null && _photoEditSession?.ImageId == review.ImageId)
                review.PreviewReady = true;
            StatusText.Text = previewFailed
                ? UiLanguage.T("Vorschau nicht verfügbar; externes Ergebnis kann gespeichert werden.") +
                    " · " + Path.GetFileName(result)
                : result is null
                ? UiLanguage.T("Externe Bearbeitung: warte auf Ergebnis oder Abbrechen · ") +
                    Path.GetFileName(review.Snapshot.OriginalPath)
                : UiLanguage.T("Externes Ergebnis erkannt: Speichern, Kopie oder Abbrechen · ") +
                    Path.GetFileName(result);
            UpdateEditControls();
        }
        catch (Exception ex)
        {
            // Alpha.33 could remove session.json before a locked photo.jpg.
            // The completed decision must not trap the viewer or crash WPF.
            PCasaDiagnostics.Write("ExternalEdit.Review", ex);
            bool missing = false;
            try { missing = ExternalEditTransaction.FindPending() is null; }
            catch (Exception pendingError)
            { PCasaDiagnostics.Write("ExternalEdit.PendingState", pendingError); }
            if (missing && ReferenceEquals(_externalEditReview, review))
            {
                _externalEditReview = null;
                ResumeSchedulerAfterExternalEdit();
                UpdateEditControls();
                StatusText.Text = UiLanguage.T(
                    "Externe Sitzung bereits abgeschlossen; Galerie wieder verfügbar.");
            }
            else
                StatusText.Text = UiLanguage.T("Externe Bearbeitung: ") + ex.Message;
        }
        finally { _externalReviewRefreshing = false; }
    }

    private async Task<bool> CompleteExternalReviewAsync(
        ExternalEditDecision decision, bool returnToGalleryAfterCancel = false)
    {
        var review = _externalEditReview;
        if (review is null || !review.Ready || review.Busy || HasPendingExternalWrites()) return false;
        if (review.Combined && decision != ExternalEditDecision.Cancel &&
            (!review.PreviewReady || review.AwaitingReturn || _externalReviewRefreshing)) return false;
        // A crop/rotation/tonal adjustment to Topaz's displayed result
        // belongs to the SAME external Save/Copy/Cancel decision, not to a
        // second save request against the untouched original.
        if (decision != ExternalEditDecision.Cancel &&
            CanEditPendingExternalResult(review.ImageId) &&
            (_cropActive || _straightenActive ||
             _brightnessActive || _advancedToneActive))
        {
            if (_cropActive && GetPendingLiveEditStep() is null)
                EndCrop();
            else
                ApplyEditTool();
        }
        PhotoEditStep[] internalSteps = decision == ExternalEditDecision.Cancel ||
            _photoEditSession?.ImageId != review.ImageId
            ? [] : _photoEditSession.AppliedSteps.ToArray();
        if (review.Combined && decision != ExternalEditDecision.Cancel && internalSteps.Length == 0) return false;
        review.Busy = true;
        _cropSavePending = review.Combined && decision != ExternalEditDecision.Cancel;
        UpdateEditControls();
        try
        {
            // Capture the two EXACT session images while both still exist.
            // Commit removes its isolated workspace; a post-commit lookup of
            // the most recent Undo snapshot can select a different edit.
            // Preview failures must never block Save or Cancel.
            // Before/After exists ONLY for a pending editing decision.
            // Never retain a completed comparison as a reusable gallery or
            // Restore source. The active preview captured it on demand.
            // (The previous Alpha.41 post-Save snapshot is intentionally gone.)
            // Re-evaluate the local working folder at commit time:
            // an export may have finished AFTER the last window activation.
            var saveKey = "external:commit:" + Guid.NewGuid().ToString("N");
            BeginVisibleManualOperation(saveKey,
                decision == ExternalEditDecision.Cancel
                    ? UiLanguage.T("Abbrechen")
                    : decision == ExternalEditDecision.SaveCopy
                        ? UiLanguage.T("Als Kopie speichern")
                        : UiLanguage.T("Speichern"), 100);
            string? committed;
            try
            {
                if (decision == ExternalEditDecision.Cancel)
                {
                    committed = await _scheduler.RunAsync(saveKey,
                        SchedulerPriority.ManualAction, token =>
                            ExternalEditTransaction.Commit(review.Snapshot, null, decision, token),
                        workKind: SchedulerWorkKind.Catalog);
                }
                else
                {
                    ExternalCommitRecipe request;
                    if (review.Combined)
                    {
                        // Resolve the current cursor into ONE private full-resolution
                        // result before the durable final-save admission. No history
                        // file is required by the accepted recipe or its restart.
                        request = await _scheduler.RunAsync(saveKey + ":prepare",
                            SchedulerPriority.ManualAction, token =>
                            {
                                var snapshot = ExternalEditTransaction.FindPending(review.Snapshot.OriginalPath)
                                    ?? throw new IOException("External session is missing.");
                                var directory = Path.Combine(snapshot.DirectoryPath, ".pcasa-internal");
                                Directory.CreateDirectory(directory);
                                foreach (var old in Directory.EnumerateFiles(directory, "result.*")) File.Delete(old);
                                var staged = JpegCropWriter.MaterializeWorkingImage(snapshot.BeforePath,
                                    directory, "result", internalSteps, token);
                                var info = new FileInfo(staged);
                                return new ExternalCommitRecipe(review.ImageId, snapshot, decision,
                                    staged, info.Length, info.LastWriteTimeUtc, [], StagedPath: staged);
                            }, workKind: SchedulerWorkKind.Catalog);
                    }
                    else
                    {
                        var result = review.InternalResultPath ?? review.ResultPath
                            ?? throw new IOException("Das externe Programm hat kein Ergebnis gespeichert.");
                        request = new ExternalCommitRecipe(review.ImageId, review.Snapshot,
                            decision, result, review.ResultSize, review.ResultWriteUtc,
                            review.InternalEditsStaged ? [] : internalSteps);
                    }
                    var accepted = _scheduler.EnqueueDurableRecipeAsync(saveKey,
                        ExternalCommitType, System.Text.Json.JsonSerializer.Serialize(request),
                        SchedulerPriority.ManualAction);
                    _cropSavePending = false;
                    if (_closeAfterEditAdmission)
                    {
                        _closeAfterEditAdmission = false;
                        Dispatcher.BeginInvoke(new Action(Close));
                    }
                    await accepted;
                    if (_shutdownStarted || Dispatcher.HasShutdownStarted) return true;
                    committed = ReadWriteRecipe<ExternalCommitRecipe>(saveKey).Target;
                }
            }
            finally { EndVisibleManualOperation(saveKey); }
            // The file transaction is now complete, even if a subsequent
            // catalog refresh fails. It must not be submitted a second time.
            _externalEditReview = null;
            _photoEditSession = null;
            if (_cropActive) EndCrop();
            if (_straightenActive) LeaveStraighten();
            if (_brightnessActive) LeaveBrightness();
            if (_advancedToneActive) LeaveAdvancedTone();
            _photoEditSession = null;
            _latestExternalComparison = null;
            _externalComparisonUnavailableImageId = null;
            ResetEditComparison();
            _externalVideoReviewDialog?.Close();
            _externalVideoReviewDialog = null;
            UpdateEditControls();

            try
            {
                await RefreshExternalEditCatalogAsync(review, committed,
                    alreadyIndexed: decision != ExternalEditDecision.Cancel);
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("ExternalEdit.CatalogRefresh", ex);
                PcasaDialog.Show(this, UiLanguage.T(
                    "Die Dateientscheidung wurde gespeichert, aber die Galerieaktualisierung ist fehlgeschlagen: ") +
                    ex.Message, UiLanguage.T("Externe Bearbeitung"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // Once Save/Copy/Cancel has committed, internal tools belong
            // to the current (possibly newly activated) image again.
            // Do not leave them disabled from the completed Topaz review.
            ResumeSchedulerAfterExternalEdit();
            if (_viewerOpen) UpdateEditControls();
            if (decision == ExternalEditDecision.Cancel &&
                returnToGalleryAfterCancel && _viewerOpen &&
                _viewerSequence is { } activeSequence &&
                _viewerCurrentIndex >= 0 &&
                _viewerCurrentIndex < activeSequence.Count &&
                activeSequence.At(_viewerCurrentIndex).ImageId == review.ImageId)
                ClosePhotoViewer();
            var cleanupPending = ExternalEditTransaction.PendingCleanupCount();
            StatusText.Text = (decision switch
            {
                ExternalEditDecision.Cancel => UiLanguage.T("Externe Bearbeitung abgebrochen."),
                ExternalEditDecision.SaveCopy => UiLanguage.T("Kopie gespeichert: ") +
                    Path.GetFileName(committed),
                _ => UiLanguage.T("Externe Bearbeitung gespeichert.")
            }) + (cleanupPending == 0 ? string.Empty :
                UiLanguage.T(" · Temporäre Bereinigung ausstehend: ") + cleanupPending);
            _scheduler.NotifyTaskViewChanged();
            return true;
        }
        catch (OperationCanceledException) when (_shutdownStarted || Dispatcher.HasShutdownStarted) { return true; }
        catch (Exception ex)
        {
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return false;
            PCasaDiagnostics.Write("ExternalEdit.Commit", ex);
            PcasaDialog.Show(this, ex.Message,
                UiLanguage.T("Externe Bearbeitung"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            review.Busy = false;
            _cropSavePending = false;
            if (_closeAfterEditAdmission)
            {
                _closeAfterEditAdmission = false;
                Dispatcher.BeginInvoke(new Action(Close));
            }
            if (!Dispatcher.HasShutdownStarted) UpdateEditControls();
        }
    }

    private async Task RefreshExternalEditCatalogAsync(
        ExternalEditReview review, string? committed, bool alreadyIndexed = false)
    {
        var original = review.Snapshot.OriginalPath;
        if (committed is null)
        {
            // Cancel never touched the original or catalog. Do not queue a
            // pointless P0 scan or re-index the whole gallery.
            if (_viewerOpen && _viewerImageInfo?.Id == review.ImageId)
                ShowViewerImage();
            return;
        }
        var folderId = await _galleryCatalogReader.ReadAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            using var db = _database.OpenConnection();
            using var command = db.CreateCommand();
            command.CommandText =
                "SELECT folder_id FROM images WHERE id=$id LIMIT 1";
            command.Parameters.AddWithValue("$id", review.ImageId);
            return command.ExecuteScalar() is { } v
                ? Convert.ToInt64(v) : 0L;
        });
        if (!alreadyIndexed)
        {
        var label = UiLanguage.T("Externe Bearbeitung katalogisieren");
        var key = "external:catalog:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key, label, committed is not null &&
            !committed.Equals(original, StringComparison.OrdinalIgnoreCase) ? 2 : 1);
        try
        {
            await _scheduler.EnqueueAsync(key, SchedulerPriority.ManualAction,
                token =>
                {
                    var root = _folderRepository.GetAll().FirstOrDefault(
                        f => f.Id == folderId)
                        ?? throw new IOException(
                            UiLanguage.T("Der Katalogordner der externen Bearbeitung fehlt."));
                    var isCopy = committed is { } output &&
                        !output.Equals(original, StringComparison.OrdinalIgnoreCase);
                    if (!isCopy)
                    {
                        new BrowseCacheRepository(_database).InvalidateImage(review.ImageId);
                        var res = _folderScanner.ScanFile(root, original,
                            cancellationToken: token,
                            publishCatalogChange: false, forceExifRefresh: true);
                        if (res.Failed > 0)
                            throw new IOException(UiLanguage.T(
                                "Original konnte nicht katalogisiert werden."));
                    }
                    if (isCopy && committed is { } copy)
                    {
                        var result = _folderScanner.ScanFile(root, copy,
                            cancellationToken: token,
                            publishCatalogChange: false, forceExifRefresh: true);
                        if (result.Failed > 0 || result.ImageFilesSeen == 0)
                            throw new IOException(UiLanguage.T("Kopie konnte nicht katalogisiert werden."));
                        // The viewer can activate the copy only after the
                        // gallery index has real packed thumbnail tiers.
                        using var lookup = _database.OpenConnection();
                        using var command = lookup.CreateCommand();
                        command.CommandText =
                            "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
                        command.Parameters.AddWithValue("$path", copy);
                        if (command.ExecuteScalar() is not { } found)
                            throw new IOException(
                                "Die gespeicherte Kopie fehlt im Katalog.");
                        new BrowseCacheBuilder(
                            new BrowseCacheRepository(_database),
                            new BrowseThumbnailProfileRepository(_database))
                            .RebuildSingleImage(Convert.ToInt64(found), token);
                    }
                    return Task.CompletedTask;
                });
        }
        finally { EndVisibleManualOperation(key); }
        }

        _viewerOriginalCache.Remove(review.ImageId);
        _ = RefreshRestoreBadgesAsync();
        if (committed is not null &&
            committed.Equals(original, StringComparison.OrdinalIgnoreCase))
            await RefreshThumbnailsAsync([review.ImageId]);
        if (committed is { } copyPath &&
            !copyPath.Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            // The same viewer activation path as Crop -> Save as Copy.
            await ActivateSavedCopyAsync(new EditWriteRecipe(review.ImageId,
                folderId, original, [], copyPath));
        }
        else
        {
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            // If a Save appears to "remove" the photo, record whether it
            // disappeared from the catalog index or only from the viewer.
            // This is a read-only check of the index already in memory.
            if (!_browseIndex.Any(entry => entry.ImageId == review.ImageId))
                PCasaDiagnostics.WriteMessage("ExternalEdit.GalleryVisibility",
                    $"Saved={original}; ImageId={review.ImageId}; " +
                    $"InPublishedIndex=false; ViewerOpen={_viewerOpen}; " +
                    $"CurrentViewerId={_viewerImageInfo?.Id}");
            if (_viewerOpen && _viewerImageInfo?.Id == review.ImageId)
            {
                // The result has already been decoded for the review. Do not
                // blank it while an asynchronous catalog/metadata/original
                // re-read catches up with the just-replaced source file.
                ShowViewerImage(keepCurrentAsPlaceholder: true);
            }
        }
    }
}
