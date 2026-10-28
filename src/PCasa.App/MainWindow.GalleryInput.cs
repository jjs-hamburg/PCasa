using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private void GalleryWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewerOpen || !_browseInitialized || e.Handled) return;
        // Do not steal Ctrl+A or Delete from tag search, text editors or menus.
        if (Keyboard.FocusedElement is DependencyObject focused &&
            (FindAncestor<System.Windows.Controls.Primitives.TextBoxBase>(focused) is not null ||
             FindAncestor<ComboBox>(focused) is not null ||
             FindAncestor<Menu>(focused) is not null ||
             FindAncestor<MenuItem>(focused) is not null))
            return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var galleryHasFocus = ReferenceEquals(Keyboard.FocusedElement, BrowseSurface) ||
            (Keyboard.FocusedElement is DependencyObject selectionFocus &&
             FindAncestor<Controls.BrowseGallerySurface>(selectionFocus) is not null);
        var treeHasFocus = Keyboard.FocusedElement is DependencyObject treeFocus &&
            FindAncestor<TreeView>(treeFocus) is not null;
        // Folder-tree Ctrl+A deliberately selects that folder's gallery images.
        // Other shortcuts, especially Delete, must ONLY affect the gallery
        // while the thumbnail canvas actually owns the keyboard focus.
        if (!galleryHasFocus && !(ctrl && e.Key == Key.A && treeHasFocus))
            return;
        if (ctrl && e.Key == Key.I)
        {
            InvertCurrentFolderSelection();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.A)
        {
            // The selected TREE item is authoritative, not the current scroll
            // position or last image under the mouse.
            if ((FoldersTree.SelectedItem as TreeViewItem)?.Tag is
                FolderTreeTag { PhotoFolderId: long folderId })
            {
                _gallerySelection.SelectFolder(folderId,
                    _displayedBrowseIndex, _selectedImageIds);
                ApplyGallerySelection();
                var offset = BrowseSurface.GetFolderOffset(folderId);
                if (offset.HasValue)
                    BrowseScrollViewer.ScrollToVerticalOffset(offset.Value);
                BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                    Math.Max(1, BrowseScrollViewer.ViewportHeight));
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _selectedImageIds.Clear();
            _gallerySelection.Reset();
            ApplyGallerySelection();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && _selectedImageIds.Count != 0)
        {
            _ = DeleteSelectedImagesAsync();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.C)
        {
            _ = CopyGalleryFilesToClipboardAsync(cut: false);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.X)
        {
            _ = CopyGalleryFilesToClipboardAsync(cut: true);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.V)
        {
            _ = PasteGalleryFilesAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            _ = RefreshBrowseGalleryAsync(forceIndexReload: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && _gallerySelection.FocusedId is long openId)
        {
            BrowseSurface_ImageOpened(BrowseSurface,
                new Controls.BrowseImageClickedEventArgs(openId, ModifierKeys.None));
            e.Handled = true;
            return;
        }
        if (!ctrl && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // The gallery owns a coherent keyboard selection just as Explorer.
            // Left/right move one image. Up/down follow gallery row geometry;
            // column count is measured by the gallery control.
            var delta = e.Key switch
            {
                Key.Left => -1,
                Key.Right => 1,
                Key.Up => -BrowseSurface.VisibleColumnCount,
                _ => BrowseSurface.VisibleColumnCount
            };
            var id = _gallerySelection.MoveFocus(delta, shift,
                _displayedBrowseIndex, _selectedImageIds, BrowseSurface.HiddenFolderIds);
            if (id is long selected)
            {
                ApplyGallerySelection();
                ScrollGalleryImageIntoView(selected);
            }
            e.Handled = true;
        }
    }

    private void GalleryInvertSelection_Click(object sender, RoutedEventArgs e)
        => InvertCurrentFolderSelection();

    private void InvertCurrentFolderSelection()
    {
        if ((FoldersTree.SelectedItem as TreeViewItem)?.Tag is not
            FolderTreeTag { PhotoFolderId: long folderId })
        {
            StatusText.Text = UiLanguage.T("Zum Invertieren einen Bildordner auswählen.");
            return;
        }

        // Invert the WHOLE folder, not the currently filtered results.
        // This makes hidden originals selectable while topaz results are
        // still displayed; clearing the filter then reveals the selection.
        foreach (var image in _browseIndex.Where(item => item.PhotoFolderId == folderId))
        {
            if (!_selectedImageIds.Add(image.ImageId))
                _selectedImageIds.Remove(image.ImageId);
        }
        _gallerySelection.Reset();
        ApplyGallerySelection();
        StatusText.Text = UiLanguage.Format("{0:N0} Dateien ausgewählt (Ordner invertiert)", _selectedImageIds.Count);
    }

    private static T? FindAncestor<T>(DependencyObject? element)
        where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void ScrollGalleryImageIntoView(long id)
    {
        if (BrowseSurface.GetImageAnchorOffset(id, 0) is not double y) return;
        var top = BrowseScrollViewer.VerticalOffset;
        var height = Math.Max(1, BrowseScrollViewer.ViewportHeight);
        if (y < top)
            BrowseScrollViewer.ScrollToVerticalOffset(y);
        else if (y + BrowseSurface.ThumbnailRowHeight > top + height)
            BrowseScrollViewer.ScrollToVerticalOffset(
                y + BrowseSurface.ThumbnailRowHeight - height);
    }
}
