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
    /// <summary>
    /// Expand ancestors by the actual ItemsControl object graph. LogicalTreeHelper
    /// cannot reliably see descendants of a collapsed or not-yet-realized
    /// TreeViewItem; on large NAS roots this left the destination hidden and
    /// forced repeated full catalog reads instead of navigating it.
    /// </summary>
    public static bool ExpandAncestors(ItemsControl root, TreeViewItem target)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);
        var ancestors = new List<TreeViewItem>();
        return Visit(root);

        bool Visit(ItemsControl container)
        {
            foreach (var child in container.Items.OfType<TreeViewItem>())
            {
                if (ReferenceEquals(child, target))
                {
                    var changed = false;
                    foreach (var parent in ancestors)
                    {
                        if (parent.IsExpanded) continue;
                        parent.IsExpanded = true;
                        changed = true;
                    }
                    return changed || ancestors.Count == 0;
                }
                ancestors.Add(child);
                if (Visit(child)) return true;
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            return false;
        }
    }

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
