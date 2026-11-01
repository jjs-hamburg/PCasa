using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PCasa.App;

public partial class MainWindow
{
    // WPF can retain a top MenuItem's prior desired width when a localized
    // header becomes shorter (its dropdown participates in the measure).
    // Size the FOUR top-level header items from their actual localized
    // text, not from submenu content or previously displayed labels.
    private void MainMenu_Loaded(object sender, RoutedEventArgs e)
        => UpdateMainMenuCaptionWidths();

    private void UpdateMainMenuCaptionWidths()
    {
        if (MainMenu is null) return;
        foreach (var item in MainMenu.Items.OfType<MenuItem>())
        {
            var probe = new TextBlock
            {
                Text = item.Header?.ToString() ?? string.Empty,
                FontFamily = item.FontFamily,
                FontSize = item.FontSize,
                FontWeight = item.FontWeight,
                FontStyle = item.FontStyle,
                FontStretch = item.FontStretch
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // 8 DIPs either side of caption, as in the top-level WPF
            // template. The 12 DIP menu-item margin is a separate gap.
            item.Width = Math.Ceiling(probe.DesiredSize.Width) + 16;
        }
    }

    private void QueueMainMenuCaptionSizing()
        => Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(UpdateMainMenuCaptionWidths));
}
