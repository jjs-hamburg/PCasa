using PCasa.App.Models;

namespace PCasa.App.Services;

// A single immutable projection of what the gallery is currently showing.
// Both the interactive viewer and the future slideshow can navigate this
// sequence. No filesystem enumeration or separate tree traversal is needed.
public sealed class PhotoSequence
{
    private readonly IReadOnlyList<BrowseThumbnailIndexEntry> _gallery;
    private readonly int[] _galleryPositions;

    private PhotoSequence(IReadOnlyList<BrowseThumbnailIndexEntry> gallery,
        int[] galleryPositions)
    {
        _gallery = gallery;
        _galleryPositions = galleryPositions;
    }

    public int Count => _galleryPositions.Length;

    public bool UsesGallery(IReadOnlyList<BrowseThumbnailIndexEntry> gallery)
        => ReferenceEquals(_gallery, gallery);

    public int GalleryPositionAt(int position)
        => _galleryPositions[position];

    public BrowseThumbnailIndexEntry At(int position)
        => _gallery[_galleryPositions[position]];

    public int IndexOfImage(long imageId)
    {
        for (var position = 0; position < Count; position++)
            if (At(position).ImageId == imageId) return position;
        return -1;
    }

    public int Move(int position, int direction)
    {
        if (Count == 0) return -1;
        return (int)(((long)position + direction % Count + Count) % Count);
    }

    public static PhotoSequence Create(
        IReadOnlyList<BrowseThumbnailIndexEntry> displayedGallery,
        IReadOnlySet<long> hiddenFolderIds,
        long openedImageId, PhotoSequenceScope scope)
    {
        ArgumentNullException.ThrowIfNull(displayedGallery);
        ArgumentNullException.ThrowIfNull(hiddenFolderIds);

        var opened = displayedGallery.FirstOrDefault(entry =>
            entry.ImageId == openedImageId &&
            !hiddenFolderIds.Contains(entry.PhotoFolderId));
        if (opened is null)
            return new PhotoSequence(displayedGallery, []);

        var positions = new List<int>();
        for (var i = 0; i < displayedGallery.Count; i++)
        {
            var image = displayedGallery[i];
            if (hiddenFolderIds.Contains(image.PhotoFolderId)) continue;
            if (scope == PhotoSequenceScope.CurrentFolder &&
                image.PhotoFolderId != opened.PhotoFolderId) continue;
            positions.Add(i);
        }
        return new PhotoSequence(displayedGallery, positions.ToArray());
    }
}
