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
        if (window.FindName("FolderTreeToggleButton") is not Button)
            throw new Exception("Original compressed/full path button missing.");
        if (window.FindName("ShowAllFilesystemFoldersButton") is not Button)
            throw new Exception("Original Explorer button missing.");
        if (window.FindName("SlideshowStartButton") is not Button play ||
            play.HorizontalAlignment != HorizontalAlignment.Left)
            throw new Exception("Slideshow launcher must remain left aligned.");
        if (window.FindName("FoldersTree") is not TreeView realTree ||
            window.FindName("FlatFoldersTree") is not TreeView flatList ||
            ReferenceEquals(realTree, flatList))
            throw new Exception("Independent tree and flat folder controls missing.");
        var originalItems = realTree.Items.Cast<object>().ToArray();
        var originalState = originalItems.OfType<TreeViewItem>()
            .Select(item => (Item: item, item.IsExpanded, item.IsSelected))
            .ToArray();
        var originalSelection = realTree.SelectedItem;

        var appearanceField = typeof(MainWindow).GetField("_appearance",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance)!;
        bool IsFlat()
        {
            var settings = appearanceField.GetValue(window)!;
            return (bool)settings.GetType().GetProperty("FlatFolderView")!
                .GetValue(settings)!;
        }
        flat.IsChecked = true;
        if (!IsFlat() || realTree.Visibility != Visibility.Hidden ||
            flatList.Visibility != Visibility.Visible)
            throw new Exception("Flat radio did not display the isolated flat list.");
        if (!realTree.Items.Cast<object>().SequenceEqual(originalItems) ||
            originalState.Any(state =>
                state.Item.IsExpanded != state.IsExpanded ||
                state.Item.IsSelected != state.IsSelected) ||
            !ReferenceEquals(realTree.SelectedItem, originalSelection))
            throw new Exception("Flat view changed tree nodes, expansion or selection.");
        tree.IsChecked = true;
        if (IsFlat() || realTree.Visibility != Visibility.Visible ||
            flatList.Visibility != Visibility.Collapsed)
            throw new Exception("Tree radio did not restore the original tree.");
        if (!realTree.Items.Cast<object>().SequenceEqual(originalItems) ||
            originalState.Any(state =>
                state.Item.IsExpanded != state.IsExpanded ||
                state.Item.IsSelected != state.IsSelected) ||
            !ReferenceEquals(realTree.SelectedItem, originalSelection))
            throw new Exception("Returning from flat view rebuilt or changed the tree.");

        // Alpha.60: one 22-pixel folder row per wheel notch,
        // including half-notch touchpad deltas and clamped bounds.
        var wheelMath = typeof(MainWindow).GetMethod(
            "FolderWheelTargetOffset", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception("Slow folder-tree wheel handler missing.");
        double Wheel(double current, double delta, double maximum)
            => (double)wheelMath.Invoke(null, [current, delta, maximum])!;
        if (Math.Abs(Wheel(110, -120, 500) - 132) > .001 ||
            Math.Abs(Wheel(110, +120, 500) - 88) > .001 ||
            Math.Abs(Wheel(110, -60, 500) - 121) > .001 ||
            Wheel(4, +120, 500) != 0 ||
            Wheel(492, -120, 500) != 500)
            throw new Exception(
                "Folder wheel exceeds one row/notch, loses precision or overscrolls.");
        Console.WriteLine(
            "PASS: Alpha.60 folder wheel scrolls 22px per notch and clamps boundaries.");

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

        Console.WriteLine("PASS: Alpha.59 keeps original tree objects and selection " +
            "across flat/tree switching, retains both original folder tools, " +
            "and anchors slideshow left.");
        Console.WriteLine(
            "PASS: Alpha.58 slideshow bounds, quick tags, radio folder modes, " +
            "independent option, selected buttons, and viewport slider placement.");
    }
}
