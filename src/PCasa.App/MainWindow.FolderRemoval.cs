using System.Windows;
using System.Windows.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Sole owner of a user-requested folder removal. Both the folder manager
    // and the folder-tree context menu call this exact operation.
    private async Task<int> RemoveFolderFromPcasaAsync(long folderId,
        string path, bool removeMonitoredRoot)
    {
        // UI reads its published tree snapshot; SQLite/UNC probes never
        // precede the immediate visible removal on the WPF dispatcher.
        var folder = _dropTargetProjection
            .Select(root => root.Folder)
            .FirstOrDefault(root => root.Id == folderId)
            ?? throw new InvalidOperationException(
                "Der überwachte Ordner ist nicht mehr im Ordnerbaum vorhanden.");
        var normalized = FolderExclusionRules.Normalize(path);
        if (!FolderExclusionRules.IsAtOrBelow(normalized, folder.LocalPath))
            throw new InvalidOperationException("Der Ordner liegt außerhalb der Überwachung.");
        if (removeMonitoredRoot &&
            !string.Equals(normalized,
                FolderExclusionRules.Normalize(folder.LocalPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nur ein überwachter Stammordner kann entfernt werden.");

        // Establish the cancellation barrier first. It may be awaiting
        // background work, but hiding the subtree below is synchronous and
        // immediately visible on Dispatcher while cancellation completes.
        var preparation = PrepareFolderRemovalAsync(folderId, normalized);
        try
        {
            HideFolderFromCurrentGallery(normalized);
            StatusText.Text = "Ordner aus PCasa entfernen …";
            await preparation;

            int removed;
            if (removeMonitoredRoot)
            {
                // No scan may repopulate this folder after the removal barrier.
                removed = 0;
                await _scheduler.EnqueueAsync(
                    $"monitor-remove:{folder.CanonicalPath}:{folderId}",
                    SchedulerPriority.ManualAction,
                    async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        var current = _folderRepository.GetAll()
                            .FirstOrDefault(root => root.Id == folderId)
                            ?? throw new OperationCanceledException(
                                "Dieser Überwachungsordner existiert nicht mehr.");
                        if (!string.Equals(
                                FolderExclusionRules.Normalize(current.LocalPath),
                                normalized, StringComparison.OrdinalIgnoreCase))
                            throw new OperationCanceledException(
                                "Ein inzwischen geänderter Ordner darf nicht entfernt werden.");
                        var before = _imageCatalog.CountAll();
                        _folderRepository.Remove(folderId);
                        removed = Math.Max(0, before - _imageCatalog.CountAll());

                        // Like an exclusion, monitored-root removal is
                        // Completed only after its catalog view was published.
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            _dropTargetProjection = _dropTargetProjection
                                .Where(root => root.Folder.Id != folderId).ToArray();
                            _dragTargetValidity.Clear();
                            ForgetFolderActivity(normalized);
                            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                                warmViewport: false);
                        }).Task.Unwrap();
                    });
            }
            else
            {
                removed = await new FolderExclusionService(_folderRepository, _database, _scheduler)
                    .ChangeAsync(folderId, normalized, excluded: true,
                        publish: async (id, changedPath, excluded, currentRules) =>
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                OnFolderExclusionCommitted(id, changedPath,
                                    excluded, currentRules);
                                await RefreshBrowseGalleryAsync(forceIndexReload: true,
                                    warmViewport: false);
                            }).Task.Unwrap());
            }

            // Both root and subtree operations now publish inside their
            // single scheduler job, before returning to either UI entry.
            StatusText.Text = $"{removed:N0} Bildeinträge aus PCasa entfernt";
            return removed;
        }
        catch
        {
            // Undo the optimistic projection fence BEFORE rebuilding the
            // view from the actual committed catalog after a failure.
            _pendingFolderRemovalPath = null;
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            // A DB commit might have succeeded while its UI publication
            // failed. Reload the authoritative configuration on a catalog
            // worker before restoring the visual tree.
            _dropTargetProjection = await _scheduler.RunAsync(
                $"catalog:restore-folder-tree:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ReadFolderTreeConfigurationSnapshot();
                }, workKind: SchedulerWorkKind.CatalogRead);
            _dragTargetValidity.Clear();
            RefreshFolderTree(preserveGalleryAnchor: true);
            throw;
        }
        finally
        {
            CompleteFolderRemoval();
        }
    }

    private void HideFolderFromCurrentGallery(string path)
    {
        // This is solely an immediate, reversible projection of the current
        // catalog. The database remains the authority and the final refresh
        // replaces this projection after the transaction has completed.
        var hiddenFolders = _photoFolders
            .Where(folder => FolderExclusionRules.IsAtOrBelow(folder.LocalPath, path))
            .Select(folder => folder.Id)
            .ToHashSet();

        var removedImageIds = _browseIndex
            .Where(image => hiddenFolders.Contains(image.PhotoFolderId))
            .Select(image => image.ImageId)
            .ToHashSet();
        _browseRefreshRequests.Invalidate();
        _photoFolders = _photoFolders
            .Where(folder => !hiddenFolders.Contains(folder.Id))
            .ToArray();
        _browseIndex = _browseIndex
            .Where(image => !hiddenFolders.Contains(image.PhotoFolderId))
            .ToArray();
        _displayedBrowseIndex = _displayedBrowseIndex
            .Where(image => !hiddenFolders.Contains(image.PhotoFolderId))
            .ToArray();
        _selectedImageIds.RemoveWhere(removedImageIds.Contains);
        if (_activePhotoFolderId is long active &&
            hiddenFolders.Contains(active))
            _activePhotoFolderId = null;

        if (_browsePageCache is not null)
        {
            _browsePageCache.SetIndex(_displayedBrowseIndex);
            BrowseSurface.SetData(_displayedBrowseIndex, _photoFolders,
                _browsePageCache);
            BrowseSurface.SetSelection(_selectedImageIds);
        }
        EmptyPanel.Visibility = _displayedBrowseIndex.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
        UpdateComparisonButtons();

        // The tree is not rebuilt from the still-uncommitted repository:
        // remove the existing visual node immediately instead.
        RemoveVisualFolderNode(FoldersTree, path);
    }

    private static bool RemoveVisualFolderNode(ItemsControl parent, string path)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>().ToArray())
        {
            if (item.Tag is FolderTreeTag tag &&
                string.Equals(FolderExclusionRules.Normalize(tag.FullPath), path,
                    StringComparison.OrdinalIgnoreCase))
            {
                parent.Items.Remove(item);
                return true;
            }
            if (RemoveVisualFolderNode(item, path))
                return true;
        }
        return false;
    }
}
