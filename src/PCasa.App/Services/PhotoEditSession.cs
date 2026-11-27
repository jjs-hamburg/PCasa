using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// One in-memory edit transaction per still image. All tools share a single
/// cursor; no original is modified until the user's explicit Save command.
/// The same operations are replayed at full resolution by the P0 writer.
/// </summary>
public sealed record PhotoEditStep(
    string Kind, double X = 0, double Y = 0,
    double Width = 1, double Height = 1, double Degrees = 0,
    double Amount = 0, double Radius = 1, double Detail = .5,
    string Mask = "all", PhotoMaskStroke[]? MaskStrokes = null,
    string? ExternalPath = null, string? ExternalName = null);

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
        // Auto operations are calculated once per source/mask, not layered
        // cumulatively on every click. Undo permits re-applying from scratch.
        if (step.Kind is "auto-fix" or "auto-contrast" or
            "auto-color" or "auto-sharpen")
            if (_steps.Take(_cursor).Any(s =>
                s.Kind == step.Kind && s.Mask == step.Mask))
                return;
        if (_cursor < _steps.Count)
        {
            foreach (var discarded in _steps.Skip(_cursor))
                if (discarded.Kind == "external" && discarded.ExternalPath is { } path)
                    ExternalEditTransaction.TryRemoveHistoryFile(path);
            _steps.RemoveRange(_cursor, _steps.Count - _cursor);
        }
        _steps.Add(step);
        _cursor++;
    }
    public void Undo() { if (CanUndo) _cursor--; }
    public void Redo() { if (CanRedo) _cursor++; }
    public string? LatestExternalPath => AppliedSteps.LastOrDefault(s => s.Kind == "external")?.ExternalPath;
    public BitmapSource Render() => Render(_original, AppliedSteps,
        previewSize: Math.Max(2400, Math.Max(_original.PixelWidth, _original.PixelHeight)));

    /// <summary>
    /// For the swipe comparator, show ONLY the original area that maps to
    /// the edited image. Tone/denoise/sharpen do not alter the source's
    /// geometry; crop and rotate do. Replaying those same geometric steps
    /// preserves pixel alignment without scaling the full original to a
    /// cropped aspect ratio. No extra files or sidecars are created.
    /// </summary>
    public BitmapSource RenderAlignedOriginal(PhotoEditStep? pending = null)
    {
        var geometry = AppliedSteps.Where(s => s.Kind is "crop" or "rotate");
        if (pending is { Kind: "crop" or "rotate" })
            geometry = geometry.Append(pending);
        var steps = geometry.ToArray();
        return steps.Length == 0 ? _original : Render(_original, steps);
    }

    public static BitmapSource Render(BitmapSource original,
        IEnumerable<PhotoEditStep> steps, CancellationToken token = default,
        int? previewSize = null)
    {
        BitmapSource bitmap = original;
        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();
            if (step.Kind == "external")
            {
                var path = step.ExternalPath ?? throw new InvalidDataException("External history file missing.");
                if (previewSize is int size)
                {
                    var dimensions = EditableImageFormats.Dimensions(path);
                    bitmap = PhotoViewerImageLoader.Load(new PhotoViewerImageInfo(0, path,
                        Path.GetFileName(path), dimensions.Width, dimensions.Height), size, token);
                }
                else bitmap = EditableImageFormats.DecodeForEdit(path);
            }
            else if (step.Kind == "crop")
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
                     "auto-color" or "brightness" or "denoise-luma" or
                     "denoise-chroma" or "sharpen" or "highlights" or
                     "shadows" or "contrast" or "temperature" or
                     "tint" or "saturation" or "auto-sharpen")
            {
                if (!double.IsFinite(step.Amount) ||
                    Math.Abs(step.Amount) > 3)
                    throw new InvalidDataException(
                        "Ungültige Helligkeitskorrektur.");
                bitmap = RenderTone(bitmap, step.Kind, step.Amount, token,
                    step.Radius, step.Detail, step.Mask, step.MaskStrokes);
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
        string kind, double amount, CancellationToken token,
        double radius, double detail, string maskMode,
        PhotoMaskStroke[]? maskStrokes)
    {
        if (kind == "auto-sharpen")
        {
            kind = "sharpen";
            // A 1px/.4 preset barely changes moderately softened edges.
            // Use the same bounded, noise-gated unsharp kernel at 2px.
            amount = .65;
            radius = 2;
            detail = .65;
        }
        var argb = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = argb.PixelWidth, height = argb.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        argb.CopyPixels(pixels, stride, 0);
        // The immutable edit step owns its mask selection, not a mutable UI
        // state. Undo, preview and the durable full-resolution replay agree.
        var masked = !string.Equals(maskMode, "all", StringComparison.Ordinal);
        var beforeMask = masked ? (byte[])pixels.Clone() : null;

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

        if (kind is "sharpen" or "denoise-luma" or "denoise-chroma")
            ApplySpatialTone(pixels, width, height, kind, amount,
                radius, detail, token);
        else if (kind is "highlights" or "shadows" or "contrast" or
                 "temperature" or "tint" or "saturation")
            ApplyManualTone(pixels, kind, amount, token);

        if (beforeMask is not null)
        {
            var weights = PhotoEditMasks.Compute(beforeMask,
                width, height, maskMode, token, maskStrokes);
            for (var i = 0; i < weights.Length; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var p = i * 4;
                var w = weights[i];
                if (w == 255) continue;
                if (w == 0)
                {
                    pixels[p] = beforeMask[p];
                    pixels[p + 1] = beforeMask[p + 1];
                    pixels[p + 2] = beforeMask[p + 2];
                    continue;
                }
                for (var channel = 0; channel < 3; channel++)
                    pixels[p + channel] = (byte)Math.Clamp(
                        (int)Math.Round(beforeMask[p + channel] +
                            (pixels[p + channel] - beforeMask[p + channel]) *
                            (w / 255.0)), 0, 255);
            }
        }

        var result = BitmapSource.Create(width, height,
            source.DpiX, source.DpiY, PixelFormats.Bgra32,
            null, pixels, stride);
        result.Freeze();
        return result;
    }

    // All 8-bit manual adjustments operate on the same immutable Bgra32
    // transaction pixels as crop/rotation. Neutral slider position is exact
    // identity, including alpha. Values are deliberately bounded to [-1, 1].
    private static void ApplyManualTone(
        byte[] pixels, string kind, double amount, CancellationToken token)
    {
        if (Math.Abs(amount) < .00001) return;
        amount = Math.Clamp(amount, -1, 1);
        // Fine response around neutral, full effect only near the extremes.
        amount = Math.Sign(amount) * Math.Pow(Math.Abs(amount), 1.3);
        static double Smooth(double value)
        {
            value = Math.Clamp(value, 0, 1);
            return value * value * (3 - 2 * value);
        }
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if ((i & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            if (pixels[i + 3] == 0) continue;
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var y = (.2126 * r + .7152 * g + .0722 * b);
            switch (kind)
            {
                case "contrast":
                    var multiplier = 1 + amount * .8;
                    r = 127.5 + (r - 127.5) * multiplier;
                    g = 127.5 + (g - 127.5) * multiplier;
                    b = 127.5 + (b - 127.5) * multiplier;
                    break;
                case "shadows":
                    var shadowDelta = amount * 70 * (1 - Smooth((y - 18) / 145));
                    r += shadowDelta; g += shadowDelta; b += shadowDelta;
                    break;
                case "highlights":
                    var highlightDelta = amount * 70 * Smooth((y - 100) / 140);
                    r += highlightDelta; g += highlightDelta; b += highlightDelta;
                    break;
                case "saturation":
                    r = y + (r - y) * (1 + amount);
                    g = y + (g - y) * (1 + amount);
                    b = y + (b - y) * (1 + amount);
                    break;
                case "temperature":
                    r += amount * 33; b -= amount * 33;
                    break;
                case "tint":
                    r += amount * 15; g -= amount * 30; b += amount * 15;
                    break;
            }
            pixels[i] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
            pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
            pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
        }
    }

    // A single, deterministic image-space kernel for preview AND the P0
    // full-resolution writer. The old 3x3 filter could neither suppress
    // broad chroma noise nor distinguish a real edge from noisy grain.
    //
    // Luma/chroma denoise: 5x5 spatial Gaussian with separate luminance and
    // chroma range gates. It preserves color edges (including equal-luma
    // red/green edges) and never modifies the source luminance for chroma.
    //
    // Sharpen: Gaussian unsharp mask at the user's 1–3px radius, with a
    // detail-dependent noise threshold and local halo/overshoot limiter.
    // Every path preserves alpha, cancellation and exact neutral identity.
    private static void ApplySpatialTone(byte[] pixels, int width, int height,
        string kind, double amount, double radius, double detail,
        CancellationToken token)
    {
        if (amount <= .00001 || width < 3 || height < 3) return;
        if (!double.IsFinite(radius) || !double.IsFinite(detail))
            throw new InvalidDataException("Ungültige Detailparameter.");
        amount = Math.Clamp(amount, 0, 1);
        detail = Math.Clamp(detail, 0, 1);

        // Keep the original immutable for this one edit step. The shared
        // luma plane is 1 byte/pixel and avoids converting each neighbor
        // repeatedly in the inner 5x5 loop.
        var source = (byte[])pixels.Clone();
        var luminance = new byte[checked(width * height)];
        for (var i = 0; i < luminance.Length; i++)
        {
            var p = i * 4;
            luminance[i] = (byte)((54 * source[p + 2] +
                183 * source[p + 1] + 19 * source[p] + 128) >> 8);
        }

        int[] gaussian = [1, 4, 6, 4, 1];
        var isSharpen = kind == "sharpen";
        var isChroma = kind == "denoise-chroma";
        var stride = checked(width * 4);
        var spacing = isSharpen ? Math.Clamp((int)Math.Round(radius), 1, 3) : 1;
        var lumaLimit = 14 + amount * 64;
        var chromaLimit = 24 + amount * 92;
        var noiseFloor = 2.5 + (1 - detail) * 8;
        var sharpenGain = amount * (1.5 + detail * .85);
        var denoiseBlend = amount * .96;

        for (var y = 0; y < height; y++)
        {
            if ((y & 15) == 0) token.ThrowIfCancellationRequested();
            var rowStart = y * width;
            for (var x = 0; x < width; x++)
            {
                var pixel = rowStart + x;
                var p = pixel * 4;
                if (source[p + 3] == 0) continue;

                var centerY = (int)luminance[pixel];
                var centerB = source[p] - centerY;
                var centerG = source[p + 1] - centerY;
                var centerR = source[p + 2] - centerY;
                double total = 0, sumY = 0;
                double sumB = 0, sumG = 0, sumR = 0;
                var nearMin = centerY;
                var nearMax = centerY;

                for (var dy = -2; dy <= 2; dy++)
                {
                    var ny = y + dy * spacing;
                    if ((uint)ny >= (uint)height) continue;
                    var neighborRow = ny * width;
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var nx = x + dx * spacing;
                        if ((uint)nx >= (uint)width) continue;
                        var neighborPixel = neighborRow + nx;
                        var q = neighborPixel * 4;
                        if (source[q + 3] == 0) continue;
                        var nearY = (int)luminance[neighborPixel];
                        double weight = gaussian[dy + 2] * gaussian[dx + 2];

                        if (isSharpen)
                        {
                            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1)
                            {
                                nearMin = Math.Min(nearMin, nearY);
                                nearMax = Math.Max(nearMax, nearY);
                            }
                        }
                        else
                        {
                            // Do not average across high-contrast contours.
                            var yDifference = Math.Abs(nearY - centerY);
                            var yGate = Math.Max(0, 1 - yDifference / lumaLimit);
                            if (yGate <= 0) continue;
                            weight *= yGate;

                            if (isChroma)
                            {
                                // Luminance alone cannot detect colored
                                // edges of equal brightness. Gating chroma
                                // prevents red/green color leakage.
                                var db = source[q] - nearY;
                                var dg = source[q + 1] - nearY;
                                var dr = source[q + 2] - nearY;
                                var separation = (Math.Abs(db - centerB) +
                                    Math.Abs(dg - centerG) +
                                    Math.Abs(dr - centerR)) / 3.0;
                                weight *= Math.Max(0, 1 - separation / chromaLimit);
                                if (weight <= 0) continue;
                                sumB += weight * db;
                                sumG += weight * dg;
                                sumR += weight * dr;
                            }
                        }

                        total += weight;
                        sumY += weight * nearY;
                    }
                }
                if (total <= 0) continue;

                if (isChroma)
                {
                    // Keep the source's original Y while replacing only
                    // noisy B-Y, G-Y and R-Y residuals. Never cross color
                    // contours just because both sides have equal Y.
                    pixels[p] = ToneByte(source[p] + denoiseBlend *
                        (centerY + sumB / total - source[p]));
                    pixels[p + 1] = ToneByte(source[p + 1] + denoiseBlend *
                        (centerY + sumG / total - source[p + 1]));
                    pixels[p + 2] = ToneByte(source[p + 2] + denoiseBlend *
                        (centerY + sumR / total - source[p + 2]));
                    continue;
                }

                var average = sumY / total;
                double delta;
                if (isSharpen)
                {
                    var highPass = centerY - average;
                    var magnitude = Math.Abs(highPass) - noiseFloor;
                    if (magnitude <= 0) continue;
                    delta = Math.Sign(highPass) *
                        Math.Min(38, magnitude * sharpenGain);
                    // Only a bounded extension of nearby intensities is
                    // permitted: large radius must not cause bright or
                    // dark ringing around high-contrast edges.
                    var sharpenedY = Math.Clamp(centerY + delta,
                        Math.Max(0, nearMin - 9), Math.Min(255, nearMax + 9));
                    delta = sharpenedY - centerY;
                }
                else
                    delta = (average - centerY) * denoiseBlend;

                // Shared RGB delta preserves chroma instead of smearing
                // colors or adding false saturation around edges.
                pixels[p] = ToneByte(source[p] + delta);
                pixels[p + 1] = ToneByte(source[p + 1] + delta);
                pixels[p + 2] = ToneByte(source[p + 2] + delta);
            }
        }

        static byte ToneByte(double value) =>
            (byte)Math.Clamp((int)Math.Round(value), 0, 255);
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
