using System.IO;
using System.Security.Cryptography;
using System.Text;
using NetVips;

namespace PCasa.App.Services;

public sealed record ThumbnailResult(string Path, int Width, int Height);

public sealed class ThumbnailCache
{
    private const int ThumbnailSize = 512;
    private readonly string _cacheRoot;

    public ThumbnailCache()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa",
            "thumbnails");

        Directory.CreateDirectory(_cacheRoot);
    }

    public ThumbnailResult CreateOrUpdate(
        string sourcePath,
        string canonicalPath,
        long fileSize,
        long modifiedUtcTicks,
        string? originalSourcePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        originalSourcePath ??= sourcePath;
        var rawSource = RawImageSource.IsRawPath(originalSourcePath);
        var cachePath = GetCachePath(canonicalPath, fileSize, modifiedUtcTicks,
            rawSource);
        if (File.Exists(cachePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            cancellationToken.ThrowIfCancellationRequested();
            var rotated = rawSource && RawImageSource.GetRawOrientation(originalSourcePath)
                is 5 or 6 or 7 or 8;
            return new ThumbnailResult(cachePath,
                rotated ? header.Height : header.Width,
                rotated ? header.Width : header.Height);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = cachePath + ".tmp.jpg";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            cancellationToken.ThrowIfCancellationRequested();
            var rotated = rawSource && RawImageSource.GetRawOrientation(originalSourcePath)
                is 5 or 6 or 7 or 8;
            var width = rotated ? header.Height : header.Width;
            var height = rotated ? header.Width : header.Height;

            using var thumbnail = RawImageSource.LoadDisplayThumbnail(
                sourcePath, originalSourcePath, ThumbnailSize, ThumbnailSize,
                cancellationToken);
            // Scheduler cancellation reaches the native libvips evaluation,
            // not merely the boundary between two images.
            thumbnail.SetProgress(new Progress<int>(_ => { }), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (thumbnail.HasAlpha())
            {
                using var flattened = thumbnail.Flatten(background: new[] { 255.0, 255.0, 255.0 });
                flattened.SetProgress(new Progress<int>(_ => { }), cancellationToken);
                flattened.Jpegsave(
                    temporaryPath,
                    q: 82,
                    optimizeCoding: true,
                    keep: Enums.ForeignKeep.None);
            }
            else
            {
                thumbnail.Jpegsave(
                    temporaryPath,
                    q: 82,
                    optimizeCoding: true,
                    keep: Enums.ForeignKeep.None);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, cachePath, overwrite: true);
            return new ThumbnailResult(cachePath, width, height);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    // Read-only compatibility with catalogs restored by the historical
    // path-flattening bug. No SQLite mutation and no original-file access.
    // The existing thumbnail writer already uses the first two SHA-256
    // characters as a bucket; recover that same location from its basename.
    public static string? ResolveExistingLocalPath(
        string? recordedPath, string? cacheRoot = null)
    {
        if (string.IsNullOrWhiteSpace(recordedPath)) return null;
        if (File.Exists(recordedPath)) return recordedPath;

        var filename = Path.GetFileName(recordedPath);
        if (!filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            return null;
        var stem = Path.GetFileNameWithoutExtension(filename);
        if (stem.StartsWith("raw2-", StringComparison.OrdinalIgnoreCase))
            stem = stem[5..];
        if (stem.Length != 64 || !stem.All(char.IsAsciiHexDigit))
            return null;
        cacheRoot ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa", "thumbnails");
        var candidate = Path.Combine(cacheRoot, stem[..2].ToLowerInvariant(), filename);
        return File.Exists(candidate) ? candidate : null;
    }

    private string GetCachePath(string canonicalPath, long fileSize,
        long modifiedUtcTicks, bool raw)
    {
        var identity = $"{canonicalPath}\n{fileSize}\n{modifiedUtcTicks}" +
            (raw ? "\nraw-orientation-v2" : "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var shard = hash[..2];
        return Path.Combine(_cacheRoot, shard,
            (raw ? "raw2-" : "") + hash + ".jpg");
    }
}
