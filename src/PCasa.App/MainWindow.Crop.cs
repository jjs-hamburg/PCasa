using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Shapes;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record CropWriteRecipe(long ImageId, long FolderId, string Path,
        double X, double Y, double Width, double Height);
    private sealed record CropStep(Rect Bounds, double AspectRatio, string Mode);
    private readonly List<CropStep> _cropHistory = [];
    private int _cropHistoryIndex = -1;
    private bool _cropActive;
    private bool _cropDragging;
    private bool _cropResizing;
    private int _cropResizeEdges; // 1 left, 2 right, 4 top, 8 bottom
    private bool _cropIgnoreChanges;
    private Point _cropMouseStart;
    private Rect _cropOriginalRect;
    private Rect _cropRect = new(.1, .1, .8, .8);
    private double _cropRatio; // 0 = free
    private string _cropRatioMode = "Original";
    private bool _cropToolsReady;

    // Histogram is sampled from the decoded in-memory preview; do not open the
    // original (particularly important for slow/offline NAS paths).
    private void UpdateViewerHistogram(BitmapSource? source)
    {
        ViewerHistogramCanvas.Children.Clear();
        ViewerCameraDetails.Text = _viewerImageInfo is { } info
            ? info.FileName + "  ·  " + info.Width + " × " + info.Height
            : string.Empty;
        if (source is null || source.PixelWidth < 1 || source.PixelHeight < 1)
            return;
        if (source.Metadata is BitmapMetadata metadata &&
            !string.IsNullOrWhiteSpace(metadata.CameraModel))
            ViewerCameraDetails.Text = metadata.CameraModel + "  ·  " +
                ViewerCameraDetails.Text;
        try
        {
            var factor = Math.Min(1.0,
                192.0 / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource sampled = factor < 1
                ? new TransformedBitmap(source, new ScaleTransform(factor, factor))
                : source;
            var argb = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32,
                null, 0);
            int width = argb.PixelWidth, height = argb.PixelHeight;
            if (width < 1 || height < 1) return;
            var bytes = new byte[checked(width * height * 4)];
            argb.CopyPixels(bytes, width * 4, 0);
            var bins = new int[3, 256];
            for (int i = 0; i < bytes.Length; i += 4)
            {
                bins[0, bytes[i + 2]]++;
                bins[1, bytes[i + 1]]++;
                bins[2, bytes[i]]++;
            }
            int peak = 1;
            foreach (var count in bins) peak = Math.Max(peak, count);
            var colors = new[] { Colors.IndianRed, Colors.MediumSeaGreen,
                Colors.CornflowerBlue };
            for (int channel = 0; channel < 3; channel++)
            {
                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(colors[channel]),
                    StrokeThickness = 1.1,
                    Opacity = .8,
                    IsHitTestVisible = false
                };
                for (int n = 0; n < 256; n++)
                    poly.Points.Add(new Point(n / 255.0 * 256,
                        73 - 70.0 * bins[channel, n] / peak));
                ViewerHistogramCanvas.Children.Add(poly);
            }
            ViewerHistogramCanvas.Width = 256;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Viewer.Histogram", ex);
        }
    }

    private void InitializeCropTools()
    {
        if (_cropToolsReady) return;
        _cropToolsReady = true;
        var tool = new Button
        {
            ToolTip = UiLanguage.T("Zuschneiden"),
            Width = 105, Height = 88, Padding = new Thickness(5),
            Margin = new Thickness(4, 5, 4, 5)
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var frame = new Viewbox { Width = 46, Height = 43, Margin = new Thickness(0, 0, 0, 4) };
        frame.Child = new ShapePath
        {
            Data = Geometry.Parse("M4,3 L4,24 L25,24 M3,9 L24,9 L24,29 M9,3 L9,24 M3,19 L24,19"),
            StrokeThickness = 1.25, Stroke = Brushes.SlateGray,
            Stretch = Stretch.Uniform
        };
        stack.Children.Add(frame);
        stack.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Zuschneiden"), TextAlignment = TextAlignment.Center,
            FontSize = 11
        });
        tool.Content = stack;
        tool.Click += (_, _) => BeginCrop();
        ViewerToolsList.Children.Add(tool);
        foreach (var (name, ratio) in new[]
        {
            ("Original", -1.0), ("Frei", 0.0), ("1:1", 1.0),
            ("3:2", 1.5), ("4:3", 4.0/3), ("16:9", 16.0/9),
            ("2:3", 2.0/3), ("3:4", .75), ("9:16", 9.0/16)
        })
        {
            var button = new Button { Tag = name, ToolTip = name,
                Width = 54, Height = 55, Margin = new Thickness(2),
                Padding = new Thickness(2) };
            var glyph = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var width = ratio == -1.0 ? 28 : ratio == 0.0 ? 26 :
                29 * Math.Sqrt(ratio);
            var height = ratio <= 0 ? 22 : 29 / Math.Sqrt(ratio);
            var shape = new Rectangle
            {
                Width = Math.Min(34, width), Height = Math.Min(31, height),
                Stroke = Brushes.SlateGray, StrokeThickness = 1.25,
                Fill = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 2)
            };
            var iconFrame = new Grid { Width = 37, Height = 34 };
            iconFrame.Children.Add(shape);
            glyph.Children.Add(iconFrame);
            glyph.Children.Add(new TextBlock
            {
                Text = name, FontSize = 9.5, TextAlignment = TextAlignment.Center
            });
            button.Content = glyph;
            button.Click += CropRatio_Click;
            CropAspectButtons.Children.Add(button);
        }
        ViewerCropOverlay.MouseLeftButtonDown += Crop_MouseDown;
        ViewerCropOverlay.MouseMove += Crop_MouseMove;
        ViewerCropOverlay.MouseLeave += (_, _) =>
        {
            if (!_cropDragging) ViewerCropOverlay.Cursor = Cursors.Arrow;
        };
        CropPreviewButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            ViewerCropOverlay.Visibility = Visibility.Hidden;
            e.Handled = true;
        };
        CropPreviewButton.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_cropActive) ViewerCropOverlay.Visibility = Visibility.Visible;
            e.Handled = true;
        };
        CropPreviewButton.LostMouseCapture += (_, _) =>
        {
            if (_cropActive) ViewerCropOverlay.Visibility = Visibility.Visible;
        };
        ViewerCropOverlay.MouseLeftButtonUp += Crop_MouseUp;
        ViewerCropOverlay.LostMouseCapture += (_, _) => _cropDragging = false;
        PhotoViewerImage.SizeChanged += (_, _) => DrawCropFrame();
        CropUndoButton.Click += (_, _) => MoveCropHistory(-1);
        CropRedoButton.Click += (_, _) => MoveCropHistory(1);
        CropHorizontalButton.Click += (_, _) => FlipCropOrientation(true);
        CropVerticalButton.Click += (_, _) => FlipCropOrientation(false);
        CropBackButton.Click += (_, _) =>
        {
            ViewerToolsOverview.Visibility = Visibility.Visible;
            ViewerCropOptions.Visibility = Visibility.Collapsed;
        ViewerCropFooter.Visibility = Visibility.Collapsed;
            ViewerCropOverlay.Visibility = Visibility.Collapsed;
        };
        CropSaveButton.Click += (_, _) => SaveCrop();
        CropCancelButton.Click += (_, _) => EndCrop();
    }

    private void BeginCrop()
    {
        if (!_viewerOpen || _viewerImageInfo is not { LocalPath: { } path } ||
            !System.IO.Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !System.IO.Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            PcasaDialog.Show(this,
                "Zuschneiden ist nur für JPG-Fotos verfügbar. RAW-Dateien bleiben unverändert.",
                UiLanguage.T("Zuschneiden"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (PhotoViewerImage.Source is not BitmapSource bitmap || bitmap.PixelWidth == 0)
            return;
        if (_cropActive)
        {
            ViewerToolsOverview.Visibility = Visibility.Collapsed;
            ViewerCropOptions.Visibility = Visibility.Visible;
        ViewerCropFooter.Visibility = Visibility.Visible;
            ViewerCropFooter.Visibility = Visibility.Visible;
            ViewerCropOverlay.Visibility = Visibility.Visible;
            DrawCropFrame();
            return;
        }
        _cropActive = true;
        _cropRatioMode = "Original";
        _cropRatio = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        _cropRect = FitToImageAspect(new Rect(.08, .08, .84, .84));
        _cropHistory.Clear();
        _cropHistory.Add(new CropStep(_cropRect, _cropRatio, _cropRatioMode));
        _cropHistoryIndex = 0;
        ViewerToolsOverview.Visibility = Visibility.Collapsed;
        ViewerCropOptions.Visibility = Visibility.Visible;
        ViewerCropOverlay.Visibility = Visibility.Visible;
        DrawCropFrame();
        UpdateCropHistoryControls();
        UpdateCropSelectionButtons();
    }

    private void EndCrop()
    {
        _cropActive = false;
        _cropDragging = false;
        ViewerCropOverlay.ReleaseMouseCapture();
        ViewerCropOverlay.Visibility = Visibility.Collapsed;
        ViewerCropOptions.Visibility = Visibility.Collapsed;
        ViewerToolsOverview.Visibility = Visibility.Visible;
        _cropHistory.Clear();
        _cropHistoryIndex = -1;
    }

    private bool ConfirmCropExit()
    {
        if (!_cropActive) return true;
        if (_cropHistory.Count > 1 || _cropRect.Width < .95 ||
            _cropRect.Height < .95)
        {
            var response = PcasaDialog.Show(this,
                "Zuschneiden ohne Speichern verlassen?",
                UiLanguage.T("Zuschneiden"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (response != MessageBoxResult.Yes) return false;
        }
        EndCrop();
        return true;
    }

    private void CropRatio_Click(object sender, RoutedEventArgs e)
    {
        if (!_cropActive || sender is not Button { Tag: string name }) return;
        _cropRatioMode = name;
        if (name == "Frei") _cropRatio = 0;
        else if (name == "Original" && PhotoViewerImage.Source is BitmapSource bmp)
            _cropRatio = bmp.PixelWidth / (double)bmp.PixelHeight;
        else
        {
            var numbers = name.Split(':');
            if (numbers.Length == 2 &&
                double.TryParse(numbers[0], out var w) &&
                double.TryParse(numbers[1], out var h) && h > 0) _cropRatio = w / h;
        }
        _cropRect = FitToImageAspect(_cropRect);
        AddCropHistory(_cropRect);
        UpdateCropSelectionButtons();
    }

    private void FlipCropOrientation(bool horizontal)
    {
        if (!_cropActive) return;
        var ratio = _cropRatio > 0 ? _cropRatio : _cropRect.Width / _cropRect.Height;
        if (horizontal && ratio < 1 || !horizontal && ratio > 1) ratio = 1 / ratio;
        _cropRatio = ratio;
        _cropRatioMode = "Fixed";
        _cropRect = FitToImageAspect(_cropRect);
        AddCropHistory(_cropRect);
        UpdateCropSelectionButtons();
    }

    private void UpdateCropSelectionButtons()
    {
        foreach (var item in CropAspectButtons.Children.OfType<Button>())
        {
            var chosen = string.Equals(item.Tag as string, _cropRatioMode,
                StringComparison.Ordinal);
            item.BorderThickness = new Thickness(chosen ? 2.5 : 1);
            item.BorderBrush = chosen ? Brushes.DodgerBlue : Brushes.SlateGray;
            item.Background = chosen ? Brushes.LightSteelBlue : Brushes.Transparent;
            item.FontWeight = chosen ? FontWeights.Bold : FontWeights.Normal;
        }
        bool landscape = _cropRatio > 0 ? _cropRatio >= 1 :
            _cropRect.Width >= _cropRect.Height;
        foreach (var pair in new[] { (CropHorizontalButton, landscape),
                                     (CropVerticalButton, !landscape) })
        {
            pair.Item1.BorderThickness = new Thickness(pair.Item2 ? 2.5 : 1);
            pair.Item1.BorderBrush = pair.Item2 ? Brushes.DodgerBlue : Brushes.SlateGray;
            pair.Item1.Background = pair.Item2 ? Brushes.LightSteelBlue : Brushes.Transparent;
            pair.Item1.FontWeight = pair.Item2 ? FontWeights.Bold : FontWeights.Normal;
        }
    }

    private static Rect FitCrop(Rect rect, double ratio)
    {
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var width = Math.Clamp(rect.Width, .04, 1);
        var height = Math.Clamp(rect.Height, .04, 1);
        if (ratio > 0)
        {
            // Ratios apply to actual pixel dimensions, not the normalized unit square.
            // The caller supplies the oriented source pixel aspect below.
            var areaAspect = 1.0;
            width = Math.Min(width, height * ratio / areaAspect);
            height = width * areaAspect / ratio;
            if (height > 1) { height = 1; width = height * ratio / areaAspect; }
        }
        width = Math.Clamp(width, .02, 1);
        height = Math.Clamp(height, .02, 1);
        return new Rect(Math.Clamp(center.X - width / 2, 0, 1 - width),
            Math.Clamp(center.Y - height / 2, 0, 1 - height), width, height);
    }

    private Rect FitToImageAspect(Rect rect)
    {
        if (_cropRatio <= 0 || PhotoViewerImage.Source is not BitmapSource bitmap)
            return FitCrop(rect, 0);
        var pixelAspect = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        var ratioNormalized = _cropRatio / pixelAspect;
        return FitCrop(rect, ratioNormalized);
    }

    private void Crop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_cropActive || e.ChangedButton != MouseButton.Left) return;
        var actual = PixelCropRect();
        var pt = e.GetPosition(ViewerCropOverlay);
        _cropResizeEdges = CropEdgesAt(pt, actual);
        if (_cropResizeEdges == 0 && !actual.Contains(pt)) return;
        _cropMouseStart = pt;
        _cropOriginalRect = _cropRect;
        _cropResizing = _cropResizeEdges != 0;
        _cropDragging = true;
        ViewerCropOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_cropActive) return;
        var pt = e.GetPosition(ViewerCropOverlay);
        if (!_cropDragging)
        {
            var edges = CropEdgesAt(pt, PixelCropRect());
            ViewerCropOverlay.Cursor = edges switch
            {
                1 or 2 => Cursors.SizeWE,
                4 or 8 => Cursors.SizeNS,
                5 or 10 => Cursors.SizeNWSE,
                6 or 9 => Cursors.SizeNESW,
                _ => PixelCropRect().Contains(pt) ? Cursors.SizeAll : Cursors.Arrow
            };
            return;
        }
        var w = ViewerCropOverlay.ActualWidth;
        var h = ViewerCropOverlay.ActualHeight;
        if (w < 1 || h < 1) return;
        var dx = (pt.X - _cropMouseStart.X) / w;
        var dy = (pt.Y - _cropMouseStart.Y) / h;
        if (_cropResizing)
        {
            var original = _cropOriginalRect;
            var left = (_cropResizeEdges & 1) != 0
                ? Math.Clamp(original.Left + dx, 0, original.Right - .04)
                : original.Left;
            var right = (_cropResizeEdges & 2) != 0
                ? Math.Clamp(original.Right + dx, original.Left + .04, 1)
                : original.Right;
            var top = (_cropResizeEdges & 4) != 0
                ? Math.Clamp(original.Top + dy, 0, original.Bottom - .04)
                : original.Top;
            var bottom = (_cropResizeEdges & 8) != 0
                ? Math.Clamp(original.Bottom + dy, original.Top + .04, 1)
                : original.Bottom;
            // Eight grips (corners and edge midpoints) share a single
            // normalized geometry and stay within the original image.
            _cropRect = FitToImageAspect(new Rect(left, top,
                right - left, bottom - top));
        }
        else
        {
            _cropRect = new Rect(Math.Clamp(_cropOriginalRect.X + dx,
                    0, 1 - _cropOriginalRect.Width),
                Math.Clamp(_cropOriginalRect.Y + dy, 0, 1 - _cropOriginalRect.Height),
                _cropOriginalRect.Width, _cropOriginalRect.Height);
        }
        DrawCropFrame();
        e.Handled = true;
    }

    private void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging) return;
        _cropDragging = false;
        ViewerCropOverlay.ReleaseMouseCapture();
        AddCropHistory(_cropRect);
        e.Handled = true;
    }

    // Invisible magnetic hit target; no corner handles. Corners win when
    // both edges are within the 15-pixel tolerance.
    private static int CropEdgesAt(Point point, Rect rect)
    {
        const double magnet = 15;
        if (point.X < rect.Left - magnet || point.X > rect.Right + magnet ||
            point.Y < rect.Top - magnet || point.Y > rect.Bottom + magnet)
            return 0;
        int edges = 0;
        if (Math.Abs(point.X - rect.Left) <= magnet) edges |= 1;
        else if (Math.Abs(point.X - rect.Right) <= magnet) edges |= 2;
        if (Math.Abs(point.Y - rect.Top) <= magnet) edges |= 4;
        else if (Math.Abs(point.Y - rect.Bottom) <= magnet) edges |= 8;
        return edges;
    }

    private Rect PixelCropRect()
    {
        var w = ViewerCropOverlay.ActualWidth;
        var h = ViewerCropOverlay.ActualHeight;
        return new Rect(_cropRect.X * w, _cropRect.Y * h,
            _cropRect.Width * w, _cropRect.Height * h);
    }

    private void DrawCropFrame()
    {
        if (!_cropActive || ViewerCropOverlay is null) return;
        var rect = PixelCropRect();
        Canvas.SetLeft(ViewerCropOutline, rect.X);
        Canvas.SetTop(ViewerCropOutline, rect.Y);
        ViewerCropOutline.Width = rect.Width;
        ViewerCropOutline.Height = rect.Height;
        // Four shaded regions leave only the selected rectangle bright.
        SetCropShade(ViewerCropShadeTop, 0, 0,
            ViewerCropOverlay.ActualWidth, rect.Top);
        SetCropShade(ViewerCropShadeBottom, 0, rect.Bottom,
            ViewerCropOverlay.ActualWidth,
            ViewerCropOverlay.ActualHeight - rect.Bottom);
        SetCropShade(ViewerCropShadeLeft, 0, rect.Top, rect.Left, rect.Height);
        SetCropShade(ViewerCropShadeRight, rect.Right, rect.Top,
            ViewerCropOverlay.ActualWidth - rect.Right, rect.Height);
        foreach (var (line, fraction, vertical) in new[]
        {
            (ViewerCropThirdV1, 1.0/3, true), (ViewerCropThirdV2, 2.0/3, true),
            (ViewerCropThirdH1, 1.0/3, false), (ViewerCropThirdH2, 2.0/3, false)
        })
        {
            line.X1 = vertical ? rect.X + fraction * rect.Width : rect.X;
            line.X2 = vertical ? line.X1 : rect.Right;
            line.Y1 = vertical ? rect.Y : rect.Y + fraction * rect.Height;
            line.Y2 = vertical ? rect.Bottom : line.Y1;
        }
    }

    private static void SetCropShade(Rectangle shade, double x, double y,
        double width, double height)
    {
        Canvas.SetLeft(shade, x);
        Canvas.SetTop(shade, y);
        shade.Width = Math.Max(0, width);
        shade.Height = Math.Max(0, height);
    }

    private void AddCropHistory(Rect rect)
    {
        if (!_cropActive) return;
        if (_cropHistoryIndex >= 0 && _cropHistory[_cropHistoryIndex] ==
            new CropStep(rect, _cropRatio, _cropRatioMode))
        {
            DrawCropFrame();
            return;
        }
        if (_cropHistoryIndex + 1 < _cropHistory.Count)
            _cropHistory.RemoveRange(_cropHistoryIndex + 1,
                _cropHistory.Count - _cropHistoryIndex - 1);
        _cropHistory.Add(new CropStep(rect, _cropRatio, _cropRatioMode));
        _cropHistoryIndex++;
        UpdateCropHistoryControls();
        DrawCropFrame();
    }

    private void UpdateCropHistoryControls()
    {
        CropUndoButton.IsEnabled = _cropHistoryIndex > 0;
        CropRedoButton.IsEnabled = _cropHistoryIndex >= 0 &&
            _cropHistoryIndex < _cropHistory.Count - 1;
    }

    private void MoveCropHistory(int delta)
    {
        var next = _cropHistoryIndex + delta;
        if (next < 0 || next >= _cropHistory.Count) return;
        _cropHistoryIndex = next;
        _cropRect = _cropHistory[next].Bounds;
        _cropRatio = _cropHistory[next].AspectRatio;
        _cropRatioMode = _cropHistory[next].Mode;
        UpdateCropHistoryControls();
        UpdateCropSelectionButtons();
        DrawCropFrame();
    }

    private void SaveCrop()
    {
        if (!_cropActive || _viewerImageInfo is not { } info) return;
        var area = FitToImageAspect(_cropRect);
        if (area.Width <= .01 || area.Height <= .01) return;
        var imageId = info.Id;
        long folderId;
        using (var db = _database.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT folder_id FROM images WHERE id=$id";
            command.Parameters.AddWithValue("$id", imageId);
            folderId = Convert.ToInt64(command.ExecuteScalar() ??
                throw new InvalidDataException("Foto nicht im Katalog."));
        }
        var request = new CropWriteRecipe(imageId, folderId, info.LocalPath,
            area.X, area.Y, area.Width, area.Height);
        var key = "image:crop:" + Guid.NewGuid().ToString("N");
        BeginVisibleManualOperation(key, "Bild zuschneiden", 1);
        Task task;
        try
        {
            task = _scheduler.EnqueueDurableRecipeAsync(key, "image-crop-v1",
                JsonSerializer.Serialize(request),
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            _taskManagerWindow?.RequestRefresh();
        }
        catch
        {
            EndVisibleManualOperation(key);
            throw;
        }
        EndCrop();
        _ = ObserveCropAsync(task, key, request);
    }

    private SchedulerTaskPackage BuildCropPackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<CropWriteRecipe>(payload) ??
            throw new InvalidDataException("Ungültiger Zuschneideauftrag.");
        var package = new SchedulerTaskPackage(key);
        package.AddStep(token =>
        {
            _scheduler.ReportProgress(key, "Bild zuschneiden", 0, 1, request.Path);
            JpegCropWriter.Save(request.Path,
                new Rect(request.X, request.Y, request.Width, request.Height), token);
            _scheduler.ReportProgress(key, "Bild zuschneiden", 1, 1, request.Path);
            return Task.CompletedTask;
        });
        return package;
    }

    private async Task ObserveCropAsync(Task task, string key, CropWriteRecipe request)
    {
        var success = false;
        try { await task; success = true; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Image.Crop", ex);
            if (!Dispatcher.HasShutdownStarted)
                PcasaDialog.Show(this, ex.Message, UiLanguage.T("Zuschneiden"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                if (success)
                {
                    _viewerOriginalCache.Remove(request.ImageId);
                    QueueBackgroundChange(new BackgroundFolderChange(
                        request.FolderId, request.Path,
                        BackgroundFolderChangeKind.ScanFile,
                        Recursive: false, ForceExifRefresh: true));
                    if (_viewerOpen && _viewerImageInfo?.Id == request.ImageId)
                        ShowViewerImage();
                }
            }
        }
    }
}
