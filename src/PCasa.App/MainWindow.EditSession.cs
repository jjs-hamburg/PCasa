using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record EditWriteRecipe(
        long ImageId, long FolderId, string Path, PhotoEditStep[] Steps,
        string? CopyPath = null);

    private PhotoEditSession? _photoEditSession;
    // The durable P0 recipe owns the pixels after enqueue. UI navigation may
    // continue; only a second write of the SAME image stays disabled.
    private readonly HashSet<long> _pendingEditImageIds = [];
    private bool _straightenActive;
    private double _straightenAngle;
    private int _straightenQuarterTurns;
    private BitmapSource? _straightenPreviewBase;
    private int _straightenPreviewQuarterTurns = int.MinValue;

    private bool EnsureEditSession()
    {
        if (!_viewerOpen || _viewerImageInfo is not { } info ||
            PhotoViewerImage.Source is not BitmapSource bitmap ||
            _externalBatchReview is not null ||
            (_externalEditReview is not null &&
             !CanEditPendingExternalResult(info.Id)) ||
            _pendingEditImageIds.Contains(info.Id))
            return false;
        if (!EditableImageFormats.Supports(info.LocalPath))
        {
            PcasaDialog.Show(this, UiLanguage.T(
                "Dieses Format kann nicht bearbeitet werden. RAW und Videos bleiben unverändert."),
                UiLanguage.T("Bearbeiten"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (_photoEditSession?.ImageId != info.Id)
            _photoEditSession = new PhotoEditSession(info.Id, bitmap);
        return true;
    }

    private void InitializeStraightenTools()
    {
        AddEditorTool(new TextBlock
        {
            Text = "⟲", FontSize = 25,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        }, "Begradigen", "Begradigen / Drehen",
            () => BeginStraighten());

        StraightenBackButton.Click += (_, _) => LeaveStraighten();
        StraightenLeftButton.Click += (_, _) =>
        {
            _straightenQuarterTurns = NormalizeStraightenTurns(_straightenQuarterTurns - 1);
            QueueStraightenPreview();
        };
        StraightenRightButton.Click += (_, _) =>
        {
            _straightenQuarterTurns = NormalizeStraightenTurns(_straightenQuarterTurns + 1);
            QueueStraightenPreview();
        };
        StraightenAngleSlider.ValueChanged += (_, _) =>
        {
            if (!_straightenActive) return;
            _straightenAngle = StraightenAngleSlider.Value;
            QueueStraightenPreview();
        };
        StraightenWideRangeCheckBox.Checked += (_, _) =>
        {
            StraightenAngleSlider.Minimum = -45;
            StraightenAngleSlider.Maximum = 45;
        };
        StraightenWideRangeCheckBox.Unchecked += (_, _) =>
        {
            // Keep the selected angle while returning to fine mode.
            // A value outside the fine range is explicitly clamped.
            StraightenAngleSlider.Minimum = -10;
            StraightenAngleSlider.Maximum = 10;
        };
        UpdateEditControls();
    }

    private void BeginStraighten()
    {
        if (!EnsureEditSession()) return;
        if (_cropActive) EndCrop();
        if (_brightnessActive) ApplyEditTool();
        if (_advancedToneActive) ApplyEditTool();
        _straightenPreviewBase = _photoEditSession!.Render();
        _straightenPreviewQuarterTurns = int.MinValue;
        _straightenActive = true;
        _straightenAngle = 0;
        _straightenQuarterTurns = 0;
        StraightenAngleSlider.Value = 0;
        StraightenAngleValue.Text = "0,0°";
        ViewerToolsOverview.Visibility = Visibility.Collapsed;
        ViewerCropOptions.Visibility = Visibility.Collapsed;
        ViewerStraightenOptions.Visibility = Visibility.Visible;
        StraightenAngleSlider.Visibility = Visibility.Visible;
        ViewerStraightenGrid.Visibility = Visibility.Visible;
        RenderStraightenPreview();
        UpdateEditControls();
    }

    private static int NormalizeStraightenTurns(int quarterTurns)
        => ((quarterTurns % 4 + 6) % 4) - 2;

    private void QueueStraightenPreview()
    {
        if (!_straightenActive) return;
        StraightenAngleValue.Text =
            (_straightenAngle + 90 * _straightenQuarterTurns)
                .ToString("+0.00;-0.00;0.00",
                    System.Globalization.CultureInfo.CurrentCulture) + "°";
        // No debounce. Slider motion changes only a GPU-side WPF transform,
        // not a multi-megapixel bitmap or the viewport geometry.
        RenderStraightenPreview();
    }

    private void ResetStraightenPreviewTransform()
    {
        PhotoViewerImage.RenderTransform = Transform.Identity;
        PhotoViewerImage.RenderTransformOrigin = new Point(0, 0);
        PhotoViewerCanvas.Clip = null;
    }

    private void RenderStraightenPreview()
    {
        if (!_straightenActive || _straightenPreviewBase is null) return;
        try
        {
            // A quarter turn is a cheap, discrete WPF bitmap rotation.
            // It is only regenerated when an actual 90° button is clicked.
            if (_straightenPreviewQuarterTurns != _straightenQuarterTurns)
            {
                var source = _straightenQuarterTurns == 0
                    ? _straightenPreviewBase
                    : PhotoEditSession.Render(_straightenPreviewBase,
                        [new PhotoEditStep("rotate",
                            Degrees: _straightenQuarterTurns * 90)]);
                ResetStraightenPreviewTransform();
                SetViewerBitmap(source);
                ApplyViewerImageLayout();
                _straightenPreviewQuarterTurns = _straightenQuarterTurns;
            }

            // The same centered, auto-inscribed rectangle as the durable
            // image-space renderer: keep the visible frame fixed while the
            // angle changes continuously. This is a *visual* preview only;
            // ApplyEditTool commits the precise full-res pixels once.
            var width = PhotoViewerImage.Width;
            var height = PhotoViewerImage.Height;
            if (!(width > 0) || !(height > 0)) return;
            var radians = _straightenAngle * Math.PI / 180.0;
            var cosine = Math.Abs(Math.Cos(radians));
            var sine = Math.Abs(Math.Sin(radians));
            var scale = Math.Max(cosine + sine * height / width,
                                 cosine + sine * width / height);
            PhotoViewerImage.RenderTransformOrigin = new Point(.5, .5);
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new RotateTransform(_straightenAngle));
            PhotoViewerImage.RenderTransform = transform;
            var canvasWidth = PhotoViewerCanvas.Width;
            var canvasHeight = PhotoViewerCanvas.Height;
            PhotoViewerCanvas.Clip = new RectangleGeometry(new Rect(
                (canvasWidth - width) / 2, (canvasHeight - height) / 2,
                width, height));
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Image.StraightenPreview", ex);
        }
    }

    private void LeaveStraighten()
    {
        ResetStraightenPreviewTransform();
        _straightenPreviewBase = null;
        _straightenPreviewQuarterTurns = int.MinValue;
        _straightenActive = false;
        ViewerStraightenGrid.Visibility = Visibility.Collapsed;
        ViewerStraightenOptions.Visibility = Visibility.Collapsed;
        StraightenAngleSlider.Visibility = Visibility.Collapsed;
        ViewerOnImageToolsBar.Visibility = Visibility.Collapsed;
        ViewerToolsOverview.Visibility = Visibility.Visible;
        if (_photoEditSession is not null)
        {
            SetViewerBitmap(_photoEditSession.Render());
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }

    private void UpdateEditHistoryTooltips()
    {
        static string ActionName(PhotoEditStep step) => step.Kind switch
        {
            "crop" => UiLanguage.T("Zuschneiden"),
            "rotate" => UiLanguage.T("Begradigen") + " " +
                step.Degrees.ToString("+0.##;-0.##;0",
                    System.Globalization.CultureInfo.CurrentCulture) + "°",
            "auto-fix" => UiLanguage.T("Auf gut Glück!"),
            "auto-contrast" => UiLanguage.T("Kontrast (automatisch)"),
            "auto-color" => UiLanguage.T("Farbe (automatisch)"),
            "brightness" => UiLanguage.T("Aufhellen / Abdunkeln") + " " +
                step.Amount.ToString("+0.##;-0.##;0",
                    System.Globalization.CultureInfo.CurrentCulture) + " EV",
            "denoise-luma" => UiLanguage.T("Rauschen (Helligkeit)"),
            "denoise-chroma" => UiLanguage.T("Rauschen (Farbe)"),
            "sharpen" => UiLanguage.T("Schärfen"),
            "auto-sharpen" => UiLanguage.T("Schärfen (automatisch)"),
            "highlights" => UiLanguage.T("Lichter"),
            "shadows" => UiLanguage.T("Tiefen"),
            "contrast" => UiLanguage.T("Kontrast (manuell)"),
            "temperature" => UiLanguage.T("Farbtemperatur"),
            "tint" => UiLanguage.T("Farbstich (Grün/Magenta)"),
            "saturation" => UiLanguage.T("Sättigung"),
            _ => UiLanguage.T("Bildbearbeitung")
        };
        var undo = _photoEditSession?.NextUndoStep;
        var redo = _photoEditSession?.NextRedoStep;
        CropUndoButton.ToolTip = undo is null
            ? UiLanguage.T("Rückgängig machen")
            : UiLanguage.Format("Rückgängig: {0}", ActionName(undo));
        CropRedoButton.ToolTip = redo is null
            ? UiLanguage.T("Wiederholen")
            : UiLanguage.Format("Wiederholen: {0}", ActionName(redo));
    }

    private void UpdateEditControls()
    {
        UpdateEditHistoryTooltips();
        ViewerOnImageToolsBar.Visibility = _cropActive || _straightenActive
            ? Visibility.Visible : Visibility.Collapsed;
        CropUndoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanUndo == true;
        CropRedoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanRedo == true;
        var editingTool = _cropActive || _straightenActive ||
            (_brightnessActive && Math.Abs(_brightnessAmount) > .00001) ||
            (_advancedToneActive && Math.Abs(_advancedToneAmount) > .00001);
        // Keep all four buttons in a fixed footprint below the histogram.
        // A disabled action is still visible; the histogram never jumps.
        CropApplyButton.Visibility = Visibility.Visible;
        ViewerToolCancelButton.IsEnabled = !_cropSavePending &&
            (_cropActive || _straightenActive);
        CropApplyButton.IsEnabled = !_cropSavePending &&
            (_cropActive || _straightenActive);
        CropSaveButton.IsEnabled = !_cropSavePending &&
            (_viewerImageInfo is null || !_pendingEditImageIds.Contains(_viewerImageInfo.Id)) &&
            (editingTool || _photoEditSession?.HasChanges == true);
        CropSaveCopyButton.IsEnabled = CropSaveButton.IsEnabled;
        CropCancelButton.IsEnabled = !_cropSavePending &&
            (editingTool || _photoEditSession?.HasChanges == true);
        if (_externalEditReview is { } external)
        {
            // The SAME three Crop footer buttons now own an external edit.
            // Never permit another tool to start while the original is in
            // an unresolved external-editor transaction.
            var ready = external.Ready && !external.Busy;
            CropSaveCopyButton.IsEnabled = ready && external.ResultPath is not null;
            CropSaveButton.IsEnabled = CropSaveCopyButton.IsEnabled &&
                !ExternalEditTransaction.RequiresNewFile(
                    external.Snapshot.OriginalPath, external.ResultPath!);
            CropCancelButton.IsEnabled = ready;
            var canEditResult = CanEditPendingExternalResult(external.ImageId);
            CropApplyButton.IsEnabled = canEditResult &&
                (_cropActive || _straightenActive);
            ViewerToolCancelButton.IsEnabled = CropApplyButton.IsEnabled;
            CropUndoButton.IsEnabled = canEditResult &&
                _photoEditSession?.CanUndo == true;
            CropRedoButton.IsEnabled = canEditResult &&
                _photoEditSession?.CanRedo == true;
        }
        if (_externalBatchReview is { } batch)
        {
            // The exact SAME footer that decides a single external edit
            // decides a panorama or multi-image result. No auto-import.
            var ready = !batch.Busy && batch.Outputs.Length > 0;
            CropSaveCopyButton.IsEnabled = ready;
            CropSaveButton.IsEnabled = ready &&
                PanoramaTransaction.CanSaveMultiple(batch.Session, batch.Outputs);
            CropCancelButton.IsEnabled = !batch.Busy;
            CropApplyButton.IsEnabled = false;
            ViewerToolCancelButton.IsEnabled = false;
            CropUndoButton.IsEnabled = CropRedoButton.IsEnabled = false;
        }
        // The visible image's file extension is authoritative even before
        // the asynchronous database metadata arrives. Unsupported/RAW/video
        // tools are disabled upfront, not clickable prompts.
        var currentFile = _viewerSequence is { } seq &&
            _viewerCurrentIndex >= 0 && _viewerCurrentIndex < seq.Count
                ? seq.At(_viewerCurrentIndex).FileName
                : _viewerImageInfo?.LocalPath;
        var editable = currentFile is not null &&
            EditableImageFormats.Supports(currentFile);
        var noExternalEdit = _externalEditReview is null &&
            _externalBatchReview is null;
        // The user must not have to decode low-contrast disabled icons.
        // Internal tools follow the same visibility rule as configured
        // external editors: if this format/session cannot use them, they
        // simply are not present. Keep the Quick panel itself: it contains
        // the independently filtered external-program cards.
        // Visibility follows the loaded image FORMAT, never a later
        // internal or external editing action. Topaz may temporarily own
        // editing rights, but it must not collapse tabs or select Quick.
        var internalToolsVisible = editable;
        var internalToolsEnabled = editable &&
            (noExternalEdit ||
             (_viewerImageInfo is { } readyImage &&
              CanEditPendingExternalResult(readyImage.Id)));
        EditorToolTabs.Visibility = internalToolsVisible
            ? Visibility.Visible : Visibility.Collapsed;
        EditorToolTabs.IsEnabled = internalToolsEnabled;
        ViewerToolsList.Visibility = internalToolsVisible
            ? Visibility.Visible : Visibility.Collapsed;
        ViewerAutoToolsList.Visibility = internalToolsVisible
            ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tool in _editorToolLabels)
            tool.Button.IsEnabled = internalToolsEnabled;
        // A newly loaded PSD/RAW/video still switches to Quick so
        // independently compatible external editors remain accessible.
        if (!editable)
            SwitchEditorPanel("Quick");
        // With no detected external result, a deliberate click on another
        // editor automatically discards the unchanged working copy.
        var canChooseAnotherEditor = noExternalEdit ||
            _externalEditReview is { Ready: true, Busy: false, ResultPath: null };
        foreach (var card in ExternalEditorToolsList.Children.OfType<StackPanel>())
            foreach (var button in card.Children.OfType<Button>())
                button.IsEnabled = canChooseAnotherEditor;
        // Undo/Redo are internal actions too: show each only when it can
        // actually operate. Low-contrast disabled arrow glyphs are not a
        // usable availability cue for everyone.
        CropUndoButton.Visibility = CropUndoButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        CropRedoButton.Visibility = CropRedoButton.IsEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        // Footer Save/Cancel remains stable at the bottom of the panel;
        // hiding these would move the user's primary decision controls.
        ViewerCropFooter.Visibility = Visibility.Visible;
        UpdateEditComparisonAvailability();
        UpdateEditComparisonFromSession();
    }

    // One edit-step identity is shared by the live comparison and the
    // existing Save/Undo transaction. All image pixels still use PhotoEditSession.Render.
    private PhotoEditStep? GetActiveEditStep()
    {
        if (_cropActive)
        {
            var area = FitToImageAspect(_cropRect);
            return area.Width > .01 && area.Height > .01
                ? new PhotoEditStep("crop", area.X, area.Y, area.Width, area.Height)
                : null;
        }
        if (_straightenActive)
        {
            var degrees = _straightenAngle + 90 * _straightenQuarterTurns;
            return Math.Abs(degrees) > .00001
                ? new PhotoEditStep("rotate", Degrees: degrees) : null;
        }
        if (_brightnessActive)
            return Math.Abs(_brightnessAmount) > .00001
                ? new PhotoEditStep("brightness", Amount: _brightnessAmount) : null;
        if (_advancedToneActive && _advancedToneKind is { } kind)
            return Math.Abs(_advancedToneAmount) > .00001
                ? new PhotoEditStep(kind, Amount: _advancedToneAmount,
                    Radius: kind == "sharpen" ? SharpenRadiusSlider.Value : 1,
                    Detail: kind == "sharpen" ? SharpenDetailSlider.Value : .5)
                : null;
        return null;
    }

    private PhotoEditStep? GetPendingLiveEditStep()
    {
        var step = GetActiveEditStep();
        if (!_cropActive || step is null) return step;
        // Simply entering Crop does not make a before/after change.
        return _cropHistory.Count > 0 &&
               (_cropRect != _cropHistory[0].Bounds ||
                _cropRatio != _cropHistory[0].AspectRatio ||
                _cropRatioMode != _cropHistory[0].Mode)
            ? step : null;
    }

    private bool ApplyEditTool()
    {
        if (_photoEditSession is null) return false;
        if (!_cropActive && !_straightenActive && !_brightnessActive &&
            !_advancedToneActive) return false;
        var step = GetActiveEditStep();
        if (_cropActive && step is null) return false;
        // Commit BEFORE Leave* updates comparison availability. Otherwise
        // the live-preview-only comparison briefly resets and loses its mode.
        if (step is not null)
            _photoEditSession.Apply(step);
        if (_cropActive) EndCrop();
        else if (_straightenActive) LeaveStraighten();
        else if (_brightnessActive) LeaveBrightness();
        else if (_advancedToneActive) LeaveAdvancedTone();
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
        return true;
    }

    private void MoveEditHistory(int direction)
    {
        if (_cropSavePending || _photoEditSession is null) return;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        if (_advancedToneActive) LeaveAdvancedTone();
        if (direction < 0) _photoEditSession.Undo();
        else _photoEditSession.Redo();
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
    }

    private void CancelEditSession()
    {
        if (_externalBatchReview is not null)
        {
            _ = CompleteExternalBatchReviewAsync(ExternalEditDecision.Cancel);
            return;
        }
        if (_externalEditReview is not null)
        {
            // Explicit Cancel is a complete user action: discard the isolated
            // workspace and return to the gallery. An unchanged click or a
            // switch to another editor uses a different continuation instead.
            _ = CompleteExternalReviewAsync(ExternalEditDecision.Cancel,
                returnToGalleryAfterCancel: true);
            return;
        }
        // An already submitted immutable P0 recipe is independent of the
        // viewer. Leaving the editor never cancels that durable write.
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        if (_advancedToneActive) LeaveAdvancedTone();
        var original = _photoEditSession?.Original;
        _photoEditSession = null;
        if (original is not null)
        {
            SetViewerBitmap(original);
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }


    // Include queued, parked and restored durable writes, not merely the
    // current viewer's in-memory pending IDs. Two edits of the same original
    // must never compete in File.Replace while either receipt is unresolved.
    private Task<bool> HasUnfinishedImageEditAsync(long imageId)
        => Task.Run(() => new DurablePackageJournal(_database).Pending()
            .Where(item => item.Type == "image-edit-v1")
            .Any(item => JsonSerializer.Deserialize<EditWriteRecipe>(
                item.Payload)?.ImageId == imageId));

    private async void SaveEdits(bool asCopy = false)
    {
        if (_externalBatchReview is not null)
        {
            await CompleteExternalBatchReviewAsync(asCopy
                ? ExternalEditDecision.SaveCopy : ExternalEditDecision.Save);
            return;
        }
        if (_externalEditReview is not null)
        {
            await CompleteExternalReviewAsync(asCopy
                ? ExternalEditDecision.SaveCopy : ExternalEditDecision.Save);
            return;
        }
        if (_cropSavePending || _viewerImageInfo is not { } info ||
            _pendingEditImageIds.Contains(info.Id)) return;
        if (_cropActive || _straightenActive || _brightnessActive ||
            _advancedToneActive)
            if (!ApplyEditTool()) return;
        if (_photoEditSession is not { HasChanges: true } session ||
            session.ImageId != info.Id) return;

        long folderId;
        using (var db = _database.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT folder_id FROM images WHERE id=$id";
            command.Parameters.AddWithValue("$id", info.Id);
            folderId = Convert.ToInt64(command.ExecuteScalar() ??
                throw new InvalidDataException("Foto nicht im Katalog."));
        }
        var request = new EditWriteRecipe(info.Id, folderId, info.LocalPath,
            session.AppliedSteps.ToArray());
        var key = "image:edit:" + Guid.NewGuid().ToString("N");
        _cropSavePending = true;
        UpdateEditControls();
        BeginVisibleManualOperation(key, UiLanguage.T("Bildbearbeitung speichern"),
            asCopy ? 2 : 1);
        try
        {
            if (await HasUnfinishedImageEditAsync(info.Id))
                throw new InvalidOperationException(UiLanguage.T(
                    "Für dieses Bild ist bereits ein Speicherauftrag vorgemerkt. Bitte dessen Status im Taskmanager prüfen; keinen zweiten Auftrag starten."));
            if (asCopy)
            {
                // A NAS File.Exists must never block the WPF dispatcher.
                // Persist this exact destination in the durable recipe so a
                // resumed operation cannot silently make -Kopie-2 instead.
                var copyPath = await Task.Run(() =>
                    JpegCropWriter.GetAvailableCopyPath(request.Path));
                request = request with { CopyPath = copyPath };
            }
            var task = StartDirectImageWrite(key,
                UiLanguage.T("Bildbearbeitung speichern"), request.Path,
                BuildEditPackage(key, JsonSerializer.Serialize(request)),
                request.FolderId, reindexOriginal: request.CopyPath is null);
            _pendingEditImageIds.Add(info.Id);
            _cropSavePending = false; // Navigation must not wait for a NAS write.
            UpdateEditControls();
            _taskManagerWindow?.RequestRefresh();
            _ = ObserveEditCommitAsync(task, key, request);
        }
        catch (Exception ex)
        {
            // Includes an unreachable source folder while choosing the copy
            // name. The unsaved edit session stays available for a retry.
            _cropSavePending = false;
            EndVisibleManualOperation(key);
            UpdateEditControls();
            PCasaDiagnostics.Write("Image.Edit.Enqueue", ex);
            if (!Dispatcher.HasShutdownStarted)
                PcasaDialog.Show(this, ex.Message,
                    UiLanguage.T("Bildbearbeitung speichern"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private SchedulerTaskPackage BuildEditPackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<EditWriteRecipe>(payload)
            ?? throw new InvalidDataException("Ungültige Bildbearbeitung.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            _scheduler.ReportProgress(key, UiLanguage.T("Bildbearbeitung speichern"),
                0, request.CopyPath is null ? 1 : 2, request.Path);
            if (request.CopyPath is { Length: > 0 } destination)
                JpegCropWriter.SaveAsCopy(request.Path, destination,
                    request.Steps, token);
            else
            {
                JpegCropWriter.Save(request.Path, request.Steps, token);
                // Invalidating the gallery projection is NOT part of the
                // original-file commit. A transient SQLite/cache fault after
                // File.Replace must not misreport a successful save as failed.
                try { new BrowseCacheRepository(_database).InvalidateImage(request.ImageId); }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Image.Edit.CacheInvalidation", ex);
                }
            }
            _scheduler.ReportProgress(key, request.CopyPath is null
                    ? UiLanguage.T("Bildbearbeitung speichern")
                    : UiLanguage.T("Kopie katalogisieren"),
                1, request.CopyPath is null ? 1 : 2, request.Path);
            return Task.CompletedTask;
        });
        if (request.CopyPath is { Length: > 0 } copy)
        {
            // The save AND catalog import are ONE durable P0 package.
            // Never rely on a low-priority background watcher to make a
            // newly saved copy available to the viewer minutes later.
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                var root = _folderRepository.GetAll().FirstOrDefault(folder =>
                    folder.Id == request.FolderId)
                    ?? throw new InvalidOperationException(
                        "Der Überwachungsordner der Bildkopie fehlt.");
                var result = _folderScanner.ScanFile(root, copy,
                    cancellationToken: token, publishCatalogChange: false,
                    forceExifRefresh: true);
                // ScanResult reports the number of files inspected, not
                // an Added property. Zero means the saved copy was not
                // available/eligible for catalog import.
                if (result.Failed > 0 || result.ImageFilesSeen == 0)
                    throw new IOException(
                        "Die gespeicherte Bildkopie konnte nicht katalogisiert werden.");
                // The catalogue row alone cannot enter the viewer sequence:
                // publish its packed thumbnail tiers in this same P0 step,
                // matching external Save Copy and panorama import.
                using (var db = _database.OpenConnection())
                using (var command = db.CreateCommand())
                {
                    command.CommandText =
                        "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
                    command.Parameters.AddWithValue("$path", copy);
                    if (command.ExecuteScalar() is not { } found)
                        throw new IOException(
                            "Die gespeicherte Bildkopie fehlt im Katalog.");
                    new BrowseCacheBuilder(
                        new BrowseCacheRepository(_database),
                        new BrowseThumbnailProfileRepository(_database))
                        .RebuildSingleImage(Convert.ToInt64(found), token);
                }
                _scheduler.ReportProgress(key, UiLanguage.T("Kopie katalogisieren"),
                    2, 2, copy);
                return Task.CompletedTask;
            });
        }
        return package;
    }

    private async Task ActivateSavedCopyAsync(EditWriteRecipe request,
        bool linkToSourceForComparison = true)
    {
        var copy = request.CopyPath
            ?? throw new InvalidOperationException("Kein Kopiepfad vorhanden.");
        // The scanner has already committed the image and packed tiers in the
        // SAME manual package. Read the identity from SQLite off-dispatcher;
        // do not guess it from a filename or wait for automatic P3 scanning.
        var copyId = await _galleryCatalogReader.ReadAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            using var db = _database.OpenConnection();
            using var command = db.CreateCommand();
            command.CommandText =
                "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$path", copy);
            return command.ExecuteScalar() is { } value
                ? (long?)Convert.ToInt64(value) : null;
        });
        if (copyId is not long newId)
            throw new InvalidDataException("Die Bildkopie wurde nicht im Katalog gefunden.");
        if (linkToSourceForComparison && request.ImageId != newId)
        {
            if (_editCompareCopySources.Count >= 64)
                _editCompareCopySources.Remove(_editCompareCopySources.Keys.First());
            _editCompareCopySources[newId] = request.ImageId;
            // The externally produced pixels were captured before the editor
            // workspace was removed. Rebind only their VIEW identity, never
            // reconstruct a comparison using the current gallery original.
            if (_latestExternalComparison is { } external &&
                external.ImageId == request.ImageId)
                _latestExternalComparison = external with { ImageId = newId };
            if (_externalComparisonUnavailableImageId == request.ImageId)
                _externalComparisonUnavailableImageId = newId;
        }

        await RefreshBrowseGalleryAsync(forceIndexReload: true);
        if (!_viewerOpen || _viewerSequence is null ||
            _viewerCurrentIndex < 0 ||
            _viewerSequence.At(_viewerCurrentIndex).ImageId != request.ImageId)
            return; // User already navigated away; never hijack that viewer.

        var sequence = PhotoSequence.Create(_displayedBrowseIndex,
            BrowseSurface.HiddenFolderIds, newId, _viewerSettings.SequenceScope);
        var next = sequence.IndexOfImage(newId);
        if (next < 0)
        {
            // An active search/property filter may hide the new copy. It is
            // still the just-saved CURRENT photo. Show a temporary one-image
            // sequence instead of jumping back to the original.
            var indexed = await _galleryCatalogReader.ReadAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                return _browseCacheRepository?.GetIndexEntry(
                    _activeBrowseTier ?? 192, newId);
            });
            if (indexed is null)
                throw new InvalidDataException("Die Bildkopie besitzt keinen Galerieeintrag.");
            sequence = PhotoSequence.Create(new[] { indexed },
                new HashSet<long>(), newId, PhotoSequenceScope.Gallery);
            next = sequence.IndexOfImage(newId);
        }

        // Clear the old undo/crop state without restoring the original photo
        // as the selected viewer identity.
        _photoEditSession = null;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        if (_advancedToneActive) LeaveAdvancedTone();
        _viewerSequence = sequence;
        _viewerCurrentIndex = next;
        SelectViewerImage(newId);
        ShowViewerImage();
    }

    private async Task ObserveEditCommitAsync(
        Task task, string key, EditWriteRecipe request)
    {
        try
        {
            await task;
            if (_shutdownStarted || Dispatcher.HasShutdownStarted) return;
            if (request.CopyPath is { Length: > 0 } copy)
            {
                // A different image or gallery may now be active. Refresh the
                // catalog but never steal the viewer back after navigation.
                await ActivateSavedCopyAsync(request);
                StatusText.Text = UiLanguage.T("Kopie gespeichert: ") +
                    Path.GetFileName(copy);
            }
            else
            {
                // Do not clear a *different* photo's new edit session while a
                // previous P0 write completes asynchronously.
                if (_photoEditSession?.ImageId == request.ImageId)
                {
                    _browsePageCache?.ShowEditedImage(
                        request.ImageId, _photoEditSession.Render());
                    _photoEditSession = null;
                    if (_cropActive) EndCrop();
                    if (_straightenActive) LeaveStraighten();
                    if (_brightnessActive) LeaveBrightness();
                    if (_advancedToneActive) LeaveAdvancedTone();
                }
                _viewerOriginalCache.Remove(request.ImageId);
                _ = RefreshRestoreBadgesAsync();
                // The P0 transaction below owns the immediate re-index.
                // Do not also enqueue the same file as a redundant P3 scan.
                if (_viewerOpen && _viewerImageInfo?.Id == request.ImageId)
                    ShowViewerImage(keepCurrentAsPlaceholder: true);
                // The direct write already reconciled the source and
                // its packed thumbnails before releasing the catalogue lease.
                await RefreshBrowseGalleryAsync(forceIndexReload: true,
                    warmViewport: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Image.Edit", ex);
            if (!Dispatcher.HasShutdownStarted)
                PcasaDialog.Show(this, ex.Message,
                    UiLanguage.T("Bildbearbeitung speichern"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _pendingEditImageIds.Remove(request.ImageId);
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                UpdateEditControls();
            }
        }
    }

}
