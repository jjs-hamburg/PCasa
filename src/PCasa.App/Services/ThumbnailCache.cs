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
        _cacheRoot = Path.Combine(PcasaDataPaths.SharedDataRoot, "thumbnails");

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
        NativeImageCachePolicy.EnsureConfigured();
        originalSourcePath ??= sourcePath;
        var rawSource = RawImageSource.IsRawPath(originalSourcePath);
        var psdSource = RawImageSource.IsPsdPath(originalSourcePath);
        var cachePath = GetCachePath(canonicalPath, fileSize, modifiedUtcTicks,
            rawSource, psdSource);
        if (File.Exists(cachePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var header = Image.NewFromFile(sourcePath, access: Enums.Access.Sequential);
            cancellationToken.ThrowIfCancellationRequested();
            var rotated = rawSource && RawImageSource.GetRawOrientation(originalSourcePath)
                is 5 or 6 or 7 or 8;
            if (psdSource)
            {
                var (sourceWidth, sourceHeight) =
                    PsdCompositePreview.GetDimensions(originalSourcePath);
                return new ThumbnailResult(cachePath, sourceWidth, sourceHeight);
            }
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
            if (psdSource)
                (width, height) = PsdCompositePreview.GetDimensions(originalSourcePath);

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

    // A per-file local thumbnail, deliberately distinct from any successful
    // decode cache identity. A high-contrast cross is visible without color.
    public ThumbnailResult CreateImportErrorPlaceholder(string canonicalPath,
        long fileSize, long modifiedUtcTicks, CancellationToken token)
    {
        const int size = 256;
        var path = GetCachePath(canonicalPath + "\nimport-error-v1",
            fileSize, modifiedUtcTicks, false, false);
        if (File.Exists(path)) return new ThumbnailResult(path, size, size);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".jpg";
        try
        {
            // Use the same WPF JPEG encoder as video placeholders, without
            // depending on optional libvips PPM loader support.
            var pixels = new byte[size * size * 3];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var offset = (y * size + x) * 3;
                var cross = x > 48 && x < 208 && y > 48 && y < 208 &&
                    (Math.Abs(x - y) < 12 || Math.Abs(x + y - size) < 12);
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] =
                    cross ? (byte)240 : (byte)45;
            }
            token.ThrowIfCancellationRequested();
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                size, size, 96, 96, System.Windows.Media.PixelFormats.Rgb24,
                null, pixels, size * 3);
            bitmap.Freeze();
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder
                { QualityLevel = 85 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(temporary)) encoder.Save(output);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return new ThumbnailResult(path, size, size);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
        cacheRoot ??= Path.Combine(PcasaDataPaths.SharedDataRoot, "thumbnails");
        var candidate = Path.Combine(cacheRoot, stem[..2].ToLowerInvariant(), filename);
        return File.Exists(candidate) ? candidate : null;
    }

    private string GetCachePath(string canonicalPath, long fileSize,
        long modifiedUtcTicks, bool raw, bool psd)
    {
        var identity = $"{canonicalPath}\n{fileSize}\n{modifiedUtcTicks}" +
            (raw ? "\nraw-orientation-v2" :
             psd ? "\npsd-merged-composite-v1" : "");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var shard = hash[..2];
        return Path.Combine(_cacheRoot, shard,
            (raw ? "raw2-" : "") + hash + ".jpg");
    }
}
