using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public sealed class FolderExclusionChangedEventArgs : EventArgs
{
    public FolderExclusionChangedEventArgs(long folderId, string path, bool excluded)
    {
        FolderId = folderId;
        Path = path;
        Excluded = excluded;
    }

    public long FolderId { get; }
    public string Path { get; }
    public bool Excluded { get; }
}

public partial class FolderManagerWindow : Window
{
    private sealed record ManagedNode(
        MonitoredFolder? Root, string LocalPath, bool IsRoot,
        bool IsSystemFolder = false, string? Label = null);

    private readonly FolderRepository _repository;
    private readonly Func<long, string, bool, Task<int>>? _removeFolderAsync;
    private readonly CatalogRemovalService _catalogRemovalService;
    private readonly PCasaScheduler? _scheduler;
    private readonly Func<string, Task>? _addFolderAsync;
    private readonly BackgroundScanSettings _backgroundScanSettings;
    private readonly Dictionary<long, IReadOnlyList<string>> _exclusions = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyList<(string Name, string Path)> _systemFolders;
    private IReadOnlyList<MonitoredFolder> _monitoredRoots = Array.Empty<MonitoredFolder>();
    private ManagedNode? _selected;
    private bool _updatingChecks;
    private bool _busy;

    public FolderManagerWindow(FolderRepository repository, bool serverDatabaseConfigured,
        Func<long, string, bool, Task<int>>? removeFolderAsync = null,
        PCasaScheduler? scheduler = null,
        Func<string, Task>? addFolderAsync = null)
    {
        InitializeComponent();
        _repository = repository;
        _removeFolderAsync = removeFolderAsync;
        _scheduler = scheduler;
        _addFolderAsync = addFolderAsync;
        _catalogRemovalService = new CatalogRemovalService(new LocalDatabase());
        _backgroundScanSettings = BackgroundScanSettings.Load();

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

    public event EventHandler? FoldersChanged;
    public event Action<string>? CleanupStatusChanged;
    public event EventHandler<FolderExclusionChangedEventArgs>? FolderExclusionChanged;

    private void Reload(string? expandSystemFolder = null)
    {
        _selected = null;
        FolderManagerTree.Items.Clear();
        _exclusions.Clear();
        _monitoredRoots = _repository.GetAll();

        foreach (var folder in _monitoredRoots)
        {
            _exclusions[folder.Id] = _repository.GetExcludedPaths(folder.Id);
        }

        // Picasa-style hierarchy: these are normal expandable tree entries.
        // Once included, their subfolders use exactly the same exclusion
        // handling as any user-added monitored folder.
        foreach (var (name, path) in _systemFolders)
        {
            var coveringRoot = FindCoveringRoot(path);
            var isRoot = coveringRoot is not null &&
                PathEquals(coveringRoot.LocalPath, path);
            var item = CreateItem(new ManagedNode(
                coveringRoot, path, isRoot, IsSystemFolder: true, Label: name));
            FolderManagerTree.Items.Add(item);
            if (PathEquals(expandSystemFolder, path))
            {
                item.IsExpanded = true;
                item.IsSelected = true;
            }
        }

        foreach (var folder in _monitoredRoots)
        {
            if (_systemFolders.Any(special => PathEquals(special.Path, folder.LocalPath)))
                continue;

            FolderManagerTree.Items.Add(CreateItem(
                new ManagedNode(folder,
                    FolderExclusionRules.Normalize(folder.LocalPath),
                    IsRoot: true)));
        }

        UpdateSelectedDetails();
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
        var name = node.Label ?? (node.IsRoot ? node.LocalPath
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(node.LocalPath)));
        if (string.IsNullOrWhiteSpace(name)) name = node.LocalPath;

        var checkbox = new CheckBox
        {
            Content = name,
            ToolTip = node.LocalPath,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(1, 0, 1, 0),
            IsChecked = IsIncluded(node),
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
        item.Items.Add(new TreeViewItem { Header = "Unterordner laden …", IsEnabled = false });
        item.Expanded += Folder_Expanded;
        return item;
    }

    private bool IsIncluded(ManagedNode node)
        => node.Root is not null && !IsExcluded(node);

    private bool CanToggle(ManagedNode node)
        => !_busy && !HasExcludedAncestor(node) &&
           (node.IsSystemFolder || node.Root is not null);

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
            item.Items.Count != 1 ||
            item.Items[0] is not TreeViewItem { Tag: null })
        {
            return;
        }

        var token = _lifetime.Token;
        try
        {
            var directories = await Task.Run(() =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                };
                token.ThrowIfCancellationRequested();
                return Directory.GetDirectories(node.LocalPath, "*", options)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }, token);

            if (token.IsCancellationRequested) return;
            item.Items.Clear();
            foreach (var directory in directories)
            {
                var normalized = FolderExclusionRules.Normalize(directory);
                var root = FindCoveringRoot(normalized);
                item.Items.Add(CreateItem(new ManagedNode(
                    root, normalized,
                    IsRoot: root is not null && PathEquals(root.LocalPath, normalized))));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            item.Items.Clear();
            item.Items.Add(new TreeViewItem
            {
                Header = "Unterordner nicht erreichbar: " + ex.Message,
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
        SelectedFolderText.Text = _selected?.LocalPath ?? "Bitte links einen Ordner auswählen.";
        SelectedModeText.Text = _selected is null ? ""
            : _selected.Root is null ? "Noch nicht überwacht. Ordner im Baum aktivieren."
            : _selected.IsRoot ? "Überwachte Wurzel. Kann über das Häkchen des Systemordners oder über „Wurzel entfernen“ abgewählt werden."
            : IsExcluded(_selected) ? "Von der PCasa-Bibliothek ausgeschlossen."
            : "Dieser Ordner wird von PCasa eingelesen.";
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
            checkBox.IsChecked = IsIncluded(node);
            checkBox.IsEnabled = CanToggle(node);
            if (excluded)
                checkBox.Foreground = new SolidColorBrush(Color.FromRgb(171, 50, 50));
            else
                checkBox.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
            UpdateItem(child);
    }

    private async void InclusionCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingChecks || _busy || sender is not CheckBox { Tag: TreeViewItem item } box ||
            item.Tag is not ManagedNode node || !CanToggle(node))
        {
            return;
        }

        item.IsSelected = true;
        var excluded = box.IsChecked != true;
        _busy = true;
        AddFolderButton.IsEnabled = false;
        RemoveFolderButton.IsEnabled = false;

        var saved = false;
        try
        {
            if (node.IsSystemFolder && node.Root is null)
            {
                if (!excluded)
                {
                    if (_addFolderAsync is not null)
                        await _addFolderAsync(node.LocalPath);
                    else
                        await Task.Run(() => _repository.Add(node.LocalPath));
                }
                saved = true;
                Reload(expandSystemFolder: node.LocalPath);
            }
            else if (node.Root is { } monitoredRoot)
            {
                if (excluded && _removeFolderAsync is not null)
                {
                    await _removeFolderAsync(monitoredRoot.Id,
                        node.LocalPath, false);
                }
                else
                {
                    var progress = new Progress<string>(status =>
                        CleanupStatusChanged?.Invoke(status));
                    await new FolderExclusionService(_repository, new LocalDatabase(), _scheduler)
                        .ChangeAsync(monitoredRoot.Id, node.LocalPath, excluded,
                            (id, path, isExcluded) =>
                                FolderExclusionChanged?.Invoke(this,
                                    new FolderExclusionChangedEventArgs(id, path, isExcluded)),
                            progress);
                }
                saved = true;
                _exclusions[monitoredRoot.Id] =
                    _repository.GetExcludedPaths(monitoredRoot.Id);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Die Ordnerauswahl konnte nicht vollständig übernommen werden.\n\n" + ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            AddFolderButton.IsEnabled = true;
            if (saved)
            {
                if (node.Root is { } root && !node.IsSystemFolder)
                    _exclusions[root.Id] = _repository.GetExcludedPaths(root.Id);
                FoldersChanged?.Invoke(this, EventArgs.Empty);
            }
            UpdateLoadedCheckboxes();
        }
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Ordner zur dauerhaften Überwachung auswählen"
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            if (_addFolderAsync is not null)
                await _addFolderAsync(dialog.FolderName);
            else
                await Task.Run(() => _repository.Add(dialog.FolderName));
            Reload();
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Der Ordner konnte nicht hinzugefügt werden.\n\n" + ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoot: true, Root: { } selectedRoot } || _busy) return;
        _busy = true;
        RemoveFolderButton.IsEnabled = false;
        AddFolderButton.IsEnabled = false;
        try
        {
            CleanupStatusChanged?.Invoke("Ordner aus PCasa entfernen …");
            if (_removeFolderAsync is not null)
                await _removeFolderAsync(selectedRoot.Id, selectedRoot.LocalPath, true);
            else
                await Task.Run(() => _repository.Remove(selectedRoot.Id));
            Reload();
            FoldersChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Der Ordner konnte nicht entfernt werden.\\n\\n" + ex.Message,
                "PCasa", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            AddFolderButton.IsEnabled = true;
            UpdateLoadedCheckboxes();
        }
    }

    private void ServerSync_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { IsRoot: true, Root: { } selectedRoot } ||
            !selectedRoot.CanServerSync)
            return;
        try
        {
            _repository.SetServerSync(selectedRoot.Id, ServerSyncCheckBox.IsChecked == true);
            var path = selectedRoot.LocalPath;
            Reload();
            foreach (var item in FolderManagerTree.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is ManagedNode node &&
                    string.Equals(node.LocalPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PCasa",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BackgroundScan_Click(object sender, RoutedEventArgs e)
    {
        _backgroundScanSettings.Enabled = BackgroundScanCheckBox.IsChecked == true;
        _backgroundScanSettings.Save();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
