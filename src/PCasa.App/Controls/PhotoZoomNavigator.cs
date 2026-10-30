using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PCasa.App.Controls;

public sealed class PhotoNavigatorCenterEventArgs : EventArgs
{
    public PhotoNavigatorCenterEventArgs(double x, double y)
    {
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

// One lightweight overview of the current photograph. No new thumbnail files,
// source decode, or image memory are allocated by this control.
public sealed class PhotoZoomNavigator : Border
{
    private const double PreviewWidth = 184;
    private const double PreviewHeight = 148;
    private readonly TextBlock _caption;
    private readonly Canvas _preview;
    private readonly Image _image;
    private readonly Rectangle _visible;
    private Rect _imageRect;
    private bool _dismissed;

    public PhotoZoomNavigator()
    {
        Background = new SolidColorBrush(Color.FromArgb(232, 65, 81, 88));
        CornerRadius = new CornerRadius(5);
        Padding = new Thickness(10, 7, 10, 10);
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(16);
        Visibility = Visibility.Collapsed;
        SnapsToDevicePixels = true;

        var root = new StackPanel();
        Child = root;
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.Children.Add(header);

        _caption = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(_caption);

        var close = new Button
        {
            Content = new ContentControl
            {
                Width = 18,
                Height = 18,
                Template = (ControlTemplate)Application.Current.FindResource(
                    "PcasaCloseGlyphTemplate")
            },
            Padding = new Thickness(0),
            Height = 20,
            MinWidth = 20,
            FontSize = 12,
            ToolTip = "Übersicht ausblenden",
            Cursor = Cursors.Hand
        };
        Grid.SetColumn(close, 1);
        close.Click += (_, e) =>
        {
            _dismissed = true;
            Visibility = Visibility.Collapsed;
            e.Handled = true;
        };
        header.Children.Add(close);

        _preview = new Canvas
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Background = new SolidColorBrush(Color.FromRgb(34, 40, 42)),
            Cursor = Cursors.SizeAll,
            ClipToBounds = true
        };
        root.Children.Add(_preview);

        _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _preview.Children.Add(_image);

        _visible = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(240, 243, 245)),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromArgb(27, 244, 247, 250)),
            IsHitTestVisible = false
        };
        _preview.Children.Add(_visible);

        _preview.MouseLeftButtonDown += NavigatorPreviewMouseDown;
        _preview.MouseMove += NavigatorPreviewMouseMove;
        _preview.MouseLeftButtonUp += NavigatorPreviewMouseUp;
        _preview.LostMouseCapture += (_, _) => { };
    }

    public event EventHandler<PhotoNavigatorCenterEventArgs>? CenterRequested;

    public void ShowForNewImage() => _dismissed = false;

    public void Update(
        BitmapSource? bitmap,
        double percent,
        int sourceWidth,
        int sourceHeight,
        double displayedWidth,
        double displayedHeight,
        double viewportWidth,
        double viewportHeight,
        double canvasWidth,
        double canvasHeight,
        double scrollX,
        double scrollY)
    {
        // Only show an overview when the current zoom actually clips the photo.
        if (_dismissed || bitmap is null || sourceWidth <= 0 || sourceHeight <= 0 ||
            (displayedWidth <= viewportWidth + 1 &&
             displayedHeight <= viewportHeight + 1))
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        _caption.Text = $"Zoom auf {percent:0.#} %";
        _image.Source = bitmap;
        var scale = Math.Min(PreviewWidth / sourceWidth, PreviewHeight / sourceHeight);
        var width = sourceWidth * scale;
        var height = sourceHeight * scale;
        var left = (PreviewWidth - width) / 2;
        var top = (PreviewHeight - height) / 2;
        _imageRect = new Rect(left, top, width, height);

        _image.Width = width;
        _image.Height = height;
        Canvas.SetLeft(_image, left);
        Canvas.SetTop(_image, top);

        // When the displayed photo is centered inside a larger viewport,
        // the image does not begin at canvas coordinate (0,0).
        var imageLeft = Math.Max(0, (canvasWidth - displayedWidth) / 2);
        var imageTop = Math.Max(0, (canvasHeight - displayedHeight) / 2);
        var x0 = Math.Clamp((scrollX - imageLeft) / displayedWidth, 0, 1);
        var y0 = Math.Clamp((scrollY - imageTop) / displayedHeight, 0, 1);
        var x1 = Math.Clamp((scrollX + viewportWidth - imageLeft) / displayedWidth, 0, 1);
        var y1 = Math.Clamp((scrollY + viewportHeight - imageTop) / displayedHeight, 0, 1);
        var w = Math.Max(1, (x1 - x0) * width);
        var h = Math.Max(1, (y1 - y0) * height);
        _visible.Width = w;
        _visible.Height = h;
        Canvas.SetLeft(_visible, left + x0 * width);
        Canvas.SetTop(_visible, top + y0 * height);
    }

    private void NavigatorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 0)
        {
            _preview.CaptureMouse();
            RequestCenter(e.GetPosition(_preview));
            e.Handled = true;
        }
    }

    private void NavigatorPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_preview.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            RequestCenter(e.GetPosition(_preview));
            e.Handled = true;
        }
    }

    private void NavigatorPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_preview.IsMouseCaptured)
        {
            _preview.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void RequestCenter(Point point)
    {
        if (_imageRect.Width <= 0 || _imageRect.Height <= 0)
        {
            return;
        }

        CenterRequested?.Invoke(this, new PhotoNavigatorCenterEventArgs(
            Math.Clamp((point.X - _imageRect.X) / _imageRect.Width, 0, 1),
            Math.Clamp((point.Y - _imageRect.Y) / _imageRect.Height, 0, 1)));
    }
}
