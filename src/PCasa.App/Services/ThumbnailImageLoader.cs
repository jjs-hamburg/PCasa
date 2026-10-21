using System.IO;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

public sealed class ThumbnailImageLoader
{
    public BitmapSource Load(
        string thumbnailPath,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(
            thumbnailPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, decodePixelWidth);
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
