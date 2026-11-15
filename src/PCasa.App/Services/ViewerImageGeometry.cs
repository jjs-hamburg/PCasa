namespace PCasa.App.Services;

/// <summary>
/// Compare only orientation geometry, never screen size or decoded resolution.
/// The source may be a small preview while the catalog stores full-size pixels.
/// For EXIF 90-degree rotations the swapped source aspect must match better
/// than the original aspect. Square images do not have a meaningful swap.
/// </summary>
public static class ViewerImageGeometry
{
    public static (double Width, double Height) LogicalEditPixels(
        int catalogWidth, int catalogHeight,
        int originalPreviewWidth, int originalPreviewHeight,
        int currentPreviewWidth, int currentPreviewHeight)
    {
        if (catalogWidth < 1 || catalogHeight < 1 ||
            originalPreviewWidth < 1 || originalPreviewHeight < 1 ||
            currentPreviewWidth < 1 || currentPreviewHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(catalogWidth));
        // Editing works on a screen-sized decode. A tonal step may replace
        // that bitmap but must not change physical 100% zoom and pan. Crop
        // and rotation change logical pixel size in proportion to the
        // original full-resolution geometry.
        return (currentPreviewWidth *
                    (catalogWidth / (double)originalPreviewWidth),
                currentPreviewHeight *
                    (catalogHeight / (double)originalPreviewHeight));
    }

    public static bool NeedsAspectCorrection(int catalogWidth, int catalogHeight,
        int decodedWidth, int decodedHeight)
    {
        if (catalogWidth < 1 || catalogHeight < 1 ||
            decodedWidth < 1 || decodedHeight < 1) return false;
        var expected = catalogWidth / (double)catalogHeight;
        var actual = decodedWidth / (double)decodedHeight;
        return Math.Abs(Math.Log(actual / expected)) > .035;
    }

    // An edit may have completed on disk while the asynchronous catalog still
    // holds the PRE-CROP dimensions. Do not force the new square bitmap into
    // that old rectangle. Keep the old longest dimension as a temporary zoom
    // estimate; the next catalog refresh supplies exact new pixel dimensions.
    public static (double Width, double Height) CorrectStaleAspect(
        int catalogWidth, int catalogHeight, int decodedWidth, int decodedHeight)
    {
        if (!NeedsAspectCorrection(catalogWidth, catalogHeight,
                decodedWidth, decodedHeight))
            return (catalogWidth, catalogHeight);
        var factor = Math.Max(catalogWidth, catalogHeight) /
            (double)Math.Max(decodedWidth, decodedHeight);
        return (decodedWidth * factor, decodedHeight * factor);
    }

    public static bool NeedsAxisSwap(int sourceWidth, int sourceHeight,
        int decodedWidth, int decodedHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            decodedWidth <= 0 || decodedHeight <= 0 ||
            sourceWidth == sourceHeight) return false;
        var decodedRatio = decodedWidth / (double)decodedHeight;
        var normalRatio = sourceWidth / (double)sourceHeight;
        var swappedRatio = sourceHeight / (double)sourceWidth;
        var normalError = Math.Abs(Math.Log(decodedRatio / normalRatio));
        var rotatedError = Math.Abs(Math.Log(decodedRatio / swappedRatio));
        return rotatedError < 0.1 && rotatedError + 0.15 < normalError;
    }
}
