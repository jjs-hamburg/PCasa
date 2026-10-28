using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Explorer Ansicht is a temporary projection WITHIN the existing
    // FoldersTree. It never replaces the tree, its visual nodes, or the
    // catalog/gallery folder-order owner.
    private string? _allFolderProjectionRootPath;
    private CancellationTokenSource? _explorerSession;
    private int _explorerGeneration;
    private TreeViewItem? _explorerOriginalSelection;
    private string? _explorerOriginalSelectionPath;
    private double? _explorerOriginalScroll;
    private readonly HashSet<string> _explorerInsertedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explorerLoadedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explorerPending =
        new(StringComparer.OrdinalIgnoreCase);

    private void UpdateAllFoldersButtonState()
    {
        if (ShowAllFilesystemFoldersButton is null) return;
        ShowAllFilesystemFoldersButton.Tag =
            _allFolderProjectionRootPath is null ? null : "ActiveGold";
        ShowAllFilesystemFoldersButton.ToolTip =
            _allFolderProjectionRootPath is null
                ? "Explorer Ansicht: Unterordner des ausgewählten Ordners anzeigen"
                : "Explorer Ansicht: zusätzliche Unterordner wieder ausblenden";
    }

    private void ShowAllFilesystemFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_allFolderProjectionRootPath is not null)
        {
            HideExplorerFolders();
            return;
        }

        if (FoldersTree.SelectedItem is not TreeViewItem { Tag: FolderTreeTag selected })
        {
            StatusText.Text = "Zuerst einen Ordner auswählen.";
            return;
        }

        _allFolderProjectionRootPath =
            FolderExclusionRules.Normalize(selected.FullPath);
        _explorerOriginalSelection = (TreeViewItem)FoldersTree.SelectedItem;
        _explorerOriginalSelectionPath = selected.FullPath;
        _explorerOriginalScroll = FindTreeScrollViewer(FoldersTree)?.VerticalOffset;
        _explorerSession = new CancellationTokenSource();
        ++_explorerGeneration;
        UpdateAllFoldersButtonState();

        // No Items.Clear(), no other TreeView, no selecting a new root.
        // Existing opened nodes and their WPF object identities survive.
        AddExplorerExpanders(_explorerOriginalSelection);
        ProbeOpenedExplorerFolders();
        RestoreExplorerScroll();
    }

    private void HideExplorerFolders()
    {
        ++_explorerGeneration; // fence late NAS/drive completions
        _explorerSession?.Cancel();
        _explorerSession?.Dispose();
        _explorerSession = null;

        _bulkFolderExpansion = true;
        try
        {
            // A temporary Explorer node becomes permanent as soon as its
            // photo folder is committed to the catalog. This can happen while
            // Explorer mode is open, especially for hidden/dot folders such
            // as .picasaoriginals. Promote its WHOLE parent chain before
            // pruning temporary-only nodes; no filesystem/SMB reads needed.
            // The catalog is the sole authority for which paths contain
            // indexed photos, regardless of directory name or attributes.
            PromoteExplorerIndexedPhotoFolders();

            // If a temporary-only entry is selected, restore the original
            // selection before removing it. Otherwise do not change focus.
            if (FoldersTree.SelectedItem is TreeViewItem { Tag: FolderTreeTag current } &&
                _explorerInsertedPaths.Contains(current.FullPath))
            {
                var original = _explorerOriginalSelection;
                if (original is null || original.Tag is not FolderTreeTag)
                    original = _explorerOriginalSelectionPath is string path
                        ? FindFolderTreeItemByPath(FoldersTree, path) : null;
                if (original is not null) original.IsSelected = true;
            }

            // Only our original subtree was augmented; all other
            // branches stay exactly as the user left them.
            if (_explorerOriginalSelectionPath is string scopePath &&
                FindFolderTreeItemByPath(FoldersTree, scopePath) is { } scopeRoot)
                RemoveTemporaryExplorerNodes(scopeRoot);
            _explorerInsertedPaths.Clear();
            _explorerLoadedPaths.Clear();
            _explorerPending.Clear();
            _allFolderProjectionRootPath = null;
            UpdateAllFoldersButtonState();
        }
        finally { _bulkFolderExpansion = false; }

        // The original WPF scroll and expanded positions are unchanged.
        // Persist only the genuine catalog tree, never temporary nodes.
        SaveFolderTreeState();
        UpdateGalleryFolderVisibility();
        RestoreExplorerScroll();
        _explorerOriginalSelection = null;
        _explorerOriginalSelectionPath = null;
        _explorerOriginalScroll = null;
    }

    private static ScrollViewer? FindTreeScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindTreeScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void RestoreExplorerScroll()
    {
        if (_explorerOriginalScroll is not double offset) return;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Dispatcher.HasShutdownStarted)
                FindTreeScrollViewer(FoldersTree)?.ScrollToVerticalOffset(offset);
        }), DispatcherPriority.Loaded);
    }

    private void PromoteExplorerIndexedPhotoFolders()
    {
        if (_explorerInsertedPaths.Count == 0) return;
        foreach (var folder in _photoFolders)
        {
            if (!IsWithinExplorerScope(folder.LocalPath)) continue;
            // Removing only the indexed leaf would still delete it along
            // with a temporary parent. The existing hierarchy function is
            // the same one used by the ordinary folder tree.
            foreach (var part in GetFolderPathHierarchy(folder.LocalPath))
                _explorerInsertedPaths.Remove(part.FullPath);
        }
    }

    private void RemoveTemporaryExplorerNodes(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>().ToArray())
        {
            if (item.Tag is FolderTreeTag tag &&
                _explorerInsertedPaths.Contains(tag.FullPath))
            {
                parent.Items.Remove(item);
                continue;
            }

            if (item.Tag is FolderTreeTag)
                RemoveTemporaryExplorerNodes(item);
            // A placeholder never has a FolderTreeTag, and belongs only to
            // the temporary projection.
            foreach (var placeholder in item.Items.OfType<TreeViewItem>()
                         .Where(child => child.Tag is null).ToArray())
                item.Items.Remove(placeholder);
        }
    }

    private bool IsWithinExplorerScope(string path)
        => _allFolderProjectionRootPath is string root &&
           FolderExclusionRules.IsAtOrBelow(path, root);

    private void AddExplorerExpanders(ItemsControl parent)
    {
        // Include the selected start node itself. Previously only children
        // received placeholders, leaving an empty selected folder unable to
        // expand and never querying its immediate children.
        if (parent is TreeViewItem { Tag: FolderTreeTag } parentItem &&
            parentItem.Items.Count == 0)
            parentItem.Items.Add(new TreeViewItem
            {
                Header = "Ordner laden …", IsEnabled = false
            });
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is FolderTreeTag)
                AddExplorerExpanders(item);
        }
    }

    private void ProbeOpenedExplorerFolders()
    {
        if (_allFolderProjectionRootPath is null) return;
        if (FindFolderTreeItemByPath(FoldersTree,
                _allFolderProjectionRootPath) is not { } scopeRoot)
            return;
        // Opened() walks children, so explicitly enumerate the selected
        // start node as well; everything ABOVE or BESIDE it stays untouched.
        if (scopeRoot.IsExpanded)
            _ = LoadExplorerChildrenAsync(scopeRoot);
        foreach (var node in Opened(scopeRoot))
            _ = LoadExplorerChildrenAsync(node);

        static IEnumerable<TreeViewItem> Opened(ItemsControl parent)
        {
            foreach (var node in parent.Items.OfType<TreeViewItem>())
            {
                if (node.Tag is not FolderTreeTag) continue;
                if (!node.IsExpanded) continue;
                yield return node;
                foreach (var descendant in Opened(node))
                    yield return descendant;
            }
        }
    }

    private void ExplorerFolders_Expanded(RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem { Tag: FolderTreeTag tag } item ||
            !IsWithinExplorerScope(tag.FullPath))
            return;
        _ = LoadExplorerChildrenAsync(item);
    }

    private async Task LoadExplorerChildrenAsync(TreeViewItem item)
    {
        if (_explorerSession is null ||
            item.Tag is not FolderTreeTag folder ||
            !IsWithinExplorerScope(folder.FullPath)) return;
        var path = folder.FullPath;
        if (_explorerLoadedPaths.Contains(path) || !_explorerPending.Add(path))
            return;

        var session = _explorerSession;
        var generation = _explorerGeneration;
        try
        {
            // Only ONE level for each opened folder. Filesystem work remains
            // in the existing interactive SourceProbe scheduler lane.
            var children = await _scheduler.RunAsync(
                $"folder-tree:explorer:{generation}:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = 0 // include system and hidden folders
                    };
                    var paths = Directory.EnumerateDirectories(path, "*", options)
                        .Select(FolderExclusionRules.Normalize)
                        .OrderBy(p => Path.GetFileName(
                                Path.TrimEndingDirectorySeparator(p)),
                            StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    token.ThrowIfCancellationRequested();
                    return paths;
                },
                () => generation == _explorerGeneration &&
                    ReferenceEquals(session, _explorerSession),
                cancellationToken: session.Token,
                workKind: SchedulerWorkKind.SourceProbe);

            if (generation != _explorerGeneration ||
                !ReferenceEquals(session, _explorerSession) ||
                Dispatcher.HasShutdownStarted) return;

            var existing = item.Items.OfType<TreeViewItem>()
                .Where(node => node.Tag is FolderTreeTag)
                .Select(node => ((FolderTreeTag)node.Tag).FullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var child in children)
            {
                if (!existing.Add(child)) continue;
                var indexed = _photoFolders.FirstOrDefault(f =>
                    string.Equals(FolderExclusionRules.Normalize(f.LocalPath),
                        child, StringComparison.OrdinalIgnoreCase));
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(child));
                if (string.IsNullOrWhiteSpace(name)) name = child;
                var newNode = new TreeViewItem
                {
                    Header = indexed is { ImageCount: > 0 }
                        ? $"{name} ({indexed.ImageCount:N0})" : name,
                    ToolTip = child,
                    Tag = CreateFolderTreeTag(child, indexed?.Id)
                };
                newNode.Items.Add(new TreeViewItem
                {
                    Header = "Ordner laden …", IsEnabled = false
                });
                _explorerInsertedPaths.Add(child);
                // Insert alphabetically among *existing* catalog items.
                // Items.Sort/Items.Clear would replace open WPF nodes and
                // would make the NAS scrollbar jump, so only insert ONE row.
                var insertAt = item.Items.Count;
                for (var i = 0; i < item.Items.Count; i++)
                {
                    if (item.Items[i] is not TreeViewItem
                        { Tag: FolderTreeTag sibling }) continue;
                    var siblingName = Path.GetFileName(
                        Path.TrimEndingDirectorySeparator(sibling.FullPath));
                    if (StringComparer.CurrentCultureIgnoreCase.Compare(
                            name, siblingName) >= 0) continue;
                    insertAt = i;
                    break;
                }
                item.Items.Insert(insertAt, newNode);
                if (indexed is not null) _folderTreeItems[indexed.Id] = newNode;
            }

            foreach (var placeholder in item.Items.OfType<TreeViewItem>()
                .Where(node => node.Tag is null).ToArray())
                item.Items.Remove(placeholder);
            _explorerLoadedPaths.Add(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            PCasaDiagnostics.Write("Explorer.EnumerateDirectory", ex);
            // Keep the placeholder to allow another attempt after the
            // network share becomes reachable.
        }
        finally
        {
            if (generation == _explorerGeneration)
                _explorerPending.Remove(path);
        }
    }

    // Existing photo-folder rows can become known after a manual scan.
    // Update tags and image counts in place; never clear the TreeView.
    private void RefreshExplorerIndexedFolders()
    {
        if (_allFolderProjectionRootPath is null) return;
        var byPath = _photoFolders.GroupBy(
            f => FolderExclusionRules.Normalize(f.LocalPath),
            StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        Refresh(FoldersTree);

        void Refresh(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not FolderTreeTag existing) continue;
                if (byPath.TryGetValue(existing.FullPath, out var indexed))
                {
                    if (existing.PhotoFolderId != indexed.Id)
                        item.Tag = CreateFolderTreeTag(existing.FullPath, indexed.Id);
                    _folderTreeItems[indexed.Id] = item;
                    var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(
                        existing.FullPath));
                    if (string.IsNullOrWhiteSpace(name)) name = existing.FullPath;
                    item.Header = indexed.ImageCount > 0
                        ? $"{name} ({indexed.ImageCount:N0})" : name;
                }
                Refresh(item);
            }
        }
    }

    // A newly registered root must survive Explorer-mode deactivation.
    // Promote its full existing WPF ancestry from temporary to permanent.
    private void PromoteExplorerMonitoredPath(string path)
    {
        var selected = FindFolderTreeItemByPath(FoldersTree, path);
        while (selected is not null)
        {
            if (selected.Tag is FolderTreeTag tag)
                _explorerInsertedPaths.Remove(tag.FullPath);
            selected = ItemsControl.ItemsControlFromItemContainer(selected)
                as TreeViewItem;
        }
    }

    // Existing catalog refresh is still allowed when the source changes.
    // Re-attach only Explorer's temporary lazy expanders afterward; never
    // create a second logical tree or perform recursive filesystem scans.
    private void ExplorerCatalogTreeRebuilt()
    {
        if (_allFolderProjectionRootPath is null) return;
        ++_explorerGeneration;
        _explorerSession?.Cancel();
        _explorerSession?.Dispose();
        _explorerSession = new CancellationTokenSource();
        _explorerPending.Clear();
        _explorerLoadedPaths.Clear();
        _explorerInsertedPaths.Clear();
        if (FindFolderTreeItemByPath(FoldersTree,
                _allFolderProjectionRootPath) is { } scopeRoot)
        {
            AddExplorerExpanders(scopeRoot);
            _ = Dispatcher.BeginInvoke(new Action(ProbeOpenedExplorerFolders),
                DispatcherPriority.Background);
        }
    }
}
