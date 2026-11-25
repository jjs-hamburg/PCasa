using System.IO;

namespace PCasa.App.Services;

// Shared by inventory, watcher and actual image ingestion.
public static class CatalogMediaFormats
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif",
        ".webp", ".heic", ".heif", ".avif", ".jxl", ".psd",
        ".arw", ".nef", ".nrw", ".cr2", ".cr3", ".dng",
        ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw"
    };

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arw", ".nef", ".nrw", ".cr2", ".cr3", ".dng",
        ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw"
    };

    public static bool IsRawPath(string path) => RawExtensions.Contains(Path.GetExtension(path));

    static CatalogMediaFormats() => SupportedExtensions.UnionWith(VideoFormats.SupportedExtensions);

    public static bool IsSupported(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path));
}
