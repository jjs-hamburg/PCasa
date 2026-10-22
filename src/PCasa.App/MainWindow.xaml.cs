using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow : Window
{
    private readonly PCasaScheduler _scheduler = new();
    private readonly LocalDatabase _database;
    private readonly FolderRepository _folderRepository;
    private readonly ImageCatalogRepository _imageCatalog;
    private readonly FolderScanner _folderScanner;
    private readonly HashSet<long> _selectedImageIds = new();
    private readonly Dictionary<long, TreeViewItem> _folderTreeItems = new();
    private readonly FolderTreeDisplaySettings _folderTreeSettings =
        FolderTreeDisplaySettings.Load();

    private IReadOnlyList<PhotoFolderSummary> _photoFolders = Array.Empty<PhotoFolderSummary>();
    private CancellationTokenSource? _activeScanCancellation;
    private long? _activeScanFolderId;
    private long? _activePhotoFolderId;
    private int _lastCatalogImageCount;
    private bool _syncingFolderSelection;
    private bool _folderTreeInitialized;
    private bool _isScanning;

    public MainWindow()
    {
        InitializeComponent();
        Closed += async (_, _) => await _scheduler.DisposeAsync();

        _database = new LocalDatabase();
        _database.Initialize();
        _folderRepository = new FolderRepository(_database);
        _imageCatalog = new ImageCatalogRepository(_database);
        _folderScanner = new FolderScanner(_imageCatalog, new ThumbnailCache(), _folderRepository);
        InitializeAppearance();
    }

    private bool _folderRemovalPending;

    private async Task PrepareFolderRemovalAsync(long folderId, string? subtreePath = null,
        bool dropQueuedRequests = true)
    {
        if (_folderRemovalPending)
            throw new InvalidOperationException("Eine Ordnerentfernung läuft bereits.");

        // Invalidate ongoing work immediately, without waiting for unrelated
        // slow SMB enumeration or decoding. Commit fences prevent obsolete
        // scanner results from becoming visible after the removal decision.
        _folderRemovalPending = true;
        _backgroundDebounceTimer?.Stop();
        try
        {
            _activeScanCancellation?.Cancel();
            _backgroundCurrentScanCancellation?.Cancel();
            _browseCacheCancellation?.Cancel();
            _cacheMaintenanceCancellation?.Cancel();

            // Do not wait here: a cancelled SMB call is not guaranteed to
            // return promptly. The interactive scheduler lane performs the
            // actual catalog mutation and the source-generation fence protects
            // against still-running obsolete background work.

            if (dropQueuedRequests)
            {
                foreach (var key in _backgroundPending.Keys.ToArray())
                {
                    var request = _backgroundPending[key];
                    if (request.MonitoredFolderId == folderId &&
                        (subtreePath is null ||
                         FolderExclusionRules.IsAtOrBelow(request.Path, subtreePath)))
                        _backgroundPending.Remove(key);
                }
            }
        }
        catch
        {
            CompleteFolderRemoval();
            throw;
        }
    }

    private void CompleteFolderRemoval()
    {
        _folderRemovalPending = false;
        if (_backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
            _backgroundDebounceTimer?.Start();
    }

    private void FolderTreeToggle_Click(object sender, RoutedEventArgs e)
    {
        _folderTreeSettings.Compressed = !_folderTreeSettings.Compressed;
        _folderTreeSettings.Save();
        RefreshFolderTree();
    }

    private bool _rebuildingFolderTree;

    private void RefreshFolderTree(bool preserveGalleryAnchor = true)
    {
        if (_rebuildingFolderTree) return;
        _rebuildingFolderTree = true;
        try
        {
        var expandedPaths = CaptureExpandedFolderPaths();
        // An explicit tree selection can be a parent with no photo-folder ID.
        // Reconstruct that selection by path instead of forcing the active
        // scroll folder whenever a scan rebuilds the tree.
        var selectedPath = (FoldersTree.SelectedItem as TreeViewItem)?.Tag
            is FolderTreeTag selectedTag ? selectedTag.FullPath : _appearance.SelectedTreePath;
        var useDefaultExpansion = !_folderTreeInitialized || FoldersTree.Items.Count == 0;
        if (useDefaultExpansion && _appearance.FolderExpansionsSaved)
            expandedPaths = new HashSet<string>(_appearance.ExpandedTreePaths ?? [], StringComparer.OrdinalIgnoreCase);

        FoldersTree.Items.Clear();
        _folderTreeItems.Clear();

        var monitoredFolders = _folderRepository.GetAll()
            .Where(folder => !FolderExclusionRules.IsExcluded(
                folder.LocalPath, _folderRepository.GetExcludedPaths(folder.Id)))
            .ToArray();
        var monitoredFolderIds = monitoredFolders.Select(folder => folder.Id).ToHashSet();
        var roots = new Dictionary<string, FolderTreeNode>(StringComparer.OrdinalIgnoreCase);
        var nodesByPath = new Dictionary<string, FolderTreeNode>(StringComparer.OrdinalIgnoreCase);
        var defaultExpandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in monitoredFolders)
        {
            AddFolderTreePath(
                roots,
                nodesByPath,
                folder.LocalPath,
                folder.CanonicalPath,
                photoFolderId: null,
                imageCount: 0);

            foreach (var part in GetFolderPathHierarchy(folder.LocalPath))
            {
                defaultExpandedPaths.Add(part.FullPath);
            }
        }

        foreach (var folder in _photoFolders.Where(
                     folder => monitoredFolderIds.Contains(folder.MonitoredFolderId)))
        {
            AddFolderTreePath(
                roots,
                nodesByPath,
                folder.LocalPath,
                folder.CanonicalPath,
                folder.Id,
                folder.ImageCount);
        }

        foreach (var root in roots.Values)
        {
            CalculateFolderTreeTotal(root);
        }

        var pathsToExpand = useDefaultExpansion && !_appearance.FolderExpansionsSaved
            ? defaultExpandedPaths
            : expandedPaths;

        // Compress omits intermediate path nodes from the visual tree.
        // On returning to Expand, those ancestors did not exist in the
        // previous expansion snapshot. Explicitly reopen all monitored
        // root ancestors so exactly the same photo directories are visible.
        if (!_folderTreeSettings.Compressed)
        {
            pathsToExpand = new HashSet<string>(
                pathsToExpand.Concat(defaultExpandedPaths),
                StringComparer.OrdinalIgnoreCase);
        }

        FolderTreeToggleButton.Content =
            _folderTreeSettings.Compressed ? "Expand" : "Compress";
        FolderTreeToggleButton.ToolTip = _folderTreeSettings.Compressed
            ? "Vollständige Pfade zeigen"
            : "Pfadstufen bis zur überwachten Wurzel zusammenfassen";

        foreach (var root in roots.Values.OrderBy(
                     node => node.DisplayName,
                     StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_folderTreeSettings.Compressed)
            {
                FoldersTree.Items.Add(CreateFolderTreeItem(root, pathsToExpand));
                continue;
            }

            // Each monitored root under this drive/share is visible, together
            // with ALL its children and siblings. Only unnecessary ancestors
            // (such as Users/user in C:\Users\user\Pictures) disappear.
            var monitoredUnderDrive = monitoredFolders
                .Where(folder => FolderExclusionRules.IsAtOrBelow(
                    folder.LocalPath, root.FullPath))
                .Select(folder => FolderExclusionRules.Normalize(folder.LocalPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length)
                .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var directMonitoredRoots = monitoredUnderDrive
                .Where(path => !monitoredUnderDrive.Any(other =>
                    !string.Equals(path, other, StringComparison.OrdinalIgnoreCase) &&
                    FolderExclusionRules.IsAtOrBelow(path, other)))
                .ToArray();

            if (directMonitoredRoots.Any(path =>
                    string.Equals(path,
                        FolderExclusionRules.Normalize(root.FullPath),
                        StringComparison.OrdinalIgnoreCase)))
            {
                // A drive/share root itself is monitored: display its entire
                // subtree, not a duplicate root nested inside itself.
                FoldersTree.Items.Add(CreateFolderTreeItem(root, pathsToExpand));
                continue;
            }

            var driveItem = CreateFolderTreeItem(
                root, pathsToExpand, includeChildren: false);

            foreach (var monitoredPath in directMonitoredRoots
                         .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!nodesByPath.TryGetValue(monitoredPath, out var monitoredNode))
                    continue;

                // E.g. C: > Pictures > _a, _Christina, Himmel ...
                // The monitored node stays expanded so its siblings are visible.
                var monitoredItem = CreateFolderTreeItem(monitoredNode, pathsToExpand);
                monitoredItem.IsExpanded = true;
                driveItem.Items.Add(monitoredItem);
            }

            driveItem.IsExpanded = true;
            FoldersTree.Items.Add(driveItem);
        }

        _folderTreeInitialized = true;

        var selectedItem = selectedPath is string path
            ? FindFolderTreeItemByPath(FoldersTree, path) : null;
        if (selectedItem is null && _activePhotoFolderId is long activeId)
            _folderTreeItems.TryGetValue(activeId, out selectedItem);
        if (selectedItem is null)
            selectedItem = FirstPhotoFolderTreeItem(FoldersTree);
        if (selectedItem is not null)
        {
            _syncingFolderSelection = true;
            try { selectedItem.IsSelected = true; }
            finally { _syncingFolderSelection = false; }
        }

        ApplyFolderAvailabilityToTree();
        UpdateGalleryFolderVisibility(preserveGalleryAnchor);
        }
        finally { _rebuildingFolderTree = false; }
    }

    private static TreeViewItem? FirstPhotoFolderTreeItem(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is FolderTreeTag { PhotoFolderId: not null }) return item;
            if (FirstPhotoFolderTreeItem(item) is { } nested) return nested;
        }
        return null;
    }

    private void SaveFolderTreeState()
    {
        if (_rebuildingFolderTree) return;
        _appearance.FolderExpansionsSaved = true;
        _appearance.ExpandedTreePaths = CaptureExpandedFolderPaths().ToArray();
        SaveAppearance();
    }

    private static void AddFolderTreePath(
        IDictionary<string, FolderTreeNode> roots,
        IDictionary<string, FolderTreeNode> nodesByPath,
        string localPath,
        string canonicalPath,
        long? photoFolderId,
        int imageCount)
    {
        FolderTreeNode? parent = null;
        foreach (var part in GetFolderPathHierarchy(localPath))
        {
            if (!nodesByPath.TryGetValue(part.FullPath, out var node))
            {
                node = new FolderTreeNode(part.DisplayName, part.FullPath);
                nodesByPath.Add(part.FullPath, node);

                if (parent is null)
                {
                    roots[part.FullPath] = node;
                }
                else
                {
                    parent.Children.Add(node);
                }
            }

            parent = node;
        }

        if (parent is null)
        {
            return;
        }

        parent.CanonicalPath = canonicalPath;
        if (photoFolderId is long id)
        {
            parent.PhotoFolderId = id;
            parent.DirectImageCount = imageCount;
        }
    }

    private static IReadOnlyList<(string DisplayName, string FullPath)> GetFolderPathHierarchy(
        string path)
    {
        var normalized = Path.GetFullPath(path).Replace('/', '\\');
        var rootPath = Path.GetPathRoot(normalized)?.Replace('/', '\\');
        var result = new List<(string DisplayName, string FullPath)>();

        if (string.IsNullOrEmpty(rootPath))
        {
            var segments = normalized.Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var current = string.Empty;
            foreach (var segment in segments)
            {
                current = string.IsNullOrEmpty(current)
                    ? segment
                    : Path.Combine(current, segment);
                result.Add((segment, current));
            }

            return result;
        }

        var rootDisplayName = rootPath.TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(rootDisplayName))
        {
            rootDisplayName = rootPath;
        }

        result.Add((rootDisplayName, rootPath));

        var remainder = normalized[rootPath.Length..].Trim('\\');
        if (remainder.Length == 0)
        {
            return result;
        }

        var currentPath = rootPath;
        foreach (var segment in remainder.Split(
                     '\\',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            currentPath = Path.Combine(currentPath, segment).Replace('/', '\\');
            result.Add((segment, currentPath));
        }

        return result;
    }

    private static int CalculateFolderTreeTotal(FolderTreeNode node)
    {
        var total = node.DirectImageCount;
        foreach (var child in node.Children)
        {
            total += CalculateFolderTreeTotal(child);
        }

        node.TotalImageCount = total;
        return total;
    }

    private TreeViewItem CreateFolderTreeItem(
        FolderTreeNode node,
        IReadOnlySet<string> expandedPaths,
        bool includeChildren = true)
    {
        var header = node.TotalImageCount > 0
            ? $"{node.DisplayName} ({node.TotalImageCount:N0})"
            : node.DisplayName;

        var tooltip = node.CanonicalPath is { Length: > 0 } canonicalPath &&
                      !string.Equals(canonicalPath, node.FullPath, StringComparison.OrdinalIgnoreCase)
            ? $"{node.FullPath}\n{canonicalPath}"
            : node.FullPath;

        var item = new TreeViewItem
        {
            Header = new Border
            {
                // A real border, not just selection color, makes the live
                // drop destination visible independently of tree selection.
                Child = new TextBlock { Text = header },
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2, 0, 2, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = System.Windows.Media.Brushes.Transparent
            },
            ToolTip = tooltip,
            Tag = CreateFolderTreeTag(node.FullPath, node.PhotoFolderId),
            AllowDrop = true,
            IsExpanded = expandedPaths.Contains(node.FullPath)
        };

        if (node.PhotoFolderId is long photoFolderId)
        {
            _folderTreeItems[photoFolderId] = item;
        }

        if (includeChildren)
        {
            foreach (var child in node.Children.OrderBy(
                         child => child.DisplayName,
                         StringComparer.CurrentCultureIgnoreCase))
            {
                item.Items.Add(CreateFolderTreeItem(child, expandedPaths));
            }
        }

        return item;
    }

    private HashSet<string> CaptureExpandedFolderPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in FoldersTree.Items.OfType<TreeViewItem>())
        {
            CaptureExpandedFolderPaths(item, result);
        }

        return result;
    }

    private static void CaptureExpandedFolderPaths(
        TreeViewItem item,
        ISet<string> result)
    {
        if (item.IsExpanded && item.Tag is FolderTreeTag tag)
        {
            result.Add(tag.FullPath);
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            CaptureExpandedFolderPaths(child, result);
        }
    }

    private static TreeViewItem? FindFolderTreeItemByPath(
        ItemsControl parent, string path)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            if (item.Tag is FolderTreeTag tag &&
                string.Equals(tag.FullPath, path, StringComparison.OrdinalIgnoreCase))
                return item;
            if (FindFolderTreeItemByPath(item, path) is { } descendant)
                return descendant;
        }
        return null;
    }

    // The physical order of nodes in the visible tree is the one source of
    // truth for gallery sections. Traverse even collapsed children so a
    // later expansion cannot change an unrelated folder's sort position.
    private IReadOnlyDictionary<long, int> CaptureGalleryFolderOrder()
    {
        var positions = new Dictionary<long, int>();
        foreach (var root in FoldersTree.Items.OfType<TreeViewItem>())
            CaptureGalleryFolderOrder(root, positions);
        return positions;
    }

    private static void CaptureGalleryFolderOrder(TreeViewItem item,
        IDictionary<long, int> positions)
    {
        if (item.Tag is FolderTreeTag { PhotoFolderId: long id } &&
            !positions.ContainsKey(id))
            positions.Add(id, positions.Count);
        foreach (var child in item.Items.OfType<TreeViewItem>())
            CaptureGalleryFolderOrder(child, positions);
    }

    private void UpdateActiveFolderDisplay(long photoFolderId, bool updateTreeSelection)
    {
        var folder = _photoFolders.FirstOrDefault(candidate => candidate.Id == photoFolderId);
        if (folder is null)
        {
            return;
        }

        // Active folder is highlighted in the folder tree itself.
        // No duplicate "Galerie / Sortierung" header occupies image space.

        if (!updateTreeSelection || !_folderTreeItems.TryGetValue(photoFolderId, out var treeItem))
        {
            return;
        }

        _syncingFolderSelection = true;
        try
        {
            treeItem.IsSelected = true;
            treeItem.BringIntoView();
        }
        finally
        {
            _syncingFolderSelection = false;
        }
    }


}
