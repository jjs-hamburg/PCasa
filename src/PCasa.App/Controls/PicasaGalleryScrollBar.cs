using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCasa.App.Controls;

/// <summary>
/// Picasa-style, rate-controlled gallery scrollbar. The central grip is a
/// velocity joystick, not a second scroll-position authority. All movement
/// goes through the existing ScrollViewer and its ScrollChanged pipeline.
/// No database, scheduler, gallery layout or thumbnail state is owned here.
/// </summary>
public sealed class PicasaGalleryScrollBar : UserControl
{
    private readonly Grid _track;
    private readonly Thumb _grip;
    private readonly DispatcherTimer _clock;
    private readonly TranslateTransform _gripOffset = new();
    private ScrollViewer? _viewer;
    private bool _dragging;
    private double _dragDisplacement;
    private double _pageStart;
    private double _pageTarget;
    private double _pageElapsed;
    private bool _paging;
    private long _lastTick;
    private const double AnimationSeconds = 0.27;

    public PicasaGalleryScrollBar()
    {
        Width = 25;
        HorizontalAlignment = HorizontalAlignment.Right;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = surface;

        // Outside buttons page; inside buttons move by one short step.
        // All four buttons and their blue Picasa-style pictograms share
        // exactly the same dimensions, unlike the larger center joystick.
        AddArrow(surface, 0, "page-up", "Eine Seite nach oben", () => MovePages(-1));
        AddArrow(surface, 1, "line-up", "Eine Zeile nach oben", () => MoveLines(-1));

        _track = new Grid { MinHeight = 54, Cursor = Cursors.Hand };
        _track.SetResourceReference(BackgroundProperty, "PcasaPanelAltBrush");
        Grid.SetRow(_track, 2);
        surface.Children.Add(_track);
        _track.MouseLeftButtonDown += Track_MouseLeftButtonDown;

        _grip = new Thumb
        {
            Height = 86,
            Margin = new Thickness(1, 0, 1, 0),
            RenderTransform = _gripOffset,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Cursor = Cursors.SizeNS,
            ToolTip = "Ziehen: Scrollgeschwindigkeit steuern"
        };
        var gripTemplate = new ControlTemplate(typeof(Thumb));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BackgroundProperty, "PcasaSelectionBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PcasaMutedTextBrush");
        // Large center control has Picasa's opposed blue arrows, not
        // a conventional scrollbar position thumb / three-line glyph.
        var symbols = new FrameworkElementFactory(typeof(Grid));
        var up = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        up.SetValue(System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 10,2 L 3,11 H 17 Z"));
        up.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.None);
        up.SetValue(FrameworkElement.WidthProperty, 20d);
        up.SetValue(FrameworkElement.HeightProperty, 14d);
        up.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        up.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 9, 0, 0));
        up.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "PcasaAccentBrush");
        symbols.AppendChild(up);
        var down = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        down.SetValue(System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M 3,3 H 17 L 10,12 Z"));
        down.SetValue(System.Windows.Shapes.Path.StretchProperty, Stretch.None);
        down.SetValue(FrameworkElement.WidthProperty, 20d);
        down.SetValue(FrameworkElement.HeightProperty, 14d);
        down.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
        down.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 9));
        down.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "PcasaAccentBrush");
        symbols.AppendChild(down);
        border.AppendChild(symbols);
        gripTemplate.VisualTree = border;
        _grip.Template = gripTemplate;
        _grip.DragStarted += Grip_DragStarted;
        _grip.DragDelta += Grip_DragDelta;
        _grip.DragCompleted += Grip_DragCompleted;
        _track.Children.Add(_grip);

        AddArrow(surface, 3, "line-down", "Eine Zeile nach unten", () => MoveLines(1));
        AddArrow(surface, 4, "page-down", "Eine Seite nach unten", () => MovePages(1));

        _clock = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _clock.Tick += Clock_Tick;
        Unloaded += (_, _) => StopMotion();
    }

    private static void AddArrow(Grid parent, int row, string symbol,
        string tooltip, Action action)
    {
        // Same 16x16 icon box and stroke/visual weight on all four buttons.
        // A page symbol has an end-stop bar; a line symbol only a chevron.
        var shape = symbol switch
        {
            "page-up" => "M 8,4 L 2,11 H 14 Z M 2,1 H 14 V 2.8 H 2 Z",
            "line-up" => "M 8,3 L 2,11 H 14 Z",
            "line-down" => "M 2,5 H 14 L 8,13 Z",
            "page-down" => "M 2,5 H 14 L 8,12 Z M 2,13.2 H 14 V 15 H 2 Z",
            _ => throw new ArgumentOutOfRangeException(nameof(symbol))
        };
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(shape),
            Stretch = Stretch.Uniform,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(System.Windows.Shapes.Path.FillProperty,
            "PcasaAccentBrush");
        var arrow = new RepeatButton
        {
            Height = 23,
            Margin = new Thickness(1),
            Padding = new Thickness(0),
            Content = icon,
            ToolTip = tooltip,
            Delay = 360,
            Interval = 95,
            Focusable = false
        };
        arrow.SetResourceReference(BackgroundProperty, "PcasaPanelAltBrush");
        arrow.Click += (_, _) => action();
        Grid.SetRow(arrow, row);
        parent.Children.Add(arrow);
    }

    public void Attach(ScrollViewer scrollViewer)
    {
        _viewer = scrollViewer;
    }

    public void StopMotion()
    {
        _paging = false;
        _dragging = false;
        _dragDisplacement = 0;
        _gripOffset.Y = 0;
        _clock.Stop();
    }

    private void MoveLines(int direction)
    {
        StopMotion();
        if (_viewer is null) return;
        ScrollBy(direction * 64);
    }

    private void MovePages(int direction)
    {
        if (_viewer is null) return;
        // Repeated page arrows use the same smooth motion as track clicks.
        StartPage(direction);
    }

    private void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewer is null || e.OriginalSource is Thumb ||
            IsFromGrip(e.OriginalSource as DependencyObject))
            return;
        var location = e.GetPosition(_track).Y;
        var center = _track.ActualHeight / 2;
        if (Math.Abs(location - center) <= _grip.ActualHeight / 2) return;
        StartPage(location < center ? -1 : 1);
        e.Handled = true;
    }

    private bool IsFromGrip(DependencyObject? element)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, _grip)) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        StopMotion();
        _dragging = true;
        _dragDisplacement = 0;
        _gripOffset.Y = 0;
        StartClock();
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_dragging) return;
        // Thumb captures the mouse, so the grip really moves while held.
        // The travel range depends on the track, never on catalog position.
        // Clamping prevents the large grip covering the four arrow buttons.
        var maxTravel = Math.Max(0,
            (_track.ActualHeight - _grip.ActualHeight) / 2);
        _dragDisplacement = Math.Clamp(
            _dragDisplacement + e.VerticalChange, -maxTravel, maxTravel);
        _gripOffset.Y = _dragDisplacement;
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e)
        => StopMotion();

    private void StartPage(int direction)
    {
        if (_viewer is null) return;
        _dragging = false;
        _paging = true;
        _pageStart = _viewer.VerticalOffset;
        _pageTarget = Math.Clamp(_pageStart +
            direction * Math.Max(1, _viewer.ViewportHeight * 0.9),
            0, _viewer.ScrollableHeight);
        _pageElapsed = 0;
        StartClock();
    }

    private void StartClock()
    {
        _lastTick = Stopwatch.GetTimestamp();
        _clock.Start();
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        if (_viewer is null || _viewer.ScrollableHeight <= 0)
        {
            StopMotion();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        // Cap delta after a blocked UI frame: no overshoot or giant jumps.
        var dt = Math.Clamp((now - _lastTick) / (double)Stopwatch.Frequency,
            0, 0.05);
        _lastTick = now;

        if (_dragging)
        {
            var distance = _dragDisplacement;
            if (Math.Abs(distance) < 5) return;
            // Precise near center, increasingly fast when pulled away.
            // Speed is measured in viewports/second, not image counts.
            var pixelsPerSecond = SpeedPixelsPerSecond(distance,
                _viewer.ViewportHeight, _viewer.ScrollableHeight);
            ScrollBy(Math.Sign(distance) * pixelsPerSecond * dt);
        }
        else if (_paging)
        {
            _pageElapsed += dt;
            var t = Math.Clamp(_pageElapsed / AnimationSeconds, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            _viewer.ScrollToVerticalOffset(Math.Clamp(
                _pageStart + (_pageTarget - _pageStart) * eased,
                0, _viewer.ScrollableHeight));
            if (t >= 1)
            {
                _paging = false;
                _clock.Stop();
            }
        }
        else
        {
            _clock.Stop();
        }
    }

    // Pure movement curve: exposed for a deterministic regression test.
    public static double SpeedInViewportsPerSecond(double displacement)
    {
        if (!double.IsFinite(displacement) || Math.Abs(displacement) < 5)
            return 0;
        var normalized = Math.Clamp((Math.Abs(displacement) - 5) / 115, 0, 1);
        return 0.35 + 20 * normalized * normalized;
    }

    // Enormous libraries need a fast traversal mode: a fully displaced
    // grip can cross the catalog in seconds, rather than needing thousands
    // of viewport-sized steps. Fine movement near center stays precise.
    public static double SpeedPixelsPerSecond(double displacement,
        double viewportHeight, double scrollableHeight)
    {
        if (!double.IsFinite(displacement) ||
            !double.IsFinite(viewportHeight) ||
            !double.IsFinite(scrollableHeight) ||
            Math.Abs(displacement) < 5) return 0;
        var viewport = Math.Max(1, viewportHeight);
        var normalized = Math.Clamp((Math.Abs(displacement) - 5) / 115, 0, 1);
        return 0.35 * viewport + normalized * normalized *
            Math.Max(20 * viewport, 0.15 * Math.Max(0, scrollableHeight));
    }

    private void ScrollBy(double pixels)
    {
        if (_viewer is null || !double.IsFinite(pixels)) return;
        _viewer.ScrollToVerticalOffset(Math.Clamp(
            _viewer.VerticalOffset + pixels, 0, _viewer.ScrollableHeight));
    }
}
