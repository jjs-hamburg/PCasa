using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class FolderManagerWindow : Window
{
    private sealed record ManagedNode(
        MonitoredFolder? Root, string LocalPath, bool IsRoot,
        bool IsSystemFolder = false, string? Label = null,
        int CatalogImageCount = 0);

    private readonly ForegroundReadService _foregroundReadService;
    // Already-indexed folders are a read-only catalog projection. They are
    // not additional monitored roots, do not change scan scope, and require
    // no NAS directory enumeration.
    private readonly IReadOnlyList<PhotoFolderSummary> _catalogPhotoFolders;
    private readonly Dictionary<long, IReadOnlyList<string>> _exclusions = new();
    private readonly Dictionary<long, IReadOnlyList<string>> _inclusions = new();
    // Roots unchecked during the dialog remain available for rechecking in
    // the right pane, with their original IDs and exclusion rules intact.
    // Only OK persists the final staged selection.
    private readonly Dictionary<long,
        (MonitoredFolder Root, IReadOnlyList<string> Exclusions,
         IReadOnlyList<string> Inclusions)> _uncheckedRoots = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<(string Name, string Path)> _systemFolders;
    private IReadOnlyList<MonitoredFolder> _monitoredRoots = Array.Empty<MonitoredFolder>();
    private ManagedNode? _selected;
    private bool _updatingChecks;
    private readonly Dictionary<string, TreeViewItem> _folderNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enumeratedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private long _nextTemporaryRootId = -1;

    // No catalog or watcher change occurs while this dialog is open.
    // DialogResult true exposes the selection, false drops it entirely.
    public FolderManagerSelection SelectedConfiguration => new(
        _monitoredRoots.Select(root => new FolderManagerRootChoice(
            root.LocalPath,
            _exclusions.TryGetValue(root.Id, out var excluded)
                ? excluded.ToArray() : Array.Empty<string>(),
            root.ServerSyncEnabled,
            _inclusions.TryGetValue(root.Id, out var included)
                ? included.ToArray() : Array.Empty<string>())).ToArray(),
        true);

    public FolderManagerWindow(
        IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths)> configuration,
        ForegroundReadService foregroundReadService, bool serverDatabaseConfigured,
        IReadOnlyList<PhotoFolderSummary>? catalogPhotoFolders = null,
        IReadOnlyDictionary<long, IReadOnlyList<string>>? includedByRoot = null)
    {
        InitializeComponent();
        _catalogPhotoFolders = catalogPhotoFolders ?? Array.Empty<PhotoFolderSummary>();
        _foregroundReadService = foregroundReadService;
        _monitoredRoots = configuration.Select(item => item.Folder).ToArray();
        foreach (var (folder, excludedPaths) in configuration)
        {
            _exclusions[folder.Id] = excludedPaths;
            _inclusions[folder.Id] = includedByRoot is not null &&
                includedByRoot.TryGetValue(folder.Id, out var included)
                    ? included : Array.Empty<string>();
        }

        ServerSyncCheckBox.Visibility = serverDatabaseConfigured
            ? Visibility.Visible : Visibility.Collapsed;

        _systemFolders = new[]
        {
            (Name: "Desktop", Path: Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory)),
            (Name: UiLanguage.Current.Code == "en" ? "Pictures" : "Eigene Bilder",
                Path: Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (Name: UiLanguage.Current.Code == "en" ? "Documents" : "Eigene Dokumente",
                Path: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
        }
        .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
        .Select(folder => (folder.Name, FolderExclusionRules.Normalize(folder.Path)))
        .DistinctBy(folder => folder.Item2, StringComparer.OrdinalIgnoreCase)
        .ToArray();

        Closed += (_, _) => _lifetime.Cancel();
        Reload();
    }


    // One read-only tree of explicitly monitored roots AND already indexed
    // image folders under them. An S:\ root must display its catalogued
    // descendants even when none are separate monitoring roots. Source
    // membership and thumbnail identities never change in this projection.
    // All folder selection and filesystem browsing remain in Add folder.
    private void Reload(string? expandSystemFolder = null)
    {
        // Rebuilding the projection must not recursively expand every
        // descendant. Keep only branches the user explicitly opened.
        var expanded = _folderNodes
            .Where(entry => entry.Value.IsExpanded)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previouslyVisible = _folderNodes.Keys.ToArray();
        var previouslyEnumerated = _enumeratedPaths.ToArray();
        _selected = null;
        FolderManagerTree.Items.Clear();
        _folderNodes.Clear();
        _enumeratedPaths.Clear();
        foreach (var path in previouslyEnumerated) _enumeratedPaths.Add(path);

        // Keep unchecked old roots and their indexed children visible for
        // staging a new selection. No filesystem/SMB enumeration required.
        var allRoots = _monitoredRoots.Concat(
            _uncheckedRoots.Values.Select(entry => entry.Root)).ToArray();
        var rootsByPath = allRoots.ToDictionary(
            root => FolderExclusionRules.Normalize(root.LocalPath),
            StringComparer.OrdinalIgnoreCase);

        // Count all catalogued descendants on each existing ancestor: a
        // branch containing ten indexed subfolders can be chosen ONE time.
        var catalogByPath = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var folder in _catalogPhotoFolders)
        {
            var owner = allRoots.FirstOrDefault(root =>
                root.Id == folder.MonitoredFolderId);
            if (owner is null ||
                !FolderExclusionRules.IsAtOrBelow(folder.LocalPath,
                    owner.LocalPath)) continue;
            foreach (var ancestor in ConfiguredPathHierarchy(folder.LocalPath))
            {
                if (!FolderExclusionRules.IsAtOrBelow(ancestor,
                        owner.LocalPath)) continue;
                catalogByPath.TryGetValue(ancestor, out var count);
                catalogByPath[ancestor] = checked(count + folder.ImageCount);
            }
        }

        // Sorting by path LENGTH previously put short drive roots (S:\)
        // before C:\ or G:\. Build the tree in natural path order so drive
        // letters and siblings are alphabetical, independent of depth.
        // Root volumes are cheap to list; physical children load lazily.
        // Excluded/included rule paths stay visible even on an offline NAS.
        var visiblePaths = DriveInfo.GetDrives().Select(drive => drive.Name)
            .Concat(_systemFolders.Select(folder => folder.Path))
            .Concat(rootsByPath.Keys)
            .Concat(catalogByPath.Keys)
            .Concat(_exclusions.Values.SelectMany(paths => paths))
            .Concat(_inclusions.Values.SelectMany(paths => paths))
            .Concat(previouslyVisible)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        foreach (var visiblePath in visiblePaths)
        {
            TreeViewItem? parent = null;
            foreach (var path in ConfiguredPathHierarchy(visiblePath))
            {
                if (!_folderNodes.TryGetValue(path, out var item))
                {
                    rootsByPath.TryGetValue(path, out var exact);
                    catalogByPath.TryGetValue(path, out var count);
                    var special = _systemFolders.FirstOrDefault(folder =>
                        PathEquals(folder.Path, path));
                    var isSpecial = !string.IsNullOrEmpty(special.Path);
                    item = CreateItem(new ManagedNode(
                        exact ?? FindCoveringRoot(path), path,
                        IsRoot: exact is not null,
                        IsSystemFolder: isSpecial,
                        Label: isSpecial ? special.Name : null,
                        CatalogImageCount: count));
                    _folderNodes[path] = item;
                    // Real Windows user folders are prominent shortcuts at
                    // the top, not separately monitored roots. Their actual
                    // paths remain the sole selection identity.
                    if (parent is null || isSpecial) FolderManagerTree.Items.Add(item);
                    else parent.Items.Add(item);
                }
                parent = item;
            }
        }

        // Keep the three Windows user folders above alphabetic drive roots.
        // These are the very same path nodes, never duplicated scan roots.
        var specialNodes = _systemFolders
            .Select(folder => _folderNodes.GetValueOrDefault(folder.Path))
            .Where(item => item is not null).Distinct().ToArray();
        foreach (var shortcut in specialNodes)
            FolderManagerTree.Items.Remove(shortcut!);
        for (var index = 0; index < specialNodes.Length; index++)
            FolderManagerTree.Items.Insert(index, specialNodes[index]!);

        // Expansion is user-controlled. Opening a parent must NEVER
        // recursively open children or deeper levels.
        foreach (var (path, item) in _folderNodes)
            item.IsExpanded = expanded.Contains(path);

        if (expandSystemFolder is not null &&
            _folderNodes.TryGetValue(
                FolderExclusionRules.Normalize(expandSystemFolder), out var selected))
            selected.IsSelected = true;

        RefreshMonitoredRootsList();
        UpdateLoadedCheckboxes();
    }

    private static IEnumerable<string> ConfiguredPathHierarchy(string path)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        var volume = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(volume))
        {
            yield return normalized;
            yield break;
        }
        var current = FolderExclusionRules.Normalize(volume);
        yield return current;
        var relative = Path.GetRelativePath(volume, normalized);
        if (relative == ".") yield break;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = FolderExclusionRules.Normalize(Path.Combine(current, part));
            yield return current;
        }
    }

    private void RefreshMonitoredRootsList()
    {
        // The right-hand side is ONLY a short summary of explicit roots.
        // The whole catalog hierarchy, including root replacement choices,
        // belongs in the ONE tree on the left.
        MonitoredRootsList.Children.Clear();
        foreach (var root in _monitoredRoots.OrderBy(x => x.LocalPath,
            StringComparer.OrdinalIgnoreCase))
        {
            var item = new TextBlock
            {
                Text = root.LocalPath,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
            item.SetResourceReference(TextBlock.ForegroundProperty,
                "PcasaTextBrush");
            MonitoredRootsList.Children.Add(item);
        }
    }

    private void RemoveStagedRoot(long rootId)
    {
        var root = _monitoredRoots.FirstOrDefault(candidate =>
            candidate.Id == rootId);
        if (root is null) return;
        // A newly staged root has no database record to restore. Unchecking
        // it is simply a cancellation, not a second unchecked root row.
        if (root.Id > 0)
            _uncheckedRoots[rootId] = (root,
                _exclusions.GetValueOrDefault(rootId) ?? Array.Empty<string>(),
                _inclusions.GetValueOrDefault(rootId) ?? Array.Empty<string>());
        _monitoredRoots = _monitoredRoots
            .Where(candidate => candidate.Id != rootId).ToArray();
        _exclusions.Remove(rootId);
        _inclusions.Remove(rootId);
        Reload();
    }

    private void RestoreStagedRoot(long rootId)
    {
        if (!_uncheckedRoots.Remove(rootId, out var previous)) return;
        _monitoredRoots = _monitoredRoots.Append(previous.Root).ToArray();
        _exclusions[rootId] = previous.Exclusions
            .Concat(_monitoredRoots
                .Where(root => root.Id != rootId &&
                    FolderExclusionRules.IsAtOrBelow(root.LocalPath,
                        previous.Root.LocalPath))
                .Select(root => root.LocalPath))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _inclusions[rootId] = previous.Inclusions;
        Reload();
    }

    private static bool PathEquals(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
           string.Equals(FolderExclusionRules.Normalize(a),
               FolderExclusionRules.Normalize(b),
               StringComparison.OrdinalIgnoreCase);

    private MonitoredFolder? FindCoveringRoot(string path)
        => _monitoredRoots
            .Where(root => FolderExclusionRules.IsAtOrBelow(path, root.LocalPath))
            .OrderByDescending(root => root.LocalPath.Length)
            .FirstOrDefault();

    private TreeViewItem CreateItem(ManagedNode node)
    {
        var name = node.Label ?? Path.GetFileName(
            Path.TrimEndingDirectorySeparator(node.LocalPath));
        if (node.IsRoot && node.LocalPath == Path.GetPathRoot(node.LocalPath))
            name = node.LocalPath;
        if (string.IsNullOrWhiteSpace(name)) name = node.LocalPath;

        var checkbox = new CheckBox
        {
            Content = node.CatalogImageCount > 0
                ? $"{name} ({node.CatalogImageCount:N0})" : name,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(1, 0, 1, 0),
            IsChecked = DisplayIncludedState(node),
            // Three visual states; interaction intentionally toggles only
            // fully included / fully excluded. Mixed is never a saved rule.
            IsThreeState = true,
            IsEnabled = CanToggle(node)
        };
        checkbox.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");
        checkbox.Style = (Style)FindResource("ManagerFolderCheckBoxStyle");

        var item = new TreeViewItem
        {
            Header = checkbox,
            HeaderTemplate = (DataTemplate)FolderManagerTree.Resources[
                "ManagerFolderHeaderTemplate"],
            Tag = node
        };

        ToolTipService.SetIsEnabled(item, false);
        ToolTipService.SetIsEnabled(checkbox, false);
        // Physical children are read only when THIS node expands.
        item.Expanded += Folder_Expanded;
        checkbox.Tag = item;
        checkbox.PreviewMouseLeftButtonDown += InclusionCheckbox_PreviewMouseLeftButtonDown;
        checkbox.PreviewKeyDown += InclusionCheckbox_PreviewKeyDown;
        checkbox.Checked += InclusionCheckbox_Changed;
        checkbox.Unchecked += InclusionCheckbox_Changed;
        checkbox.Indeterminate += InclusionCheckbox_Indeterminate;
        if (!_enumeratedPaths.Contains(node.LocalPath))
            item.Items.Add(new TreeViewItem
            {
                Visibility = Visibility.Collapsed,
                IsEnabled = false
            });
        return item;
    }

    private bool IsIncluded(ManagedNode node)
        => node.Root is not null && !IsExcluded(node);

    // Compute the state from the effective directory ownership and the
    // persisted/staged rule boundaries. Never enumerate a NAS hierarchy to
    // determine the gray (partially included) state.
    private bool CanToggle(ManagedNode node) => !_busy;

    private bool EffectivelyIncluded(string path)
    {
        var owner = FindCoveringRoot(path);
        return owner is not null && !FolderExclusionRules.IsExcluded(
            path,
            _exclusions.GetValueOrDefault(owner.Id) ?? Array.Empty<string>(),
            _inclusions.GetValueOrDefault(owner.Id) ?? Array.Empty<string>());
    }

    private bool? DisplayIncludedState(ManagedNode node)
    {
        var baseIncluded = EffectivelyIncluded(node.LocalPath);
        // An ancestor is mixed whenever a deeper rule changes the effective
        // inclusion, including a re-included child of an excluded directory
        // or a separately monitored subtree below an unmonitored drive.
        foreach (var boundary in _monitoredRoots.Select(root => root.LocalPath)
            .Concat(_exclusions.Values.SelectMany(paths => paths))
            .Concat(_inclusions.Values.SelectMany(paths => paths)))
        {
            if (FolderExclusionRules.IsAtOrBelow(boundary, node.LocalPath) &&
                !PathEquals(boundary, node.LocalPath) &&
                EffectivelyIncluded(boundary) != baseIncluded)
                return null;
        }
        return baseIncluded;
    }

    private bool IsExcluded(ManagedNode node)
        => FindCoveringRoot(node.LocalPath) is { } root &&
           FolderExclusionRules.IsExcluded(node.LocalPath,
               _exclusions.GetValueOrDefault(root.Id) ?? Array.Empty<string>(),
               _inclusions.GetValueOrDefault(root.Id) ?? Array.Empty<string>());

    // An indeterminate checkbox is a visual summary, never an extra option
    // in the user's click cycle: mixed -> checked -> unchecked -> checked.
    private static void ToggleInclusionCheckbox(CheckBox box)
        => box.IsChecked = box.IsChecked != true;

    private void InclusionCheckbox_PreviewMouseLeftButtonDown(
        object sender, MouseButtonEventArgs e)
    {
        if (sender is not CheckBox { IsEnabled: true } box) return;
        e.Handled = true;
        box.Focus();
        ToggleInclusionCheckbox(box);
    }

    private void InclusionCheckbox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not CheckBox { IsEnabled: true } box)
            return;
        e.Handled = true;
        ToggleInclusionCheckbox(box);
    }

    private void InclusionCheckbox_Indeterminate(object sender, RoutedEventArgs e)
    {
        // Keyboard/mouse use the binary toggle above. Automation clients
        // that cycle to indeterminate still trigger a real exclusion rather
        // than silently modifying a third, unsupported user choice.
        if (!_updatingChecks && sender is CheckBox box)
            box.IsChecked = false;
    }

    private void StageSubtreeRule(MonitoredFolder root, string path, bool include)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        var exclusions = (_exclusions.GetValueOrDefault(root.Id) ??
            Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inclusions = (_inclusions.GetValueOrDefault(root.Id) ??
            Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A parent checkbox owns its entire subtree. Explicitly checking
        // one of its children later creates a deeper inclusion exception.
        exclusions.RemoveWhere(value => FolderExclusionRules.IsAtOrBelow(value, normalized));
        inclusions.RemoveWhere(value => FolderExclusionRules.IsAtOrBelow(value, normalized));
        if (include)
        {
            if (FolderExclusionRules.IsExcluded(normalized, exclusions, inclusions))
                inclusions.Add(normalized);
        }
        else if (!FolderExclusionRules.IsExcluded(normalized, exclusions, inclusions))
            exclusions.Add(normalized);
        _exclusions[root.Id] = exclusions.ToArray();
        _inclusions[root.Id] = inclusions.ToArray();
    }

    private void StageEntireSubtreeIncluded(string path)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        var owner = FindCoveringRoot(normalized);
        if (owner is not null)
            StageSubtreeRule(owner, normalized, include: true);
        else
            StageNewRoot(normalized);

        // Multiple explicit scanner roots may already live under the chosen
        // directory. Keep their source ownership independent but reset any
        // nested exclusion exceptions, so "checked" really means EVERY
        // descendant is included.
        foreach (var root in _monitoredRoots.Where(root =>
                     FolderExclusionRules.IsAtOrBelow(root.LocalPath, normalized)))
        {
            _inclusions[root.Id] = Array.Empty<string>();
            _exclusions[root.Id] = _monitoredRoots
                .Where(nested => nested.Id != root.Id &&
                    FolderExclusionRules.IsAtOrBelow(nested.LocalPath, root.LocalPath))
                .Select(nested => nested.LocalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        // A selected path can be nested inside a broad monitored root.
        // Retain the rule protecting separately monitored child sources so
        // they are not indexed twice after clearing the parent's exclusion.
        if (owner is not null)
        {
            var nestedPaths = _monitoredRoots
                .Where(root => root.Id != owner.Id &&
                    FolderExclusionRules.IsAtOrBelow(root.LocalPath, normalized))
                .Select(root => root.LocalPath);
            _exclusions[owner.Id] = (_exclusions.GetValueOrDefault(owner.Id)
                    ?? Array.Empty<string>())
                .Concat(nestedPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private void StageEntireSubtreeExcluded(string path)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        var owner = FindCoveringRoot(normalized);
        var coveredRoots = _monitoredRoots.Where(root =>
                FolderExclusionRules.IsAtOrBelow(root.LocalPath, normalized))
            .ToArray();
        foreach (var root in coveredRoots)
        {
            if (root.Id > 0)
                _uncheckedRoots[root.Id] = (root,
                    _exclusions.GetValueOrDefault(root.Id) ?? Array.Empty<string>(),
                    _inclusions.GetValueOrDefault(root.Id) ?? Array.Empty<string>());
            _monitoredRoots = _monitoredRoots
                .Where(candidate => candidate.Id != root.Id).ToArray();
            _exclusions.Remove(root.Id);
            _inclusions.Remove(root.Id);
        }
        // If the clicked folder lies WITHIN a still-monitored parent, its
        // subtree must be excluded there. Removed nested root choices remain
        // available for restoration until OK.
        if (owner is not null && coveredRoots.All(root => root.Id != owner.Id))
            StageSubtreeRule(owner, normalized, include: false);
    }

    private async void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item ||
            !ReferenceEquals(sender, e.OriginalSource) ||
            item.Tag is not ManagedNode node ||
            !_enumeratedPaths.Add(node.LocalPath))
        {
            return;
        }

        var token = _lifetime.Token;
        try
        {
            // Folder-manager visibility is another read-only foreground
            // operation, not a catalog or source mutation. Reuse the
            // Explorer reader capacity instead of blocking on queued P0s.
            var directories = await _foregroundReadService.ReadManagerFoldersAsync(
                node.LocalPath, token);

            if (token.IsCancellationRequested) return;
            foreach (var placeholder in item.Items.OfType<TreeViewItem>()
                         .Where(child => child.Tag is null).ToArray())
                item.Items.Remove(placeholder);
            foreach (var directory in directories)
            {
                var normalized = FolderExclusionRules.Normalize(directory);
                if (item.Items.OfType<TreeViewItem>().Any(child =>
                        child.Tag is ManagedNode known &&
                        PathEquals(known.LocalPath, normalized)))
                    continue;
                var root = FindCoveringRoot(normalized);
                var child = CreateItem(new ManagedNode(root, normalized,
                    IsRoot: root is not null &&
                            PathEquals(root.LocalPath, normalized)));
                item.Items.Add(child);
                _folderNodes.TryAdd(normalized, child);
            }
            // Sort only this one level; existing expanded nodes are preserved.
            var sorted = item.Items.OfType<TreeViewItem>()
                .Where(child => child.Tag is ManagedNode)
                .OrderBy(child => ((ManagedNode)child.Tag).LocalPath,
                    StringComparer.CurrentCultureIgnoreCase).ToArray();
            item.Items.Clear();
            foreach (var child in sorted) item.Items.Add(child);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _enumeratedPaths.Remove(node.LocalPath);
            // Preserve known monitored descendants even if a NAS drive is
            // disconnected. A later expansion can retry the physical scan.
            if (!item.Items.OfType<TreeViewItem>().Any(child => child.Tag is null))
                item.Items.Add(new TreeViewItem
                {
                    Header = UiLanguage.T("Unterordner nicht erreichbar: ") + ex.Message,
                    IsEnabled = false
                });
        }
    }

    private void FolderManagerTree_SelectedItemChanged(
        object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = (e.NewValue as TreeViewItem)?.Tag as ManagedNode;
        UpdateSelectedDetails();
    }

    private void UpdateSelectedDetails()
    {
        SelectedFolderText.Text = _selected?.LocalPath ?? UiLanguage.T("Bitte links einen Ordner auswählen.");
        SelectedModeText.Text = _selected is null ? ""
            : _selected.Root is null ? UiLanguage.T("Laufwerk oder Pfadgruppe mit überwachten Unterordnern.")
            : _selected.IsRoot ? UiLanguage.T("Überwachte Wurzel. Enthaltene Bildordner werden aus dem PCasa-Katalog angezeigt.")
            : _selected.CatalogImageCount > 0
                ? $"{_selected.CatalogImageCount:N0} katalogisierte Bilder · über eine übergeordnete Wurzel überwacht (kein zusätzlicher Root)."
            : UiLanguage.T("Ordnergruppe bereits katalogisierter Unterordner.");
        RemoveFolderButton.IsEnabled =
            _selected is { IsRoot: true, Root: not null } &&
            _selected.Root is { } selectedRoot &&
            _monitoredRoots.Any(root => root.Id == selectedRoot.Id) && !_busy;
        if (_selected is { IsRoot: true, Root: { } serverRoot } &&
            serverRoot.CanServerSync)
        {
            ServerSyncCheckBox.IsEnabled = true;
            ServerSyncCheckBox.IsChecked = serverRoot.ServerSyncEnabled;
        }
        else
        {
            ServerSyncCheckBox.IsEnabled = false;
            ServerSyncCheckBox.IsChecked = false;
        }
    }

    private void UpdateLoadedCheckboxes()
    {
        _updatingChecks = true;
        try
        {
            foreach (var root in FolderManagerTree.Items.OfType<TreeViewItem>())
                UpdateItem(root);
        }
        finally
        {
            _updatingChecks = false;
        }

        UpdateSelectedDetails();
    }

    private void UpdateItem(TreeViewItem item)
    {
        if (item.Tag is ManagedNode node && item.Header is CheckBox checkBox)
        {
            var state = DisplayIncludedState(node);
            checkBox.IsChecked = state;
            checkBox.IsEnabled = CanToggle(node);
            // The checkbox itself indicates inclusion; never prefix
            // excluded folder names with a second redundant symbol.
            var name = node.Label ?? Path.GetFileName(
                Path.TrimEndingDirectorySeparator(node.LocalPath));
            if (string.IsNullOrEmpty(name)) name = node.LocalPath;
            checkBox.Content = node.CatalogImageCount > 0
                ? $"{name} ({node.CatalogImageCount:N0})" : name;
            checkBox.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
            UpdateItem(child);
    }

    private void InclusionCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingChecks || _busy || sender is not CheckBox { Tag: TreeViewItem item } box ||
            item.Tag is not ManagedNode node || !CanToggle(node))
            return;

        item.IsSelected = true;
        var include = box.IsChecked == true;
        if (include && node.IsRoot && node.Root is { } root &&
            !_monitoredRoots.Any(current => current.Id == root.Id))
        {
            RestoreStagedRoot(root.Id);
            StageEntireSubtreeIncluded(node.LocalPath);
            UpdateLoadedCheckboxes();
            return;
        }

        if (include)
        {
            var wasCovered = FindCoveringRoot(node.LocalPath) is not null;
            StageEntireSubtreeIncluded(node.LocalPath);
            if (wasCovered) UpdateLoadedCheckboxes();
            else Reload(expandSystemFolder: node.LocalPath);
        }
        else
        {
            // A fully checked parent excludes ALL descendants, including
            // any nested explicitly monitored roots, without touching disk.
            var hasNestedRoots = _monitoredRoots.Any(root =>
                FolderExclusionRules.IsAtOrBelow(root.LocalPath, node.LocalPath));
            StageEntireSubtreeExcluded(node.LocalPath);
            if (hasNestedRoots) Reload(expandSystemFolder: node.LocalPath);
            else UpdateLoadedCheckboxes();
        }
    }

    private void StageNewRoot(string path)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        if (_monitoredRoots.Any(root => PathEquals(root.LocalPath, normalized)))
            return;
        var covering = FindCoveringRoot(normalized);
        if (covering is not null)
        {
            StageSubtreeRule(covering, normalized, include: true);
            UpdateLoadedCheckboxes();
            return;
        }
        var prior = _uncheckedRoots.Values.FirstOrDefault(item =>
            PathEquals(item.Root.LocalPath, normalized));
        if (prior.Root is not null)
        {
            RestoreStagedRoot(prior.Root.Id);
            return;
        }
        // Do not probe a potentially offline NAS on the WPF dispatcher.
        // Classification is authoritative in FolderManagerSelectionService
        // on the scheduler worker when the user clicks OK.
        var provisional = new MonitoredFolder
        {
            Id = _nextTemporaryRootId--,
            LocalPath = normalized,
            CanonicalPath = normalized,
            IsNetwork = normalized.StartsWith(@"\\", StringComparison.Ordinal)
        };
        _monitoredRoots = _monitoredRoots.Append(provisional).ToArray();
        // If a parent of a separately monitored root is chosen, never
        // scan the existing child twice through overlapping roots.
        _exclusions[provisional.Id] = _monitoredRoots
            .Where(root => root.Id != provisional.Id &&
                FolderExclusionRules.IsAtOrBelow(root.LocalPath, normalized))
            .Select(root => root.LocalPath).ToArray();
        _inclusions[provisional.Id] = Array.Empty<string>();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFolderDialog
        {
            Title = UiLanguage.T("Ordner zur dauerhaften Überwachung auswählen")
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            StageNewRoot(dialog.FolderName);
            Reload();
        }
        catch (Exception ex)
        {
            PcasaDialog.Show(this, UiLanguage.T("Ordner konnte nicht vorgemerkt werden.") + "\n\n" +
                ex.Message, "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoot: true, Root: { } root } || _busy) return;
        RemoveStagedRoot(root.Id);
    }

    private void ServerSync_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoot: true, Root: { } root } ||
            !root.CanServerSync)
            return;
        root.ServerSyncEnabled = ServerSyncCheckBox.IsChecked == true;
    }

    private void BackgroundScan_Click(object sender, RoutedEventArgs e)
    {
        // The setting is also part of the selection. Do not send a Changed
        // event that would stop a running scanner before the user clicks OK.
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var roots = SelectedConfiguration.Roots;
        var noLongerIncluded = _catalogPhotoFolders
            .Where(folder => !roots.Any(root =>
                FolderExclusionRules.IsAtOrBelow(folder.LocalPath, root.LocalPath) &&
                !FolderExclusionRules.IsExcluded(folder.LocalPath,
                    root.ExcludedPaths)))
            .Sum(folder => (long)folder.ImageCount);
        if (noLongerIncluded > 0)
        {
            // Removing a root without selecting its existing descendants
            // intentionally removes uncaptured photos from the PCasa catalog,
            // but never from disk. Never let a mistaken checkbox silently
            // cascade-delete their database records/thumbnails.
            var answer = PcasaDialog.Show(this,
                $"{noLongerIncluded:N0} katalogisierte Bilder liegen außerhalb " +
                "der neu gewählten Überwachungsordner und würden aus dem " +
                "PCasa-Katalog entfernt.\n\n" +
                "Bereits von neuen Wurzeln abgedeckte Bilder behalten " +
                "ihre IDs, GPS-Daten, Tags und Thumbnails.\n\n" +
                "Die Originaldateien bleiben unverändert.\n\n" +
                "Möchtest Du diese Ordnerauswahl wirklich übernehmen?",
                UiLanguage.T("Ordnerauswahl übernehmen"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
