using System.Windows;
using System.Windows.Controls;
using PCasa.App;
using PCasa.App.Services;

internal static class Alpha58WindowSmoke
{
    public static void Run(MainWindow window)
    {
        var slideshow = new SlideshowSettings { IntervalSeconds = 3600 };
        slideshow.Normalize();
        if (slideshow.IntervalSeconds != 300)
            throw new Exception("Alpha.58 must cap slideshow intervals at 300 seconds.");
        slideshow.IntervalSeconds = 0;
        if (slideshow.Normalize().IntervalSeconds != 1)
            throw new Exception("Alpha.58 must keep the 1-second lower bound.");

        if (window.FindName("TagQuickPanel") is not null)
            throw new Exception("The removed quick-tag panel is still present.");

        var tree = window.FindName("FolderTreeViewRadio") as RadioButton;
        var flat = window.FindName("FolderFlatViewRadio") as RadioButton;
        if (tree is null || flat is null ||
            tree.GroupName != flat.GroupName || string.IsNullOrWhiteSpace(tree.GroupName))
            throw new Exception("Folder tree and flat view must use exclusive radio controls.");
        if (window.FindName("FolderTreeToggleButton") is not null)
            throw new Exception("Old folder-hierarchy control was not moved from the folder bar.");

        var slider = window.FindName("StraightenAngleSlider") as Slider;
        var viewer = window.FindName("PhotoViewerPanel") as Grid;
        if (slider is null || viewer is null ||
            !ReferenceEquals(slider.Parent, viewer) ||
            slider.VerticalAlignment != VerticalAlignment.Bottom ||
            slider.HorizontalAlignment != HorizontalAlignment.Center ||
            slider.Margin.Bottom < 8)
            throw new Exception(
                "Only the straightening angle slider must overlay the bottom of the viewport.");

        var optionsType = typeof(AppearanceDialogValues);
        if (optionsType.GetProperty("HideCollapsedInFlat") is null)
            throw new Exception("Independent flat-folder collapsed option missing.");
        foreach (var name in new[]
                 { "FolderSidebarToggleButton", "TagSidebarButton",
                   "GpsSidebarButton", "InfoSidebarButton" })
        {
            if (window.FindName(name) is not Button button ||
                button.Style is null)
                throw new Exception("Missing selected-state button: " + name);
        }

        Console.WriteLine(
            "PASS: Alpha.58 slideshow bounds, quick tags, radio folder modes, " +
            "independent option, active buttons, and viewport slider placement.");
    }
}
