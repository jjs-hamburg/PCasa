using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // The existing gallery index already carries GPS, tag and RAW flags.
    // No second catalog query, scan, index or projection is introduced.
    private string CurrentAttributeFilter =>
        _appearance.GalleryAttributeFilter is "Gps" or "Tags" or "Raw" or "Video"
            ? _appearance.GalleryAttributeFilter : "None";

    private bool AttributeFilterActive => CurrentAttributeFilter != "None";

    private static bool AttributeFilterMatches(
        BrowseThumbnailIndexEntry image, string filter, bool inverted)
    {
        var matches = filter switch
        {
            "Gps" => image.HasGps,
            "Tags" => image.HasTags,
            "Raw" => image.IsRaw,
            "Video" => VideoFormats.IsVideo(image.FileName),
            _ => true
        };
        return filter == "None" || (inverted ? !matches : matches);
    }

    private void SyncGalleryAttributeFilterUi()
    {
        if (GalleryAttributeFilterLabel is null) return;
        var filter = CurrentAttributeFilter;
        var inverse = _appearance.GalleryAttributeFilterInverted;
        GalleryAttributeFilterLabel.Text = UiLanguage.T(filter switch
        {
            "Gps" => inverse ? "Ohne GPS" : "Mit GPS",
            "Tags" => inverse ? "Ohne Tags" : "Mit Tags",
            "Raw" => inverse ? "Ohne RAW" : "Nur RAW",
            "Video" => inverse ? "Nur Fotos" : "Nur Videos",
            _ => "Filter"
        });
        GalleryAttributeGpsMenu.IsChecked = filter == "Gps";
        GalleryAttributeTagsMenu.IsChecked = filter == "Tags";
        GalleryAttributeRawMenu.IsChecked = filter == "Raw";
        GalleryAttributeVideoMenu.IsChecked = filter == "Video";
        GalleryAttributeFilterInvertButton.IsEnabled = filter != "None";
        GalleryAttributeFilterClearButton.IsEnabled = filter != "None";
        // Rotate the 24x24 CANVAS around its own center. Rotating a Path
        // with a second, absolute 12/12 center moved it outside the button.
        GalleryAttributeFilterFunnel.RenderTransform =
            new RotateTransform(inverse && filter != "None" ? 180 : 0);
        GalleryAttributeFilterInvertButton.ToolTip = UiLanguage.T(
            inverse ? "Eigenschaftsfilter normal anzeigen" : "Eigenschaftsfilter umkehren");
    }

    private void GalleryAttributeFilterSelector_Click(object sender, RoutedEventArgs e)
    {
        if (GalleryAttributeFilterSelectorButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = GalleryAttributeFilterSelectorButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void GalleryAttributeFilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string choice }) return;
        await ChangeGalleryAttributeFilterAsync(choice, false);
    }

    private async void GalleryAttributeFilterInvert_Click(object sender, RoutedEventArgs e)
    {
        if (!AttributeFilterActive) return;
        await ChangeGalleryAttributeFilterAsync(
            CurrentAttributeFilter, !_appearance.GalleryAttributeFilterInverted);
    }

    private async void GalleryAttributeFilterClear_Click(object sender, RoutedEventArgs e)
        => await ChangeGalleryAttributeFilterAsync("None", false);

    // Keeps the photo already in sight if it survives the property filter.
    // Otherwise choose the closest still-visible image in the ORIGINAL
    // gallery order; prefer the next image at an equal distance.
    private static long? NearestPropertyFilterImage(
        IReadOnlyList<BrowseThumbnailIndexEntry> original,
        IReadOnlyList<BrowseThumbnailIndexEntry> visible,
        long? sourceImageId, long? sourceFolderId)
    {
        if (visible.Count == 0) return null;
        var visibleIds = visible.Select(x => x.ImageId).ToHashSet();
        var startingIndex = -1;
        if (sourceImageId is long imageId)
        {
            for (var i = 0; i < original.Count; i++)
                if (original[i].ImageId == imageId)
                {
                    startingIndex = i;
                    break;
                }
        }
        if (startingIndex < 0 && sourceFolderId is long folderId)
        {
            for (var i = 0; i < original.Count; i++)
                if (original[i].PhotoFolderId == folderId)
                {
                    startingIndex = i;
                    break;
                }
        }
        if (startingIndex < 0) return visible[0].ImageId;
        for (var distance = 0; distance < original.Count; distance++)
        {
            var after = startingIndex + distance;
            if (after < original.Count &&
                visibleIds.Contains(original[after].ImageId))
                return original[after].ImageId;
            var before = startingIndex - distance;
            if (before >= 0 && visibleIds.Contains(original[before].ImageId))
                return original[before].ImageId;
        }
        return visible[0].ImageId;
    }

    private long _attributeFilterChangeVersion;

    private async Task ChangeGalleryAttributeFilterAsync(string next, bool inverted)
    {
        if (next is not ("None" or "Gps" or "Tags" or "Raw" or "Video")) return;
        if (CurrentAttributeFilter == next &&
            _appearance.GalleryAttributeFilterInverted == inverted) return;

        // Capture BEFORE replacing the projection. Never use ScrollToTop()
        // for a property-filter change; the previous image's identity owns
        // the focal point even if the user has not selected it.
        var before = CaptureGalleryAnchor();
        var originalFolder = BrowseSurface.GetFolderIdAtOffset(
            BrowseScrollViewer.VerticalOffset + 4);
        var originalOffset = BrowseScrollViewer.VerticalOffset;
        var change = ++_attributeFilterChangeVersion;
        _appearance.GalleryAttributeFilter = next;
        _appearance.GalleryAttributeFilterInverted = inverted;
        SyncGalleryAttributeFilterUi();
        SaveAppearance();
        if (!_browseInitialized || _browsePageCache is null) return;
        try
        {
            if (_scanGalleryPublicationDeferred)
            {
                _scanGalleryPublicationDeferred = false;
                await RefreshBrowseGalleryAsync(forceIndexReload: true,
                    warmViewport: false);
            }
            else
                await ApplyTagFilterAsync(scrollToTop: false);

            if (Dispatcher.HasShutdownStarted ||
                change != _attributeFilterChangeVersion) return;

            var targetImage = NearestPropertyFilterImage(
                _browseIndex, _displayedBrowseIndex,
                before?.ImageId, originalFolder);
            double? target = targetImage is long id
                ? BrowseSurface.GetImageAnchorOffset(
                    id, before?.RelativeY ?? -36)
                : null;
            if (target is double top)
            {
                BrowseScrollViewer.ScrollToVerticalOffset(
                    Math.Max(0, Math.Min(top, BrowseScrollViewer.ScrollableHeight)));
                BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                    Math.Max(1, BrowseScrollViewer.ViewportHeight));
            }
            else if (_displayedBrowseIndex.Count > 0)
                BrowseScrollViewer.ScrollToVerticalOffset(
                    Math.Min(originalOffset, BrowseScrollViewer.ScrollableHeight));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Gallery.AttributeFilter", ex);
            StatusText.Text = UiLanguage.T("Eigenschaftsfilter fehlgeschlagen");
        }
    }

    // Navigation uses ONLY the visible folder sections from the SAME
    // virtual surface; it never selects a photo or expands the tree.
    private async void GalleryNavigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } ||
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
            double? destination = null;
            var folderId = BrowseSurface.GetFolderIdAtOffset(
                BrowseScrollViewer.VerticalOffset + 4);
            switch (action)
            {
                case "GalleryStart":
                    destination = 0;
                    break;
                case "GalleryEnd":
                    destination = BrowseScrollViewer.ScrollableHeight;
                    break;
                case "FolderStart":
                    if (folderId is long first)
                        destination = BrowseSurface.GetFolderOffset(first);
                    break;
                case "FolderEnd":
                    if (folderId is long last)
                        destination = BrowseSurface.GetFolderTrailingOffset(last,
                            Math.Max(1, BrowseScrollViewer.ViewportHeight));
                    break;
                case "PreviousFolder":
                case "NextFolder":
                    var folderIds = BrowseSurface.GetVisibleFolderIds();
                    var pos = folderId is long current ? folderIds.IndexOf(current) : -1;
                    var target = pos + (action == "PreviousFolder" ? -1 : 1);
                    if (pos >= 0 && target >= 0 && target < folderIds.Count)
                        destination = BrowseSurface.GetFolderOffset(folderIds[target]);
                    break;
            }
            if (destination is not double top) return;
            BrowseScrollViewer.ScrollToVerticalOffset(
                Math.Clamp(top, 0, BrowseScrollViewer.ScrollableHeight));
            BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
            if (BrowseSurface.GetFolderIdAtOffset(
                BrowseScrollViewer.VerticalOffset + 4) is long targetFolder)
                UpdateActiveFolderDisplay(targetFolder, updateTreeSelection: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.Navigate", ex); }
    }
}
