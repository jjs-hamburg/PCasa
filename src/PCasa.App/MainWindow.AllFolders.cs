using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // One temporary visibility scope, not a per-folder flag or catalog state.
    // Selection changes never silently move the scope while images are copied.
    private string? _allFolderProjectionRootPath;
    private CancellationTokenSource? _allFolderProjectionCancellation;
    private readonly HashSet<string> _allFilesystemFolderPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private void UpdateAllFoldersButtonState()
    {
        if (ShowAllFilesystemFoldersButton is null) return;
        ShowAllFilesystemFoldersButton.Tag =
            _allFolderProjectionRootPath is not null ? "ActiveGold" : null;
        ShowAllFilesystemFoldersButton.ToolTip =
            _allFolderProjectionRootPath is string path
                ? $"Auch bildlose Ordner ausblenden: {path}"
                : "Auch bildlose Ordner im gewählten Ordner und seinen Unterordnern anzeigen";
    }

    private async void ShowAllFilesystemFolders_Click(object sender, RoutedEventArgs e)
    {
        if (_allFolderProjectionRootPath is not null)
        {
            // Switch OFF immediately, even if a source probe is suspended on
            // inaccessible NAS I/O. Its cancellation and identity fence keep
            // any late progress/result from restoring hidden tree nodes.
            _allFolderProjectionRootPath = null;
            _allFolderProjectionCancellation?.Cancel();
            _allFolderProjectionCancellation = null;
            _allFilesystemFolderPaths.Clear();
            UpdateAllFoldersButtonState();
            RefreshFolderTree(preserveGalleryAnchor: true);
            return;
        }

        if (FoldersTree.SelectedItem is not TreeViewItem
            { Tag: FolderTreeTag selected })
            return;

        // The path is captured exactly once on activation, including if the
        // selection later changes to a newly exposed destination directory.
        var scope = FolderExclusionRules.Normalize(selected.FullPath);
        var cancellation = new CancellationTokenSource();
        _allFolderProjectionRootPath = scope;
        _allFolderProjectionCancellation = cancellation;
        _allFilesystemFolderPaths.Clear();
        selected.IsExpanded = true;
        UpdateAllFoldersButtonState();
        await RefreshAllFilesystemFolderPathsAsync(scope, cancellation);
    }

    private async Task RefreshAllFilesystemFolderPathsAsync(
        string scope, CancellationTokenSource cancellation)
    {
        var progress = new Progress<string[]>(batch =>
        {
            if (Dispatcher.HasShutdownStarted ||
                !ReferenceEquals(_allFolderProjectionCancellation, cancellation) ||
                !string.Equals(_allFolderProjectionRootPath, scope,
                    StringComparison.OrdinalIgnoreCase))
                return;

            foreach (var path in batch)
                _allFilesystemFolderPaths.Add(path);
            RefreshFolderTree(preserveGalleryAnchor: true);
        });

        try
        {
            // All filesystem enumeration belongs to the existing scheduler.
            // Each registered source contributes only its intersection with
            // the selected subtree, never an unrelated NAS or drive root.
            await _scheduler.RunAsync(
                $"folder-tree:scope:{Guid.NewGuid():N}",
                SchedulerPriority.ManualAction,
                token =>
                {
                    var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pendingBatch = new List<string>(512);
                    var reportClock = System.Diagnostics.Stopwatch.StartNew();
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.ReparsePoint |
                            FileAttributes.System
                    };

                    foreach (var root in _folderRepository.GetAll())
                    {
                        token.ThrowIfCancellationRequested();
                        string startPath;
                        if (FolderExclusionRules.IsAtOrBelow(scope, root.LocalPath))
                            startPath = scope;
                        else if (FolderExclusionRules.IsAtOrBelow(root.LocalPath, scope))
                            startPath = root.LocalPath;
                        else
                            continue;

                        var exclusions = _folderRepository.GetExcludedPaths(root.Id);
                        var pending = new Stack<string>();
                        pending.Push(startPath);
                        while (pending.Count > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            var directory = pending.Pop();
                            if (FolderExclusionRules.IsExcluded(directory, exclusions) ||
                                !known.Add(directory))
                                continue;

                            pendingBatch.Add(directory);
                            if (pendingBatch.Count >= 512 ||
                                reportClock.ElapsedMilliseconds >= 500)
                            {
                                ((IProgress<string[]>)progress).Report(pendingBatch.ToArray());
                                pendingBatch.Clear();
                                reportClock.Restart();
                            }

                            try
                            {
                                foreach (var child in Directory.EnumerateDirectories(
                                    directory, "*", options))
                                {
                                    token.ThrowIfCancellationRequested();
                                    pending.Push(child);
                                }
                            }
                            catch (Exception ex) when (
                                ex is IOException or UnauthorizedAccessException)
                            {
                                // No catalog deletion or failed-source cleanup.
                                PCasaDiagnostics.Write("FolderTree.ReadDirectories", ex);
                            }
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    if (pendingBatch.Count > 0)
                        ((IProgress<string[]>)progress).Report(pendingBatch.ToArray());
                    return true;
                },
                cancellationToken: cancellation.Token,
                workKind: SchedulerWorkKind.SourceProbe);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("FolderTree.SelectedDirectories", ex);
            if (ReferenceEquals(_allFolderProjectionCancellation, cancellation) &&
                !Dispatcher.HasShutdownStarted)
                StatusText.Text = "Ordnerliste konnte nicht vollständig geladen werden.";
        }
        finally
        {
            if (ReferenceEquals(_allFolderProjectionCancellation, cancellation))
                _allFolderProjectionCancellation = null;
            cancellation.Dispose();
        }
    }
}
