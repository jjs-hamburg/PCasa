using PCasa.App.Models;

namespace PCasa.App.Services;

// One global setting, applied INSIDE each photo folder. The tree alone
// decides folder order. Sorting never moves images across folder sections,
// probes source files or changes SQLite ownership/thumbnail identities.
public enum GallerySortField
{
    Name,
    Captured,
    Created,
    Modified,
    Size,
    Dimensions
}

public readonly record struct GallerySortOptions(
    GallerySortField Field, bool Descending)
{
    public static GallerySortOptions Default => new(GallerySortField.Name, false);
}

public static class GalleryFolderOrdering
{
    public static IReadOnlyList<BrowseThumbnailIndexEntry> Order(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyDictionary<long, int> treePositions)
        => Order(index, treePositions, GallerySortOptions.Default);

    public static IReadOnlyList<BrowseThumbnailIndexEntry> Order(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyDictionary<long, int> treePositions,
        GallerySortOptions options)
    {
        if (index.Count < 2) return index;

        var comparer = CreateComparer(treePositions, options);
        var alreadyOrdered = true;
        for (var i = 1; i < index.Count; i++)
            if (comparer.Compare(index[i - 1], index[i]) > 0)
            {
                alreadyOrdered = false;
                break;
            }
        if (alreadyOrdered) return index; // No invalidation of decoded pages.

        // One complete ordering at an index change, never per rendered row.
        // Stable image ID breaks ties so every refresh and incremental
        // relocation agrees on the same order.
        var copy = index.ToArray();
        Array.Sort(copy, comparer);
        return copy;
    }

    public static IComparer<BrowseThumbnailIndexEntry> CreateComparer(
        IReadOnlyDictionary<long, int> treePositions,
        GallerySortOptions options)
        => Comparer<BrowseThumbnailIndexEntry>.Create((left, right) =>
            Compare(left, right, treePositions, options));

    public static int Compare(
        BrowseThumbnailIndexEntry left,
        BrowseThumbnailIndexEntry right,
        IReadOnlyDictionary<long, int> treePositions,
        GallerySortOptions options)
    {
        // Direction reverses images within folders, NEVER folder order.
        var folder = Position(left.PhotoFolderId, treePositions)
            .CompareTo(Position(right.PhotoFolderId, treePositions));
        if (folder != 0) return folder;
        // A newly discovered folder may not have received a tree position
        // yet. Never merge two such folders into one mixed image section.
        if (left.PhotoFolderId != right.PhotoFolderId)
            return left.PhotoFolderId.CompareTo(right.PhotoFolderId);

        int order = options.Field switch
        {
            GallerySortField.Captured => CaptureTime(left).CompareTo(CaptureTime(right)),
            GallerySortField.Created => CreatedTime(left).CompareTo(CreatedTime(right)),
            GallerySortField.Modified => left.ModifiedUtcTicks.CompareTo(right.ModifiedUtcTicks),
            GallerySortField.Size => left.FileSize.CompareTo(right.FileSize),
            GallerySortField.Dimensions => PixelCount(left).CompareTo(PixelCount(right)),
            _ => 0
        };
        if (order == 0)
            order = StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
        if (order == 0)
            order = StringComparer.Ordinal.Compare(left.FileName, right.FileName);
        if (order == 0)
            order = left.ImageId.CompareTo(right.ImageId);
        return options.Descending ? -Math.Sign(order) : Math.Sign(order);
    }

    public static bool MatchesCaptureDate(BrowseThumbnailIndexEntry item,
        DateTime? from, DateTime? until)
        => MatchesDate(item, GallerySortField.Captured, from, until);

    public static bool MatchesDate(BrowseThumbnailIndexEntry item,
        GallerySortField field, DateTime? from, DateTime? until)
    {
        if (from is null && until is null) return true;
        if (field == GallerySortField.Captured && item.HasInvalidCaptureDate)
            return false;
        // Camera dates are wall-clock values. File dates are UTC instants
        // and must match the user's local calendar day, not the UTC day.
        var nativeCapture = field == GallerySortField.Captured && item.TakenUtcTicks.HasValue;
        long? ticks = field switch
        {
            GallerySortField.Captured => item.TakenUtcTicks ?? item.CreatedUtcTicks,
            GallerySortField.Created => item.CreatedUtcTicks,
            GallerySortField.Modified => item.ModifiedUtcTicks,
            _ => null
        };
        if (ticks is not long stamp || stamp <= 0 || stamp > DateTime.MaxValue.Ticks)
            return false;
        var day = nativeCapture ? new DateTime(stamp).Date
            : new DateTime(stamp, DateTimeKind.Utc).ToLocalTime().Date;
        return (!from.HasValue || day >= from.Value.Date) &&
            (!until.HasValue || day <= until.Value.Date);
    }

    public static IReadOnlyList<BrowseThumbnailIndexEntry> FilterDateRange(
        IReadOnlyList<BrowseThumbnailIndexEntry> index, GallerySortField field,
        DateTime from, DateTime? until, bool inverted)
        => index.Where(image => MatchesDate(image, field, from, until) != inverted).ToArray();

    private static readonly string[] CaptureFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.FFFFFFF",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];
    private static readonly string[] CaptureOffsetFormats = CaptureFormats
        .SelectMany(format => new[] { format + "zzz", format + " zzz", format + "'Z'" }).ToArray();

    public static long? ParseCaptureDateTicks(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var value = source.Trim();
        if (DateTime.TryParseExact(value, CaptureFormats, culture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var local))
            return local.Ticks;
        return DateTimeOffset.TryParseExact(value, CaptureOffsetFormats, culture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var offset)
            ? offset.DateTime.Ticks : null;
    }

    private static long CaptureTime(BrowseThumbnailIndexEntry item)
        // EXIF missing: source creation; if unavailable: modification.
        => item.HasInvalidCaptureDate ? 0 : item.TakenUtcTicks ?? item.CreatedUtcTicks ?? 0;

    private static long CreatedTime(BrowseThumbnailIndexEntry item)
        => item.CreatedUtcTicks is > 0 ? item.CreatedUtcTicks.Value : 0;

    private static long PixelCount(BrowseThumbnailIndexEntry item)
        => (long)item.OriginalWidth * item.OriginalHeight;

    private static int Position(long id, IReadOnlyDictionary<long, int> positions)
        => positions.TryGetValue(id, out var value) ? value : int.MaxValue;
}
