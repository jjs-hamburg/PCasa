using PCasa.App.Models;

namespace PCasa.App.Services;

/// <summary>
/// There is one displayed folder order: the actual folder-tree traversal.
/// Canonical (including UNC) paths identify sources, but must not impose a
/// different image order on the gallery. Preserve the file order within every
/// photo folder, already supplied by the catalog index query.
/// </summary>
public static class GalleryFolderOrdering
{
    public static IReadOnlyList<BrowseThumbnailIndexEntry> Order(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyDictionary<long, int> treePositions)
    {
        if (index.Count < 2) return index;
        var ordered = true;
        var previous = Position(index[0].PhotoFolderId, treePositions);
        for (var i = 1; i < index.Count; i++)
        {
            var current = Position(index[i].PhotoFolderId, treePositions);
            if (current < previous) { ordered = false; break; }
            previous = current;
        }
        if (ordered) return index;

        // Stable OrderBy retains filename order in each folder. A half-million
        // images require one sort per changed index, not on each scroll event.
        return index.OrderBy(entry => Position(entry.PhotoFolderId, treePositions))
            .ToArray();
    }

    private static int Position(long folderId, IReadOnlyDictionary<long, int> positions)
        => positions.TryGetValue(folderId, out var index) ? index : int.MaxValue;
}
