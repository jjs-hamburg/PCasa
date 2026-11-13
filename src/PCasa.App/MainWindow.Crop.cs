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
    private BitmapSource? _cropPreviewOriginal;
    private Rect? _cropPreviewRectBefore;
    private (double ImageWidth, double ImageHeight, double CanvasWidth,
        double CanvasHeight, double ScrollX, double ScrollY)? _cropPreviewLayout;

    private string? _viewerCachedCameraDetails;

    // Use the ONE persisted EXIF catalog, never re-open the original
    // over a potentially slow network just to fill the editor footer.
    private string? ReadCachedViewerCameraDetails(long imageId)
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText =
            "SELECT camera_model, exif_json FROM image_exif WHERE image_id=$id";
        command.Parameters.AddWithValue("$id", imageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var model = reader.IsDBNull(0) ? "" : reader.GetString(0);
        if (reader.IsDBNull(1)) return model;
        using var json = JsonDocument.Parse(reader.GetString(1));
        var obj = json.RootElement;
        if (obj.ValueKind == JsonValueKind.Array && obj.GetArrayLength() == 1)
            obj = obj[0];
        if (obj.ValueKind != JsonValueKind.Object) return model;

        static double? Get(JsonElement root, params string[] tags)
        {
            foreach (var tag in tags)
                foreach (var prop in root.EnumerateObject())
                    if ((prop.Name.EndsWith(":" + tag, StringComparison.OrdinalIgnoreCase) ||
                         prop.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)) &&
                        prop.Value.ValueKind == JsonValueKind.Number &&
                        prop.Value.TryGetDouble(out var number))
                        return number;
            return null;
        }
        var exposure = Get(obj, "ExposureTime");
        var aperture = Get(obj, "FNumber");
        var iso = Get(obj, "ISO");
        var focal = Get(obj, "FocalLength");
        var parts = new List<string>();
        if (exposure is > 0)
            parts.Add(exposure < 1
                ? "1/" + Math.Round(1 / exposure.Value).ToString("0") + " s"
                : exposure.Value.ToString("0.###") + " s");
        if (aperture is > 0) parts.Add("f/" + aperture.Value.ToString("0.#"));
        if (iso is > 0) parts.Add("ISO " + iso.Value.ToString("0"));
        if (focal is > 0) parts.Add(focal.Value.ToString("0.#") + " mm");
        if (model.Length == 0 && parts.Count == 0) return null;
        return model + (parts.Count > 0
            ? (model.Length > 0 ? Environment.NewLine : "") +
              string.Join("   ·   ", parts)
            : "");
    }

    // Histogram is sampled from the decoded in-memory preview; do not open the
    // original (particularly important for slow/offline NAS paths).
    private void UpdateViewerHistogram(BitmapSource? source)
    {
        ViewerHistogramCanvas.Children.Clear();
        ViewerCameraDetails.Text = _viewerImageInfo is { } info
            ? (_viewerCachedCameraDetails is { Length: > 0 } details
                ? details + Environment.NewLine : string.Empty) +
                info.FileName + "  ·  " + info.Width + " × " + info.Height
            : string.Empty;
        if (source is null || source.PixelWidth < 1 || source.PixelHeight < 1)
            return;
        if (_viewerCachedCameraDetails is null &&
            source is BitmapFrame frame &&
            frame.Metadata is BitmapMetadata metadata &&
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
            Width = 67, Height = 59, Padding = new Thickness(2),
            Margin = new Thickness(3, 3, 3, 3)
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var frame = new Viewbox { Width = 30, Height = 27, Margin = new Thickness(0, 0, 0, 2) };
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
                Width = 46, Height = 46, Margin = new Thickness(2),
                Padding = new Thickness(1) };
            var glyph = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var width = ratio == -1.0 ? 28 : ratio == 0.0 ? 26 :
                29 * Math.Sqrt(ratio);
            var height = ratio <= 0 ? 22 : 29 / Math.Sqrt(ratio);
            var shape = new Rectangle
            {
                Width = Math.Min(29, width), Height = Math.Min(25, height),
                Stroke = Brushes.SlateGray, StrokeThickness = 1.25,
                Fill = Brushes.Transparent, Margin = new Thickness(0, 2, 0, 2)
            };
            var iconFrame = new Grid { Width = 34, Height = 28 };
            iconFrame.Children.Add(shape);
            glyph.Children.Add(iconFrame);
            glyph.Children.Add(new TextBlock
            {
                Text = name, FontSize = 9, TextAlignment = TextAlignment.Center
            });
            button.Content = glyph;
            button.Click += CropRatio_Click;
            CropAspectButtons.Children.Add(button);
        }
        ViewerCropOverlay.MouseLeftButtonDown += Crop_MouseDown;
        ViewerCropOverlay.MouseMove += Crop_MouseMove;
        ViewerCropOverlay.MouseLeave += (_, _) =>
        {
            if (!_cropDragging)
            {
                ViewerCropOverlay.Cursor = Cursors.Arrow;
                ShowCropHoverEdges(0);
            }
        };
        CropPreviewButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_cropActive && PhotoViewerImage.Source is BitmapSource source &&
                _cropPreviewOriginal is null)
            {
                var area = FitToImageAspect(_cropRect);
                var x = Math.Clamp((int)Math.Floor(area.X * source.PixelWidth), 0, source.PixelWidth - 1);
                var y = Math.Clamp((int)Math.Floor(area.Y * source.PixelHeight), 0, source.PixelHeight - 1);
                var width = Math.Clamp((int)Math.Ceiling(area.Width * source.PixelWidth),
                    1, source.PixelWidth - x);
                var height = Math.Clamp((int)Math.Ceiling(area.Height * source.PixelHeight),
                    1, source.PixelHeight - y);
                _cropPreviewOriginal = source;
                _cropPreviewRectBefore = _cropRect;
                _cropPreviewLayout = (PhotoViewerImage.Width, PhotoViewerImage.Height,
                    PhotoViewerCanvas.Width, PhotoViewerCanvas.Height,
                    PhotoViewerScroller.HorizontalOffset, PhotoViewerScroller.VerticalOffset);
                var cropped = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
                cropped.Freeze();
                var (viewportWidth, viewportHeight) = ViewerViewportSize();
                var scale = Math.Min(viewportWidth / cropped.PixelWidth,
                    viewportHeight / cropped.PixelHeight);
                PhotoViewerImage.Width = Math.Max(1, cropped.PixelWidth * scale);
                PhotoViewerImage.Height = Math.Max(1, cropped.PixelHeight * scale);
                PhotoViewerCanvas.Width = Math.Max(viewportWidth, PhotoViewerImage.Width);
                PhotoViewerCanvas.Height = Math.Max(viewportHeight, PhotoViewerImage.Height);
                PhotoViewerImage.Source = cropped;
                PhotoViewerScroller.ScrollToHorizontalOffset(0);
                PhotoViewerScroller.ScrollToVerticalOffset(0);
                ViewerCropOverlay.Visibility = Visibility.Hidden;
            }
            e.Handled = true;
        };
        CropPreviewButton.PreviewMouseLeftButtonUp += (_, e) =>
        {
            EndCropPreview();
            e.Handled = true;
        };
        CropPreviewButton.LostMouseCapture += (_, _) => EndCropPreview();
        ViewerCropOverlay.MouseLeftButtonUp += Crop_MouseUp;
        ViewerCropOverlay.LostMouseCapture += (_, _) => _cropDragging = false;
        PhotoViewerImage.SizeChanged += (_, _) => DrawCropFrame();
        CropUndoButton.Click += (_, _) => MoveEditHistory(-1);
        CropRedoButton.Click += (_, _) => MoveEditHistory(1);
        CropHorizontalButton.Click += (_, _) => FlipCropOrientation(true);
        CropVerticalButton.Click += (_, _) => FlipCropOrientation(false);
        CropBackButton.Click += (_, _) =>
        {
            EndCrop(); // Discard only the unaccepted tool mask.
            if (_photoEditSession is not null)
            {
                SetViewerBitmap(_photoEditSession.Render());
                ApplyViewerImageLayout();
            }
            UpdateEditControls();
        };
        CropApplyButton.Click += (_, _) => ApplyEditTool();
        CropSaveButton.Click += (_, _) => SaveEdits();
        CropSaveCopyButton.Click += (_, _) => SaveEdits(asCopy: true);
        CropCancelButton.Click += (_, _) => CancelEditSession();
        InitializeStraightenTools();
        InitializeAutoImageTools();
    }

    private void BeginCrop()
    {
        if (!_viewerOpen || _viewerImageInfo is not { LocalPath: { } path } ||
            !System.IO.Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !System.IO.Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            PcasaDialog.Show(this,
                UiLanguage.T("Zuschneiden ist nur für JPG-Fotos verfügbar. RAW-Dateien bleiben unverändert."),
                UiLanguage.T("Zuschneiden"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!EnsureEditSession()) return;
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        if (_photoEditSession is null) return;
        // Work on the currently accepted, unsaved image, not an old source.
        if (_photoEditSession.HasChanges)
        {
            SetViewerBitmap(_photoEditSession.Render());
            ApplyViewerImageLayout();
        }
        if (PhotoViewerImage.Source is not BitmapSource bitmap || bitmap.PixelWidth == 0)
            return;
        if (_cropActive)
        {
            ViewerToolsOverview.Visibility = Visibility.Collapsed;
            ViewerCropOptions.Visibility = Visibility.Visible;
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
        ViewerCropFooter.Visibility = Visibility.Visible;
        ViewerCropOverlay.Visibility = Visibility.Visible;
        DrawCropFrame();
        UpdateCropHistoryControls();
        UpdateCropSelectionButtons();
        UpdateEditControls();
    }

    private void EndCropPreview()
    {
        if (_cropPreviewOriginal is not { } source) return;
        _cropPreviewOriginal = null;
        PhotoViewerImage.Source = source;
        if (_cropPreviewRectBefore is { } originalBounds)
            _cropRect = originalBounds;
        _cropPreviewRectBefore = null;
        if (_cropPreviewLayout is { } layout)
        {
            PhotoViewerImage.Width = layout.ImageWidth;
            PhotoViewerImage.Height = layout.ImageHeight;
            PhotoViewerCanvas.Width = layout.CanvasWidth;
            PhotoViewerCanvas.Height = layout.CanvasHeight;
            PhotoViewerScroller.ScrollToHorizontalOffset(layout.ScrollX);
            PhotoViewerScroller.ScrollToVerticalOffset(layout.ScrollY);
            _cropPreviewLayout = null;
        }
        if (_cropActive) ViewerCropOverlay.Visibility = Visibility.Visible;
        DrawCropFrame();
        _ = Dispatcher.BeginInvoke(new Action(DrawCropFrame),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void EndCrop()
    {
        EndCropPreview();
        _cropActive = false;
        _cropDragging = false;
        ViewerCropOverlay.ReleaseMouseCapture();
        ViewerCropOverlay.Visibility = Visibility.Collapsed;
        ViewerCropOptions.Visibility = Visibility.Collapsed;
        // Footer is a fixed row; leaving Crop only disables its actions.
        ViewerCropFooter.Visibility = Visibility.Visible;
        ViewerToolsOverview.Visibility = Visibility.Visible;
        _cropHistory.Clear();
        _cropHistoryIndex = -1;
    }

    private bool ConfirmCropExit()
    {
        if (_cropSavePending) return false;
        var hasAccepted = _photoEditSession?.HasChanges == true;
        var editedMask = _cropActive && _cropHistory.Count > 0 &&
            (_cropRect != _cropHistory[0].Bounds ||
             _cropRatio != _cropHistory[0].AspectRatio ||
             _cropRatioMode != _cropHistory[0].Mode);
        var pendingAngle = _straightenActive &&
            (Math.Abs(_straightenAngle) > .00001 || _straightenQuarterTurns != 0);
        var pendingBrightness = _brightnessActive &&
            Math.Abs(_brightnessAmount) > .00001;
        if (hasAccepted || editedMask || pendingAngle || pendingBrightness)
        {
            var response = PcasaDialog.Show(this,
                UiLanguage.T("Ungespeicherte Bildbearbeitungen verwerfen?"),
                UiLanguage.T("Bearbeiten"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (response != MessageBoxResult.Yes) return false;
        }
        CancelEditSession();
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
            ShowCropHoverEdges(edges);
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
            _cropRect = ResizeCropByEdges(_cropOriginalRect,
                _cropResizeEdges, dx, dy);
        }
        else
        {
            _cropRect = new Rect(Math.Clamp(_cropOriginalRect.X + dx,
                    0, 1 - _cropOriginalRect.Width),
                Math.Clamp(_cropOriginalRect.Y + dy, 0, 1 - _cropOriginalRect.Height),
                _cropOriginalRect.Width, _cropOriginalRect.Height);
        }
        DrawCropFrame();
        ShowCropHoverEdges(_cropResizeEdges);
        e.Handled = true;
    }

    private void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_cropDragging) return;
        _cropDragging = false;
        ViewerCropOverlay.ReleaseMouseCapture();
        ShowCropHoverEdges(0);
        AddCropHistory(_cropRect);
        e.Handled = true;
    }

    // Invisible magnetic hit target; no corner handles. Corners win when
    // both edges are within the 15-pixel tolerance.
    // Fixed-aspect crop keeps the opposite border/corner anchored. A resize
    // on a single side grows the other axis symmetrically, rather than being
    // silently limited by the previous height/width.
    private Rect ResizeCropByEdges(Rect original, int edges, double dx, double dy)
    {
        bool left = (edges & 1) != 0, right = (edges & 2) != 0;
        bool top = (edges & 4) != 0, bottom = (edges & 8) != 0;
        var l = left ? Math.Clamp(original.Left + dx, 0, original.Right - .02)
            : original.Left;
        var r = right ? Math.Clamp(original.Right + dx, original.Left + .02, 1)
            : original.Right;
        var t = top ? Math.Clamp(original.Top + dy, 0, original.Bottom - .02)
            : original.Top;
        var b = bottom ? Math.Clamp(original.Bottom + dy, original.Top + .02, 1)
            : original.Bottom;

        if (_cropRatio <= 0 || PhotoViewerImage.Source is not BitmapSource source)
            return new Rect(l, t, r - l, b - t);

        double normalizedRatio = _cropRatio *
            source.PixelHeight / (double)source.PixelWidth;
        if (normalizedRatio <= 0) return original;
        double xAnchor = left ? original.Right : original.Left;
        double yAnchor = top ? original.Bottom : original.Top;
        double desiredWidth;
        double maxWidth;

        if (left || right)
        {
            desiredWidth = r - l;
            maxWidth = left ? xAnchor : 1 - xAnchor;
        }
        else
        {
            desiredWidth = (b - t) * normalizedRatio;
            maxWidth = 2 * Math.Min(original.Left + original.Width / 2,
                1 - (original.Left + original.Width / 2));
        }

        if (top || bottom)
        {
            double fromHeight = (b - t) * normalizedRatio;
            if (left || right)
            {
                // In a corner, use whichever mouse displacement dominates.
                if (Math.Abs(fromHeight - original.Width) >
                    Math.Abs(desiredWidth - original.Width))
                    desiredWidth = fromHeight;
            }
            maxWidth = Math.Min(maxWidth,
                normalizedRatio * (top ? yAnchor : 1 - yAnchor));
        }
        else
        {
            double midY = original.Top + original.Height / 2;
            maxWidth = Math.Min(maxWidth,
                normalizedRatio * 2 * Math.Min(midY, 1 - midY));
        }
        double width = Math.Clamp(desiredWidth,
            Math.Min(.02, maxWidth), Math.Max(.02, maxWidth));
        double height = width / normalizedRatio;

        double x = left ? xAnchor - width : right ? xAnchor
            : original.Left + (original.Width - width) / 2;
        double y = top ? yAnchor - height : bottom ? yAnchor
            : original.Top + (original.Height - height) / 2;
        return new Rect(Math.Clamp(x, 0, Math.Max(0, 1 - width)),
            Math.Clamp(y, 0, Math.Max(0, 1 - height)),
            Math.Min(1, width), Math.Min(1, height));
    }

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

    private void ShowCropHoverEdges(int edges)
    {
        ViewerCropHoverLeft.Visibility = (edges & 1) != 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewerCropHoverRight.Visibility = (edges & 2) != 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewerCropHoverTop.Visibility = (edges & 4) != 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewerCropHoverBottom.Visibility = (edges & 8) != 0 ? Visibility.Visible : Visibility.Collapsed;
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
        ViewerCropHoverLeft.X1 = ViewerCropHoverLeft.X2 = rect.Left;
        ViewerCropHoverLeft.Y1 = rect.Top;
        ViewerCropHoverLeft.Y2 = rect.Bottom;
        ViewerCropHoverRight.X1 = ViewerCropHoverRight.X2 = rect.Right;
        ViewerCropHoverRight.Y1 = rect.Top;
        ViewerCropHoverRight.Y2 = rect.Bottom;
        ViewerCropHoverTop.X1 = rect.Left;
        ViewerCropHoverTop.X2 = rect.Right;
        ViewerCropHoverTop.Y1 = ViewerCropHoverTop.Y2 = rect.Top;
        ViewerCropHoverBottom.X1 = rect.Left;
        ViewerCropHoverBottom.X2 = rect.Right;
        ViewerCropHoverBottom.Y1 = ViewerCropHoverBottom.Y2 = rect.Bottom;
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

    private void UpdateCropHistoryControls() => UpdateEditControls();

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

    private bool _cropSavePending;

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
            new BrowseCacheRepository(_database).InvalidateImage(request.ImageId);
            _scheduler.ReportProgress(key, "Bild zuschneiden", 1, 1, request.Path);
            return Task.CompletedTask;
        });
        return package;
    }


}
