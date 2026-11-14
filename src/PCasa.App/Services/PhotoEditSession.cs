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
    double Width = 1, double Height = 1, double Degrees = 0,
    double Amount = 0);

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
        ArgumentNullException.ThrowIfNull(original);
        // BitmapFrame.Freeze() alone is not sufficient for a cross-thread
        // CroppedBitmap/TransformedBitmap: WPF may still traverse its
        // STA-owned BitmapDecoder while freezing the derived bitmap.
        // Detach the decoded pixels while on the source's owning thread,
        // then share this immutable, decoder-independent source with MTA.
        var bgra = original.Format == PixelFormats.Bgra32
            ? original
            : new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        var stride = checked(bgra.PixelWidth * 4);
        var pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        _original = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight,
            bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        _original.Freeze();
    }
    public bool HasChanges => _cursor > 0;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _steps.Count;
    public PhotoEditStep? NextUndoStep => CanUndo ? _steps[_cursor - 1] : null;
    public PhotoEditStep? NextRedoStep => CanRedo ? _steps[_cursor] : null;
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
            else if (step.Kind is "auto-fix" or "auto-contrast" or
                     "auto-color" or "brightness")
            {
                if (!double.IsFinite(step.Amount) ||
                    Math.Abs(step.Amount) > 3)
                    throw new InvalidDataException(
                        "Ungültige Helligkeitskorrektur.");
                bitmap = RenderTone(bitmap, step.Kind, step.Amount);
            }
            else throw new InvalidDataException(
                "Unbekannte Bildbearbeitung: " + step.Kind);
            if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16)
                throw new InvalidDataException("Das bearbeitete Bild ist zu klein.");
            bitmap.Freeze();
        }
        return bitmap;
    }

    // All tonal tools use the SAME pixel path on the UI preview and on
    // the MTA durable JPEG writer. Every tool is exactly ONE undoable step;
    // no temporary JPEG, sidecar or independent write path is introduced.
    private static BitmapSource RenderTone(BitmapSource source,
        string kind, double amount)
    {
        var argb = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = argb.PixelWidth, height = argb.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        argb.CopyPixels(pixels, stride, 0);

        if (kind is "auto-color" or "auto-fix")
            AdjustWhiteBalance(pixels);
        if (kind is "auto-contrast" or "auto-fix")
            StretchLuminance(pixels);
        if (kind == "brightness" && Math.Abs(amount) > .0001)
        {
            // Signed exposure-like correction; 0 is EXACT identity.
            // Negative values darken, positive values brighten.
            var multiplier = Math.Pow(2, amount);
            var lut = new byte[256];
            for (var i = 0; i < 256; i++)
                lut[i] = (byte)Math.Clamp(
                    (int)Math.Round(i * multiplier), 0, 255);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] == 0) continue;
                pixels[i] = lut[pixels[i]];
                pixels[i + 1] = lut[pixels[i + 1]];
                pixels[i + 2] = lut[pixels[i + 2]];
            }
        }

        var result = BitmapSource.Create(width, height,
            source.DpiX, source.DpiY, PixelFormats.Bgra32,
            null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static void StretchLuminance(byte[] pixels)
    {
        // Ignore transparent pixels. One shared luminance histogram gives a
        // single black/white mapping and avoids arbitrary color shifts.
        var histogram = new long[256];
        long count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            var luminance = (54 * pixels[i + 2] +
                183 * pixels[i + 1] + 19 * pixels[i] + 128) >> 8;
            histogram[luminance]++;
            count++;
        }
        if (count == 0) return;
        long lowTarget = Math.Max(1, count / 100);
        long highTarget = Math.Max(1, count / 100);
        long total = 0;
        int low = 0, high = 255;
        for (; low < 255; low++)
        {
            total += histogram[low];
            if (total >= lowTarget) break;
        }
        total = 0;
        for (; high > 0; high--)
        {
            total += histogram[high];
            if (total >= highTarget) break;
        }
        if (high - low < 12) return; // Flat solid image: no amplification.
        var lut = new byte[256];
        var scale = 255.0 / (high - low);
        for (var value = 0; value < 256; value++)
            lut[value] = (byte)Math.Clamp(
                (int)Math.Round((value - low) * scale), 0, 255);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            pixels[i] = lut[pixels[i]];
            pixels[i + 1] = lut[pixels[i + 1]];
            pixels[i + 2] = lut[pixels[i + 2]];
        }
    }

    private static void AdjustWhiteBalance(byte[] pixels)
    {
        // Gray-world estimate calculated on usable mid-tones; highlights
        // and near-black regions are excluded. Limit gains to avoid
        // extreme shifts in strongly single-colored scenes.
        double red = 0, green = 0, blue = 0;
        long count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            var r = pixels[i + 2];
            var g = pixels[i + 1];
            var b = pixels[i];
            var lum = (54 * r + 183 * g + 19 * b) >> 8;
            if (lum < 32 || lum > 225) continue;
            red += r;
            green += g;
            blue += b;
            count++;
        }
        if (count == 0) return;
        var neutral = (red + green + blue) / 3;
        var gains = new[] {
            Math.Clamp(neutral / Math.Max(1, blue), .8, 1.25),
            Math.Clamp(neutral / Math.Max(1, green), .8, 1.25),
            Math.Clamp(neutral / Math.Max(1, red), .8, 1.25)
        };
        var table = new byte[3, 256];
        for (var channel = 0; channel < 3; channel++)
        for (var value = 0; value < 256; value++)
            table[channel, value] = (byte)Math.Clamp(
                (int)Math.Round(value * gains[channel]), 0, 255);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] == 0) continue;
            pixels[i] = table[0, pixels[i]];
            pixels[i + 1] = table[1, pixels[i + 1]];
            pixels[i + 2] = table[2, pixels[i + 2]];
        }
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
