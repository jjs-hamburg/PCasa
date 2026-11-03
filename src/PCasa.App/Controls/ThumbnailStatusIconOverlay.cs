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
    private static readonly Brush RawIconBrush =
        new SolidColorBrush(Color.FromArgb(215, 33, 37, 42));
    private static readonly Geometry GpsPinGeometry = Geometry.Parse(
        "M 4.5,0 C 2,0 0,2 0,4.5 C 0,7 4.5,12 4.5,12 C 4.5,12 9,7 9,4.5 C 9,2 7,0 4.5,0 Z");
    private static readonly Geometry TagIconGeometry = Geometry.Parse(
        "M 0,0 L 6,0 L 10,4 L 5,9 L 0,4 Z");
    private static readonly Pen GpsOutlinePen = new(Brushes.White, 0.5);
    private static readonly Pen TagOutlinePen = new(Brushes.DimGray, 0.8);

    public static void Draw(DrawingContext context, Rect imageRect,
        bool hasGps, bool hasTags, bool isRaw, double dpi)
    {
        var x = imageRect.Right - 11;
        var y = imageRect.Bottom - 14;
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
    public bool ShowIcons
    {
        get => (bool)GetValue(ShowIconsProperty);
        set => SetValue(ShowIconsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowIcons || !(HasGps || HasTags || IsRaw)) return;
        ThumbnailStatusIconRenderer.Draw(drawingContext,
            new Rect(0, 0, ActualWidth, ActualHeight),
            HasGps, HasTags, IsRaw,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
