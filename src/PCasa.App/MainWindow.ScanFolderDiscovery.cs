using System.IO;
using System.Windows.Controls;
using PCasa.App.Services;
using PCasa.App.Models;

namespace PCasa.App;

public partial class MainWindow
{
    /// <summary>
    /// Add a newly catalogued photo directory to the visible TreeView without
    /// rebuilding the gallery, changing its scroll anchor or selecting nodes.
    /// Called on the WPF dispatcher by the scanner's Progress callback AFTER
    /// the first photo in the directory is durably committed to SQLite.
    /// </summary>
    private void ShowScannedPhotoFolder(ScanProgress progress)
    {
        if (progress.DiscoveredPhotoFolderId is not long photoId ||
            progress.DiscoveredPhotoFolderPath is not string folderPath ||
            !_browseInitialized || _folderTreeItems.ContainsKey(photoId) ||
            IsPendingFolderRemovalHidden(folderPath))
            return;

        var monitored = _dropTargetProjection
            .Select(entry => entry.Folder)
            .Where(root => FolderExclusionRules.IsAtOrBelow(
                folderPath, root.LocalPath))
            .OrderByDescending(root => root.LocalPath.Length)
            .FirstOrDefault();
        if (monitored is null) return;
        var item = FindFolderTreeItemByPath(FoldersTree, monitored.LocalPath);
        if (item is null) return;

        // The monitored root may itself directly contain images.
        if (string.Equals(FolderExclusionRules.Normalize(folderPath),
            FolderExclusionRules.Normalize(monitored.LocalPath),
            StringComparison.OrdinalIgnoreCase))
        {
            item.Tag = CreateFolderTreeTag(monitored.LocalPath, photoId);
            _folderTreeItems[photoId] = item;
            return;
        }

        foreach (var part in GetFolderPathHierarchy(folderPath))
        {
            if (!FolderExclusionRules.IsAtOrBelow(part.FullPath, monitored.LocalPath) ||
                string.Equals(FolderExclusionRules.Normalize(part.FullPath),
                    FolderExclusionRules.Normalize(monitored.LocalPath),
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var next = item.Items.OfType<TreeViewItem>().FirstOrDefault(child =>
                child.Tag is FolderTreeTag tag &&
                string.Equals(tag.FullPath, part.FullPath,
                    StringComparison.OrdinalIgnoreCase));
            var atImageFolder = string.Equals(
                FolderExclusionRules.Normalize(part.FullPath),
                FolderExclusionRules.Normalize(folderPath),
                StringComparison.OrdinalIgnoreCase);
            if (next is null)
            {
                next = new TreeViewItem
                {
                    Header = part.DisplayName,
                    ToolTip = part.FullPath,
                    Tag = CreateFolderTreeTag(part.FullPath,
                        atImageFolder ? photoId : null)
                };
                // Sorting remains consistent with the full rebuild and the
                // gallery's single authoritative folder order.
                var position = 0;
                foreach (var sibling in item.Items.OfType<TreeViewItem>())
                {
                    var siblingName = sibling.Tag is FolderTreeTag siblingTag
                        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(siblingTag.FullPath))
                        : sibling.Header?.ToString() ?? string.Empty;
                    if (StringComparer.CurrentCultureIgnoreCase.Compare(
                        siblingName, part.DisplayName) > 0) break;
                    position++;
                }
                item.Items.Insert(position, next);
                item.IsExpanded = true;
            }
            else if (atImageFolder)
            {
                next.Tag = CreateFolderTreeTag(part.FullPath, photoId);
            }
            item = next;
        }
        _folderTreeItems[photoId] = item;
    }
}
