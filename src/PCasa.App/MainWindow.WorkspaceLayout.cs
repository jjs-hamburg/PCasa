using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // One owner of workspace proportions, sidebar visibility, and GPS modes.
    // The saved folder preference is NOT overwritten by temporary GPS expansion.
    private bool _gpsExpanded;
    private bool _gpsFullscreen;
    private bool _workspaceLayoutReady;

    private static double WorkspaceFraction(double value, double fallback,
        double minimum, double maximum)
        => double.IsFinite(value) && value >= minimum && value <= maximum
            ? value : fallback;

    private void InitializeWorkspaceLayout()
    {
        _gpsExpanded = _appearance.GpsExpanded;
        _sidebarMode = _appearance.ActiveSidebarMode is "GPS" or "Tags" or "Info"
            ? _appearance.ActiveSidebarMode : null;
        _workspaceLayoutReady = true;
        ApplyWorkspaceLayout();
        Loaded += WorkspaceWindowLoaded;
    }

    private void WorkspaceWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WorkspaceWindowLoaded;
        if (_sidebarMode is not null)
        {
            if (_sidebarMode == "Tags") RefreshQuickTags();
            if (_sidebarMode == "GPS") ActivateGpsMap();
            _ = UpdateSidebarContentAsync();
        }
    }

    private void UpdateWorkspaceLayoutPreservingGallery()
    {
        if (!_workspaceLayoutReady) return;
        QueueGalleryAnchorRestore();
        // Retain the visible image's normalized center BEFORE its available
        // width changes. Fit and manual zoom remain independent user states.
        if (_viewerOpen && _viewerFullscreenWindow is null)
            _viewerPendingImageCenter ??= CaptureViewerCenter();
        ApplyWorkspaceLayout();
        RestoreAnchorAfterSidebarLayout();
        // The gallery anchor belongs to the thumbnail view; the open viewer
        // must refit to the newly measured image viewport separately.
        ScheduleViewerViewportRelayout();
    }

    // The normal sidebar fixes the width and absolute right-edge location
    // of the GPS control block. Expand/fullscreen only enlarges the map.
    private double _gpsNormalControlWidth;
    private double _gpsNormalHeaderWidth;

    private void CaptureGpsControlPlacement()
    {
        if (_sidebarMode != "GPS" || _gpsExpanded || _gpsFullscreen)
            return;
        if (GpsControlsPanel.ActualWidth >= 360)
            _gpsNormalControlWidth = GpsControlsPanel.ActualWidth;
        if (InfoSidebarHeader.ActualWidth >= 350)
            _gpsNormalHeaderWidth = InfoSidebarHeader.ActualWidth;
    }

    private void ApplyWorkspaceLayout()
    {
        if (!_workspaceLayoutReady) return;
        var gps = _sidebarMode == "GPS";
        var sidebarOpen = _sidebarMode is not null;
        var fullscreen = gps && _gpsFullscreen;
        var expanded = gps && _gpsExpanded;
        var showFolder = _appearance.FolderSidebarVisible && !expanded;
        var folderFraction = showFolder
            ? WorkspaceFraction(_appearance.FolderSidebarFraction, 0.22, 0.12, 0.42)
            : 0;
        var sidebarFraction = sidebarOpen
            ? expanded
                ? WorkspaceFraction(_appearance.ExpandedGpsFraction, 0.55, 0.30, 0.78)
                : WorkspaceFraction(_appearance.SidebarFraction, 0.25, 0.17, 0.48)
            : 0;
        // Keep a usable gallery even after a user drags both dividers.
        sidebarFraction = Math.Min(sidebarFraction, 0.78 - folderFraction);
        var galleryFraction = 1 - folderFraction - sidebarFraction;

        FolderSidebarColumn.MinWidth = showFolder ? 170 : 0;
        FolderSidebarColumn.Width = showFolder
            ? new GridLength(folderFraction, GridUnitType.Star) : new GridLength(0);
        FolderSidebarDividerColumn.Width = new GridLength(showFolder ? 5 : 0);
        FolderSidebarPanel.Visibility = showFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderSidebarSplitter.Visibility = showFolder ? Visibility.Visible : Visibility.Collapsed;

        GalleryWorkspaceColumn.MinWidth = 240;
        GalleryWorkspaceColumn.Width = new GridLength(galleryFraction, GridUnitType.Star);
        InfoSidebarColumn.MinWidth = gps ? 420 : sidebarOpen ? 170 : 0;
        InfoSidebarColumn.Width = sidebarOpen
            ? new GridLength(sidebarFraction, GridUnitType.Star) : new GridLength(0);
        InfoSidebarDividerColumn.Width =
            new GridLength(sidebarOpen && !fullscreen ? 5 : 0);
        InfoSidebarSplitter.Visibility = sidebarOpen && !fullscreen
            ? Visibility.Visible : Visibility.Collapsed;
        InfoSidebarPanel.Visibility = sidebarOpen ? Visibility.Visible : Visibility.Collapsed;

        // Full screen is an in-place overlay of the SAME sidebar; no second
        // window, no replacement gallery or lost selection/scroll state.
        Grid.SetColumn(InfoSidebarPanel, fullscreen ? 0 : 4);
        Grid.SetColumnSpan(InfoSidebarPanel, fullscreen ? 5 : 1);
        Panel.SetZIndex(InfoSidebarPanel, fullscreen ? 10 : 0);
        UpdateGpsPhotoOverlayPlacement();

        // Control positions are anchored to the unchanged right edge of
        // the workspace, NOT to the map pane's movable left edge.
        // Width is the one measured in the normal GPS sidebar. All button
        // captions stay visible; the map alone uses the expanded area.
        if (gps && (expanded || fullscreen))
        {
            var fallback = Math.Max(400, BrowseWorkspaceGrid.ActualWidth *
                WorkspaceFraction(_appearance.SidebarFraction, 0.25, 0.17, 0.48) - 20);
            GpsControlsPanel.Width = _gpsNormalControlWidth >= 360
                ? _gpsNormalControlWidth : fallback;
            InfoSidebarHeader.Width = _gpsNormalHeaderWidth >= 350
                ? _gpsNormalHeaderWidth : Math.Max(360, fallback - 4);
            GpsControlsPanel.HorizontalAlignment = HorizontalAlignment.Right;
            InfoSidebarHeader.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            GpsControlsPanel.Width = double.NaN;
            InfoSidebarHeader.Width = double.NaN;
            GpsControlsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            InfoSidebarHeader.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        GpsCoordinatesText.Visibility = gps
            ? Visibility.Visible : Visibility.Collapsed;

        GpsExpandButton.Visibility = gps ? Visibility.Visible : Visibility.Collapsed;
        GpsExpandButton.Tag = expanded ? "expanded" : "normal";
        GpsExpandArrow.Data = System.Windows.Media.Geometry.Parse(expanded
            ? "M 7,16 L 26,16 M 17,7 L 26,16 L 17,25"
            : "M 25,16 L 6,16 M 15,7 L 6,16 L 15,25");
        GpsExpandButton.ToolTip = UiLanguage.T(expanded
            ? "GPS-Leiste verkleinern" : "GPS-Leiste vergrößern");
        GpsExpandButton.IsEnabled = !fullscreen;
        GpsFullscreenButton.Visibility = gps ? Visibility.Visible : Visibility.Collapsed;
        GpsFullscreenButton.Tag = fullscreen ? "fullscreen" : "normal";
        GpsFullscreenCorners.Data = System.Windows.Media.Geometry.Parse(fullscreen
            ? "M 4,12 L 12,12 L 12,4 M 28,12 L 20,12 L 20,4 M 20,28 L 20,20 L 28,20 M 12,28 L 12,20 L 4,20"
            : "M 12,4 L 4,4 L 4,12 M 20,4 L 28,4 L 28,12 M 28,20 L 28,28 L 20,28 M 12,28 L 4,28 L 4,20");
        FolderSidebarSymbolArrow.Data = System.Windows.Media.Geometry.Parse(
            _appearance.FolderSidebarVisible
                ? "M 16,14 L 24,14 M 20,10 L 24,14 L 20,18"
                : "M 24,14 L 16,14 M 20,10 L 16,14 L 20,18");
        GpsFullscreenButton.ToolTip = UiLanguage.T(fullscreen
            ? "GPS-Vollbild verlassen" : "GPS-Vollbild");
        FolderSidebarToggleButton.IsEnabled = !expanded && !fullscreen;
        SidePanelHeading.Text = UiLanguage.T(_sidebarMode switch
        {
            "GPS" => "GPS",
            "Tags" => "Tags",
            _ => "Bildinformationen"
        });
        TagSidebar.Visibility = _sidebarMode == "Tags"
            ? Visibility.Visible : Visibility.Collapsed;
        GeneralSidebarScroll.Visibility = _sidebarMode is "Tags" or "GPS"
            ? Visibility.Collapsed : Visibility.Visible;
        GpsMapPanel.Visibility = gps ? Visibility.Visible : Visibility.Collapsed;
        // Leaving GPS also closes its temporary search/edit context.
        // The map camera and gallery/filmstrip selection are not changed.
        if (!gps && (GpsPlaceSearchBox.Text.Length > 0 || _gpsDraft is not null))
            ClearGpsEditorDraft();
        if (!gps && GpsFullscreenImageOverlay.Visibility == Visibility.Visible)
            CloseGpsFullscreenImage();
    }

    // One photo overlay, two placements. Never transfer photo ownership to
    // the map or replace gallery selection when GPS becomes fullscreen.
    private void UpdateGpsPhotoOverlayPlacement()
    {
        var fullscreen = _sidebarMode == "GPS" && _gpsFullscreen;
        Grid.SetColumn(GpsFullscreenImageOverlay, fullscreen ? 0 : 2);
        Grid.SetColumnSpan(GpsFullscreenImageOverlay, fullscreen ? 5 : 1);
        // The fullscreen photo must cover the ENTIRE GPS workspace, map and
        // filmstrip included. No inset, fixed-size black box or translucent
        // view of the map is allowed behind the picture. The same overlay
        // covers only the gallery column in the normal two-pane layout.
        GpsFullscreenImageOverlay.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(28, 29, 32));
        GpsFullscreenImageFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
        GpsFullscreenImageFrame.VerticalAlignment = VerticalAlignment.Stretch;
        GpsFullscreenImageFrame.Margin = new Thickness(0);
        GpsFullscreenImageFrame.Width = double.NaN;
        GpsFullscreenImageFrame.Height = double.NaN;
    }

    private void BrowseWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_gpsFullscreen && _sidebarMode == "GPS")
            UpdateGpsPhotoOverlayPlacement();
    }

    private void FolderSidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarMode == "GPS" && (_gpsExpanded || _gpsFullscreen)) return;
        _appearance.FolderSidebarVisible = !_appearance.FolderSidebarVisible;
        UpdateWorkspaceLayoutPreservingGallery();
        SaveAppearance();
    }

    private void GpsExpand_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarMode != "GPS" || _gpsFullscreen) return;
        CaptureGpsControlPlacement();
        _gpsExpanded = !_gpsExpanded;
        _appearance.GpsExpanded = _gpsExpanded;
        UpdateWorkspaceLayoutPreservingGallery();
        SaveAppearance();
    }

    private void GpsFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarMode != "GPS") return;
        CaptureGpsControlPlacement();
        _gpsFullscreen = !_gpsFullscreen;
        if (!_gpsFullscreen && GpsFullscreenImageOverlay.Visibility == Visibility.Visible)
            CloseGpsFullscreenImage();
        UpdateWorkspaceLayoutPreservingGallery();
        // Entering fullscreen must keep the currently opened photo visible
        // above the map. The existing viewer and its zoom state stay intact.
        if (_gpsFullscreen && _viewerOpen)
        {
            long? imageId = _viewerImageInfo?.Id;
            if (imageId is null && _viewerSequence is not null &&
                _viewerCurrentIndex >= 0 && _viewerCurrentIndex < _viewerSequence.Count)
                imageId = _viewerSequence.At(_viewerCurrentIndex).ImageId;
            if (imageId is long openedPhotoId)
                ShowGpsFullscreenImage(openedPhotoId);
        }
    }

    private void WorkspaceSplitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_workspaceLayoutReady) return;
        QueueGalleryAnchorRestore();
        if (_viewerOpen && _viewerFullscreenWindow is null)
            _viewerPendingImageCenter ??= CaptureViewerCenter();
    }

    private void WorkspaceSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        CaptureGpsControlPlacement();
        // One proportion owner for both WPF splitters; persist ratios, never
        // the temporary pixel widths GridSplitter uses during a drag.
        var denominator = FolderSidebarColumn.ActualWidth +
            GalleryWorkspaceColumn.ActualWidth + InfoSidebarColumn.ActualWidth;
        if (denominator > 0 && !_gpsFullscreen)
        {
            if (FolderSidebarPanel.IsVisible)
                _appearance.FolderSidebarFraction =
                    Math.Clamp(FolderSidebarColumn.ActualWidth / denominator, 0.12, 0.42);
            if (_sidebarMode is not null)
            {
                var widthFraction = InfoSidebarColumn.ActualWidth / denominator;
                if (_sidebarMode == "GPS" && _gpsExpanded)
                    _appearance.ExpandedGpsFraction = Math.Clamp(widthFraction, 0.30, 0.78);
                else
                    _appearance.SidebarFraction = Math.Clamp(widthFraction, 0.17, 0.48);
            }
            SaveAppearance();
        }
        RestoreAnchorAfterSidebarLayout();
        ScheduleViewerViewportRelayout();
    }
}
