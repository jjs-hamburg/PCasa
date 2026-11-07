using System.Windows;

namespace PCasa.App;

public partial class MainWindow
{
    private void PicasaGalleryScrollBar_Loaded(object sender, RoutedEventArgs e)
    {
        // Exactly one scroll authority: the original WPF BrowseScrollViewer.
        // The new control never touches the scheduler or gallery projection.
        PicasaGalleryScrollBar.Attach(BrowseScrollViewer);
    }
}
