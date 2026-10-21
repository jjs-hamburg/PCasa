using System.Windows;
using System.Windows.Controls;

namespace PCasa.App.Services;

/// <summary>
/// Makes a selected photo folder actually visible in the folder tree.
/// The gallery's hidden-folder filter remains derived solely from expansion
/// state, without permanent exceptions for individual photo-folder IDs.
/// </summary>
public static class FolderTreeNavigation
{
    public static bool ExpandAncestors(TreeViewItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parents = new List<TreeViewItem>();
        var current = LogicalTreeHelper.GetParent(item);
        while (current is not null)
        {
            if (current is TreeViewItem treeItem)
                parents.Add(treeItem);
            current = LogicalTreeHelper.GetParent(current);
        }

        var changed = false;
        // Expand from the outermost parent inward, just as a user would.
        for (var i = parents.Count - 1; i >= 0; i--)
        {
            if (parents[i].IsExpanded) continue;
            parents[i].IsExpanded = true;
            changed = true;
        }
        return changed;
    }
}
