using PCasa.App.Models;

namespace PCasa.App.Services;

/// <summary>
/// A thumbnail's packed bytes can change without changing its catalog
/// identity, sorted position, caption, GPS/Undo badges or gallery selection.
/// One replacement operation is shared by the main index, filtered view,
/// cached pages and gallery surface. No catalog re-query or reflow is needed
/// unless the replacement's actual aspect ratio changed.
/// </summary>
public static class ThumbnailRefreshProjection
{
    public static BrowseThumbnailIndexEntry ReplaceLocation(
        BrowseThumbnailIndexEntry existing, BrowseThumbnailIndexEntry rebuilt)
    {
        if (existing.ImageId != rebuilt.ImageId || existing.Tier != rebuilt.Tier)
            throw new ArgumentException("Thumbnail replacement must keep image ID and tier.");
        return existing with
        {
            PackFile = rebuilt.PackFile,
            ByteOffset = rebuilt.ByteOffset,
            ByteLength = rebuilt.ByteLength,
            PixelWidth = rebuilt.PixelWidth,
            PixelHeight = rebuilt.PixelHeight,
            LegacyThumbnailPath = rebuilt.LegacyThumbnailPath,
            DeferredLocationSnapshot = rebuilt.DeferredLocationSnapshot
        };
    }

    public static IReadOnlyList<BrowseThumbnailIndexEntry> Replace(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyDictionary<long, BrowseThumbnailIndexEntry> replacements)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(replacements);
        BrowseThumbnailIndexEntry[]? changed = null;
        for (var i = 0; i < index.Count; i++)
        {
            var previous = index[i];
            if (!replacements.TryGetValue(previous.ImageId, out var rebuilt)) continue;
            changed ??= index.ToArray();
            changed[i] = ReplaceLocation(previous, rebuilt);
        }
        return changed ?? index;
    }

    public static bool ChangesLayout(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyDictionary<long, BrowseThumbnailIndexEntry> replacements)
    {
        foreach (var entry in index)
        {
            if (!replacements.TryGetValue(entry.ImageId, out var next)) continue;
            if (entry.PixelWidth <= 0 || entry.PixelHeight <= 0 ||
                next.PixelWidth <= 0 || next.PixelHeight <= 0)
            {
                if (entry.PixelWidth != next.PixelWidth ||
                    entry.PixelHeight != next.PixelHeight) return true;
            }
            else if ((long)entry.PixelWidth * next.PixelHeight !=
                     (long)next.PixelWidth * entry.PixelHeight)
                return true;
        }
        return false;
    }
}
