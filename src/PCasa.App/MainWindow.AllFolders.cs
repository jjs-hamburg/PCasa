using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // A temporary alternative visual projection, NOT another catalog tree.
    // The existing FoldersTree remains alive and controls gallery ordering,
    // collapsed-folder visibility, selection and saved expansion state.
    private string? _allFolderProjectionRootPath;
    private CancellationTokenSource? _explorerSession;
    private int _explorerGeneration;
    private readonly HashSet<string> _explorerPending =
        new(StringComparer.OrdinalIgnoreCase);

    private void UpdateAllFoldersButtonState()
    {
        if (ShowAllFilesystemFoldersButton is null) return;
        ShowAllFilesystemFoldersButton.Tag =
            _allFolderProjectionRootPath is null ? null : "ActiveGold";
        ShowAllFilesystemFoldersButton.ToolTip =
            _allFolderProjectionRootPath is string root
                ? $"Exploreransicht schließen: {root}"
                : "Exploreransicht: alle Verzeichnisse des ausgewählten Ordners";
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
            StatusText.Text = "Zuerst einen Ordner im Ordnerbaum auswählen.";
            return;
        }

        // Only the chosen path is captured. No whole-drive/NAS enumeration.
        // Every subsequent click on a twisty loads exactly ONE level.
        var path = FolderExclusionRules.Normalize(selected.FullPath);
        _allFolderProjectionRootPath = path;
        _explorerSession = new CancellationTokenSource();
        ++_explorerGeneration;
        _explorerPending.Clear();

        ExplorerTree.Items.Clear();
        var root = MakeExplorerItem(path);
        ExplorerTree.Items.Add(root);
        FoldersTree.Visibility = Visibility.Collapsed;
        ExplorerTree.Visibility = Visibility.Visible;
        UpdateAllFoldersButtonState();
        root.IsSelected = true;
        root.IsExpanded = true;
        _ = LoadExplorerChildrenAsync(root); // also safe if Expanded routed first
    }

    private void HideExplorerFolders()
    {
        ++_explorerGeneration; // fence late NAS completions and stale dispatcher posts
        _explorerSession?.Cancel();
        _explorerSession?.Dispose();
        _explorerSession = null;
        _explorerPending.Clear();
        _allFolderProjectionRootPath = null;
        ExplorerTree.Visibility = Visibility.Collapsed;
        ExplorerTree.Items.Clear();
        FoldersTree.Visibility = Visibility.Visible; // original UI state untouched
        UpdateAllFoldersButtonState();
    }

    private TreeViewItem MakeExplorerItem(string path)
    {
        var known = _photoFolders.FirstOrDefault(folder =>
            string.Equals(FolderExclusionRules.Normalize(folder.LocalPath),
                path, StringComparison.OrdinalIgnoreCase));
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(name)) name = path;
        var item = new TreeViewItem
        {
            Header = known is { ImageCount: > 0 }
                ? $"{name} ({known.ImageCount:N0})" : name,
            ToolTip = path,
            Tag = CreateFolderTreeTag(path, known?.Id)
        };
        // Explorer expansion arrows are optimistic: the underlying filesystem
        // is queried only on expansion, never once per child to test emptiness.
        item.Items.Add(new TreeViewItem
        {
            Header = "Ordner laden …",
            IsEnabled = false
        });
        return item;
    }

    private void ExplorerTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (_allFolderProjectionRootPath is null ||
            e.OriginalSource is not TreeViewItem { Tag: FolderTreeTag } item ||
            item.Items.Count != 1 ||
            item.Items[0] is not TreeViewItem { Tag: null })
            return;
        _ = LoadExplorerChildrenAsync(item);
    }

    private async Task LoadExplorerChildrenAsync(TreeViewItem item)
    {
        if (item.Tag is not FolderTreeTag tag || _explorerSession is null ||
            _allFolderProjectionRootPath is null ||
            item.Items.Count != 1 ||
            item.Items[0] is not TreeViewItem { Tag: null })
            return;

        var path = tag.FullPath;
        if (!_explorerPending.Add(path)) return;
        var generation = _explorerGeneration;
        var session = _explorerSession;
        ((TreeViewItem)item.Items[0]).Header = "Wird geladen …";
        try
        {
            // Exactly one directory per interaction. The shared scheduler
            // owns all filesystem probes; never enumerate NAS paths in WPF.
            var children = await _scheduler.RunAsync(
                $"folder-tree:explorer:{generation}:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var opts = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = true,
                        AttributesToSkip = 0 // show Windows, hidden and system folders
                    };
                    var result = Directory.EnumerateDirectories(path, "*", opts)
                        .Select(FolderExclusionRules.Normalize)
                        .OrderBy(p => Path.GetFileName(
                            Path.TrimEndingDirectorySeparator(p)),
                            StringComparer.CurrentCultureIgnoreCase)
                        .ToArray();
                    token.ThrowIfCancellationRequested();
                    return result;
                },
                () => generation == _explorerGeneration &&
                      ReferenceEquals(session, _explorerSession),
                cancellationToken: session.Token,
                workKind: SchedulerWorkKind.SourceProbe);
            if (generation != _explorerGeneration ||
                !ReferenceEquals(session, _explorerSession) ||
                Dispatcher.HasShutdownStarted) return;

            // ONE publication, no repeated full-tree rebuild, no scrollbar
            // resets. Reuse the existing tree-row style and context scanning.
            item.Items.Clear();
            foreach (var child in children)
                item.Items.Add(MakeExplorerItem(child));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            PCasaDiagnostics.Write("Explorer.EnumerateDirectory", ex);
            if (generation == _explorerGeneration &&
                ReferenceEquals(session, _explorerSession) &&
                !Dispatcher.HasShutdownStarted)
            {
                item.Items.Clear();
                item.Items.Add(new TreeViewItem
                {
                    Header = "Kein Zugriff oder nicht erreichbar",
                    IsEnabled = false
                });
            }
        }
        finally
        {
            if (generation == _explorerGeneration)
                _explorerPending.Remove(path);
        }
    }

    private async void ExplorerTree_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_allFolderProjectionRootPath is null ||
            e.NewValue is not TreeViewItem { Tag: FolderTreeTag tag } item ||
            tag.PhotoFolderId is not long id || !_browseInitialized)
            return;
        // Selecting an already indexed folder preserves the existing gallery
        // navigation path. Unindexed folders become navigable after a manual
        // scan commits them; no independent gallery lookup is introduced.
        await NavigateToPhotoFolderAsync(item, id);
    }

    private void RefreshExplorerIndexedFolders()
    {
        if (_allFolderProjectionRootPath is null) return;
        var byPath = _photoFolders.ToDictionary(
            folder => FolderExclusionRules.Normalize(folder.LocalPath),
            folder => folder, StringComparer.OrdinalIgnoreCase);
        Refresh(ExplorerTree);
        void Refresh(ItemsControl parent)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is not FolderTreeTag existing) continue;
                if (byPath.TryGetValue(existing.FullPath, out var indexed))
                {
                    if (existing.PhotoFolderId != indexed.Id)
                        item.Tag = CreateFolderTreeTag(existing.FullPath, indexed.Id);
                    var name = Path.GetFileName(
                        Path.TrimEndingDirectorySeparator(existing.FullPath));
                    if (string.IsNullOrEmpty(name)) name = existing.FullPath;
                    item.Header = indexed.ImageCount > 0
                        ? $"{name} ({indexed.ImageCount:N0})" : name;
                }
                Refresh(item);
            }
        }
    }
}
