using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PCasa.App;

public partial class MainWindow
{
    private void MenuPopup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Popup popup || popup.PlacementTarget is not MenuItem owner)
            return;

        // Do not let WPF's built-in Right/Left fallback produce alternating
        // directions. Each submenu anchors to its own parent entry.
        popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
        {
            var point = owner.Role is MenuItemRole.TopLevelHeader
                ? new Point(0, targetSize.Height)
                : new Point(targetSize.Width, 0);
            return new[]
            {
                new CustomPopupPlacement(point, PopupPrimaryAxis.None)
            };
        };
    }
}
