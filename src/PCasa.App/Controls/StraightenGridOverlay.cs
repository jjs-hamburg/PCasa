using System.Windows;
using System.Windows.Media;

namespace PCasa.App.Controls;

// Render after WPF layout is measured (and after every resize or
// Visibility re-entry). No mutable Canvas.Children / SizeChanged race.
public sealed class StraightenGridOverlay : FrameworkElement
{
    public const int Divisions = 24; // Half the previous grid spacing.
    private static readonly Pen GridPen = MakePen();

    private static Pen MakePen()
    {
        var brush = new SolidColorBrush(Color.FromArgb(155, 245, 245, 245));
        brush.Freeze();
        var pen = new Pen(brush, 0.7)
        {
            DashStyle = new DashStyle(new double[] { 2, 4 }, 0)
        };
        pen.Freeze();
        return pen;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (!(width > 0) || !(height > 0)) return;
        for (var n = 1; n < Divisions; n++)
        {
            var x = n * width / Divisions;
            var y = n * height / Divisions;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
            context.DrawLine(GridPen, new Point(0, y), new Point(width, y));
        }
    }
}
