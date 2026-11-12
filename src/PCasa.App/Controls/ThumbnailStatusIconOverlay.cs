using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PCasa.App.Controls;

// One renderer for gallery thumbnails and GPS filmstrip frames. Both use
// the exact same symbols, sizes, spacing and visibility semantics.
internal static class ThumbnailStatusIconRenderer
{
    private static readonly Brush GpsIconBrush =
        new SolidColorBrush(Color.FromRgb(225, 68, 76));
    private static readonly Pen RestorePen = new(new SolidColorBrush(Color.FromRgb(52, 170, 76)), 1.55)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    private static readonly Geometry RestoreGeometry = Geometry.Parse(
        "M 4.7,0.8 C 7,0.3 9.6,2.1 9.9,4.5 M 8.2,3.5 L 10,5 L 11,3.1 " +
        "M 10.2,6.1 C 9.7,8.6 7.5,10.2 5.2,9.7 M 6.9,8.7 L 4.7,9.7 L 6.2,11.2 " +
        "M 3.9,9 C 1.4,7.5 1.1,4.5 2.8,2.5 M 2.8,4.7 L 2.8,2.1 L 0.6,2.7");
    private static readonly Brush RawIconBrush =
        new SolidColorBrush(Color.FromArgb(215, 33, 37, 42));
    private static readonly Geometry GpsPinGeometry = Geometry.Parse(
        "M 4.5,0 C 2,0 0,2 0,4.5 C 0,7 4.5,12 4.5,12 C 4.5,12 9,7 9,4.5 C 9,2 7,0 4.5,0 Z");
    private static readonly Geometry TagIconGeometry = Geometry.Parse(
        "M 0,0 L 6,0 L 10,4 L 5,9 L 0,4 Z");
    private static readonly Pen GpsOutlinePen = new(Brushes.White, 0.5);
    private static readonly Pen TagOutlinePen = new(Brushes.DimGray, 0.8);

    public static void Draw(DrawingContext context, Rect imageRect,
        bool hasGps, bool hasTags, bool isRaw, double dpi, bool hasRestore = false)
    {
        var x = imageRect.Right - 11;
        var y = imageRect.Bottom - 14;
        if (hasRestore)
        {
            context.PushTransform(new TranslateTransform(x - 1, y + 1));
            context.DrawGeometry(null, RestorePen, RestoreGeometry);
            context.Pop();
            x -= 15;
        }
        if (hasGps)
        {
            context.PushTransform(new TranslateTransform(x, y));
            context.DrawGeometry(GpsIconBrush, GpsOutlinePen, GpsPinGeometry);
            context.DrawEllipse(Brushes.White, null, new Point(4.5, 4.3), 1.6, 1.6);
            context.Pop();
            x -= 15;
        }
        if (hasTags)
        {
            context.PushTransform(new TranslateTransform(x, y + 2));
            context.DrawGeometry(Brushes.White, TagOutlinePen, TagIconGeometry);
            context.DrawEllipse(Brushes.DimGray, null, new Point(2.5, 2.2), 0.8, 0.8);
            context.Pop();
            x -= 19;
        }
        if (isRaw)
        {
            var label = new FormattedText("RAW", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"),
                8, Brushes.White, dpi);
            var width = label.Width + 4;
            context.DrawRoundedRectangle(RawIconBrush, null,
                new Rect(x - width + 9, y + 1, width, 11), 2, 2);
            context.DrawText(label, new Point(x - width + 11, y + 1));
        }
    }
}

// The filmstrip needs a WPF element to host the same immediate-mode glyphs
// rendered in BrowseGallerySurface. No new metadata reads, image caches or
// independent status projection are introduced.
public sealed class ThumbnailStatusIconOverlay : FrameworkElement
{
    public static readonly DependencyProperty HasGpsProperty =
        DependencyProperty.Register(nameof(HasGps), typeof(bool),
            typeof(ThumbnailStatusIconOverlay),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HasTagsProperty =
        DependencyProperty.Register(nameof(HasTags), typeof(bool),
            typeof(ThumbnailStatusIconOverlay),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsRawProperty =
        DependencyProperty.Register(nameof(IsRaw), typeof(bool),
            typeof(ThumbnailStatusIconOverlay),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HasRestoreProperty =
        DependencyProperty.Register(nameof(HasRestore), typeof(bool),
            typeof(ThumbnailStatusIconOverlay),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowIconsProperty =
        DependencyProperty.Register(nameof(ShowIcons), typeof(bool),
            typeof(ThumbnailStatusIconOverlay),
            new FrameworkPropertyMetadata(true,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public bool HasGps
    {
        get => (bool)GetValue(HasGpsProperty);
        set => SetValue(HasGpsProperty, value);
    }
    public bool HasTags
    {
        get => (bool)GetValue(HasTagsProperty);
        set => SetValue(HasTagsProperty, value);
    }
    public bool IsRaw
    {
        get => (bool)GetValue(IsRawProperty);
        set => SetValue(IsRawProperty, value);
    }
    public bool HasRestore
    {
        get => (bool)GetValue(HasRestoreProperty);
        set => SetValue(HasRestoreProperty, value);
    }
    public bool ShowIcons
    {
        get => (bool)GetValue(ShowIconsProperty);
        set => SetValue(ShowIconsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowIcons || !(HasGps || HasTags || IsRaw || HasRestore)) return;
        ThumbnailStatusIconRenderer.Draw(drawingContext,
            new Rect(0, 0, ActualWidth, ActualHeight),
            HasGps, HasTags, IsRaw,
            VisualTreeHelper.GetDpi(this).PixelsPerDip, HasRestore);
    }
}
