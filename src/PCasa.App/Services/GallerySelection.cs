using PCasa.App.Models;

namespace PCasa.App.Services;

/// <summary>
/// Explorer-like selection on the CURRENT visible gallery order. The anchor
/// remains stable during range extension and CTRL clicks. File indices do not
/// survive catalog updates, so the anchor is an image ID instead.
/// </summary>
public sealed class GallerySelection
{
    public long? AnchorId { get; private set; }

    public static bool PreserveGroupUntilDragDecision(long clickedImageId,
        System.Windows.Input.ModifierKeys modifiers, IReadOnlySet<long> selected)
        => (modifiers & System.Windows.Input.ModifierKeys.Shift) == 0 &&
           selected.Contains(clickedImageId);
    public long? FocusedId { get; private set; }

    public void SetFocus(long imageId) => FocusedId = imageId;

    public void Reset()
    {
        AnchorId = null;
        FocusedId = null;
    }

    public void Click(long imageId, System.Windows.Input.ModifierKeys modifiers,
        IReadOnlyList<BrowseThumbnailIndexEntry> visible, HashSet<long> selected,
        IReadOnlySet<long>? hiddenFolderIds = null)
    {
        var position = Find(visible, imageId);
        if (position < 0 ||
            hiddenFolderIds?.Contains(visible[position].PhotoFolderId) == true)
            return;
        var ctrl = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control);
        var shift = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);

        if (shift)
        {
            var anchor = AnchorId is long saved ? Find(visible, saved) : -1;
            if (anchor < 0 ||
                hiddenFolderIds?.Contains(visible[anchor].PhotoFolderId) == true)
                anchor = position;
            // SHIFT selects a continuous range through the visible gallery,
            // including adjacent photo folders. CTRL+SHIFT adds this range
            // without discarding other selected images.
            if (!ctrl) selected.Clear();
            for (var i = Math.Min(anchor, position); i <= Math.Max(anchor, position); i++)
                if (hiddenFolderIds?.Contains(visible[i].PhotoFolderId) != true)
                    selected.Add(visible[i].ImageId);
            AnchorId = visible[anchor].ImageId;
        }
        else if (ctrl)
        {
            if (!selected.Add(imageId)) selected.Remove(imageId);
            AnchorId ??= imageId;
        }
        else
        {
            selected.Clear();
            selected.Add(imageId);
            AnchorId = imageId;
        }
        FocusedId = imageId;
    }

    public void SelectFolder(long folderId,
        IReadOnlyList<BrowseThumbnailIndexEntry> visible, HashSet<long> selected)
    {
        selected.Clear();
        foreach (var image in visible)
            if (image.PhotoFolderId == folderId) selected.Add(image.ImageId);
        var first = visible.FirstOrDefault(image => image.PhotoFolderId == folderId);
        AnchorId = first?.ImageId;
        FocusedId = first?.ImageId;
    }

    public long? MoveFocus(int delta, bool extend,
        IReadOnlyList<BrowseThumbnailIndexEntry> visible, HashSet<long> selected,
        IReadOnlySet<long>? hiddenFolderIds = null)
    {
        if (visible.Count == 0) return null;
        var position = FocusedId is long id ? Find(visible, id) : -1;
        var next = Math.Clamp(position < 0 ? (delta < 0 ? visible.Count - 1 : 0) :
            position + delta, 0, visible.Count - 1);
        var direction = Math.Sign(delta);
        while (hiddenFolderIds?.Contains(visible[next].PhotoFolderId) == true)
        {
            next += direction;
            if (direction == 0 || next < 0 || next >= visible.Count)
                return null;
        }
        Click(visible[next].ImageId,
            extend ? System.Windows.Input.ModifierKeys.Shift : System.Windows.Input.ModifierKeys.None,
            visible, selected, hiddenFolderIds);
        return visible[next].ImageId;
    }

    public static int Find(IReadOnlyList<BrowseThumbnailIndexEntry> images, long id)
    {
        for (var i = 0; i < images.Count; i++)
            if (images[i].ImageId == id) return i;
        return -1;
    }
}
