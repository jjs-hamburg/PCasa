using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record EditWriteRecipe(
        long ImageId, long FolderId, string Path, PhotoEditStep[] Steps);

    private PhotoEditSession? _photoEditSession;
    private bool _straightenActive;
    private double _straightenAngle;
    private int _straightenQuarterTurns;
    private DispatcherTimer? _straightenPreviewTimer;

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
        var button = new Button
        {
            Width = 67, Height = 59, Padding = new Thickness(2),
            Margin = new Thickness(3, 3, 3, 3),
            ToolTip = UiLanguage.T("Begradigen / Drehen")
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = "⟲", FontSize = 24, TextAlignment = TextAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Begradigen"), FontSize = 9.5,
            TextAlignment = TextAlignment.Center
        });
        button.Content = stack;
        button.Click += (_, _) => BeginStraighten();
        ViewerToolsList.Children.Add(button);

        StraightenBackButton.Click += (_, _) => LeaveStraighten();
        StraightenLeftButton.Click += (_, _) =>
        {
            _straightenQuarterTurns--;
            QueueStraightenPreview();
        };
        StraightenRightButton.Click += (_, _) =>
        {
            _straightenQuarterTurns++;
            QueueStraightenPreview();
        };
        StraightenAngleSlider.ValueChanged += (_, _) =>
        {
            if (!_straightenActive) return;
            _straightenAngle = StraightenAngleSlider.Value;
            QueueStraightenPreview();
        };
        _straightenPreviewTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(85), DispatcherPriority.Background,
            (_, _) =>
            {
                _straightenPreviewTimer?.Stop();
                RenderStraightenPreview();
            }, Dispatcher);
        _straightenPreviewTimer.Stop();
        ViewerStraightenGrid.SizeChanged += (_, _) => DrawStraightenGrid();
        UpdateEditControls();
    }

    private void BeginStraighten()
    {
        if (!EnsureEditSession()) return;
        if (_cropActive) EndCrop();
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

    private void QueueStraightenPreview()
    {
        if (!_straightenActive) return;
        StraightenAngleValue.Text =
            (_straightenAngle + 90 * _straightenQuarterTurns)
                .ToString("+0.0;-0.0;0.0",
                    System.Globalization.CultureInfo.CurrentCulture) + "°";
        _straightenPreviewTimer?.Stop();
        _straightenPreviewTimer?.Start();
    }

    private void RenderStraightenPreview()
    {
        if (!_straightenActive || _photoEditSession is null) return;
        var angle = _straightenAngle + 90 * _straightenQuarterTurns;
        try
        {
            var basis = _photoEditSession.Render();
            var preview = Math.Abs(angle) < .00001 ? basis :
                PhotoEditSession.Render(basis,
                    [new PhotoEditStep("rotate", Degrees: angle)]);
            SetViewerBitmap(preview);
            ApplyViewerImageLayout();
            DrawStraightenGrid();
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
        _straightenPreviewTimer?.Stop();
        _straightenActive = false;
        ViewerStraightenGrid.Visibility = Visibility.Collapsed;
        ViewerStraightenGrid.Children.Clear();
        ViewerStraightenOptions.Visibility = Visibility.Collapsed;
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
        CropUndoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanUndo == true;
        CropRedoButton.IsEnabled = !_cropSavePending &&
            _photoEditSession?.CanRedo == true;
        var editingTool = _cropActive || _straightenActive;
        CropApplyButton.Visibility = editingTool
            ? Visibility.Visible : Visibility.Collapsed;
        CropApplyButton.IsEnabled = !_cropSavePending && editingTool;
        CropSaveButton.IsEnabled = !_cropSavePending &&
            (editingTool || _photoEditSession?.HasChanges == true);
        CropCancelButton.IsEnabled = !_cropSavePending;
        ViewerCropFooter.Visibility = editingTool ||
            _photoEditSession?.HasChanges == true
                ? Visibility.Visible : Visibility.Collapsed;
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
        if (direction < 0) _photoEditSession.Undo();
        else _photoEditSession.Redo();
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
    }

    private void CancelEditSession()
    {
        if (_cropSavePending) return;
        _straightenPreviewTimer?.Stop();
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        var original = _photoEditSession?.Original;
        _photoEditSession = null;
        if (original is not null)
        {
            SetViewerBitmap(original);
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }

    private void SaveEdits()
    {
        if (_cropSavePending || _viewerImageInfo is not { } info) return;
        if (_cropActive || _straightenActive)
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
        Task task;
        try
        {
            task = _scheduler.EnqueueDurableRecipeAsync(key, "image-edit-v1",
                JsonSerializer.Serialize(request),
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            _taskManagerWindow?.RequestRefresh();
        }
        catch
        {
            _cropSavePending = false;
            EndVisibleManualOperation(key);
            UpdateEditControls();
            throw;
        }
        _ = ObserveEditCommitAsync(task, key, request);
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
            JpegCropWriter.Save(request.Path, request.Steps, token);
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
            if (Dispatcher.HasShutdownStarted) return;
            EndVisibleManualOperation(key);
            _cropSavePending = false;
            if (success)
            {
                _photoEditSession = null;
                if (_cropActive) EndCrop();
                if (_straightenActive) LeaveStraighten();
                _viewerOriginalCache.Remove(request.ImageId);
                _ = RefreshRestoreBadgesAsync();
                // Source file, catalog and the packed thumbnail become stale
                // together. A single scanner recipe owns the full rescan.
                QueueBackgroundChange(new BackgroundFolderChange(
                    request.FolderId, request.Path,
                    BackgroundFolderChangeKind.ScanFile,
                    Recursive: false, ForceExifRefresh: true));
                if (_viewerOpen && _viewerImageInfo?.Id == request.ImageId)
                    ShowViewerImage(keepCurrentAsPlaceholder: true);
                _ = RefreshBrowseGalleryAsync(forceIndexReload: true,
                    warmViewport: false);
            }
            UpdateEditControls();
        }
    }
}
