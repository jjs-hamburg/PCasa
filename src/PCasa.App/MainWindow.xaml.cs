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
    private readonly GalleryCatalogReader _galleryCatalogReader = new();
    private readonly ForegroundReadService _foregroundReadService = new();
    private readonly LocalDatabase _database;
    private readonly FolderRepository _folderRepository;
    private readonly ImageCatalogRepository _imageCatalog;
    private readonly OriginalWriteFailureRepository _originalWriteFailures;
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
        ExifToolProcessRegistry.BeginSession();
        InitializeComponent();
        Closing += MainWindow_Closing;

        _database = new LocalDatabase();
        _database.Initialize();
        LogStartupStage("database-initialized");
        // Directory.Move and SQLite cannot commit atomically. Reconcile any
        // interrupted rename BEFORE durable jobs, the first gallery projection
        // or the background monitor can read stale directory paths.
        var renameService = new CatalogRenameService(_database);
        var renameRecovery = renameService.RecoverPendingRenames();
        // Prepare the shared write-error ledger and migrate older GPS errors
        // before scheduler recovery can start any original-file write.
        _originalWriteFailures = new OriginalWriteFailureRepository(_database);
        _durableGalleryTransfers = new DurableGalleryTransferJournal(_database);
        _scheduler.EnableDurablePackages(_database);
        _durableGalleryTransfers.PruneCompleted();
        _scheduler.RegisterDurablePackageType("tag-recovery-v1", BuildTagRecoveryPackage);
        _scheduler.RegisterDurablePackageType("tag-recovery-v2", BuildTagRecoveryPackageV2);
        _scheduler.RegisterDurablePackageType("tag-edit-v1", BuildTagEditPackage);
        _scheduler.RegisterDurablePackageType("tag-edit-v2", BuildTagEditPackage);
        _scheduler.RegisterDurablePackageType("gps-edit-v1", BuildGpsEditPackage);
        _scheduler.RegisterDurablePackageType("gps-edit-v2", BuildGpsEditPackageV2);
        _scheduler.RegisterDurablePackageType("gpx-write-v1", BuildGpxWritePackage);
        _scheduler.RegisterDurablePackageType("undo-restore-v1", BuildUndoRestorePackage);
        _scheduler.RegisterDurablePackageType("image-crop-v1", BuildCropPackage);
        _scheduler.RegisterDurablePackageType("image-edit-v1", BuildEditPackage);
        _scheduler.RegisterDurablePackageType("manual-folder-scan-v1", BuildManualFolderScanPackage);
        _scheduler.RegisterDurablePackageType("manual-scan-all-v1", BuildScanAllPackage);
        _scheduler.RegisterDurablePackageType("gallery-transfer-v1", BuildDurableGalleryTransferPackage);
        _scheduler.RegisterDurablePackageType("gallery-delete-v1", BuildDurableDeletePackage);
        _scheduler.RegisterDurablePackageType("create-gallery-folder-v1", BuildCreateGalleryFolderPackage);
        _folderRepository = new FolderRepository(_database);
        _imageCatalog = new ImageCatalogRepository(_database);
        _folderScanner = new FolderScanner(_imageCatalog, new ThumbnailCache(), _folderRepository);
        InitializeAppearance();
        foreach (var recovered in renameRecovery.Applied)
        {
            try
            {
                RebaseRenamedTreePreferences(recovered.Source, recovered.Target,
                    recovered.CanonicalSource, recovered.CanonicalTarget);
                new UndoHistoryService(UndoOptions.Load())
                    .RebaseSourcePaths(recovered.Source, recovered.Target,
                        subtree: true);
                renameService.CompleteDirectoryRename(recovered.Id);
                PCasaDiagnostics.WriteMessage("Rename.FolderRecovered",
                    $"Completed interrupted folder rename {recovered.Source} -> {recovered.Target}");
            }
            catch (Exception ex)
            {
                // Retain the durable receipt, so recovery remains retryable.
                PCasaDiagnostics.Write("Rename.FolderRecoveryFollowup", ex);
            }
        }
        foreach (var unresolved in renameRecovery.Unresolved)
            PCasaDiagnostics.WriteMessage("Rename.FolderNeedsReview",
                $"Rename from {unresolved.Source} to {unresolved.Target} " +
                "could not be verified. The durable receipt is retained.");
        // Alpha.47: only the CURRENT process may own an external edit.
        // Abandoned files from a crash are discarded, never resurrected as
        // a task that would suspend the startup/background scanner.
        try { ExternalEditTransaction.RetirePreviousProcessSession(); }
        catch (Exception ex) { PCasaDiagnostics.Write("ExternalEdit.StartupCleanup", ex); }
        RecoverExternalEditSchedulerLease();
        ScheduleExternalCleanup();
        _ = WarmExternalEditorIconsAsync();
        LogStartupStage("window-constructed");
    }

    private bool _folderRemovalPending;
    private string? _pendingFolderRemovalPath;

    // Pure UI projection fence while an optimistic manual removal is in
    // flight. It never decides the persisted catalog outcome.
    private bool IsPendingFolderRemovalHidden(string path)
        => _folderRemovalPending &&
            _pendingFolderRemovalPath is string subtree &&
            FolderExclusionRules.IsAtOrBelow(path, subtree);

    private async Task PrepareFolderRemovalAsync(long folderId, string? subtreePath = null,
        bool dropQueuedRequests = true)
    {
        if (_folderRemovalPending)
            throw new InvalidOperationException("Eine Ordnerentfernung läuft bereits.");

        // Invalidate ongoing work immediately, without waiting for unrelated
        // slow SMB enumeration or decoding. Commit fences prevent obsolete
        // scanner results from becoming visible after the removal decision.
        _folderRemovalPending = true;
        _pendingFolderRemovalPath = subtreePath;
        _backgroundDebounceTimer?.Stop();
        try
        {
            // Removing one source must not permanently cancel an entire
            // older Scan All / manual package. The scheduler yields its worker;
            // source-generation and exclusion guards invalidate only the
            // affected scan step. Other root steps resume afterward.
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
        _pendingFolderRemovalPath = null;
        _folderRemovalPending = false;
        if (_backgroundPending.Count > 0 && _backgroundScanSettings?.Enabled == true)
            _backgroundDebounceTimer?.Start();
    }

    private void FolderTreeToggle_Click(object sender, RoutedEventArgs e)
    {
        var expanding = _folderTreeSettings.Compressed;
        _folderTreeSettings.Compressed = !expanding;
        _folderTreeSettings.Save();
        // Expand must also expose the intermediate ancestors omitted by Compress.
        _expandingCompressedTree = expanding;
        RefreshFolderTree();
        SaveFolderTreeState();
    }

    // The tree's read-only drop eligibility projection. SQLite remains the
    // sole authority; it is rechecked in each scheduled mutation.
    private IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths,
        IReadOnlyList<string> IncludedPaths)> _dropTargetProjection =
        Array.Empty<(MonitoredFolder, IReadOnlyList<string>, IReadOnlyList<string>)>();


    // ONE immutable view of monitored roots and exclusions, read by a
    // background catalog reader. Folder-tree layout, context menus and the
    // folder-manager dialog consume this projection without UI-thread SQLite I/O.
    private IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths,
        IReadOnlyList<string> IncludedPaths)> ReadFolderTreeConfigurationSnapshot()
    {
        var roots = _folderRepository.GetAll();
        return roots
            .Select(root => (
                Folder: root,
                ExcludedPaths: (IReadOnlyList<string>)_folderRepository
                    .GetExcludedPaths(root.Id).ToArray(),
                IncludedPaths: (IReadOnlyList<string>)_folderRepository
                    .GetIncludedPaths(root.Id).ToArray()))
            .ToArray();
    }

    private readonly HashSet<string> _createdEmptyFolderPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _rebuildingFolderTree;
    private bool _expandingCompressedTree;

    private void RefreshFolderTree(bool preserveGalleryAnchor = true)
    {
        if (_rebuildingFolderTree) return;
        _rebuildingFolderTree = true;
        UpdateFolderImageTotal();
        try
        {
        // The physical catalog tree stays alive even while the independent
        // flat list is displayed. Never capture flat rows as tree state.
        var expandedPaths = CaptureExpandedFolderPaths();
        if (_appearance.FolderExpansionsSaved)
        {
            // Compressed paths omit intermediate ancestors. Retain only
            // those previously saved paths that are absent from this
            // rendered tree; visible nodes always win.
            var rendered = CaptureRenderedFolderPaths();
            expandedPaths.UnionWith((_appearance.ExpandedTreePaths ?? [])
                .Where(path => !rendered.Contains(path)));
        }
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

        // Rebuild the UI exclusively from the last committed scheduler
        // snapshot. The user can collapse/expand or resize this tree without
        // waiting on SQLite writes or unreachable NAS paths.
        var rootsWithExclusions = _dropTargetProjection
            .Where(root => !IsPendingFolderRemovalHidden(root.Folder.LocalPath) &&
                !FolderExclusionRules.IsExcluded(
                    root.Folder.LocalPath, root.ExcludedPaths, root.IncludedPaths))
            .ToArray();
        var monitoredFolders = rootsWithExclusions
            .Select(root => root.Folder).ToArray();
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
                     folder => monitoredFolderIds.Contains(folder.MonitoredFolderId) &&
                         !IsPendingFolderRemovalHidden(folder.LocalPath)))
        {
            var owner = rootsWithExclusions.FirstOrDefault(root =>
                root.Folder.Id == folder.MonitoredFolderId);
            if (owner.Folder is null ||
                FolderExclusionRules.IsExcluded(
                    folder.LocalPath, owner.ExcludedPaths, owner.IncludedPaths))
                continue;
            AddFolderTreePath(
                roots, nodesByPath, folder.LocalPath, folder.CanonicalPath,
                folder.Id, folder.ImageCount,
                path => !FolderExclusionRules.IsExcluded(
                    path, owner.ExcludedPaths, owner.IncludedPaths));
        }

        // Targets created through the context menu stay in the folder tree
        // while empty. Scan work still owns their eventual photo catalog.
        foreach (var empty in _createdEmptyFolderPaths
            .Where(path => !IsPendingFolderRemovalHidden(path)))
        {
            var owner = rootsWithExclusions
                .Where(root => FolderExclusionRules.IsAtOrBelow(
                    empty, root.Folder.LocalPath))
                .OrderByDescending(root => root.Folder.LocalPath.Length)
                .FirstOrDefault();
            if (owner.Folder is null ||
                FolderExclusionRules.IsExcluded(
                    empty, owner.ExcludedPaths, owner.IncludedPaths))
                continue;
            AddFolderTreePath(roots, nodesByPath, empty, empty, null, 0,
                path => !FolderExclusionRules.IsExcluded(
                    path, owner.ExcludedPaths, owner.IncludedPaths));
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
        if (!_folderTreeSettings.Compressed &&
            (!_appearance.FolderExpansionsSaved || _expandingCompressedTree))
        {
            pathsToExpand = new HashSet<string>(
                pathsToExpand.Concat(defaultExpandedPaths),
                StringComparer.OrdinalIgnoreCase);
        }

        FolderTreeToggleButton.Tag = _folderTreeSettings.Compressed ? null : "Active";
        FolderTreeToggleButton.ToolTip = _folderTreeSettings.Compressed
            ? "Expand: vollständige Pfade anzeigen"
            : "Compress: Pfade verkürzt anzeigen";
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
                // First launch opens monitored roots for discoverability.
                // Once the user has saved tree state, never force-open the
                // root after a scan rebuild or PCasa restart.
                if (useDefaultExpansion && !_appearance.FolderExpansionsSaved)
                    monitoredItem.IsExpanded = true;
                driveItem.Items.Add(monitoredItem);
            }

            // Same policy for the drive/share node. An explicitly collapsed
            // drive stays collapsed on every subsequent launch.
            if (useDefaultExpansion && !_appearance.FolderExpansionsSaved)
                driveItem.IsExpanded = true;
            FoldersTree.Items.Add(driveItem);
        }


        // Flat rows have a different visual owner: rebuilding them never
        // clears, selects or scrolls the real catalog tree.
        UpdateFlatCollapsedPathsFromTree();
        if (_appearance.FlatFolderView)
            RefreshFlatFolderList();

        _folderTreeInitialized = true;
        SyncFolderViewModeButtons();
        ApplyFolderViewVisibility();

        var selectedItem = selectedPath is string path
            ? FindFolderTreeItemByPath(FoldersTree, path) : null;
        if (selectedItem is null && _activePhotoFolderId is long activeId)
            _folderTreeItems.TryGetValue(activeId, out selectedItem);
        if (selectedItem is null)
            selectedItem = FirstPhotoFolderTreeItem(FoldersTree)
                ?? FoldersTree.Items.OfType<TreeViewItem>().FirstOrDefault();
        if (selectedItem is not null)
        {
            _syncingFolderSelection = true;
            try { selectedItem.IsSelected = true; }
            finally { _syncingFolderSelection = false; }
        }

        ApplyFolderAvailabilityToTree();
        UpdateGalleryFolderVisibility(preserveGalleryAnchor);
        ExplorerCatalogTreeRebuilt();
        // Rebuild counter identities only when the *tree* is rebuilt.
        // The live scan path below patches affected ancestors instead of
        // re-walking every NAS folder for each 200-ms count notification.
        RefreshVisibleFolderCounts();
        }
        finally
        {
            _rebuildingFolderTree = false;
            _expandingCompressedTree = false;
        }
    }

    // An initial catalog projection or explicit tree rebuild can calculate
    // all counters once. Continuous scanner progress is intentionally NOT
    // allowed to run this O(all folders + all tree nodes) routine every 200ms.
    private readonly Dictionary<string, int> _folderCountTotals =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TreeViewItem> _folderCountItems =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _folderCountLabels =
        new(StringComparer.OrdinalIgnoreCase);

    private void UpdateFolderImageTotal()
    {
        if (FolderImageTotalText is not null)
            FolderImageTotalText.Text =
                $"({(_liveCatalogImageCount ?? _lastCatalogImageCount):N0})";
    }

    private void RefreshVisibleFolderCounts()
    {
        UpdateFolderImageTotal();
        if (_appearance.FlatFolderView)
            RefreshFlatFolderCounts();
        _folderCountTotals.Clear();
        var folderCounts = _photoFolders.ToDictionary(folder => folder.Id);
        foreach (var (id, latest) in _liveFolderCountSummaries)
        {
            if (latest is null) folderCounts.Remove(id);
            else folderCounts[id] = latest;
        }

        foreach (var folder in folderCounts.Values)
            foreach (var part in GetFolderPathHierarchy(folder.LocalPath))
            {
                _folderCountTotals.TryGetValue(part.FullPath, out var count);
                _folderCountTotals[part.FullPath] = count + folder.ImageCount;
            }

        _folderCountItems.Clear();
        _folderCountLabels.Clear();
        IndexNodes(FoldersTree);
        void IndexNodes(ItemsControl container)
        {
            foreach (var item in container.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is FolderTreeTag tag)
                {
                    var name = GetFolderPathHierarchy(tag.FullPath)[^1].DisplayName;
                    _folderCountItems[tag.FullPath] = item;
                    _folderCountLabels[tag.FullPath] = name;
                    _folderCountTotals.TryGetValue(tag.FullPath, out var count);
                    var expected = count > 0 ? $"{name} ({count:N0})" : name;
                    if (!string.Equals(item.Header as string, expected,
                        StringComparison.Ordinal))
                        item.Header = expected;
                }
                IndexNodes(item);
            }
        }
    }

    // A scanner commit changes only ONE photo-folder identity. The entire
    // folder tree may contain thousands of nested NAS folders: update that
    // photo-folder's ancestors instead of re-enumerating every other folder
    // and rebuilding all WPF labels in response to a single image.
    private void ApplyLiveFolderCounts(
        IReadOnlyDictionary<long, PhotoFolderSummary?> changes, int imageCount)
    {
        if (_folderCountItems.Count == 0 && FoldersTree.Items.Count > 0)
            RefreshVisibleFolderCounts();

        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var published = _photoFolders.ToDictionary(folder => folder.Id);
        foreach (var (id, latest) in changes)
        {
            var previous = _liveFolderCountSummaries.TryGetValue(id, out var older)
                ? older : published.GetValueOrDefault(id);
            if (previous is not null)
                Accumulate(previous, -previous.ImageCount);
            if (latest is not null)
                Accumulate(latest, latest.ImageCount);

            _liveFolderCountSummaries[id] = latest;
            if (latest is not null && !_folderTreeItems.ContainsKey(id))
                ShowScannedPhotoFolder(new ScanProgress(0, 0, 0, 0, null,
                    DiscoveredPhotoFolderId: id,
                    DiscoveredPhotoFolderPath: latest.LocalPath));
        }

        _liveCatalogImageCount = imageCount;
        _liveFolderCountSerial++;
        UpdateFolderImageTotal();
        if (_appearance.FlatFolderView)
        {
            // Only the flat projection changes while this view is visible.
            // The existing catalog-tree WPF objects and scroll stay intact.
            RefreshFlatFolderList();
        }
        foreach (var path in affected)
        {
            if (!_folderCountItems.TryGetValue(path, out var item))
            {
                item = FindFolderTreeItemByPath(FoldersTree, path);
                if (item is null) continue;
                _folderCountItems[path] = item;
            }
            if (!_folderCountLabels.TryGetValue(path, out var label))
            {
                label = GetFolderPathHierarchy(path)[^1].DisplayName;
                _folderCountLabels[path] = label;
            }

            _folderCountTotals.TryGetValue(path, out var count);
            var expected = count > 0 ? $"{label} ({count:N0})" : label;
            if (!string.Equals(item.Header as string, expected,
                StringComparison.Ordinal))
                item.Header = expected;
        }

        void Accumulate(PhotoFolderSummary folder, int delta)
        {
            if (delta == 0) return;
            foreach (var part in GetFolderPathHierarchy(folder.LocalPath))
            {
                _folderCountTotals.TryGetValue(part.FullPath, out var earlier);
                _folderCountTotals[part.FullPath] = Math.Max(0, earlier + delta);
                affected.Add(part.FullPath);
            }
        }
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
        // Do not replace a valid previous snapshot with an empty startup
        // tree before the catalog projection has been initialized.
        if (_rebuildingFolderTree || !_folderTreeInitialized ||
            FoldersTree.Items.Count == 0) return;

        var expanded = CaptureExpandedFolderPaths();
        // In compact tree mode some parents are omitted from the UI, not
        // intentionally collapsed. Keep their last saved expansion value.
        var renderedPaths = CaptureRenderedFolderPaths();
        expanded.UnionWith((_appearance.ExpandedTreePaths ?? [])
            .Where(path => !renderedPaths.Contains(path)));
        if (_allFolderProjectionRootPath is not null)
        {
            // Explorer Ansicht adds temporary, non-catalogued nodes and
            // temporarily expands its selected root. Neither is a user's
            // permanent catalog tree preference. Unrelated real branches
            // still retain their latest open/closed states.
            expanded.ExceptWith(_explorerInsertedPaths);
            if (_explorerOriginalSelectionPath is { } originalPath)
            {
                if (_explorerOriginalExpanded) expanded.Add(originalPath);
                else expanded.Remove(originalPath);
            }
        }

        _appearance.FolderExpansionsSaved = true;
        _appearance.ExpandedTreePaths = expanded
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        SaveAppearance();
    }

    private static void AddFolderTreePath(
        IDictionary<string, FolderTreeNode> roots,
        IDictionary<string, FolderTreeNode> nodesByPath,
        string localPath,
        string canonicalPath,
        long? photoFolderId,
        int imageCount,
        Func<string, bool>? includeAncestor = null)
    {
        FolderTreeNode? parent = null;
        var skipped = new List<string>();
        foreach (var part in GetFolderPathHierarchy(localPath))
        {
            if (includeAncestor is not null && !includeAncestor(part.FullPath))
            {
                skipped.Add(part.DisplayName);
                continue;
            }
            if (!nodesByPath.TryGetValue(part.FullPath, out var node))
            {
                // A re-included descendant must be reachable without a
                // visible tree node for its excluded ancestor.
                var label = skipped.Count == 0 ? part.DisplayName :
                    string.Join("\\", skipped.Append(part.DisplayName));
                node = new FolderTreeNode(label, part.FullPath);
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
            skipped.Clear();
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

        var item = new TreeViewItem
        {
            // Keep the header a string: the global TreeViewItem DataTemplate
            // already owns icons, folder names, status and clipping.
            Header = header,
            Tag = CreateFolderTreeTag(node.FullPath, node.PhotoFolderId),
            AllowDrop = true,
            IsExpanded = expandedPaths.Contains(node.FullPath)
        };

        ToolTipService.SetIsEnabled(item, false);

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

    private HashSet<string> CaptureRenderedFolderPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(FoldersTree);
        return result;
        void Visit(ItemsControl parent)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>())
            {
                if (child.Tag is FolderTreeTag tag)
                    result.Add(tag.FullPath);
                Visit(child);
            }
        }
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

        if (_appearance.FlatFolderView || !updateTreeSelection ||
            !_folderTreeItems.TryGetValue(photoFolderId, out var treeItem))
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
