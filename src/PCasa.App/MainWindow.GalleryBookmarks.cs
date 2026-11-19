using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // One user-controlled bookmark; independent of image selection or the
    // absolute pixel offset in the continuously changing gallery.
    private (long FolderId, double RelativeTop,
        (long ImageId, double RelativeY)? ImageAnchor)? _rememberedGalleryPosition;

    // Single-use departure snapshot, available ONLY after jumping to
    // the bookmark. No navigation stack or independent job mechanism.
    private (long FolderId, double RelativeTop,
        (long ImageId, double RelativeY)? ImageAnchor)? _bookmarkDeparturePosition;

    private (long FolderId, double RelativeTop,
        (long ImageId, double RelativeY)? ImageAnchor)? CaptureBookmarkPosition()
    {
        var top = BrowseScrollViewer.VerticalOffset;
        var folderId = BrowseSurface.GetFolderIdAtOffset(top + 4);
        var folderTop = folderId is long id
            ? BrowseSurface.GetFolderOffset(id) : null;
        return folderId is long folder && folderTop is double origin
            ? (folder, top - origin, CaptureGalleryAnchor())
            : null;
    }

    private double? ResolveBookmarkPosition(
        (long FolderId, double RelativeTop,
         (long ImageId, double RelativeY)? ImageAnchor) saved)
    {
        var anchored = saved.ImageAnchor is { } image
            ? BrowseSurface.GetImageAnchorOffset(image.ImageId, image.RelativeY)
            : null;
        return anchored ?? (BrowseSurface.GetFolderOffset(saved.FolderId) is double origin
            ? origin + saved.RelativeTop : null);
    }

    private void ScrollToBookmarkPosition(double top)
    {
        var destination = Math.Clamp(top, 0, BrowseScrollViewer.ScrollableHeight);
        if (BrowseSurface.GetFolderIdAtOffset(destination + 4) is long folder)
        {
            ScrollToExplicitGalleryFolder(folder, destination);
            UpdateActiveFolderDisplay(folder, updateTreeSelection: false);
        }
        else
        {
            BrowseScrollViewer.ScrollToVerticalOffset(destination);
            BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }
        if (GalleryAtLiveEdge()) PublishDeferredScanGallery();
    }

    private void ClearBookmarkDeparture()
    {
        _bookmarkDeparturePosition = null;
        if (GalleryReturnToDepartureButton is not null)
            GalleryReturnToDepartureButton.IsEnabled = false;
    }

    private void GalleryRememberPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (GalleryRememberPositionToggle is null ||
            GalleryReturnToRememberedButton is null)
            return;

        if (GalleryRememberPositionToggle.IsChecked != true)
        {
            _rememberedGalleryPosition = null;
            ClearBookmarkDeparture();
            GalleryReturnToRememberedButton.IsEnabled = false;
            GalleryRememberPositionToggle.ToolTip =
                UiLanguage.T("Aktuelle Position merken");
            return;
        }

        var position = CaptureBookmarkPosition();
        if (position is null)
        {
            GalleryRememberPositionToggle.IsChecked = false;
            return;
        }
        _rememberedGalleryPosition = position;
        ClearBookmarkDeparture();
        GalleryRememberPositionToggle.ToolTip =
            UiLanguage.T("Gemerkte Position löschen");
        GalleryReturnToRememberedButton.IsEnabled = true;
    }

    private void GalleryReturnToRemembered_Click(object sender, RoutedEventArgs e)
    {
        if (_rememberedGalleryPosition is not { } saved ||
            !_browseInitialized) return;

        try
        {
            // Capture departure BEFORE a postponed scan can update the
            // virtual gallery. Resolve by image identity on the new layout.
            var departure = CaptureBookmarkPosition();
            MarkExplicitGalleryNavigation();
            // The saved image identity is resolved in the currently
            // published surface; scanner refresh never blocks a jump.
            var target = ResolveBookmarkPosition(saved);
            if (target is not double top)
            {
                StatusText.Text = UiLanguage.T(
                    "Position nicht mehr in der aktuellen Galerie sichtbar");
                return;
            }

            var destination = Math.Clamp(top, 0,
                BrowseScrollViewer.ScrollableHeight);
            ClearBookmarkDeparture();
            if (departure is { } original &&
                Math.Abs(BrowseScrollViewer.VerticalOffset - destination) > 0.5)
            {
                _bookmarkDeparturePosition = original;
                GalleryReturnToDepartureButton.IsEnabled = true;
            }
            ScrollToBookmarkPosition(destination);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.RememberedPosition", ex);
        }
    }

    private void GalleryReturnToDeparture_Click(object sender, RoutedEventArgs e)
    {
        if (_bookmarkDeparturePosition is not { } original ||
            !_browseInitialized) return;
        try
        {
            MarkExplicitGalleryNavigation();
            // The saved image identity is resolved in the currently
            // published surface; scanner refresh never blocks a jump.
            var target = ResolveBookmarkPosition(original);
            if (target is not double top)
            {
                StatusText.Text = UiLanguage.T(
                    "Position nicht mehr in der aktuellen Galerie sichtbar");
                return;
            }
            ScrollToBookmarkPosition(top);
            // Returning consumes the departure; the bookmark stays intact.
            ClearBookmarkDeparture();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.BookmarkDeparture", ex);
        }
    }
}
