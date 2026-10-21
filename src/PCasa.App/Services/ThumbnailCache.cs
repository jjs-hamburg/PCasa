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
        long modifiedUtcTicks)
    {
        var cachePath = GetCachePath(canonicalPath, fileSize, modifiedUtcTicks);
        if (File.Exists(cachePath))
        {
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            return new ThumbnailResult(cachePath, header.Width, header.Height);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temporaryPath = cachePath + ".tmp.jpg";

        try
        {
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            var width = header.Width;
            var height = header.Height;

            using var thumbnail = Image.Thumbnail(
                sourcePath,
                ThumbnailSize,
                height: ThumbnailSize,
                size: Enums.Size.Down);

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

    private string GetCachePath(string canonicalPath, long fileSize, long modifiedUtcTicks)
    {
        var identity = $"{canonicalPath}\n{fileSize}\n{modifiedUtcTicks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var shard = hash[..2];
        return Path.Combine(_cacheRoot, shard, hash + ".jpg");
    }
}
