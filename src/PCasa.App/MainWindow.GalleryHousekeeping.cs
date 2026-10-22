using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _bulkFolderExpansion;
    private void ThumbnailSpacing_Click(object sender, RoutedEventArgs e)
    {
        var original = _appearance.ThumbnailSpacing;
        ReflowSpacingPreservingScroll(BrowseSurface.BeginSpacingPreview);
        var accepted = false;
        var window = new Window
        {
            Owner = this,
            Title = "Thumbnail-Abstand",
            Width = 365,
            Height = 165,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left + Math.Max(0, ActualWidth * 0.61 - 180),
            Top = Top + Math.Max(90, ActualHeight * 0.14),
            ShowInTaskbar = false
        };
        window.SetResourceReference(Window.BackgroundProperty, "PcasaPanelBrush");
        window.SetResourceReference(Window.ForegroundProperty, "PcasaTextBrush");

        var panel = new Grid { Margin = new Thickness(18, 12, 18, 12) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = "Abstand zwischen den Vorschaubildern", Margin = new Thickness(0, 0, 0, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        panel.Children.Add(heading);
        var slider = new Slider { Minimum = 0, Maximum = 40, Value = original, TickFrequency = 1, IsSnapToTickEnabled = false };
        Grid.SetRow(slider, 1);
        panel.Children.Add(slider);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Okay", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        // Use the same tested template as the status toolbar. Native Windows
        // button hover visuals ignored the theme foreground in light mode.
        foreach (var button in new[] { ok, cancel })
            button.Style = (Style)FindResource("PcasaStatusButtonStyle");
        controls.Children.Add(ok);
        controls.Children.Add(cancel);
        Grid.SetRow(controls, 2);
        panel.Children.Add(controls);
        window.Content = panel;
        slider.ValueChanged += (_, args) => ApplySpacingPreservingScroll(args.NewValue);
        ok.Click += (_, _) => { accepted = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Closed += (_, _) =>
        {
            if (accepted)
            {
                _appearance.ThumbnailSpacing = slider.Value;
                SaveAppearance();
            }
            else
                ApplySpacingPreservingScroll(original);
            ReflowSpacingPreservingScroll(BrowseSurface.EndSpacingPreview);
        };
        window.Show();
    }

    private void ApplySpacingPreservingScroll(double value)
        => ReflowSpacingPreservingScroll(() => BrowseSurface.SetThumbnailSpacing(value));

    private void ReflowSpacingPreservingScroll(Action update)
    {
        // Spacing preview uses the same image identity anchor as sidebar,
        // thumbnail-size and folder-layout changes. No parallel scroll math.
        QueueGalleryAnchorRestore();
        update();
        BrowseScrollViewer.UpdateLayout();
        RestorePendingGalleryAnchor();
        BrowseSurface.SetViewport(
            BrowseScrollViewer.VerticalOffset,
            Math.Max(1, BrowseScrollViewer.ViewportHeight));
    }

    private void HideCollapsedFolders_Click(object sender, RoutedEventArgs e)
    {
        _appearance.HideCollapsedFolders = HideCollapsedFoldersMenu.IsChecked;
        SaveAppearance();
        UpdateGalleryFolderVisibility();
    }

    private void FoldersTree_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (_bulkFolderExpansion || _rebuildingFolderTree || !_folderTreeInitialized)
            return;
        SaveFolderTreeState();
        if (_appearance.HideCollapsedFolders)
            UpdateGalleryFolderVisibility();
    }

    private void FolderExpandAll_Click(object sender, RoutedEventArgs e)
        => SetAllFolderExpansion(true);

    private void FolderCollapseAll_Click(object sender, RoutedEventArgs e)
        => SetAllFolderExpansion(false);

    private void SetAllFolderExpansion(bool expanded)
    {
        _bulkFolderExpansion = true;
        try
        {
            if (FoldersTree.SelectedItem is TreeViewItem selected)
                SetFolderExpansion(selected, expanded);
            else
                foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
                    SetFolderExpansion(root, expanded);
        }
        finally { _bulkFolderExpansion = false; }
        SaveFolderTreeState();
        UpdateGalleryFolderVisibility();
    }

    private static void SetFolderExpansion(TreeViewItem item, bool expanded)
    {
        foreach (var child in item.Items.OfType<TreeViewItem>())
            SetFolderExpansion(child, expanded);
        item.IsExpanded = expanded;
    }

    private void UpdateGalleryFolderVisibility(bool preserveGalleryAnchor = true)
    {
        // An asynchronous catalog refresh owns its anchor until the new index
        // has arrived. A direct folder visibility toggle restores immediately.
        var previousAnchor = preserveGalleryAnchor && _browseInitialized
            ? CaptureGalleryAnchor() : null;
        IReadOnlySet<long> hidden;
        if (!_appearance.HideCollapsedFolders)
        {
            hidden = new HashSet<long>();
        }
        else
        {
            var collapsedPaths = new List<string>();
            foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
                GatherCollapsedPaths(root, collapsedPaths);

            // Keep a collapsed folder's own photos; only descendants hide.
            hidden = _photoFolders
                .Where(folder => collapsedPaths.Any(path =>
                        !string.Equals(
                            FolderExclusionRules.Normalize(folder.LocalPath),
                            FolderExclusionRules.Normalize(path),
                            StringComparison.OrdinalIgnoreCase) &&
                        IsInside(folder.LocalPath, path)))
                .Select(folder => folder.Id)
                .ToHashSet();
        }

        if (BrowseSurface.SetHiddenFolders(hidden) && previousAnchor is { } anchor)
        {
            QueueGalleryAnchorRestore(anchor);
            RestoreAnchorAfterSidebarLayout();
        }
    }

    private static void GatherCollapsedPaths(TreeViewItem item, List<string> paths)
    {
        if (item.Items.Count == 0) return;
        if (!item.IsExpanded && item.Tag is FolderTreeTag tag)
        {
            paths.Add(tag.FullPath);
            return;
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
            GatherCollapsedPaths(child, paths);
    }
}
