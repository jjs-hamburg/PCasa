using System.Diagnostics;
using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// User-invoked, isolated A/B test for one existing JPEG on any writable
/// SMB share (or local volume as a control). Never mutates the source.
/// Production metadata scheduling and writer selection are untouched.
/// </summary>
public static class JpegTagBenchmarkRunner
{
    public sealed record Report(string SourcePath, long SourceBytes,
        TimeSpan ExifToolElapsed, TimeSpan PrototypeElapsed,
        bool ServerSideCopyUsed, bool Verified)
    {
        public override string ToString()
        {
            var faster = PrototypeElapsed < ExifToolElapsed
                ? "JPEG-Prototyp" : "ExifTool";
            var ratio = Math.Max(ExifToolElapsed.TotalSeconds,
                PrototypeElapsed.TotalSeconds) /
                Math.Max(0.001d, Math.Min(ExifToolElapsed.TotalSeconds,
                    PrototypeElapsed.TotalSeconds));
            return "PCasa JPEG-Tag-Benchmark (nur Testkopien)" +
                Environment.NewLine +
                "Quelle: " + SourcePath + Environment.NewLine +
                "Größe: " + (SourceBytes / (1024d * 1024d)).ToString("F2") +
                " MiB" + Environment.NewLine +
                "ExifTool (Kopie + Tags): " +
                ExifToolElapsed.TotalSeconds.ToString("F2") + " s" +
                Environment.NewLine +
                "JPEG-Prototyp (Kopie + Tags): " +
                PrototypeElapsed.TotalSeconds.ToString("F2") + " s" +
                Environment.NewLine +
                "SMB CopyChunk genutzt: " +
                (ServerSideCopyUsed ? "Ja" : "Nein") +
                Environment.NewLine +
                "Schneller: " + faster + " (" + ratio.ToString("F2") +
                "x)" + Environment.NewLine +
                "Tag-/Bilddatenprüfung: " +
                (Verified ? "Bestanden" : "NICHT BESTANDEN") +
                Environment.NewLine +
                "Original unverändert; Testkopien werden gelöscht.";
        }
    }

    public static Report Run(string sourcePath, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Das JPG wurde nicht gefunden.", source);
        var ext = Path.GetExtension(source);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Bitte ein JPG oder JPEG auswählen.");

        var dir = Path.GetDirectoryName(source)!;
        var nonce = Guid.NewGuid().ToString("N");
        var baselinePath = Path.Combine(dir, ".pcasa-jpeg-benchmark-" +
            nonce + "-exiftool.jpg");
        var prototypePath = Path.Combine(dir, ".pcasa-jpeg-benchmark-" +
            nonce + "-prototype.jpg");
        // Two unique files on the SAME filesystem/share ensure that A/B
        // measurements include comparable file creation, transfer and tags.
        // Neither is ever an original. Cleanup runs even when validation fails.
        var original = new FileInfo(source);
        var sourceBytes = original.Length;
        var sourceTicks = original.LastWriteTimeUtc.Ticks;
        var sourceHash = JpegGpsPatchPrototype.CompressedImageHash(source);
        TimeSpan baseline = default, prototype = default;
        bool copyChunk = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using (var reader = new ExifToolKeywordWriter.Session())
            {
                var tags = reader.ReadSnapshot(source, token).Combined.ToList();
                tags.Add("PCasaBenchmark-" + nonce[..10]);
                var timer = Stopwatch.StartNew();
                File.Copy(source, baselinePath);
                reader.Write(baselinePath, tags, token,
                    knownSourceLengthBytes: sourceBytes);
                baseline = timer.Elapsed;

                token.ThrowIfCancellationRequested();
                timer.Restart();
                var outcome = JpegTagRewritePrototype.CreateTagCopy(source,
                    prototypePath, tags, token);
                prototype = timer.Elapsed;
                copyChunk = outcome.ServerSideCopyUsed;

                // Verification is outside both timed periods. ExifTool is
                // the reference for metadata semantics, the compressed
                // JPEG tail is compared by SHA-256.
                var actualOriginal = reader.ReadSnapshot(source, token);
                var baselineTags = reader.ReadSnapshot(baselinePath, token);
                var prototypeTags = reader.ReadSnapshot(prototypePath, token);
                var expected = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!baselineTags.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(expected) ||
                    !baselineTags.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(expected) ||
                    !prototypeTags.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(expected) ||
                    !prototypeTags.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase)
                        .SetEquals(expected) ||
                    actualOriginal.Combined.Any(t => t.StartsWith(
                        "PCasaBenchmark-", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        "Die JPEG-Metadaten stimmen nicht überein.");
            }

            if (!sourceHash.SequenceEqual(
                    JpegGpsPatchPrototype.CompressedImageHash(baselinePath)) ||
                !sourceHash.SequenceEqual(
                    JpegGpsPatchPrototype.CompressedImageHash(prototypePath)) ||
                File.GetLastWriteTimeUtc(source).Ticks != sourceTicks ||
                new FileInfo(source).Length != sourceBytes)
                throw new InvalidDataException(
                    "Der Vergleich der JPEG-Bilddaten oder Quelldatei ist fehlgeschlagen.");

            return new Report(source, sourceBytes, baseline, prototype,
                copyChunk, Verified: true);
        }
        finally
        {
            // Only our two unique test outputs. Never touch the selected
            // original or any neighboring file.
            foreach (var path in new[] { baselinePath, prototypePath })
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
