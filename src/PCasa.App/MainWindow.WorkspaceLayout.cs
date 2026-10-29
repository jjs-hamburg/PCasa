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
            _ = UpdateSidebarContentAsync();
        }
    }

    private void UpdateWorkspaceLayoutPreservingGallery()
    {
        if (!_workspaceLayoutReady) return;
        QueueGalleryAnchorRestore();
        ApplyWorkspaceLayout();
        RestoreAnchorAfterSidebarLayout();
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
        InfoSidebarColumn.MinWidth = sidebarOpen ? 170 : 0;
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

        GpsExpandButton.Visibility = gps ? Visibility.Visible : Visibility.Collapsed;
        GpsExpandButton.Tag = expanded ? "expanded" : "normal";
        GpsExpandArrow.Data = System.Windows.Media.Geometry.Parse(expanded
            ? "M 5,14 L 14,14 M 10,10 L 14,14 L 10,18"
            : "M 13,14 L 4,14 M 8,10 L 4,14 L 8,18");
        GpsExpandButton.ToolTip = UiLanguage.T(expanded
            ? "GPS-Leiste verkleinern" : "GPS-Leiste vergrößern");
        GpsExpandButton.IsEnabled = !fullscreen;
        GpsFullscreenButton.Visibility = gps ? Visibility.Visible : Visibility.Collapsed;
        GpsFullscreenButton.Tag = fullscreen ? "fullscreen" : "normal";
        GpsFullscreenArrows.Data = System.Windows.Media.Geometry.Parse(fullscreen
            ? "M 5,5 L 12,12 M 23,5 L 16,12 M 23,23 L 16,16 M 5,23 L 12,16"
            : "M 12,12 L 5,5 M 16,12 L 23,5 M 16,16 L 23,23 M 12,16 L 5,23");
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
        GeneralSidebarScroll.Visibility = _sidebarMode == "Tags"
            ? Visibility.Collapsed : Visibility.Visible;
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
        _gpsExpanded = !_gpsExpanded;
        _appearance.GpsExpanded = _gpsExpanded;
        UpdateWorkspaceLayoutPreservingGallery();
        SaveAppearance();
    }

    private void GpsFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarMode != "GPS") return;
        _gpsFullscreen = !_gpsFullscreen;
        UpdateWorkspaceLayoutPreservingGallery();
    }

    private void WorkspaceSplitter_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (_workspaceLayoutReady) QueueGalleryAnchorRestore();
    }

    private void WorkspaceSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
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
    }
}
