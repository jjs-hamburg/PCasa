using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // A single user-controlled bookmark, independent of gallery selection
    // and of the transient WPF scroll offset. It remains stable when the
    // virtual gallery is rescanned or folders above it change height.
    private (long FolderId, double RelativeTop,
        (long ImageId, double RelativeY)? ImageAnchor)? _rememberedGalleryPosition;

    private void GalleryRememberPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (GalleryRememberPositionToggle is null ||
            GalleryReturnToRememberedButton is null)
            return;

        if (GalleryRememberPositionToggle.IsChecked != true)
        {
            _rememberedGalleryPosition = null;
            GalleryReturnToRememberedButton.IsEnabled = false;
            GalleryRememberPositionToggle.ToolTip =
                UiLanguage.T("Aktuelle Position merken");
            return;
        }

        var top = BrowseScrollViewer.VerticalOffset;
        var folderId = BrowseSurface.GetFolderIdAtOffset(top + 4);
        var folderTop = folderId is long id
            ? BrowseSurface.GetFolderOffset(id) : null;
        if (folderId is not long folder || folderTop is not double origin)
        {
            GalleryRememberPositionToggle.IsChecked = false;
            return;
        }
        _rememberedGalleryPosition = (folder, top - origin,
            CaptureGalleryAnchor());
        GalleryRememberPositionToggle.ToolTip =
            UiLanguage.T("Gemerkte Position löschen");
        GalleryReturnToRememberedButton.IsEnabled = true;
    }

    private async void GalleryReturnToRemembered_Click(object sender, RoutedEventArgs e)
    {
        if (_rememberedGalleryPosition is not { } saved ||
            !_browseInitialized) return;

        try
        {
            MarkExplicitGalleryNavigation();
            if (_scanGalleryPublicationDeferred)
            {
                _scanGalleryPublicationDeferred = false;
                await RefreshBrowseGalleryAsync(forceIndexReload: true,
                    warmViewport: false);
                if (Dispatcher.HasShutdownStarted || _shutdownStarted) return;
            }

            BrowseScrollViewer.UpdateLayout();
            double? target = saved.ImageAnchor is { } image
                ? BrowseSurface.GetImageAnchorOffset(
                    image.ImageId, image.RelativeY)
                : null;
            if (target is null &&
                BrowseSurface.GetFolderOffset(saved.FolderId) is double folderTop)
                target = folderTop + saved.RelativeTop;

            if (target is not double top)
            {
                StatusText.Text = UiLanguage.T(
                    "Position nicht mehr in der aktuellen Galerie sichtbar");
                return;
            }

            BrowseScrollViewer.ScrollToVerticalOffset(
                Math.Clamp(top, 0, BrowseScrollViewer.ScrollableHeight));
            BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
            if (BrowseSurface.GetFolderIdAtOffset(
                BrowseScrollViewer.VerticalOffset + 4) is long folder)
                UpdateActiveFolderDisplay(folder, updateTreeSelection: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.RememberedPosition", ex);
        }
    }
}
