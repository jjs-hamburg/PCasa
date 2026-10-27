using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

// Sole owner of external gallery textures, including file discovery,
// decode, native-pixel tiling and caption contrast.
public static class GalleryTextureLibrary
{
    public static string DirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "Texturen");

    public static IReadOnlyList<string> GetFiles()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        return Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsJpeg(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static string? FindFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            !IsJpeg(Path.GetExtension(fileName))) return null;
        return GetFiles().FirstOrDefault(file =>
            string.Equals(Path.GetFileName(file), fileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsJpeg(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
           extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    public static BitmapSource LoadBitmap(string path)
    {
        // Explicitly load and close the stream. A texture file must remain
        // replaceable while PCasa is running; no stale URI bitmap cache.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static ImageBrush CreateBrush(BitmapSource bitmap, DpiScale dpi)
    {
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.FlipXY,
            ViewportUnits = BrushMappingMode.Absolute,
            // WPF viewports are device-independent pixels, not image pixels.
            // This compensates for the actual monitor's DPI.
            Viewport = new Rect(0, 0,
                bitmap.PixelWidth / dpi.DpiScaleX,
                bitmap.PixelHeight / dpi.DpiScaleY),
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    public static bool IsLight(BitmapSource bitmap)
    {
        // The filename is arbitrary. Decide caption contrast from pixels,
        // sampling only a tiny central crop even for very large JPGs.
        var width = Math.Min(16, bitmap.PixelWidth);
        var height = Math.Min(16, bitmap.PixelHeight);
        var x = (bitmap.PixelWidth - width) / 2;
        var y = (bitmap.PixelHeight - height) / 2;
        var crop = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
        var converted = new FormatConvertedBitmap(
            crop, PixelFormats.Bgra32, null, 0);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        long luminance = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            luminance += (54 * pixels[i + 2]) +
                         (183 * pixels[i + 1]) +
                         (19 * pixels[i]);
        return luminance / (width * height * 256) >= 140;
    }
}
