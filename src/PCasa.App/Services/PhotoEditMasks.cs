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
public static class PhotoEditMasks
{
    public static readonly string[] Modes =
        ["all", "sky", "subject", "background", "mask-highlights", "mask-shadows"];

    public static byte[] Compute(byte[] pixels, int width, int height,
        string mode, CancellationToken token = default)
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

        if (mode is "subject" or "background")
        {
            // Estimate edge/background color from only the image perimeter;
            // favor centrally located regions which differ from those edges.
            // This is a deterministic approximation, not an object detector.
            double edgeR = 0, edgeG = 0, edgeB = 0;
            var count = 0;
            var stride = Math.Max(1, Math.Min(width, height) / 100);
            void Sample(int x, int y)
            {
                var p = (y * width + x) * 4;
                if (pixels[p + 3] == 0) return;
                edgeB += pixels[p]; edgeG += pixels[p + 1];
                edgeR += pixels[p + 2]; count++;
            }
            for (var x = 0; x < width; x += stride)
            {
                Sample(x, 0); Sample(x, height - 1);
            }
            for (var y = 0; y < height; y += stride)
            {
                Sample(0, y); Sample(width - 1, y);
            }
            var b0 = edgeB / Math.Max(1, count);
            var g0 = edgeG / Math.Max(1, count);
            var r0 = edgeR / Math.Max(1, count);
            for (var y = 0; y < height; y++)
            {
                if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var p = i * 4;
                    if (pixels[p + 3] == 0) continue;
                    var db = pixels[p] - b0;
                    var dg = pixels[p + 1] - g0;
                    var dr = pixels[p + 2] - r0;
                    var delta = Math.Sqrt(db * db + dg * dg + dr * dr);
                    var dx = (x + .5) / width - .5;
                    var dy = (y + .5) / height - .5;
                    var central = 1 - Math.Min(1,
                        Math.Sqrt(dx * dx + dy * dy) * 1.5);
                    var score = Math.Clamp((delta - 22) / 85, 0, 1) *
                        (.55 + .45 * central);
                    score = score * score * (3 - 2 * score);
                    mask[i] = (byte)Math.Clamp(
                        (int)Math.Round((mode == "subject" ? score : 1 - score) * 255),
                        0, 255);
                }
            }
            return mask;
        }

        // Sky: choose blue or near-neutral bright candidates that are
        // connected to the TOP edge. No unrelated blue object lower down can
        // become part of the sky mask without a connected candidate path.
        var candidate = new bool[length];
        for (var i = 0; i < length; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            var p = i * 4;
            if (pixels[p + 3] == 0) continue;
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
        return mask;
    }

    public static BitmapSource Preview(BitmapSource source, string mode,
        CancellationToken token = default)
    {
        var pixels = ToBgra(source);
        var mask = Compute(pixels, source.PixelWidth, source.PixelHeight, mode, token);
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
