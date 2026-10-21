using System.Windows;
using System.Windows.Controls;

namespace PCasa.App;

public partial class MainWindow
{
    private void BrowseThumbnailShadowMenu_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        item.IsChecked = _appearance.ShowThumbnailShadows == true;
        BrowseSurface.SetShadowEnabled(item.IsChecked);
    }

    private void BrowseThumbnailShadowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        _appearance.ShowThumbnailShadows = item.IsChecked;
        SaveAppearance();
        BrowseSurface.SetShadowEnabled(item.IsChecked);
    }
}
