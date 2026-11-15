using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Deterministic seeded image segmentation. A rough foreground stroke is a
/// foreground HINT, not a painted final silhouette. Boundary strokes mark
/// definite background. Propagation follows small color changes and strongly
/// penalizes crossing a contrast edge. Results are computed on a bounded
/// analysis grid, then mapped to any source resolution without large graph
/// allocations. No network, sidecars, native runtime or model downloads.
/// </summary>
internal static class SeededMaskSegmentation
{
    private const int MaximumAnalysisSide = 640;

    internal static byte[] Compute(byte[] pixels, int width, int height,
        IReadOnlyList<PhotoMaskStroke> strokes, CancellationToken token)
    {
        var result = new byte[checked(width * height)];
        if (!strokes.Any(s => !s.Erase)) return result;

        foreach (var stroke in strokes)
            if (!double.IsFinite(stroke.X) || !double.IsFinite(stroke.Y) ||
                !double.IsFinite(stroke.Radius) || stroke.X < 0 || stroke.X > 1 ||
                stroke.Y < 0 || stroke.Y > 1 || stroke.Radius <= 0 ||
                stroke.Radius > .2)
                throw new InvalidDataException("Invalid subject selection hint.");

        var factor = Math.Min(1d, MaximumAnalysisSide / (double)Math.Max(width, height));
        var sw = Math.Max(1, (int)Math.Round(width * factor));
        var sh = Math.Max(1, (int)Math.Round(height * factor));
        var count = checked(sw * sh);
        var colors = new Rgb[count];
        for (var y = 0; y < sh; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            var sy = Math.Min(height - 1, (int)((y + .5) * height / sh));
            for (var x = 0; x < sw; x++)
            {
                var sx = Math.Min(width - 1, (int)((x + .5) * width / sw));
                var pos = (sy * width + sx) * 4;
                colors[y * sw + x] = new Rgb(
                    pixels[pos + 2], pixels[pos + 1], pixels[pos]);
            }
        }

        // Scribbles are strong interior seeds, not the outer boundary of the
        // resulting mask. Negative strokes are user-supplied background seeds.
        var seeds = new byte[count];
        for (var y = 0; y < sh; y++)
        for (var x = 0; x < sw; x++)
            if (x == 0 || y == 0 || x == sw - 1 || y == sh - 1)
                seeds[y * sw + x] = 2;

        foreach (var stroke in strokes)
        {
            token.ThrowIfCancellationRequested();
            var x0 = stroke.X * (sw - 1);
            var y0 = stroke.Y * (sh - 1);
            // Interior quarter of the loose brush is a definite sample.
            var r = Math.Max(1, stroke.Radius * Math.Min(sw, sh) * .32);
            var left = Math.Max(0, (int)Math.Floor(x0 - r));
            var right = Math.Min(sw - 1, (int)Math.Ceiling(x0 + r));
            var top = Math.Max(0, (int)Math.Floor(y0 - r));
            var bottom = Math.Min(sh - 1, (int)Math.Ceiling(y0 + r));
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                if ((x - x0) * (x - x0) + (y - y0) * (y - y0) <= r * r)
                    seeds[y * sw + x] = stroke.Erase ? (byte)2 : (byte)1;
        }

        var foregroundSamples = SampleColors(colors, seeds, 1, 48);
        var backgroundSamples = SampleColors(colors, seeds, 2, 64);
        if (foregroundSamples.Length == 0 || backgroundSamples.Length == 0)
            return result;

        var foregroundPenalty = BuildColorPenalty(colors, foregroundSamples, token);
        var backgroundPenalty = BuildColorPenalty(colors, backgroundSamples, token);
        var fgCost = GeodesicCosts(colors, seeds, 1, sw, sh,
            foregroundPenalty, token);
        var bgCost = GeodesicCosts(colors, seeds, 2, sw, sh,
            backgroundPenalty, token);
        var reduced = new byte[count];
        for (var i = 0; i < count; i++)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            if (seeds[i] == 1) { reduced[i] = 255; continue; }
            if (seeds[i] == 2) { reduced[i] = 0; continue; }
            if (!float.IsFinite(fgCost[i])) continue;
            if (!float.IsFinite(bgCost[i])) { reduced[i] = 255; continue; }
            // A short feather on the actual boundary, not the brush edge.
            var diff = bgCost[i] - fgCost[i];
            reduced[i] = (byte)Math.Round(255 *
                Math.Clamp(.5 + diff / 11.0, 0, 1));
        }

        // Upsample only the bounded selection result, never the graph. The
        // same normalized strokes work in viewer preview and full-size writer.
        for (var y = 0; y < height; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            var fy = Math.Clamp((y + .5) * sh / height - .5, 0, sh - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(sh - 1, y0 + 1);
            var ty = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (pixels[i * 4 + 3] == 0) continue;
                var fx = Math.Clamp((x + .5) * sw / width - .5, 0, sw - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(sw - 1, x0 + 1);
                var tx = fx - x0;
                var a = reduced[y0 * sw + x0] * (1 - tx) +
                        reduced[y0 * sw + x1] * tx;
                var b = reduced[y1 * sw + x0] * (1 - tx) +
                        reduced[y1 * sw + x1] * tx;
                result[i] = (byte)Math.Round(a * (1 - ty) + b * ty);
            }
        }
        return result;
    }

    private static Rgb[] SampleColors(Rgb[] colors, byte[] seeds,
        byte wanted, int limit)
    {
        var locations = new List<int>();
        for (var i = 0; i < seeds.Length; i++)
            if (seeds[i] == wanted) locations.Add(i);
        if (locations.Count == 0) return [];
        var samples = new List<Rgb>(limit);
        var step = Math.Max(1, locations.Count / limit);
        for (var i = 0; i < locations.Count && samples.Count < limit; i += step)
            samples.Add(colors[locations[i]]);
        return samples.ToArray();
    }

    private static float[] BuildColorPenalty(Rgb[] colors,
        Rgb[] samples, CancellationToken token)
    {
        var penalty = new float[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            if ((i & 0x1ffff) == 0) token.ThrowIfCancellationRequested();
            var min = int.MaxValue;
            foreach (var sample in samples)
                min = Math.Min(min, colors[i].DistanceSquared(sample));
            // Palette distance discourages propagating through a visually
            // unrelated region even if its boundary has no sharp gradient.
            penalty[i] = MathF.Min(18f, MathF.Sqrt(min) * .055f);
        }
        return penalty;
    }

    private static float[] GeodesicCosts(Rgb[] colors, byte[] seeds,
        byte wanted, int width, int height, float[] penalty,
        CancellationToken token)
    {
        var costs = new float[colors.Length];
        Array.Fill(costs, float.PositiveInfinity);
        var heap = new PriorityQueue<int, float>();
        for (var i = 0; i < costs.Length; i++)
            if (seeds[i] == wanted)
            {
                costs[i] = 0;
                heap.Enqueue(i, 0);
            }

        var visited = 0;
        while (heap.TryDequeue(out var current, out var cost))
        {
            if ((++visited & 0x3fff) == 0) token.ThrowIfCancellationRequested();
            if (cost > costs[current]) continue;
            var x = current % width;
            var y = current / width;
            void Relax(int neighbor)
            {
                var boundary = MathF.Sqrt(
                    colors[current].DistanceSquared(colors[neighbor]));
                // Strong local edges are expensive to cross. The color
                // penalty keeps the propagation near the reference strokes.
                var step = 1 + penalty[neighbor] * 2.2f +
                    MathF.Min(26, boundary / 14) * MathF.Min(26, boundary / 14);
                var next = cost + step;
                if (next >= costs[neighbor]) return;
                costs[neighbor] = next;
                heap.Enqueue(neighbor, next);
            }
            if (x > 0) Relax(current - 1);
            if (x + 1 < width) Relax(current + 1);
            if (y > 0) Relax(current - width);
            if (y + 1 < height) Relax(current + width);
        }
        return costs;
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        internal int DistanceSquared(Rgb other)
        {
            var dr = R - other.R;
            var dg = G - other.G;
            var db = B - other.B;
            return dr * dr + dg * dg + db * db;
        }
    }
}
