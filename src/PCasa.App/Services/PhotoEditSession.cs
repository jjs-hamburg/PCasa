using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// One in-memory edit transaction per JPEG. Crop and rotation share a single
/// cursor; no original is modified until the user's explicit Save command.
/// The same operations are replayed at full resolution by the P0 writer.
/// </summary>
public sealed record PhotoEditStep(
    string Kind, double X = 0, double Y = 0,
    double Width = 1, double Height = 1, double Degrees = 0);

public sealed class PhotoEditSession
{
    private readonly BitmapSource _original;
    private readonly List<PhotoEditStep> _steps = [];
    private int _cursor;
    public long ImageId { get; }
    public BitmapSource Original => _original;

    public PhotoEditSession(long imageId, BitmapSource original)
    {
        ImageId = imageId;
        _original = original ?? throw new ArgumentNullException(nameof(original));
    }
    public bool HasChanges => _cursor > 0;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _steps.Count;
    public IReadOnlyList<PhotoEditStep> AppliedSteps =>
        _steps.Take(_cursor).ToArray();

    public void Apply(PhotoEditStep step)
    {
        if (_cursor < _steps.Count)
            _steps.RemoveRange(_cursor, _steps.Count - _cursor);
        _steps.Add(step);
        _cursor++;
    }
    public void Undo() { if (CanUndo) _cursor--; }
    public void Redo() { if (CanRedo) _cursor++; }
    public BitmapSource Render() => Render(_original, AppliedSteps);

    public static BitmapSource Render(BitmapSource original,
        IEnumerable<PhotoEditStep> steps)
    {
        BitmapSource bitmap = original;
        foreach (var step in steps)
        {
            if (step.Kind == "crop")
            {
                if (!double.IsFinite(step.X) || !double.IsFinite(step.Y) ||
                    !double.IsFinite(step.Width) || !double.IsFinite(step.Height) ||
                    step.Width < .01 || step.Height < .01 ||
                    step.X < 0 || step.Y < 0 ||
                    step.X + step.Width > 1.000001 ||
                    step.Y + step.Height > 1.000001)
                    throw new InvalidDataException("Ungültiger Crop-Bereich.");
                var x = Math.Clamp((int)Math.Floor(step.X * bitmap.PixelWidth),
                    0, bitmap.PixelWidth - 1);
                var y = Math.Clamp((int)Math.Floor(step.Y * bitmap.PixelHeight),
                    0, bitmap.PixelHeight - 1);
                var width = Math.Clamp((int)Math.Round(step.Width * bitmap.PixelWidth),
                    1, bitmap.PixelWidth - x);
                var height = Math.Clamp((int)Math.Round(step.Height * bitmap.PixelHeight),
                    1, bitmap.PixelHeight - y);
                bitmap = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
            }
            else if (step.Kind == "rotate")
            {
                if (!double.IsFinite(step.Degrees) || Math.Abs(step.Degrees) > 360)
                    throw new InvalidDataException("Ungültiger Rotationswinkel.");
                var quarters = (int)Math.Round(step.Degrees / 90);
                var fine = step.Degrees - 90 * quarters;
                if (quarters != 0)
                {
                    bitmap = new TransformedBitmap(bitmap,
                        new RotateTransform(90 * quarters));
                    bitmap.Freeze();
                }
                if (Math.Abs(fine) > .00001)
                {
                    var width = bitmap.PixelWidth;
                    var height = bitmap.PixelHeight;
                    // WPF TransformedBitmap only accepts scales, flips and
                    // right-angle rotations. Arbitrary straighten angles are
                    // rendered into the inscribed, border-free rectangle.
                    // Run this also on scheduler MTA workers: no DrawingVisual
                    // or dispatcher-bound RenderTargetBitmap is required.
                    bitmap = RotateAndAutoCrop(bitmap, fine);
                }
            }
            else throw new InvalidDataException(
                "Unbekannte Bildbearbeitung: " + step.Kind);
            if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16)
                throw new InvalidDataException("Das bearbeitete Bild ist zu klein.");
            bitmap.Freeze();
        }
        return bitmap;
    }

    /// <summary>
    /// Bilinear image-space rotation, directly into the largest centered
    /// inscribed rectangle with the original aspect ratio. Unlike
    /// TransformedBitmap this supports arbitrary angles on both the preview
    /// thread and the durable scheduler MTA worker. The crop avoids
    /// transparent/black triangles and avoids allocating an expanded canvas.
    /// </summary>
    private static BitmapSource RotateAndAutoCrop(BitmapSource source, double degrees)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var rad = degrees * Math.PI / 180.0;
        var sin = Math.Sin(rad);
        var cos = Math.Cos(rad);
        var absSin = Math.Abs(sin);
        var absCos = Math.Abs(cos);
        var ratio = width / (double)height;
        var safeHeight = Math.Min(
            width / (ratio * absCos + absSin),
            height / (ratio * absSin + absCos));
        // Two-pixel safety inset also covers edge rounding in interpolation.
        var targetWidth = Math.Clamp(
            (int)Math.Floor(ratio * safeHeight) - 2, 1, width);
        var targetHeight = Math.Clamp(
            (int)Math.Floor(safeHeight) - 2, 1, height);
        var input = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var inputStride = checked(width * 4);
        var sourcePixels = new byte[checked(inputStride * height)];
        input.CopyPixels(sourcePixels, inputStride, 0);
        var stride = checked(targetWidth * 4);
        var result = new byte[checked(stride * targetHeight)];
        var inputCenterX = (width - 1) / 2.0;
        var inputCenterY = (height - 1) / 2.0;
        var outputCenterX = (targetWidth - 1) / 2.0;
        var outputCenterY = (targetHeight - 1) / 2.0;
        for (var y = 0; y < targetHeight; y++)
        {
            var dy = y - outputCenterY;
            var sx = inputCenterX - cos * outputCenterX + sin * dy;
            var sy = inputCenterY + sin * outputCenterX + cos * dy;
            for (var x = 0; x < targetWidth; x++)
            {
                // The inset keeps the full destination inside the source;
                // clamp at fractional pixel edges for robust antialiasing.
                var px = Math.Clamp(sx, 0, width - 1.0);
                var py = Math.Clamp(sy, 0, height - 1.0);
                var x0 = (int)px;
                var y0 = (int)py;
                var x1 = Math.Min(width - 1, x0 + 1);
                var y1 = Math.Min(height - 1, y0 + 1);
                var fx = px - x0;
                var fy = py - y0;
                var a = y0 * inputStride + x0 * 4;
                var b = y0 * inputStride + x1 * 4;
                var c = y1 * inputStride + x0 * 4;
                var d = y1 * inputStride + x1 * 4;
                var output = y * stride + x * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var upper = sourcePixels[a + channel] * (1 - fx) +
                                sourcePixels[b + channel] * fx;
                    var lower = sourcePixels[c + channel] * (1 - fx) +
                                sourcePixels[d + channel] * fx;
                    result[output + channel] = (byte)Math.Clamp(
                        (int)Math.Round(upper * (1 - fy) + lower * fy),
                        0, 255);
                }
                sx += cos;
                sy -= sin;
            }
        }
        var rotated = BitmapSource.Create(targetWidth, targetHeight,
            source.DpiX, source.DpiY, PixelFormats.Bgra32, null, result, stride);
        rotated.Freeze();
        return rotated;
    }
}
