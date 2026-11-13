using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        bool IsSystemFolder = false, string? Label = null);

    private readonly ForegroundReadService _foregroundReadService;
    private readonly BackgroundScanSettings _backgroundScanSettings;
    private readonly Dictionary<long, IReadOnlyList<string>> _exclusions = new();
    // Roots unchecked during the dialog remain available for rechecking in
    // the right pane, with their original IDs and exclusion rules intact.
    // Only OK persists the final staged selection.
    private readonly Dictionary<long,
        (MonitoredFolder Root, IReadOnlyList<string> Exclusions)> _uncheckedRoots = new();
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
            root.ServerSyncEnabled)).ToArray(),
        BackgroundScanCheckBox.IsChecked == true);

    public FolderManagerWindow(
        IReadOnlyList<(MonitoredFolder Folder, IReadOnlyList<string> ExcludedPaths)> configuration,
        ForegroundReadService foregroundReadService, bool serverDatabaseConfigured)
    {
        InitializeComponent();
        _foregroundReadService = foregroundReadService;
        _backgroundScanSettings = BackgroundScanSettings.Load();
        _monitoredRoots = configuration.Select(item => item.Folder).ToArray();
        foreach (var (folder, excludedPaths) in configuration)
            _exclusions[folder.Id] = excludedPaths;

        ServerSyncCheckBox.Visibility = serverDatabaseConfigured
            ? Visibility.Visible : Visibility.Collapsed;
        BackgroundScanCheckBox.IsChecked = _backgroundScanSettings.Enabled;

        _systemFolders = new[]
        {
            (Name: "Desktop", Path: Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory)),
            (Name: "Bilder", Path: Environment.GetFolderPath(
                Environment.SpecialFolder.MyPictures)),
            (Name: "Dokumente", Path: Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments))
        }
        .Where(folder => !string.IsNullOrWhiteSpace(folder.Path))
        .Select(folder => (folder.Name, FolderExclusionRules.Normalize(folder.Path)))
        .DistinctBy(folder => folder.Item2, StringComparer.OrdinalIgnoreCase)
        .ToArray();

        Closed += (_, _) => _lifetime.Cancel();
        Reload();
    }


    private void Reload(string? expandSystemFolder = null)
    {
        _selected = null;
        FolderManagerTree.Items.Clear();
        _folderNodes.Clear();
        _enumeratedPaths.Clear();

        // System locations remain accessible as shortcuts; mounted drives
        // have ONE physical hierarchy, regardless of how many independent
        // monitored roots they contain. No recursive disk scan at startup.
        foreach (var (name, path) in _systemFolders)
        {
            var root = FindCoveringRoot(path);
            var item = CreateItem(new ManagedNode(root, path,
                IsRoot: root is not null && PathEquals(root.LocalPath, path),
                IsSystemFolder: true, Label: name));
            FolderManagerTree.Items.Add(item);
            if (PathEquals(expandSystemFolder, path))
                item.IsExpanded = true;
        }

        var volumes = DriveInfo.GetDrives().Select(drive => drive.Name)
            .Concat(_monitoredRoots.Select(root =>
                Path.GetPathRoot(root.LocalPath) ?? root.LocalPath))
            .Select(FolderExclusionRules.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);

        foreach (var volume in volumes)
        {
            var root = FindCoveringRoot(volume);
            var item = CreateItem(new ManagedNode(root, volume,
                IsRoot: root is not null && PathEquals(root.LocalPath, volume),
                Label: Path.TrimEndingDirectorySeparator(volume)));
            _folderNodes[volume] = item;
            FolderManagerTree.Items.Add(item);
        }

        foreach (var monitored in _monitoredRoots)
        {
            var target = FolderExclusionRules.Normalize(monitored.LocalPath);
            var volume = FolderExclusionRules.Normalize(
                Path.GetPathRoot(target) ?? target);
            if (!_folderNodes.TryGetValue(volume, out var parent)) continue;
            var relative = Path.GetRelativePath(volume, target);
            if (relative == ".") continue;
            var current = volume;
            foreach (var component in relative.Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = FolderExclusionRules.Normalize(
                    Path.Combine(current, component));
                if (_folderNodes.TryGetValue(current, out var existing))
                {
                    parent = existing;
                    continue;
                }
                var covering = FindCoveringRoot(current);
                var item = CreateItem(new ManagedNode(covering, current,
                    IsRoot: covering is not null &&
                            PathEquals(covering.LocalPath, current)));
                parent.Items.Add(item);
                _folderNodes[current] = item;
                parent = item;
            }
        }

        // Expand paths leading to explicitly selected roots, without waiting
        // for directory enumeration. Folder_Expanded merges physical siblings
        // into these existing nodes asynchronously.
        foreach (var root in _monitoredRoots)
        {
            var full = FolderExclusionRules.Normalize(root.LocalPath);
            foreach (var pair in _folderNodes)
                if (!PathEquals(pair.Key, full) &&
                    FolderExclusionRules.IsAtOrBelow(full, pair.Key))
                    pair.Value.IsExpanded = true;
        }
        if (expandSystemFolder is not null &&
            _folderNodes.TryGetValue(
                FolderExclusionRules.Normalize(expandSystemFolder), out var selected))
            selected.IsSelected = true;
        RefreshMonitoredRootsList();
        UpdateSelectedDetails();
    }

    private void RefreshMonitoredRootsList()
    {
        MonitoredRootsList.Children.Clear();
        var shown = _monitoredRoots
            .Select(root => (Root: root, Included: true))
            .Concat(_uncheckedRoots.Values.Select(entry =>
                (Root: entry.Root, Included: false)))
            .OrderBy(entry => entry.Root.LocalPath,
                StringComparer.CurrentCultureIgnoreCase);
        foreach (var (root, included) in shown)
        {
            var check = new CheckBox
            {
                Content = root.LocalPath,
                ToolTip = root.LocalPath,
                Tag = root.Id,
                IsChecked = included,
                Margin = new Thickness(2, 3, 0, 3),
                VerticalAlignment = VerticalAlignment.Center
            };
            check.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");
            check.Checked += (_, _) =>
            {
                if (_updatingChecks || _busy) return;
                RestoreStagedRoot(root.Id);
            };
            check.Unchecked += (_, _) =>
            {
                if (_updatingChecks || _busy) return;
                RemoveStagedRoot(root.Id);
            };
            MonitoredRootsList.Children.Add(check);
        }
    }

    private void RemoveStagedRoot(long rootId)
    {
        var root = _monitoredRoots.FirstOrDefault(candidate =>
            candidate.Id == rootId);
        if (root is null) return;
        _uncheckedRoots[rootId] = (root,
            _exclusions.GetValueOrDefault(rootId) ?? Array.Empty<string>());
        _monitoredRoots = _monitoredRoots
            .Where(candidate => candidate.Id != rootId).ToArray();
        _exclusions.Remove(rootId);
        Reload();
    }

    private void RestoreStagedRoot(long rootId)
    {
        if (!_uncheckedRoots.Remove(rootId, out var previous)) return;
        _monitoredRoots = _monitoredRoots.Append(previous.Root).ToArray();
        _exclusions[rootId] = previous.Exclusions;
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
            Content = name,
            ToolTip = node.LocalPath,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(1, 0, 1, 0),
            IsChecked = DisplayIncludedState(node),
            IsThreeState = true,
            IsEnabled = CanToggle(node)
        };
        checkbox.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");

        var item = new TreeViewItem
        {
            Header = checkbox,
            HeaderTemplate = (DataTemplate)FolderManagerTree.Resources[
                "ManagerFolderHeaderTemplate"],
            Tag = node,
            ToolTip = node.LocalPath
        };

        checkbox.Tag = item;
        checkbox.Checked += InclusionCheckbox_Changed;
        checkbox.Unchecked += InclusionCheckbox_Changed;
        if (node.Root is not null && IsExcluded(node))
        {
            checkbox.Foreground = new SolidColorBrush(Color.FromRgb(171, 50, 50));
        }

        // Only expand actual directories as needed; never recursively enumerate
        // the whole source tree while the manager window is opening.
        item.Items.Add(new TreeViewItem { Header = UiLanguage.T("Unterordner laden …"), IsEnabled = false });
        item.Expanded += Folder_Expanded;
        return item;
    }

    private bool IsIncluded(ManagedNode node)
        => node.Root is not null && !IsExcluded(node);

    // An unchecked physical folder can be added directly as a new root.
    private bool CanToggle(ManagedNode node)
        => !_busy && !HasExcludedAncestor(node);

    private bool? DisplayIncludedState(ManagedNode node)
    {
        if (IsIncluded(node))
        {
            if (_exclusions.TryGetValue(node.Root!.Id, out var exclusions) &&
                exclusions.Any(path => FolderExclusionRules.IsAtOrBelow(
                    path, node.LocalPath)))
                return null; // Partially included subtree.
            return true;
        }
        return _monitoredRoots.Any(root =>
            FolderExclusionRules.IsAtOrBelow(root.LocalPath, node.LocalPath))
            ? null : false; // Only some descendants have own roots.
    }

    private bool IsExcluded(ManagedNode node)
        => node.Root is not null &&
           _exclusions.TryGetValue(node.Root.Id, out var paths) &&
           FolderExclusionRules.IsExcluded(node.LocalPath, paths);

    private bool HasExcludedAncestor(ManagedNode node)
        => node.Root is not null &&
           _exclusions.TryGetValue(node.Root.Id, out var paths) &&
           paths.Any(path =>
               FolderExclusionRules.IsAtOrBelow(node.LocalPath, path) &&
               !string.Equals(FolderExclusionRules.Normalize(node.LocalPath),
                   FolderExclusionRules.Normalize(path),
                   StringComparison.OrdinalIgnoreCase));

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
            : _selected.Root is null ? UiLanguage.T("Noch nicht überwacht. Ordner im Baum aktivieren.")
            : _selected.IsRoot ? UiLanguage.T("Überwachte Wurzel. Kann über das Häkchen des Systemordners oder über „Wurzel entfernen“ abgewählt werden.")
            : IsExcluded(_selected) ? UiLanguage.T("Von der PCasa-Bibliothek ausgeschlossen.")
            : UiLanguage.T("Dieser Ordner wird von PCasa eingelesen.");
        RemoveFolderButton.IsEnabled =
            _selected is { IsRoot: true, Root: not null } && !_busy;
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
            var excluded = IsExcluded(node);
            checkBox.IsChecked = DisplayIncludedState(node);
            checkBox.IsEnabled = CanToggle(node);
            if (excluded)
                checkBox.Foreground = new SolidColorBrush(Color.FromRgb(171, 50, 50));
            else
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
        if (node.Root is null)
        {
            if (include)
                StageNewRoot(node.LocalPath);
            Reload(expandSystemFolder: node.LocalPath);
            return;
        }

        var root = node.Root;
        if (node.IsRoot)
        {
            if (!include)
            {
                RemoveStagedRoot(root.Id);
            }
            else
                UpdateLoadedCheckboxes();
            return;
        }

        var excluded = _exclusions.TryGetValue(root.Id, out var existing)
            ? existing.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = FolderExclusionRules.Normalize(node.LocalPath);
        if (include)
        {
            // A parent must be included before one of its children.
            excluded.RemoveWhere(path => string.Equals(
                FolderExclusionRules.Normalize(path), normalized,
                StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            excluded.RemoveWhere(path => FolderExclusionRules.IsAtOrBelow(path, normalized));
            excluded.Add(normalized);
        }
        _exclusions[root.Id] = excluded.OrderBy(path => path,
            StringComparer.OrdinalIgnoreCase).ToArray();
        UpdateLoadedCheckboxes();
    }

    private void StageNewRoot(string path)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        if (_monitoredRoots.Any(root => PathEquals(root.LocalPath, normalized)))
            return;
        var prior = _uncheckedRoots.Values.FirstOrDefault(item =>
            PathEquals(item.Root.LocalPath, normalized));
        if (prior.Root is not null)
        {
            RestoreStagedRoot(prior.Root.Id);
            return;
        }
        var classification = PathClassifier.Classify(normalized);
        var provisional = new MonitoredFolder
        {
            Id = _nextTemporaryRootId--,
            LocalPath = classification.LocalPath,
            CanonicalPath = classification.CanonicalPath,
            IsNetwork = classification.IsNetwork
        };
        _monitoredRoots = _monitoredRoots.Append(provisional).ToArray();
        _exclusions[provisional.Id] = Array.Empty<string>();
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
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
