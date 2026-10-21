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
        var folder = _folderRepository.GetAll().FirstOrDefault(f => f.Id == folderId)
            ?? throw new InvalidOperationException("Der überwachte Ordner ist nicht mehr vorhanden.");
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
        var preparation = PrepareFolderRemovalAsync(folderId,
            removeMonitoredRoot ? null : normalized);
        try
        {
            HideFolderFromCurrentGallery(normalized);
            StatusText.Text = "Ordner aus PCasa entfernen …";
            await preparation;

            int removed;
            if (removeMonitoredRoot)
            {
                // No scan may repopulate this folder after the removal barrier.
                var before = _imageCatalog.CountAll();
                await Task.Run(() => _folderRepository.Remove(folderId));
                removed = Math.Max(0, before - _imageCatalog.CountAll());
                ForgetFolderActivity(normalized);
            }
            else
            {
                removed = await new FolderExclusionService(_folderRepository, _database)
                    .ChangeAsync(folderId, normalized, excluded: true,
                        (id, changedPath, excluded) =>
                            BrowseFolderManager_ExclusionChanged(this,
                                new FolderExclusionChangedEventArgs(id, changedPath, excluded)));
            }

            // Complete the visible view refresh BEFORE releasing background
            // workers or returning control to either UI entry point.
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
            StatusText.Text = $"{removed:N0} Bildeinträge aus PCasa entfernt";
            return removed;
        }
        catch
        {
            // An optimistic view must never become a second source of truth.
            // Re-read the catalog if the database mutation failed.
            await RefreshBrowseGalleryAsync(forceIndexReload: true,
                warmViewport: false);
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
