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
    private bool _restoringTreeFromFlat;
    private bool _updatingFolderViewButtons;

    private void FolderViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingFolderViewButtons || !_folderTreeInitialized ||
            sender is not System.Windows.Controls.RadioButton { IsChecked: true } button)
            return;

        var flat = ReferenceEquals(button, FolderFlatViewRadio);
        if (_appearance.FlatFolderView == flat) return;
        var wasFlat = _appearance.FlatFolderView;
        if (!wasFlat) SaveFolderTreeState();

        _appearance.FlatFolderView = flat;
        _restoringTreeFromFlat = wasFlat;
        try { RefreshFolderTree(); }
        finally { _restoringTreeFromFlat = false; }
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
        var folders = catalog.Values
            .Where(folder => folder.ImageCount > 0 &&
                monitoredIds.Contains(folder.MonitoredFolderId) &&
                !IsPendingFolderRemovalHidden(folder.LocalPath))
            .Where(folder =>
            {
                var owner = roots.FirstOrDefault(root =>
                    root.Folder.Id == folder.MonitoredFolderId);
                return owner.Folder is not null &&
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
            _folderTreeItems[folder.Id] = item;
            FoldersTree.Items.Add(item);
        }
    }

    private void RefreshFlatFolderCounts()
    {
        _folderCountItems.Clear();
        _folderCountLabels.Clear();
        var catalog = _photoFolders.ToDictionary(folder => folder.Id);
        foreach (var (id, latest) in _liveFolderCountSummaries)
        {
            if (latest is null) catalog.Remove(id);
            else catalog[id] = latest;
        }
        foreach (var item in FoldersTree.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is not FolderTreeTag { PhotoFolderId: long id } tag ||
                !catalog.TryGetValue(id, out var folder)) continue;
            var label = _flatFolderDisplayNames.GetValueOrDefault(tag.FullPath);
            if (label is null) continue;
            _folderCountItems[tag.FullPath] = item;
            _folderCountLabels[tag.FullPath] = label;
            var expected = $"{label} ({folder.ImageCount:N0})";
            if (!string.Equals(item.Header as string, expected,
                    StringComparison.Ordinal))
                item.Header = expected;
        }
    }
}
