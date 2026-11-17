using System.IO;
using System.Windows.Media.Imaging;
using NetVips;

namespace PCasa.App.Services;

/// <summary>
/// One editability and multi-page policy for cataloged still images.
/// RAW and videos are read-only; animated/multipage sources are not flattened.
/// Encoding availability for modern formats depends on the bundled libvips.
/// </summary>
public static class EditableImageFormats
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp", ".gif",
            ".webp", ".heic", ".heif", ".avif", ".jxl"
        };

    public static bool Supports(string path)
        => !RawImageSource.IsPsdPath(path) &&
           Extensions.Contains(Path.GetExtension(path)) &&
           !RawImageSource.IsRawPath(path) &&
           !VideoFormats.IsVideo(path);

    public static bool SupportsExternalResult(string path) =>
        Supports(path) || RawImageSource.IsPsdPath(path);

    public static void ValidateStillImage(string path)
    {
        if (RawImageSource.IsPsdPath(path))
        {
            // Verify the PSD header, never flatten or re-encode the layers.
            // Thumbnail extraction is independent and may have to fall back.
            using var psd = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> signature = stackalloc byte[4];
            if (psd.Read(signature) != signature.Length ||
                signature[0] != (byte)'8' || signature[1] != (byte)'B' ||
                signature[2] != (byte)'P' || signature[3] != (byte)'S')
                throw new InvalidDataException("Ungültiger PSD-Dateikopf.");
            return;
        }
        if (!Supports(path))
            throw new NotSupportedException(
                UiLanguage.T("Dieses Format kann nicht bearbeitet werden. RAW und Videos bleiben unverändert."));

        var ext = Path.GetExtension(path);
        // WIC reports every GIF/TIFF frame instead of silently editing just
        // the first frame of an animation or multipage document.
        if (ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1)
                throw new NotSupportedException(
                    UiLanguage.T("Animierte oder mehrseitige Bilddateien können nicht bearbeitet werden."));
        }

        // JPG/PNG/BMP have no multipage variant that requires libvips here,
        // and GIF/TIFF have ALREADY been checked with WIC above. Opening every
        // source through libvips before an in-place File.Replace can leave
        // native loader/cache references to the source while it is replaced.
        // Only the modern formats need libvips' n-pages metadata probe.
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return;

        NativeImageCachePolicy.EnsureConfigured();
        using var header = Image.NewFromFile(path, access: Enums.Access.Sequential);
        if (header.Contains("n-pages") &&
            Convert.ToInt32(header.Get("n-pages")) > 1)
            throw new NotSupportedException(
                UiLanguage.T("Animierte oder mehrseitige Bilddateien können nicht bearbeitet werden."));
    }

    public static BitmapSource DecodeForEdit(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = input;
            image.EndInit();
            image.Freeze();
            return ExifOrientation.Apply(image, input);
        }

        // Modern still formats use the *same* pinned libvips already bundled
        // for image viewing. Transfer via lossless PNG with alpha intact.
        NativeImageCachePolicy.EnsureConfigured();
        using var source = Image.NewFromFile(path, access: Enums.Access.Sequential);
        using var oriented = source.Autorot();
        var png = oriented.PngsaveBuffer(compression: 1,
            keep: Enums.ForeignKeep.Icc);
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static void Encode(BitmapSource edited, string destination)
    {
        var ext = Path.GetExtension(destination).ToLowerInvariant();
        BitmapEncoder? encoder = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".png" => new PngBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => null
        };
        if (encoder is not null)
        {
            encoder.Frames.Add(BitmapFrame.Create(edited));
            using var output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            encoder.Save(output);
            output.Flush(flushToDisk: true);
            return;
        }

        // libvips selects the real format from the filename suffix.
        // Neither pixel data nor the extension are silently changed to JPEG.
        // A missing codec fails before the original file is replaced.
        using var encoded = new MemoryStream();
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(edited));
        png.Save(encoded);
        NativeImageCachePolicy.EnsureConfigured();
        using var source = Image.NewFromBuffer(encoded.ToArray());
        source.WriteToFile(destination);
    }

    public static (int Width, int Height) Dimensions(string path)
    {
        if (RawImageSource.IsPsdPath(path))
            return PsdCompositePreview.GetDimensions(path);
        var ext = Path.GetExtension(path);
        // Immediately before an atomic Replace/Move the staged file MUST
        // have no open libvips source loader in the operation cache. In
        // particular, probing a just-encoded JPEG with NewFromFile can hold
        // the staged *source* of File.Replace open. WIC's OnLoad frame is
        // completely detached from the FileStream before it is returned.
        if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            using var input = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(input,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        NativeImageCachePolicy.EnsureConfigured();
        using var source = Image.NewFromFile(path, access: Enums.Access.Sequential);
        return (source.Width, source.Height);
    }
}
