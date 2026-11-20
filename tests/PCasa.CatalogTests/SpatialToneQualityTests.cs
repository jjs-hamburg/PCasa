using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

// Deterministic numeric checks over actual in-memory WPF bitmaps. These
// detect the old weak 3x3 smoothing/sharpening, color-edge leakage, loss of
// detail, and any preview-vs-writer divergence. They never touch originals.
internal static class SpatialToneQualityTests
{
    public static void Run()
    {
        const int width = 160, height = 100;

        var luminancePixels = Scene(width, height, (x, y) =>
        {
            var noise = (x * 37 + y * 71 + 11) % 31 - 15;
            var gray = (byte)((x < 80 ? 88 : 188) + noise);
            return (gray, gray, gray);
        });
        var original = Bitmap(luminancePixels, width, height);
        var unchanged = PhotoEditSession.Render(original,
            [new PhotoEditStep("denoise-luma", Amount: 0)]);
        if (!Pixels(unchanged).SequenceEqual(luminancePixels))
            throw new Exception("Denoise neutral position changed pixels.");

        var luma = PhotoEditSession.Render(original,
            [new PhotoEditStep("denoise-luma", Amount: .88)]);
        var outputLuma = Pixels(luma);
        var beforeNoise = MeanError(luminancePixels, width, 12, 65, 12, 85, 88, 2);
        var afterNoise = MeanError(outputLuma, width, 12, 65, 12, 85, 88, 2);
        if (afterNoise > beforeNoise * .8)
            throw new Exception(
                $"Luminance noise was not suppressed: {beforeNoise:F2}->{afterNoise:F2}.");
        if (Average(outputLuma, width, 82, 85, 8, 92, 2) -
            Average(outputLuma, width, 75, 78, 8, 92, 2) < 85)
            throw new Exception("Denoising blurred a high-contrast picture edge.");

        var chromaPixels = Scene(width, height, (x, y) =>
        {
            var noise = (x * 37 + y * 71 + 11) % 31 - 15;
            var red = x < 80 ? 160 : 80;
            var green = x < 80 ? 80 : 110;
            return ((byte)(80 - noise),
                (byte)(green - (int)Math.Round(noise * .196)),
                (byte)(red + noise));
        });
        var chromaOriginal = Bitmap(chromaPixels, width, height);
        var chroma = PhotoEditSession.Render(chromaOriginal,
            [new PhotoEditStep("denoise-chroma", Amount: .92)]);
        var outputChroma = Pixels(chroma);
        var beforeChroma = MeanError(chromaPixels, width, 12, 65, 12, 85, 160, 2);
        var afterChroma = MeanError(outputChroma, width, 12, 65, 12, 85, 160, 2);
        if (afterChroma > beforeChroma * .8)
            throw new Exception(
                $"Color speckle was not suppressed: {beforeChroma:F2}->{afterChroma:F2}.");
        // Equal-luma red/green boundary: a luma-only bilateral gate leaks
        // color across this edge. Distinguish the two boundary pixels.
        if (Average(outputChroma, width, 78, 79, 12, 88, 2) -
            Average(outputChroma, width, 80, 81, 12, 88, 2) < 45)
            throw new Exception("Chroma denoising bled across a distinct color edge.");

        double meanLumaChange = 0;
        for (var i = 0; i < outputChroma.Length; i += 4)
        {
            var beforeY = (54 * chromaPixels[i + 2] +
                183 * chromaPixels[i + 1] + 19 * chromaPixels[i] + 128) >> 8;
            var afterY = (54 * outputChroma[i + 2] +
                183 * outputChroma[i + 1] + 19 * outputChroma[i] + 128) >> 8;
            meanLumaChange += Math.Abs(afterY - beforeY);
        }
        meanLumaChange /= width * height;
        if (meanLumaChange > 1.5)
            throw new Exception(
                $"Color-only noise reduction shifted brightness: {meanLumaChange:F2}.");

        var softEdge = Scene(width, height, (x, y) =>
        {
            var gray = (byte)Math.Round(65 +
                120 / (1 + Math.Exp(-(x - 80) / 1.5)));
            return (gray, gray, gray);
        });
        var softened = Bitmap(softEdge, width, height);
        var sharp = PhotoEditSession.Render(softened,
            [new PhotoEditStep("sharpen", Amount: .95, Radius: 2, Detail: .65)]);
        var sharpPixels = Pixels(sharp);
        var beforeContrast = (int)softEdge[(50 * width + 82) * 4] -
            softEdge[(50 * width + 78) * 4];
        var afterContrast = (int)sharpPixels[(50 * width + 82) * 4] -
            sharpPixels[(50 * width + 78) * 4];
        if (afterContrast <= beforeContrast)
            throw new Exception(
                $"Unsharp masking failed to increase an actual soft edge: " +
                $"{beforeContrast}->{afterContrast}.");
        if (sharpPixels[(50 * width + 20) * 4] !=
                softEdge[(50 * width + 20) * 4] ||
            sharpPixels[(50 * width + 140) * 4] !=
                softEdge[(50 * width + 140) * 4])
            throw new Exception("Sharpening amplified perfectly smooth photo regions.");
        var wideSharpen = PhotoEditSession.Render(softened,
            [new PhotoEditStep("sharpen", Amount: .95, Radius: 3, Detail: .65)]);
        if (Pixels(wideSharpen).SequenceEqual(sharpPixels))
            throw new Exception("Sharpen radius did not change spatial detail response.");
        var automatic = PhotoEditSession.Render(softened,
            [new PhotoEditStep("auto-sharpen")]);
        if (Pixels(automatic).SequenceEqual(softEdge))
            throw new Exception("Automatic sharpening was visually inert on a softened edge.");

        // Transparent samples must not bleed or acquire alpha; the original
        // immutable bitmap is the one the editor's writer later replays.
        luminancePixels[(15 * width + 15) * 4 + 3] = 0;
        var alphaSource = Bitmap(luminancePixels, width, height);
        var session = new PhotoEditSession(101, alphaSource);
        session.Apply(new PhotoEditStep("denoise-luma", Amount: .7));
        session.Apply(new PhotoEditStep("sharpen", Amount: .65, Radius: 2, Detail: .7));
        var preview = Pixels(session.Render());
        var durableReplay = Pixels(PhotoEditSession.Render(
            session.Original, session.AppliedSteps));
        if (!preview.SequenceEqual(durableReplay))
            throw new Exception("Spatial edit preview differs from the P0 writer recipe.");
        for (var i = 0; i < preview.Length; i += 4)
            if (preview[i + 3] != luminancePixels[i + 3])
                throw new Exception("Spatial editing changed image transparency.");
        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            PhotoEditSession.Render(original,
                [new PhotoEditStep("denoise-luma", Amount: 1)], cancelled.Token);
            throw new Exception("Spatial edit ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] Scene(int width, int height,
        Func<int, int, (byte B, byte G, byte R)> colors)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = (y * width + x) * 4;
            var (b, g, r) = colors(x, y);
            data[p] = b;
            data[p + 1] = g;
            data[p + 2] = r;
            data[p + 3] = 255;
        }
        return data;
    }

    private static BitmapSource Bitmap(byte[] data, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, data, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var result = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(result, bitmap.PixelWidth * 4, 0);
        return result;
    }

    private static double MeanError(byte[] data, int strideWidth,
        int left, int right, int top, int bottom, int expected, int channel)
    {
        long total = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
            total += Math.Abs(data[(y * strideWidth + x) * 4 + channel] - expected);
        return total / (double)((right - left) * (bottom - top));
    }

    private static double Average(byte[] data, int strideWidth,
        int left, int right, int top, int bottom, int channel)
    {
        long total = 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
            total += data[(y * strideWidth + x) * 4 + channel];
        return total / (double)((right - left) * (bottom - top));
    }
}
