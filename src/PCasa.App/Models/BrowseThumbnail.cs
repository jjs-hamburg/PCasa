namespace PCasa.App.Models;

public sealed record BrowseThumbnailProfile(IReadOnlyList<int> Tiers)
{
    public static BrowseThumbnailProfile Default { get; } =
        new(new[] { 96, 192, 384 });

    public int ChooseForDisplayWidth(double displayWidth)
    {
        if (Tiers.Count == 0)
        {
            throw new InvalidOperationException("Der Browse-Thumbnail-Cache enthält keine Größenstufen.");
        }

        var requested = Math.Max(1d, displayWidth);
        foreach (var tier in Tiers)
        {
            if (tier >= requested)
            {
                return tier;
            }
        }

        return Tiers[^1];
    }

    public static BrowseThumbnailProfile Normalize(IEnumerable<int> tiers)
    {
        var normalized = tiers
            .Where(tier => tier >= 48 && tier <= 2048)
            .Distinct()
            .OrderBy(tier => tier)
            .ToArray();

        if (normalized.Length == 0)
        {
            return Default;
        }

        return new BrowseThumbnailProfile(normalized);
    }
}

public sealed record PackedThumbnailLocation(
    int Tier,
    string PackFile,
    long ByteOffset,
    int ByteLength,
    int PixelWidth,
    int PixelHeight);

public sealed record BrowseThumbnailIndexEntry(
    long ImageId,
    long PhotoFolderId,
    int Tier,
    string PackFile,
    long ByteOffset,
    int ByteLength,
    int PixelWidth,
    int PixelHeight,
    string FileName = "",
    bool HasGps = false,
    bool HasTags = false,
    bool IsRaw = false,
    long FileSize = 0,
    long ModifiedUtcTicks = 0,
    long? CreatedUtcTicks = null,
    long? TakenUtcTicks = null,
    int OriginalWidth = 0,
    int OriginalHeight = 0);

public sealed record BrowseCacheCandidate(
    long ImageId,
    long PhotoFolderId,
    string LocalPath,
    string ThumbnailPath,
    long ModifiedUtcTicks,
    int Width,
    int Height,
    IReadOnlySet<int> ExistingTiers);
