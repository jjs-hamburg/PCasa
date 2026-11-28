using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Derived only from the real catalog/tree model. The flat projection
    // never becomes a second folder hierarchy or modifies scan ownership.
    private HashSet<string> _collapsedTreePathsForFlat =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _flatFolderDisplayNames =
        new(StringComparer.OrdinalIgnoreCase);
    private long? _flatExplicitRevealId;
    private bool _updatingFolderViewButtons;

    private void FolderViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFolderViewButtons || !_folderTreeInitialized ||
            sender is not System.Windows.Controls.RadioButton { IsChecked: true } button)
            return;

        var flat = ReferenceEquals(button, FolderFlatViewRadio);
        if (_appearance.FlatFolderView == flat) return;
        if (flat)
        {
            // Flat is strictly a presentation; the real TreeView's
            // children, expansion and vertical offset remain untouched.
            SaveFolderTreeState();
            RefreshFlatFolderList();
        }
        _appearance.FlatFolderView = flat;
        _flatExplicitRevealId = null;
        ApplyFolderViewVisibility();
        UpdateGalleryFolderVisibility();
        SaveAppearance();
    }

    private void SyncFolderViewModeButtons()
    {
        if (FolderTreeViewRadio is null || FolderFlatViewRadio is null)
            return;
        _updatingFolderViewButtons = true;
        try
        {
            FolderTreeViewRadio.IsChecked = !_appearance.FlatFolderView;
            FolderFlatViewRadio.IsChecked = _appearance.FlatFolderView;
        }
        finally { _updatingFolderViewButtons = false; }
    }

    private void ApplyFolderViewVisibility()
    {
        if (FoldersTree is null || FlatFoldersTree is null) return;
        // Hidden (not Collapsed) keeps the actual tree's scroll extent and
        // pixel position measured while only the flat list receives clicks.
        FoldersTree.Visibility = _appearance.FlatFolderView
            ? Visibility.Hidden : Visibility.Visible;
        FlatFoldersTree.Visibility = _appearance.FlatFolderView
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFlatCollapsedPathsFromTree()
    {
        var collapsed = new List<string>();
        foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
            GatherCollapsedPaths(root, collapsed);
        _collapsedTreePathsForFlat = collapsed
            .Select(FolderExclusionRules.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void RefreshFlatFolderList()
    {
        if (FlatFoldersTree is null) return;
        UpdateFlatCollapsedPathsFromTree();
        var previousId = (FlatFoldersTree.SelectedItem as TreeViewItem)?.Tag
            is FolderTreeTag { PhotoFolderId: long selectedId }
                ? selectedId : _activePhotoFolderId;
        var oldScroll = FindTreeScrollViewer(FlatFoldersTree)?.VerticalOffset;
        var roots = _dropTargetProjection
            .Where(root => !IsPendingFolderRemovalHidden(root.Folder.LocalPath)
                && !FolderExclusionRules.IsExcluded(root.Folder.LocalPath,
                    root.ExcludedPaths, root.IncludedPaths))
            .ToArray();
        var monitored = roots.Select(root => root.Folder.Id).ToHashSet();
        _syncingFolderSelection = true;
        try
        {
            FlatFoldersTree.Items.Clear();
            PopulateFlatFolderList(monitored, roots);
            if (previousId is long id)
            {
                var selection = FlatFoldersTree.Items.OfType<TreeViewItem>()
                    .FirstOrDefault(item =>
                        item.Tag is FolderTreeTag { PhotoFolderId: long folderId } &&
                        folderId == id);
                if (selection is not null) selection.IsSelected = true;
            }
        }
        finally { _syncingFolderSelection = false; }
        if (oldScroll is double offset && offset > 0)
            _ = Dispatcher.BeginInvoke(new Action(() =>
                FindTreeScrollViewer(FlatFoldersTree)?.ScrollToVerticalOffset(offset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FolderSidebarPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateSearchFolderToolbarWidth();

    private void UpdateSearchFolderToolbarWidth()
    {
        if (SearchFolderToolbarColumn is null || FolderSidebarPanel is null ||
            FolderViewModeChoices is null)
            return;
        var visible = FolderSidebarPanel.Visibility == Visibility.Visible;
        FolderViewModeChoices.Visibility = visible
            ? Visibility.Visible : Visibility.Collapsed;
        var width = visible && FolderSidebarPanel.ActualWidth > 0
            ? FolderSidebarPanel.ActualWidth
            : 45;
        SearchFolderToolbarColumn.Width = new GridLength(Math.Max(45, width));
    }

    private void PopulateFlatFolderList(
        HashSet<long> monitoredIds,
        IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths,
            IReadOnlyList<string> IncludedPaths)> roots)
    {
        _flatFolderDisplayNames.Clear();
        var catalog = _photoFolders.ToDictionary(folder => folder.Id);
        foreach (var (id, latest) in _liveFolderCountSummaries)
        {
            if (latest is null) catalog.Remove(id);
            else catalog[id] = latest;
        }
        var ownersById = roots.ToDictionary(root => root.Folder.Id);
        var folders = catalog.Values
            .Where(folder => folder.ImageCount > 0 &&
                monitoredIds.Contains(folder.MonitoredFolderId) &&
                !IsPendingFolderRemovalHidden(folder.LocalPath))
            .Where(folder =>
            {
                return ownersById.TryGetValue(folder.MonitoredFolderId, out var owner) &&
                    !FolderExclusionRules.IsExcluded(folder.LocalPath,
                        owner.ExcludedPaths, owner.IncludedPaths);
            })
            .Where(folder => !_appearance.HideCollapsedInFlatView ||
                !IsDescendantOfCollapsedPath(folder.LocalPath,
                    _collapsedTreePathsForFlat))
            .ToArray();

        var duplicateNames = folders.GroupBy(folder =>
            Path.GetFileName(Path.TrimEndingDirectorySeparator(folder.LocalPath)),
            StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        foreach (var folder in folders
            .OrderBy(folder => Path.GetFileName(
                Path.TrimEndingDirectorySeparator(folder.LocalPath)),
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(folder => folder.LocalPath,
                StringComparer.CurrentCultureIgnoreCase))
        {
            var name = Path.GetFileName(
                Path.TrimEndingDirectorySeparator(folder.LocalPath));
            if (string.IsNullOrWhiteSpace(name)) name = folder.LocalPath;
            var label = duplicateNames.Contains(name)
                ? name + " — " + Path.GetDirectoryName(
                    Path.TrimEndingDirectorySeparator(folder.LocalPath))
                : name;
            _flatFolderDisplayNames[folder.LocalPath] = label;
            var item = new TreeViewItem {
                Header = $"{label} ({folder.ImageCount:N0})",
                Tag = CreateFolderTreeTag(folder.LocalPath, folder.Id),
                AllowDrop = true
            };
            ToolTipService.SetIsEnabled(item, false);
            FlatFoldersTree.Items.Add(item);
        }
    }

    private void RefreshFlatFolderCounts()
    {
        var catalog = _photoFolders.ToDictionary(folder => folder.Id);
        foreach (var (id, latest) in _liveFolderCountSummaries)
        {
            if (latest is null) catalog.Remove(id);
            else catalog[id] = latest;
        }
        foreach (var item in FlatFoldersTree.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is not FolderTreeTag { PhotoFolderId: long id } tag ||
                !catalog.TryGetValue(id, out var folder)) continue;
            var label = _flatFolderDisplayNames.GetValueOrDefault(tag.FullPath);
            if (label is null) continue;
            var expected = $"{label} ({folder.ImageCount:N0})";
            if (!string.Equals(item.Header as string, expected,
                    StringComparison.Ordinal))
                item.Header = expected;
        }
    }
}
