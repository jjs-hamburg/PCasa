using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using NetVips;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed record PhotoViewerImageInfo(
    long Id, string LocalPath, string FileName, int Width, int Height);

public sealed class PhotoViewerImageRepository
{
    private readonly LocalDatabase _database;

    public PhotoViewerImageRepository(LocalDatabase database) => _database = database;

    public PhotoViewerImageInfo? Get(long imageId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT local_path, file_name, width, height FROM images WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", imageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PhotoViewerImageInfo(imageId,
            reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetInt32(3));
    }
}

public static class PhotoViewerImageLoader
{
    public static BitmapSource Load(
        PhotoViewerImageInfo info,
        int requiredSize,
        CancellationToken cancellationToken)
    {
        lock (SourceFileLocks.ForPath(info.LocalPath))
            return LoadLocked(info, requiredSize, cancellationToken);
    }

    private static BitmapSource LoadLocked(
        PhotoViewerImageInfo info,
        int requiredSize,
        CancellationToken cancellationToken)
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
                using var input = new FileStream(
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
        using var thumbnail = NetVips.Image.Thumbnail(
            displayPath,
            size,
            height: size,
            size: Enums.Size.Down);
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = thumbnail.HasAlpha()
            ? thumbnail.Flatten(background: new[] { 255.0, 255.0, 255.0 })
            : thumbnail.Copy();
        var pixels = prepared.PngsaveBuffer(
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
