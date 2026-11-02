using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Isolated JPEG tag rewrite/benchmark prototype. It keeps the original
/// read-only and asks ExifTool to modify ONLY a tiny, local metadata carrier.
/// The compressed source JPEG bytes are transferred unchanged to a NEW copy.
/// This is not the production write path and makes no speed/offload claim.
/// </summary>
public static class JpegTagRewritePrototype
{
    private const long MaxHeaderSize = 16 * 1024 * 1024;
    private const int MaxMarkers = 512;

    public sealed record Result(TimeSpan MetadataTime, TimeSpan CopyTime,
        long SourceBytes, long OriginalMetadataBytes,
        long ResultMetadataBytes, bool ServerSideCopyUsed);

    /// <summary>
    /// The target must not exist. The original is never written to, deleted
    /// or renamed. Any failure deletes only the new target, not the source.
    /// Unsupported JPEG marker layouts are rejected before file creation.
    /// </summary>
    public static Result CreateTagCopy(string originalPath, string targetPath,
        IReadOnlyList<string> keywords, CancellationToken token = default)
        => CreateCopy(originalPath, targetPath, keywords, updateTags: true,
            updateGps: false, latitude: null, longitude: null, session: null, token);

    public static Result CreateTagCopy(string originalPath, string targetPath,
        IReadOnlyList<string> keywords, ExifToolKeywordWriter.Session session,
        CancellationToken token = default)
        => CreateCopy(originalPath, targetPath, keywords, updateTags: true,
            updateGps: false, latitude: null, longitude: null, session, token);

    // GPS-only must NOT normalize or change a source's existing XMP/IPTC
    // keywords as an incidental effect of a coordinate operation.
    public static Result CreateGpsCopy(string originalPath, string targetPath,
        double? latitude, double? longitude,
        ExifToolKeywordWriter.Session session, CancellationToken token = default)
        => CreateCopy(originalPath, targetPath, Array.Empty<string>(),
            updateTags: false, updateGps: true, latitude, longitude, session, token);

    /// <summary>
    /// Combined JPEG Tag/GPS proof of concept; supports setting AND clearing
    /// GPS coordinates, always on an entirely new output JPEG.
    /// A caller-owned ExifTool session can be reused across many images.
    /// </summary>
    public static Result CreateMetadataCopy(string originalPath, string targetPath,
        IReadOnlyList<string> keywords, double? latitude, double? longitude,
        ExifToolKeywordWriter.Session? session = null,
        CancellationToken token = default)
        => CreateCopy(originalPath, targetPath, keywords, updateTags: true,
            updateGps: true, latitude, longitude, session, token);

    private static Result CreateCopy(string originalPath, string targetPath,
        IReadOnlyList<string> keywords, bool updateTags, bool updateGps,
        double? latitude, double? longitude,
        ExifToolKeywordWriter.Session? session, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(keywords);
        token.ThrowIfCancellationRequested();
        if (keywords.Any(x => x.Contains('\r') || x.Contains('\n')))
            throw new ArgumentException("Invalid keyword.", nameof(keywords));
        if (!Path.GetExtension(originalPath).Equals(".jpg",
                StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(originalPath).Equals(".jpeg",
                StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only JPEG input is accepted.");
        if (string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase) || File.Exists(targetPath))
            throw new IOException("Prototype output must be a distinct, new path.");

        using var original = new FileStream(originalPath, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var originalLength = original.Length;
        var sourceTime = File.GetLastWriteTimeUtc(originalPath);
        var originalMetadata = ReadApplicationPrefix(original, token,
            out var originalTail);
        var carrierPath = Path.Combine(Path.GetTempPath(),
            "PCasa-tag-prototype-" + Guid.NewGuid().ToString("N") + ".jpg");
        var watch = Stopwatch.StartNew();
        try
        {
            // The carrier must be locally decodable and extremely small.
            // It only exists for this isolated benchmark. Never use a NAS
            // source as the metadata tool's working file.
            var tinyJpeg = MakeTinyJpeg();
            using (var carrier = new FileStream(carrierPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var tiny = new MemoryStream(tinyJpeg, writable: false))
            {
                var tinyMetadata = ReadApplicationPrefix(tiny, token,
                    out var tinyTail);
                // Prefer the original's APPn segments to the tiny carrier's.
                // This retains Exif/MakerNotes, ICC profiles, IPTC, XMP and
                // unknown APPn blocks while ExifTool changes only tag fields.
                carrier.Write(originalMetadata);
                tiny.Position = tinyTail;
                tiny.CopyTo(carrier);
            }

            // EXIF/IPTC/XMP semantics remain with the verified ExifTool
            // engine; only the small local metadata carrier is rewritten.
            if (session is null)
            {
                using var owned = new ExifToolKeywordWriter.Session();
                if (updateTags) owned.Write(carrierPath, keywords, token);
                if (updateGps)
                    owned.WriteGps(carrierPath, latitude, longitude, token);
            }
            else
            {
                if (updateTags) session.Write(carrierPath, keywords, token);
                if (updateGps)
                    session.WriteGps(carrierPath, latitude, longitude, token);
            }
            var metadataTime = watch.Elapsed;

            byte[] rewrittenMetadata;
            using (var carrier = new FileStream(carrierPath, FileMode.Open,
                FileAccess.Read, FileShare.Read))
                rewrittenMetadata = ReadApplicationPrefix(carrier, token, out _);

            // Copy all unchanged compressed image data exactly once. This
            // unoptimized baseline still transfers image bytes over SMB and
            // therefore is NOT assumed faster than the production writer.
            token.ThrowIfCancellationRequested();
            watch.Restart();
            var targetCreated = false;
            var usedServerSideCopy = false;
            try
            {
                using (var output = new FileStream(targetPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    targetCreated = true;
                    output.Write(rewrittenMetadata);
                    output.Flush();
                    var offloaded = JpegSmbCopyChunkPrototype.TryCopyUnchangedTail(
                        original, output, originalTail,
                        rewrittenMetadata.LongLength, originalLength - originalTail,
                        token);
                    if (!offloaded)
                    {
                        // Some SMB servers do not implement CopyChunk. A
                        // partially completed offload is never trusted:
                        // reset the NEW output before a regular byte copy.
                        output.SetLength(rewrittenMetadata.LongLength);
                        output.Position = rewrittenMetadata.LongLength;
                        original.Position = originalTail;
                        original.CopyTo(output, 1024 * 1024);
                    }
                    output.Flush(flushToDisk: true);
                    usedServerSideCopy = offloaded;
                }
                File.SetLastWriteTimeUtc(targetPath, sourceTime);
                if (new FileInfo(originalPath).Length != originalLength ||
                    File.GetLastWriteTimeUtc(originalPath) != sourceTime)
                    throw new IOException("Original changed during benchmark.");
                if (!JpegGpsPatchPrototype.CompressedImageHash(originalPath)
                    .SequenceEqual(JpegGpsPatchPrototype.CompressedImageHash(targetPath)))
                    throw new IOException("JPEG compressed image bytes changed.");
                return new Result(metadataTime, watch.Elapsed, originalLength,
                    originalMetadata.LongLength, rewrittenMetadata.LongLength,
                    usedServerSideCopy);
            }
            catch
            {
                if (targetCreated)
                {
                    try { File.Delete(targetPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        finally
        {
            try { File.Delete(carrierPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Header-only parser with a hard resource limit. Only ordinary initial
    // APP0..APP15 metadata groups are accepted; APP markers mixed into
    // later DQT/SOF/DHT scans are not handled by this prototype.
    private static byte[] ReadApplicationPrefix(Stream input,
        CancellationToken token, out long tailOffset)
    {
        input.Position = 0;
        if (input.ReadByte() != 0xff || input.ReadByte() != 0xd8)
            throw new InvalidDataException("Missing JPEG SOI.");
        tailOffset = -1;
        var seenNonApp = false;
        var foundSos = false;
        for (var i = 0; i < MaxMarkers && input.Position < MaxHeaderSize; i++)
        {
            token.ThrowIfCancellationRequested();
            var markerStart = input.Position;
            if (input.ReadByte() != 0xff)
                throw new InvalidDataException("Invalid JPEG marker.");
            var marker = input.ReadByte();
            while (marker == 0xff) marker = input.ReadByte();
            if (marker < 0 || marker == 0 || marker == 0xd8 || marker == 0xd9 ||
                marker == 0x01 || marker is >= 0xd0 and <= 0xd7)
                throw new InvalidDataException("Invalid JPEG header marker.");
            if (marker == 0xda)
            {
                foundSos = true;
                if (tailOffset == -1) tailOffset = markerStart;
                break;
            }
            var high = input.ReadByte();
            var low = input.ReadByte();
            if (high < 0 || low < 0)
                throw new InvalidDataException("Truncated JPEG marker.");
            var length = high * 256 + low;
            if (length < 2 || input.Position + length - 2 > input.Length ||
                input.Position + length - 2 > MaxHeaderSize)
                throw new InvalidDataException("Invalid JPEG segment length.");
            var isApp = marker is >= 0xe0 and <= 0xef;
            if (!isApp && !seenNonApp)
            {
                seenNonApp = true;
                tailOffset = markerStart;
            }
            else if (isApp && seenNonApp)
                throw new NotSupportedException(
                    "JPEG has interleaved APP metadata; use standard writer.");
            input.Position += length - 2;
        }
        if (!foundSos || tailOffset < 2)
            throw new NotSupportedException(
                "JPEG has an unsupported header or is truncated.");
        if (tailOffset > int.MaxValue)
            throw new InvalidDataException("JPEG header too large.");
        var prefix = new byte[(int)tailOffset];
        input.Position = 0;
        input.ReadExactly(prefix);
        return prefix;
    }

    private static byte[] MakeTinyJpeg()
    {
        // The tiny JPEG's own APP0/JFIF marker is skipped. The original
        // source APP headers replace it, so ExifTool sees original metadata.
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24,
            null, new byte[] { 0, 0, 0 }, 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
