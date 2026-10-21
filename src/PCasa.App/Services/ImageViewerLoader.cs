using System.IO;
using System.Windows.Media.Imaging;
using NetVips;

namespace PCasa.App.Services;

public sealed class ImageViewerLoader
{
    public BitmapSource Load(
        string sourcePath,
        int maxPixelWidth,
        int maxPixelHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var width = Math.Max(1, maxPixelWidth);
        var height = Math.Max(1, maxPixelHeight);

        using var thumbnail = Image.Thumbnail(
            sourcePath,
            width,
            height: height,
            size: Enums.Size.Down);

        cancellationToken.ThrowIfCancellationRequested();

        byte[] encoded;
        if (thumbnail.HasAlpha())
        {
            using var flattened = thumbnail.Flatten(background: new[] { 0.0, 0.0, 0.0 });
            encoded = flattened.JpegsaveBuffer(
                q: 92,
                optimizeCoding: true,
                keep: Enums.ForeignKeep.None);
        }
        else
        {
            encoded = thumbnail.JpegsaveBuffer(
                q: 92,
                optimizeCoding: true,
                keep: Enums.ForeignKeep.None);
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new MemoryStream(encoded, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }
}
