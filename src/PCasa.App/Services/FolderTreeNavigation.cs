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
        var changed = false;
        Visit(root);
        return changed;

        bool Visit(ItemsControl container)
        {
            foreach (var child in container.Items.OfType<TreeViewItem>())
            {
                if (ReferenceEquals(child, target))
                {
                    foreach (var parent in ancestors)
                    {
                        if (parent.IsExpanded) continue;
                        parent.IsExpanded = true;
                        changed = true;
                    }
                    return true;
                }
                ancestors.Add(child);
                if (Visit(child)) return true;
                ancestors.RemoveAt(ancestors.Count - 1);
            }
            return false;
        }
    }

    // The custom tree template contains the entire expanded subtree in the
    // TreeViewItem's bounds. BringIntoView on that item is not a reliable
    // request to reveal its selected header. Use the measured header and
    // the physical pixel offset of the actual TreeView ScrollViewer.
    public static bool RevealHeader(TreeView tree, TreeViewItem item, ScrollViewer scroll)
    {
        ExpandAncestors(tree, item);
        tree.UpdateLayout();
        item.ApplyTemplate();
        if (item.Template?.FindName("PART_Header", item) is not FrameworkElement header ||
            !header.IsVisible || header.ActualHeight <= 0 || scroll.ViewportHeight <= 0)
            return false;
        var top = header.TranslatePoint(new Point(0, 0), scroll).Y;
        var offset = scroll.VerticalOffset + top -
            Math.Max(0, (scroll.ViewportHeight - header.ActualHeight) / 2);
        scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, scroll.ScrollableHeight));
        // Commit the scroll before any catalogue-driven tree rebuild can
        // capture the obsolete initial offset of zero.
        scroll.UpdateLayout();
        return true;
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
