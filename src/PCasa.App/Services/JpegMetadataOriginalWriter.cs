using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PCasa.App.Services;

/// <summary>
/// One decision point for original JPEG Tag and GPS edits. The repositories
/// already hold SourceFileLocks and own durable job commits. Unsupported JPEGs
/// return false BEFORE touching their source, and use the existing ExifTool
/// writer. An error after starting an optimized write is NEVER silently
/// retried with another writer.
///
/// The complete candidate is built and verified under its own unique name
/// on the ORIGINAL filesystem. File.Replace is the single commit boundary;
/// neither scanner, scheduler nor SQLite sees an incomplete JPEG.
/// </summary>
public static class JpegMetadataOriginalWriter
{
    public static string? LastFallbackDifference { get; private set; }
    private static long _profileAttempts;
    private static long _slowSamples;

    public static bool TryWriteTags(string path, IReadOnlyList<string> keywords,
        ExifToolKeywordWriter.Session session, CancellationToken token)
        => TryWrite(path, session, keywords, updateTags: true,
            latitude: null, longitude: null, token);

    public static bool TryWriteGps(string path, double? latitude, double? longitude,
        ExifToolKeywordWriter.Session session, CancellationToken token)
        => TryWrite(path, session, Array.Empty<string>(), updateTags: false,
            latitude, longitude, token);

    private static bool TryWrite(string path, ExifToolKeywordWriter.Session session,
        IReadOnlyList<string> keywords, bool updateTags,
        double? latitude, double? longitude, CancellationToken token)
    {
        var ext = Path.GetExtension(path);
        if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return false;
        token.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var ordinal = Interlocked.Increment(ref _profileAttempts);
        var checkpointHeader = 0L;
        var checkpointSource = 0L;
        var checkpointBuild = 0L;
        var checkpointValidation = 0L;
        void Report(string outcome)
        {
            var elapsed = clock.ElapsedMilliseconds;
            // Bounded diagnostics: initial samples, every 25th edit,
            // plus up to 3 unusually slow first occurrences. Never log
            // one line per file in a huge library.
            var sample = ordinal <= 4 || ordinal % 25 == 0 ||
                (elapsed >= 10000 &&
                 Interlocked.Increment(ref _slowSamples) <= 3);
            if (!sample) return;
            PCasaDiagnostics.WriteMessage("JPEG.Write.Profile",
                $"Mode={(updateTags ? "Tags" : "GPS")}; " +
                $"Result={outcome}; " +
                $"HeaderMs={checkpointHeader}; " +
                $"SourceMetadataMs={checkpointSource}; " +
                $"BuildCopyHashMs={checkpointBuild}; " +
                $"VerifyMs={checkpointValidation}; " +
                $"TotalMs={elapsed}; " +
                $"File={Path.GetFileName(path)}");
        }

        // Refuse multi-picture JPEGs and extended XMP whose absolute offsets
        // can refer to bytes outside ordinary APP1. These require ExifTool's
        // more complete metadata model. A false positive only loses speed.
        if (!SafeBasicJpeg(path, out var headerReason))
        {
            checkpointHeader = clock.ElapsedMilliseconds;
            Report("HeaderFallback:" + headerReason);
            return false;
        }
        checkpointHeader = clock.ElapsedMilliseconds;

        var original = new FileInfo(path);
        var originalSize = original.Length;
        var stamp = original.LastWriteTimeUtc;
        var creation = original.CreationTimeUtc;
        var originalAttrs = original.Attributes;
        var originalKeywords = session.ReadSnapshot(path, token);
        using var originalExifReader = new ImageExifReader();
        var originalExif = originalExifReader.Read(path, token);
        checkpointSource = clock.ElapsedMilliseconds - checkpointHeader;
        var temp = Path.Combine(Path.GetDirectoryName(path)!,
            ".pcasa-jpeg-" + Guid.NewGuid().ToString("N") + ".jpg");
        var created = false;
        try
        {
            try
            {
                if (updateTags)
                    JpegTagRewritePrototype.CreateTagCopy(
                        path, temp, keywords, session, token);
                else
                    JpegTagRewritePrototype.CreateGpsCopy(
                        path, temp, latitude, longitude, session, token);
                created = true;
            }
            catch (NotSupportedException)
            {
                // Header rejected before original replacement: the native
                // writer may handle this uncommon JPEG instead.
                checkpointBuild = clock.ElapsedMilliseconds -
                    checkpointHeader - checkpointSource;
                Report("LayoutFallback");
                return false;
            }
            checkpointBuild = clock.ElapsedMilliseconds -
                checkpointHeader - checkpointSource;

            // A candidate is not trusted just because ExifTool acknowledged
            // the tiny local carrier. Verify the actual full-size JPEG before
            // any irreversible replacement.
            using var verification = new ExifToolKeywordWriter.Session();
            var candidateTags = verification.ReadSnapshot(temp, token);
            var candidateExif = originalExifReader.Read(temp, token);
            var expectedTags = updateTags
                ? keywords.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : originalKeywords.Combined.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var actualTags = candidateTags.Combined.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            if (!actualTags.SetEquals(expectedTags) ||
                !candidateTags.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(updateTags ? expectedTags :
                        originalKeywords.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase)) ||
                !candidateTags.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(updateTags ? expectedTags :
                        originalKeywords.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "JPEG-Kandidat enthält nicht die erwarteten XMP/IPTC-Tags.");

            if (updateTags)
            {
                if (!SameGps(originalExif, candidateExif))
                    throw new InvalidDataException(
                        "Tag-Schreiben hat vorhandene GPS-Daten verändert.");
            }
            else if (latitude.HasValue
                ? !Near(candidateExif.GpsLatitude, latitude) ||
                  !Near(candidateExif.GpsLongitude, longitude)
                : candidateExif.GpsLatitude is not null ||
                  candidateExif.GpsLongitude is not null)
                throw new InvalidDataException(
                    "JPEG-Kandidat enthält nicht die erwarteten GPS-Daten.");

            if (!SameUnchangedExif(originalExif.Json, candidateExif.Json,
                ignoreGps: !updateTags))
                // This candidate cannot safely preserve this camera's
                // metadata. The source remains untouched: use ExifTool.
            {
                checkpointValidation = clock.ElapsedMilliseconds -
                    checkpointHeader - checkpointSource - checkpointBuild;
                Report("ExifFallback");
                return false;
            }

            checkpointValidation = clock.ElapsedMilliseconds -
                checkpointHeader - checkpointSource - checkpointBuild;

            // The prototype independently checks the complete compressed
            // image-tail SHA-256. Check identity again at the commit boundary.
            var before = new FileInfo(path);
            if (before.Length != originalSize || before.LastWriteTimeUtc != stamp)
                throw new IOException("JPEG-Original während des Schreibens verändert.");
            token.ThrowIfCancellationRequested(); // last safe cancellation point
            File.SetCreationTimeUtc(temp, creation);
            File.SetLastWriteTimeUtc(temp, stamp);

            // Both paths are in the SAME directory. File.Replace either
            // atomically publishes the completed candidate or throws.
            // Never fall back to a copy-overwrite of the original.
            File.Replace(temp, path, destinationBackupFileName: null);
            created = false;
            if (File.GetLastWriteTimeUtc(path) != stamp)
                File.SetLastWriteTimeUtc(path, stamp);
            Report("Fast");
            return true;
        }
        finally
        {
            // Failed attempts never delete or overwrite the original.
            // Only our own unique candidate is eligible for removal.
            if (created || File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Near(double? a, double? b) =>
        a is double left && b is double right &&
        Math.Abs(left - right) < 0.0001;

    private static bool SameGps(ImageExifData left, ImageExifData right) =>
        (left.GpsLatitude is null && right.GpsLatitude is null ||
            Near(left.GpsLatitude, right.GpsLatitude)) &&
        (left.GpsLongitude is null && right.GpsLongitude is null ||
            Near(left.GpsLongitude, right.GpsLongitude));

    private static bool SameUnchangedExif(string left, string right, bool ignoreGps)
    {
        using var a = JsonDocument.Parse(left);
        using var b = JsonDocument.Parse(right);
        static Dictionary<string, string> Fields(JsonDocument doc, bool removeGps)
        {
            var item = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement[0] : doc.RootElement;
            return item.EnumerateObject()
                .Where(p => !p.Name.Equals("SourceFile", StringComparison.Ordinal) &&
                    (!removeGps || !p.Name.Contains("GPS", StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(p => p.Name, p => p.Value.GetRawText(),
                    StringComparer.Ordinal);
        }
        var x = Fields(a, ignoreGps);
        var y = Fields(b, ignoreGps);
        foreach (var key in x.Keys.Union(y.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            x.TryGetValue(key, out var oldValue);
            y.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                LastFallbackDifference = key + ": " +
                    (oldValue ?? "(missing)") + " -> " +
                    (newValue ?? "(missing)");
                return false;
            }
        }
        LastFallbackDifference = null;
        return true;
    }

    private static bool SafeBasicJpeg(string path, out string reason)
    {
        // Strict, read-only guard. Every refusal supplies a precise category;
        // never relax protection merely because a source is on slow SMB.
        reason = "Unknown";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8)
        {
            reason = "MissingSOI";
            return false;
        }
        for (var n = 0; n < 512 && file.Position < 16 * 1024 * 1024; n++)
        {
            var markerPrefix = file.ReadByte();
            if (markerPrefix != 0xff)
            {
                reason = markerPrefix < 0 ? "TruncatedMarker" : "UnexpectedMarkerPrefix";
                return false;
            }
            int marker;
            do { marker = file.ReadByte(); }
            while (marker == 0xff);
            if (marker == 0xda)
            {
                reason = "Supported";
                return true;
            }
            if (marker <= 0 || marker is 0xd8 or 0xd9 ||
                marker is >= 0xd0 and <= 0xd7)
            {
                reason = "InvalidMarker:" + marker.ToString("X2");
                return false;
            }
            var hi = file.ReadByte();
            var lo = file.ReadByte();
            var size = (hi << 8) + lo - 2;
            if (hi < 0 || lo < 0 || size < 0 ||
                file.Position + size > file.Length)
            {
                reason = "InvalidSegment:" + marker.ToString("X2");
                return false;
            }
            if (marker is >= 0xe1 and <= 0xef)
            {
                // MPF/MPO, Extended XMP, JUMBF and gain maps can contain
                // cross-segment offsets. Do not rebase these without
                // explicitly proving the container format is safe.
                var count = Math.Min(size, 256);
                var sample = new byte[count];
                file.ReadExactly(sample);
                if (sample.AsSpan().StartsWith("MPF\0"u8))
                {
                    reason = "MPF";
                    return false;
                }
                if (sample.AsSpan().StartsWith(
                    "http://ns.adobe.com/xmp/extension/"u8))
                {
                    reason = "ExtendedXMP";
                    return false;
                }
                if (sample.AsSpan().StartsWith("JUMBF"u8))
                {
                    reason = "JUMBF";
                    return false;
                }
                if (sample.AsSpan().StartsWith("HDRGM"u8))
                {
                    reason = "HDRGainMap";
                    return false;
                }
                file.Position += size - count;
            }
            else file.Position += size;
        }
        reason = "HeaderLimitOrNoSOS";
        return false;
    }
}
