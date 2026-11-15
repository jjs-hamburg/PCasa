namespace PCasa.App.Services;

/// <summary>
/// Detect a prominent, continuous horizontal boundary separating blue sky
/// from water/land. It is a conservative horizon cue, not a semantic model:
/// photographs with no reliable horizon can still need manual correction.
/// </summary>
internal static class SkyHorizonDetector
{
    internal static int Limit(byte[] pixels, int width, int height,
        CancellationToken token = default)
    {
        if (width < 10 || height < 10) return height;
        var stride = Math.Max(1, width / 160);
        var sampleCount = (width + stride - 1) / stride;
        var bestScore = 0d;
        var bestRow = -1;
        var top = Math.Max(2, (int)(height * .14));
        var bottom = Math.Min(height - 3, (int)(height * .86));
        var deltaY = Math.Max(1, height / 400);
        for (var y = top; y <= bottom; y++)
        {
            if ((y & 31) == 0) token.ThrowIfCancellationRequested();
            var sum = 0d;
            var strong = 0;
            for (var x = 0; x < width; x += stride)
            {
                var a = ((y - deltaY) * width + x) * 4;
                var b = ((y + deltaY) * width + x) * 4;
                var diff = (Math.Abs(pixels[a] - pixels[b]) +
                    Math.Abs(pixels[a + 1] - pixels[b + 1]) +
                    Math.Abs(pixels[a + 2] - pixels[b + 2])) / 3d;
                if (diff >= 13) strong++;
                sum += diff;
            }
            var fraction = strong / (double)sampleCount;
            var average = sum / sampleCount;
            if (fraction < .53 || average < 17) continue;
            var score = fraction * average;
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = y;
            }
        }
        // Ambiguous blue-on-blue joins are bounded conservatively instead
        // of swallowing the entire sea through a connected blue candidate.
        return bestRow >= 0 ? bestRow : (int)Math.Round(height * .78);
    }
}
