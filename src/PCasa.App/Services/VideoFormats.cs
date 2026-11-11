using System.IO;

namespace PCasa.App.Services;

/// <summary>One extension policy shared by scanning, thumbnails and playback.
/// An extension is a candidate container, not a promise that Windows has its codec.</summary>
public static class VideoFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv",
        ".mpg", ".mpeg", ".m2v", ".mts", ".m2ts", ".3gp",
        ".3g2", ".webm"
    };

    public static bool IsVideo(string path) => Extensions.Contains(Path.GetExtension(path));

    public static IReadOnlyCollection<string> SupportedExtensions => Extensions;
}
