using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
    private bool _straightenActive;
    private double _straightenAngle;
    private int _straightenQuarterTurns;
    private BitmapSource? _straightenPreviewBase;
    private int _straightenPreviewQuarterTurns = int.MinValue;

    private bool EnsureEditSession()
    {
        if (!_viewerOpen || _viewerImageInfo is not { } info ||
            PhotoViewerImage.Source is not BitmapSource bitmap)
            return false;
        var extension = Path.GetExtension(info.LocalPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            PcasaDialog.Show(this, UiLanguage.T(
                "Zuschneiden ist nur für JPG-Fotos verfügbar. RAW-Dateien bleiben unverändert."),
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
        ViewerStraightenGrid.SizeChanged += (_, _) => DrawStraightenGrid();
        UpdateEditControls();
    }

    private void BeginStraighten()
    {
        if (!EnsureEditSession()) return;
        if (_cropActive) EndCrop();
        if (_brightnessActive) ApplyEditTool();
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
                DrawStraightenGrid();
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

    private void DrawStraightenGrid()
    {
        ViewerStraightenGrid.Children.Clear();
        if (!_straightenActive) return;
        var width = ViewerStraightenGrid.ActualWidth;
        var height = ViewerStraightenGrid.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var pen = new SolidColorBrush(Color.FromArgb(155, 245, 245, 245));
        pen.Freeze();
        const int divisions = 12;
        for (var n = 1; n < divisions; n++)
        {
            var x = n * width / divisions;
            var y = n * height / divisions;
            ViewerStraightenGrid.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = pen, StrokeThickness = .7,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                IsHitTestVisible = false
            });
            ViewerStraightenGrid.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = pen, StrokeThickness = .7,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                IsHitTestVisible = false
            });
        }
    }

    private void LeaveStraighten()
    {
        ResetStraightenPreviewTransform();
        _straightenPreviewBase = null;
        _straightenPreviewQuarterTurns = int.MinValue;
        _straightenActive = false;
        ViewerStraightenGrid.Visibility = Visibility.Collapsed;
        ViewerStraightenGrid.Children.Clear();
        ViewerStraightenOptions.Visibility = Visibility.Collapsed;
        ViewerOnImageToolsBar.Visibility = Visibility.Collapsed;
        ViewerToolsOverview.Visibility = Visibility.Visible;
        if (_photoEditSession is not null)
        {
            SetViewerBitmap(_photoEditSession.Render());
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }

    private void UpdateEditControls()
    {
        ViewerOnImageToolsBar.Visibility = _cropActive || _straightenActive
            ? Visibility.Visible : Visibility.Collapsed;
        CropUndoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanUndo == true;
        CropRedoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanRedo == true;
        var editingTool = _cropActive || _straightenActive ||
            (_brightnessActive && Math.Abs(_brightnessAmount) > .00001);
        // Keep all four buttons in a fixed footprint below the histogram.
        // A disabled action is still visible; the histogram never jumps.
        CropApplyButton.Visibility = Visibility.Visible;
        ViewerToolCancelButton.IsEnabled = !_cropSavePending &&
            (_cropActive || _straightenActive);
        CropApplyButton.IsEnabled = !_cropSavePending &&
            (_cropActive || _straightenActive);
        CropSaveButton.IsEnabled = !_cropSavePending &&
            (editingTool || _photoEditSession?.HasChanges == true);
        CropSaveCopyButton.IsEnabled = CropSaveButton.IsEnabled;
        CropCancelButton.IsEnabled = !_cropSavePending &&
            (editingTool || _photoEditSession?.HasChanges == true);
        ViewerCropFooter.Visibility = Visibility.Visible;
    }

    private bool ApplyEditTool()
    {
        if (_photoEditSession is null) return false;
        if (_cropActive)
        {
            var area = FitToImageAspect(_cropRect);
            if (area.Width <= .01 || area.Height <= .01) return false;
            _photoEditSession.Apply(new PhotoEditStep("crop",
                area.X, area.Y, area.Width, area.Height));
            EndCrop();
        }
        else if (_straightenActive)
        {
            var degrees = _straightenAngle + 90 * _straightenQuarterTurns;
            LeaveStraighten();
            if (Math.Abs(degrees) > .00001)
                _photoEditSession.Apply(new PhotoEditStep(
                    "rotate", Degrees: degrees));
        }
        else if (_brightnessActive)
        {
            var exposure = _brightnessAmount;
            LeaveBrightness();
            if (Math.Abs(exposure) > .00001)
                _photoEditSession.Apply(new PhotoEditStep(
                    "brightness", Amount: exposure));
        }
        else return false;
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
        if (direction < 0) _photoEditSession.Undo();
        else _photoEditSession.Redo();
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
    }

    private void CancelEditSession()
    {
        if (_cropSavePending) return;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        var original = _photoEditSession?.Original;
        _photoEditSession = null;
        if (original is not null)
        {
            SetViewerBitmap(original);
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }

    private async void SaveEdits(bool asCopy = false)
    {
        if (_cropSavePending || _viewerImageInfo is not { } info) return;
        if (_cropActive || _straightenActive || _brightnessActive)
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
        BeginVisibleManualOperation(key, UiLanguage.T("Bildbearbeitung speichern"), 1);
        try
        {
            if (asCopy)
            {
                // A NAS File.Exists must never block the WPF dispatcher.
                // Persist this exact destination in the durable recipe so a
                // resumed operation cannot silently make -Kopie-2 instead.
                var copyPath = await Task.Run(() =>
                    JpegCropWriter.GetAvailableCopyPath(request.Path));
                request = request with { CopyPath = copyPath };
            }
            var task = _scheduler.EnqueueDurableRecipeAsync(key, "image-edit-v1",
                JsonSerializer.Serialize(request),
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
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
                0, 1, request.Path);
            if (request.CopyPath is { Length: > 0 } destination)
                JpegCropWriter.SaveAsCopy(request.Path, destination,
                    request.Steps, token);
            else
            {
                JpegCropWriter.Save(request.Path, request.Steps, token);
                new BrowseCacheRepository(_database).InvalidateImage(request.ImageId);
            }
            _scheduler.ReportProgress(key, UiLanguage.T("Bildbearbeitung speichern"),
                1, 1, request.Path);
            return Task.CompletedTask;
        });
        return package;
    }

    private async Task ObserveEditCommitAsync(
        Task task, string key, EditWriteRecipe request)
    {
        var success = false;
        try { await task; success = true; }
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
            if (!Dispatcher.HasShutdownStarted)
            {
            EndVisibleManualOperation(key);
            _cropSavePending = false;
            if (success)
            {
                if (request.CopyPath is { Length: > 0 } copyPath)
                {
                    // The viewer still represents the ORIGINAL photo.
                    // Remove its temporary edit preview; never mark the
                    // original as edited or invalidate its thumbnail.
                    if (_photoEditSession?.ImageId == request.ImageId)
                        CancelEditSession();
                    // New files enter the existing catalog scanner exactly
                    // once, under the original monitored folder.
                    QueueBackgroundChange(new BackgroundFolderChange(
                        request.FolderId, copyPath,
                        BackgroundFolderChangeKind.ScanFile,
                        Recursive: false, ForceExifRefresh: true));
                    StatusText.Text = UiLanguage.T("Kopie gespeichert: ") +
                        Path.GetFileName(copyPath);
                }
                else
                {
                    if (_photoEditSession?.ImageId == request.ImageId)
                        _browsePageCache?.ShowEditedImage(
                            request.ImageId, _photoEditSession.Render());
                    _photoEditSession = null;
                    if (_cropActive) EndCrop();
                    if (_straightenActive) LeaveStraighten();
                    _viewerOriginalCache.Remove(request.ImageId);
                    _ = RefreshRestoreBadgesAsync();
                    // Source file, catalog and the packed thumbnail become
                    // stale together. One scanner recipe owns the rescan.
                    QueueBackgroundChange(new BackgroundFolderChange(
                        request.FolderId, request.Path,
                        BackgroundFolderChangeKind.ScanFile,
                        Recursive: false, ForceExifRefresh: true));
                    if (_viewerOpen && _viewerImageInfo?.Id == request.ImageId)
                        ShowViewerImage(keepCurrentAsPlaceholder: true);
                    _ = RefreshBrowseGalleryAsync(forceIndexReload: true,
                        warmViewport: false);
                }
            }
            UpdateEditControls();
            }
        }
    }
}
