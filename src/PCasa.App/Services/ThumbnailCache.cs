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
        string? originalSourcePath = null)
    {
        originalSourcePath ??= sourcePath;
        var rawSource = RawImageSource.IsRawPath(originalSourcePath);
        var cachePath = GetCachePath(canonicalPath, fileSize, modifiedUtcTicks,
            rawSource);
        if (File.Exists(cachePath))
        {
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
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
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            var rotated = rawSource && RawImageSource.GetRawOrientation(originalSourcePath)
                is 5 or 6 or 7 or 8;
            var width = rotated ? header.Height : header.Width;
            var height = rotated ? header.Width : header.Height;

            using var thumbnail = RawImageSource.LoadDisplayThumbnail(
                sourcePath, originalSourcePath, ThumbnailSize, ThumbnailSize);

            if (thumbnail.HasAlpha())
            {
                using var flattened = thumbnail.Flatten(background: new[] { 255.0, 255.0, 255.0 });
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
