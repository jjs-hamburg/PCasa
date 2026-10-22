namespace PCasa.App.Services;

/// <summary>
/// Compare only orientation geometry, never screen size or decoded resolution.
/// The source may be a small preview while the catalog stores full-size pixels.
/// For EXIF 90-degree rotations the swapped source aspect must match better
/// than the original aspect. Square images do not have a meaningful swap.
/// </summary>
public static class ViewerImageGeometry
{
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
