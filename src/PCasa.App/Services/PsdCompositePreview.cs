using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

// WPF adapter only: decoding and the cache lifecycle belong to PCasa.Imaging.
public static class PsdCompositePreview
{
    public static string Resolve(string psd, CancellationToken token = default)
        => PsdCompositeCache.Resolve(psd, WritePng, token);

    public static (int Width, int Height) GetDimensions(string psd)
        => PsdCompositeDecoder.GetDimensions(psd);

    public static BitmapSource DecodeComposite(Stream input,
        CancellationToken token = default)
        => ToBitmap(PsdCompositeDecoder.DecodeComposite(input, token));

    private static BitmapSource ToBitmap(PsdCompositePixels pixels)
    {
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels.Bgra, checked(pixels.Width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static void WritePng(string temporary, PsdCompositePixels pixels,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(pixels)));
        using var output = new FileStream(temporary, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        encoder.Save(output);
        output.Flush(true);
    }
}
