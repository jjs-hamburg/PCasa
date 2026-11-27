using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using NetVips;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed record PhotoViewerImageInfo(
    long Id, string LocalPath, string FileName, int Width, int Height,
    byte[]? ContentFingerprint = null);

public sealed class PhotoViewerImageRepository
{
    private readonly LocalDatabase _database;

    public PhotoViewerImageRepository(LocalDatabase database) => _database = database;

    public PhotoViewerImageInfo? Get(long imageId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT local_path, file_name, width, height, content_sha256 FROM images WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", imageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PhotoViewerImageInfo(imageId,
            reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetInt32(3),
            reader.IsDBNull(4) ? null : (byte[])reader.GetValue(4));
    }
}

public static class PhotoViewerImageLoader
{
    public static BitmapSource Load(
        PhotoViewerImageInfo info,
        int requiredSize,
        CancellationToken cancellationToken)
    {
        // A canceled viewer request must not remain blocked on the same
        // source stripe as a long-running network import or prefetch.
        using var lease = SourceFileLocks.Enter(info.LocalPath, cancellationToken);
        return LoadLocked(info, requiredSize, cancellationToken);
    }

    public static (BitmapSource Bitmap, byte[] Hash) LoadVerified(
        PhotoViewerImageInfo info, int requiredSize, CancellationToken token)
    {
        using var lease = SourceFileLocks.Enter(info.LocalPath, token);
        using var input = new FileStream(info.LocalPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        // Common compressed formats share one NAS read between SHA-256 and WIC.
        // Limit buffering; very large/other codecs use a streaming hash.
        var ext = Path.GetExtension(info.LocalPath).ToLowerInvariant();
        if (input.Length <= 128L * 1024 * 1024 &&
            ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff")
        {
            var bytes = new byte[(int)input.Length];
            // ValueTask may still be pending on a real disk or SMB read.
            // AsTask provides a blocking wait on this foreground worker.
            input.ReadExactlyAsync(bytes.AsMemory(), token).AsTask().GetAwaiter().GetResult();
            var hash = System.Security.Cryptography.SHA256.HashData(bytes);
            using var memory = new MemoryStream(bytes, writable: false);
            return (LoadLocked(info, requiredSize, token, memory), hash);
        }
        var fingerprint = System.Security.Cryptography.SHA256.HashDataAsync(input, token)
            .AsTask().GetAwaiter().GetResult();
        return (LoadLocked(info, requiredSize, token), fingerprint);
    }

    private static BitmapSource LoadLocked(
        PhotoViewerImageInfo info,
        int requiredSize,
        CancellationToken cancellationToken, Stream? verifiedInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var size = Math.Clamp(requiredSize, 1, 32768);

        // Direct WIC decoding avoids an entire round trip through a temporary
        // compressed image. It retains the embedded source color profile and
        // reads only the screen-sized representation.
        var ext = Path.GetExtension(info.LocalPath);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var input = verifiedInput ?? new FileStream(
                    info.LocalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    options: FileOptions.SequentialScan);

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                if (Math.Max(info.Width, info.Height) > size)
                {
                    if (info.Height > info.Width && info.Width > 0)
                    {
                        image.DecodePixelHeight = size;
                    }
                    else
                    {
                        image.DecodePixelWidth = size;
                    }
                }
                // At 100%, do not request a downsampled decode: preserve
                // the complete pixel dimensions of the source file.

                image.StreamSource = input;
                image.EndInit();
                image.Freeze();
                cancellationToken.ThrowIfCancellationRequested();
                // WPF's direct JPEG decoder exposes the stored pixel array;
                // libvips thumbnail() already applies EXIF Orientation. The
                // viewer must apply the same orientation, WITHOUT modifying
                // or re-encoding the source or its ICC profile.
                return ExifOrientation.Apply(image, input);
            }
            catch (NotSupportedException)
            {
                // Formats/codecs unsupported by WIC remain available through libvips.
            }
            catch (FileFormatException)
            {
                // WIC may reject uncommon profiles or file variants that libvips supports.
            }
        }

        // NetVips fallback for HEIC/AVIF/JXL/WebP and WIC-incompatible variants.
        // PNG is only an in-memory, LOSSLESS transfer to WPF, not a saved cache tier.
        var displayPath = RawImageSource.Resolve(info.LocalPath, cancellationToken);
        using var thumbnail = RawImageSource.LoadDisplayThumbnail(
            displayPath, info.LocalPath, size, size, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Preserve transparency in modern still formats. The editor must
        // preview the SAME alpha-bearing pixels its writer will transform;
        // flattening to white is a lossy display-only conversion.
        var pixels = thumbnail.PngsaveBuffer(
            compression: 1,
            keep: Enums.ForeignKeep.Icc);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream(pixels, writable: false);
        var fallback = new BitmapImage();
        fallback.BeginInit();
        fallback.CacheOption = BitmapCacheOption.OnLoad;
        fallback.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        fallback.StreamSource = stream;
        fallback.EndInit();
        fallback.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return fallback;
    }
}
