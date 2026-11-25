using NetVips;

namespace PCasa.App.Services;

// Windows composition root for the existing viewer and import callers.
// The shared decoder has no WPF dependency.
public static class RawImageSource
{
    public static bool IsRawPath(string path) => VipsImageSource.IsRawPath(path);
    public static bool IsPsdPath(string path) => VipsImageSource.IsPsdPath(path);

    public static string Resolve(string path, CancellationToken cancellationToken = default)
        => IsPsdPath(path) ? PsdCompositePreview.Resolve(path, cancellationToken)
            : VipsImageSource.Resolve(path, cancellationToken);

    public static Image LoadDisplayThumbnail(string renderedPath, string originalPath,
        int width, int height, CancellationToken token = default)
        => VipsImageSource.LoadDisplayThumbnail(renderedPath, originalPath, width, height, token);

    public static int GetRawOrientation(string path, CancellationToken token = default)
        => VipsImageSource.GetRawOrientation(path, token);

    public static Image ApplyOrientation(Image pixels, int orientation)
        => VipsImageSource.ApplyOrientation(pixels, orientation);
}
