using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _bulkFolderExpansion;
    private bool _spacingPreviewActive;
    private bool _spacingFrameScheduled;
    private DispatcherTimer? _footerSettingsTimer;

    private void CompactThumbnailSlider_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.ApplyTemplate();
        var track = slider.Template.FindName("PART_Track", slider) as Track;
        if (track?.Thumb is not Thumb thumb) return;
        thumb.Width = 12;
        thumb.Height = 12;
        thumb.Template = (ControlTemplate)FindResource("PcasaCompactSliderThumbTemplate");

        if (ReferenceEquals(slider, ThumbnailSpacingSlider))
        {
            thumb.DragStarted -= ThumbnailSpacingThumb_DragStarted;
            thumb.DragCompleted -= ThumbnailSpacingThumb_DragCompleted;
            thumb.DragStarted += ThumbnailSpacingThumb_DragStarted;
            thumb.DragCompleted += ThumbnailSpacingThumb_DragCompleted;
        }
        if (ReferenceEquals(slider, ThumbnailSizeSlider))
        {
            thumb.DragStarted -= ThumbnailSizeThumb_DragStarted;
            thumb.DragCompleted -= ThumbnailSizeThumb_DragCompleted;
            thumb.DragStarted += ThumbnailSizeThumb_DragStarted;
            thumb.DragCompleted += ThumbnailSizeThumb_DragCompleted;
        }
    }

    private void ThumbnailSpacingThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_browseInitialized || _spacingPreviewActive) return;
        _spacingPreviewActive = true;
        ReflowSpacingPreservingScroll(BrowseSurface.BeginSpacingPreview);
    }

    private void ThumbnailSpacingThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_spacingPreviewActive)
        {
            _spacingPreviewActive = false;
            ReflowSpacingPreservingScroll(BrowseSurface.EndSpacingPreview);
        }
        SaveFooterSettingsNow();
    }

    private void ThumbnailSpacingSlider_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThumbnailSpacingSlider is null || !_browseInitialized) return;
        var spacing = Math.Clamp(e.NewValue, 0, 160);
        if (Math.Abs(_appearance.ThumbnailSpacing - spacing) < 0.01) return;
        _appearance.ThumbnailSpacing = spacing;

        // The original single spacing-preview layout owns BOTH axes.
        // Coalescing input to one WPF frame prevents redundant reflows.
        if (!_spacingFrameScheduled)
        {
            _spacingFrameScheduled = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _spacingFrameScheduled = false;
                if (!_browseInitialized || Dispatcher.HasShutdownStarted) return;
                ApplySpacingPreservingScroll(ThumbnailSpacingSlider.Value);
            }), DispatcherPriority.Render);
        }
        if (!_spacingPreviewActive) ScheduleFooterSettingsSave();
    }

    private void ScheduleFooterSettingsSave()
    {
        _footerSettingsTimer ??= CreateFooterSettingsTimer();
        _footerSettingsTimer.Stop();
        _footerSettingsTimer.Start();
    }

    private DispatcherTimer CreateFooterSettingsTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SaveAppearance();
        };
        return timer;
    }

    private void SaveFooterSettingsNow()
    {
        _footerSettingsTimer?.Stop();
        SaveAppearance();
    }

    private void ApplySpacingPreservingScroll(double value)
        => ReflowSpacingPreservingScroll(() => BrowseSurface.SetThumbnailSpacing(value));

    private void ReflowSpacingPreservingScroll(Action update)
    {
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
