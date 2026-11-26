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
        tree.ApplyTemplate();
        scroll.ApplyTemplate();
        tree.UpdateLayout();
        if (scroll.ViewportHeight <= 0) return false;

        // Rows in the PCasa tree template are exactly 22 DIP high.
        // The selected container can be unrealized far outside the viewport
        // even though the ItemsControl already owns its TreeViewItem.
        // Scroll to its visible row index first, then refine using the
        // measured header when WPF realizes it.
        var row = 0;
        if (!FindVisibleRow(tree)) return false;
        scroll.ScrollToVerticalOffset(Math.Clamp(
            row * 22.0 - Math.Max(0, (scroll.ViewportHeight - 22) / 2),
            0, scroll.ScrollableHeight));
        tree.UpdateLayout();
        scroll.UpdateLayout();

        item.ApplyTemplate();
        if (item.Template?.FindName("PART_Header", item) is FrameworkElement header &&
            header.IsVisible && header.ActualHeight > 0 &&
            PresentationSource.FromVisual(header) is not null)
        {
            var top = header.TranslatePoint(new Point(0, 0), scroll).Y;
            var offset = scroll.VerticalOffset + top -
                Math.Max(0, (scroll.ViewportHeight - header.ActualHeight) / 2);
            scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, scroll.ScrollableHeight));
            tree.UpdateLayout();
            scroll.UpdateLayout();
        }
        return true;

        bool FindVisibleRow(ItemsControl parent)
        {
            foreach (var child in parent.Items.OfType<TreeViewItem>())
            {
                if (ReferenceEquals(child, item)) return true;
                row++;
                if (child.IsExpanded && FindVisibleRow(child)) return true;
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
