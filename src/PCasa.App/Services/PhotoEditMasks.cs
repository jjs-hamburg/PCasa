using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Transient, locally computed edit masks. No files and no new model/runtime.
/// Luminance is deterministic; sky and foreground are intentionally marked
/// experimental color/edge heuristics, NOT semantic neural segmentation.
/// The same implementation blends preview and full-resolution durable writes.
/// </summary>
/// <summary>A normalized manual paint sample, independent of preview resolution.</summary>
public sealed record PhotoMaskStroke(double X, double Y, double Radius, bool Erase = false);

public static class PhotoEditMasks
{
    public static readonly string[] Modes =
        ["all", "sky", "subject", "background", "mask-highlights", "mask-shadows"];

    public static byte[] Compute(byte[] pixels, int width, int height,
        string mode, CancellationToken token = default,
        IReadOnlyList<PhotoMaskStroke>? strokes = null)
    {
        if (!Modes.Contains(mode, StringComparer.Ordinal))
            throw new InvalidDataException("Unknown edit mask: " + mode);
        var length = checked(width * height);
        var mask = new byte[length];
        if (mode == "all")
        {
            Array.Fill(mask, (byte)255);
            return mask;
        }

        static int Lum(byte b, byte g, byte r) =>
            (54 * r + 183 * g + 19 * b + 128) >> 8;
        if (mode is "mask-highlights" or "mask-shadows")
        {
            for (var i = 0; i < length; i++)
            {
                if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
                var o = i * 4;
                if (pixels[o + 3] == 0) continue;
                var light = Lum(pixels[o], pixels[o + 1], pixels[o + 2]);
                var t = mode == "mask-highlights"
                    ? Math.Clamp((light - 115.0) / 115.0, 0, 1)
                    : Math.Clamp((155.0 - light) / 135.0, 0, 1);
                mask[i] = (byte)Math.Round(255 * t * t * (3 - 2 * t));
            }
            return mask;
        }

        // A rough subject hint is not the final silhouette. Compute an
        // edge-aware mask once on a bounded analysis grid; use its inverse
        // for Background, so a marked person is never also in Background.
        if (mode is "subject" or "background")
        {
            // Without user hints, identifying an arbitrary person or object
            // is not reliable. Prefer an empty mask over a false "AI" guess.
            if (strokes is not { Count: > 0 } ||
                !strokes.Any(stroke => !stroke.Erase))
                return mask;
            mask = SeededMaskSegmentation.Compute(pixels, width, height,
                strokes, token);
            if (mode == "background")
                for (var i = 0; i < length; i++)
                    mask[i] = (byte)(255 - mask[i]);
            for (var i = 0; i < length; i++)
                if (pixels[i * 4 + 3] == 0) mask[i] = 0;
            return mask;
        }

        // Sky: choose blue or near-neutral bright candidates that are
        // connected to the TOP edge. No unrelated blue object lower down can
        // become part of the sky mask without a connected candidate path.
        var candidate = new bool[length];
        var skyLimit = SkyHorizonDetector.Limit(pixels, width, height, token);
        for (var i = 0; i < length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            var p = i * 4;
            if (pixels[p + 3] == 0 || i / width >= skyLimit) continue;
            var b = pixels[p];
            var g = pixels[p + 1];
            var r = pixels[p + 2];
            var luminance = Lum(b, g, r);
            var blue = b >= r * 1.08 && b >= g * .91 && luminance >= 62;
            var cloud = luminance >= 165 &&
                Math.Max(r, Math.Max(g, b)) -
                Math.Min(r, Math.Min(g, b)) < 28;
            candidate[i] = blue || cloud;
        }

        var queue = new int[length];
        var head = 0;
        var tail = 0;
        for (var x = 0; x < width; x++)
            if (candidate[x])
            {
                mask[x] = 255;
                queue[tail++] = x;
            }
        while (head < tail)
        {
            if ((head & 0xffff) == 0) token.ThrowIfCancellationRequested();
            var current = queue[head++];
            var x = current % width;
            var y = current / width;
            void Visit(int neighbor)
            {
                if (!candidate[neighbor] || mask[neighbor] != 0) return;
                mask[neighbor] = 255;
                queue[tail++] = neighbor;
            }
            if (x > 0) Visit(current - 1);
            if (x + 1 < width) Visit(current + 1);
            if (y > 0) Visit(current - width);
            if (y + 1 < height) Visit(current + width);
        }

        // Feather the segmentation boundary; do not blur unrelated colors.
        if (width >= 3 && height >= 3)
        {
            var hard = (byte[])mask.Clone();
            for (var y = 1; y < height - 1; y++)
            for (var x = 1; x < width - 1; x++)
            {
                var i = y * width + x;
                var value = hard[i] * 4 + hard[i - 1] * 2 +
                    hard[i + 1] * 2 + hard[i - width] * 2 +
                    hard[i + width] * 2 +
                    hard[i - width - 1] + hard[i - width + 1] +
                    hard[i + width - 1] + hard[i + width + 1];
                mask[i] = (byte)(value / 16);
            }
        }
        // Explicit sky correction hints can restore small foreground
        // cutouts or erase blue water left by an uncertain horizon.
        if (strokes is { Count: > 0 })
            ApplySkyCorrections(mask, width, height, strokes, token);
        return mask;
    }

    private static void ApplySkyCorrections(byte[] mask, int width, int height,
        IReadOnlyList<PhotoMaskStroke> strokes, CancellationToken token)
    {
        foreach (var stroke in strokes)
        {
            token.ThrowIfCancellationRequested();
            if (!double.IsFinite(stroke.X) || !double.IsFinite(stroke.Y) ||
                !double.IsFinite(stroke.Radius) || stroke.X < 0 ||
                stroke.X > 1 || stroke.Y < 0 || stroke.Y > 1 ||
                stroke.Radius <= 0 || stroke.Radius > .2)
                throw new InvalidDataException("Invalid sky correction stroke.");
            var radius = Math.Max(1d, stroke.Radius * Math.Min(width, height));
            var cx = stroke.X * (width - 1);
            var cy = stroke.Y * (height - 1);
            var left = Math.Max(0, (int)Math.Floor(cx - radius));
            var right = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
            var top = Math.Max(0, (int)Math.Floor(cy - radius));
            var bottom = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var distance = Math.Sqrt((x - cx) * (x - cx) +
                    (y - cy) * (y - cy)) / radius;
                if (distance >= 1) continue;
                var value = (byte)Math.Round(255 *
                    Math.Clamp((1 - distance) / .13, 0, 1));
                var i = y * width + x;
                mask[i] = stroke.Erase
                    ? (byte)Math.Min(mask[i], 255 - value)
                    : (byte)Math.Max(mask[i], value);
            }
        }
    }

    public static BitmapSource Preview(BitmapSource source, string mode,
        CancellationToken token = default,
        IReadOnlyList<PhotoMaskStroke>? strokes = null)
    {
        var pixels = ToBgra(source);
        var mask = Compute(pixels, source.PixelWidth, source.PixelHeight, mode, token, strokes);
        for (var i = 0; i < mask.Length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            var p = i * 4;
            if (pixels[p + 3] == 0) continue;
            var blend = mask[i] / 255.0 * .55;
            pixels[p] = (byte)Math.Round(pixels[p] * (1 - blend));
            pixels[p + 1] = (byte)Math.Round(pixels[p + 1] * (1 - blend) + 134 * blend);
            pixels[p + 2] = (byte)Math.Round(pixels[p + 2] * (1 - blend) + 255 * blend);
        }
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight,
            source.DpiX, source.DpiY, PixelFormats.Bgra32, null,
            pixels, source.PixelWidth * 4);
        result.Freeze();
        return result;
    }

    private static byte[] ToBgra(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
}
