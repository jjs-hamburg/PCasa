using System.IO;
using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private readonly HashSet<string> _allFilesystemFolderPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _allFolderProjectionRunning;

    private void UpdateAllFoldersButtonState()
    {
        if (ShowAllFilesystemFoldersButton is null) return;
        // A visible pressed outline (not merely a color change) for the
        // persistent all-folders toggle, identical size to footer icons.
        ShowAllFilesystemFoldersButton.Tag =
            _appearance.ShowAllFilesystemFolders ? "ActiveGold" : null;
        ShowAllFilesystemFoldersButton.ToolTip =
            _appearance.ShowAllFilesystemFolders
                ? "Alle Ordner anzeigen: aktiv"
                : "Alle Ordner anzeigen, auch ohne Bilder";
    }

    private async void ShowAllFilesystemFolders_Click(object sender, RoutedEventArgs e)
    {
        _appearance.ShowAllFilesystemFolders = !_appearance.ShowAllFilesystemFolders;
        UpdateAllFoldersButtonState();
        SaveAppearance();
        if (!_appearance.ShowAllFilesystemFolders)
        {
            RefreshFolderTree(preserveGalleryAnchor: true);
            return;
        }
        await RefreshAllFilesystemFolderPathsAsync();
    }

    private async Task RefreshAllFilesystemFolderPathsAsync()
    {
        if (!_appearance.ShowAllFilesystemFolders || _allFolderProjectionRunning ||
            Dispatcher.HasShutdownStarted)
            return;

        _allFolderProjectionRunning = true;
        try
        {
            // The scheduler owns all network enumeration. This projection
            // lists DIRECTORIES only and never indexes photos or touches
            // catalog image state.
            var paths = await _scheduler.RunAsync(
                $"folder-tree:all-directories:{Guid.NewGuid():N}",
                SchedulerPriority.ManualAction,
                token =>
                {
                    var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var root in _folderRepository.GetAll())
                    {
                        token.ThrowIfCancellationRequested();
                        var exclusions = _folderRepository.GetExcludedPaths(root.Id);
                        if (!Directory.Exists(root.LocalPath)) continue;
                        var pending = new Stack<string>();
                        pending.Push(root.LocalPath);
                        while (pending.Count > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            var dir = pending.Pop();
                            if (FolderExclusionRules.IsExcluded(dir, exclusions))
                                continue;
                            result.Add(dir);
                            try
                            {
                                foreach (var child in Directory.EnumerateDirectories(
                                    dir, "*", new EnumerationOptions
                                    {
                                        RecurseSubdirectories = false,
                                        IgnoreInaccessible = false,
                                        AttributesToSkip = FileAttributes.ReparsePoint |
                                            FileAttributes.System
                                    }))
                                    pending.Push(child);
                            }
                            catch (Exception ex) when (
                                ex is IOException or UnauthorizedAccessException)
                            {
                                // An inaccessible directory is not a reason
                                // to erase catalog records or abort other roots.
                                PCasaDiagnostics.Write("FolderTree.ReadDirectories", ex);
                            }
                        }
                    }
                    return result;
                }, workKind: SchedulerWorkKind.SourceProbe);

            if (!_appearance.ShowAllFilesystemFolders ||
                Dispatcher.HasShutdownStarted)
                return;
            _allFilesystemFolderPaths.Clear();
            _allFilesystemFolderPaths.UnionWith(paths);
            RefreshFolderTree(preserveGalleryAnchor: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderTree.AllDirectories", ex);
            StatusText.Text = "Ordnerliste konnte nicht vollständig geladen werden.";
        }
        finally
        {
            _allFolderProjectionRunning = false;
        }
    }
}
