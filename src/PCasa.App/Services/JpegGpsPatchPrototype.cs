using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PCasa.App.Services;

/// <summary>
/// Isolated proof of concept only: replace existing JPEG EXIF GPS rational
/// values in a COPY without relocating any JPEG segment or changing pixels.
/// Never call this from the production Tag/GPS writer. Unsupported JPEGs
/// must continue to use the existing ExifTool path.
/// </summary>
public static class JpegGpsPatchPrototype
{
    private const int MaxHeaderBytes = 16 * 1024 * 1024;
    private const int MaxSegments = 512;
    private const ushort GpsInfoTag = 0x8825;
    private static readonly byte[] ExifSignature = [69, 120, 105, 102, 0, 0];
    private static readonly byte[] XmpSignature =
        System.Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    private sealed record Patch(long PayloadOffset, long Length,
        long WriteTicks, byte[] Payload);

    /// <summary>
    /// Returns false without creating an output when the fast path cannot
    /// prove safety. The original is NEVER opened for writing or replaced.
    /// The output must be a new path; failed copies are cleaned up.
    /// </summary>
    public static bool TryCreateGpsReplacementCopy(string originalPath,
        string outputPath, double latitude, double longitude, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) || longitude is < -180 or > 180)
        {
            reason = "Invalid GPS coordinates.";
            return false;
        }
        if (!Path.GetExtension(originalPath).Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(originalPath).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Only JPEG is supported.";
            return false;
        }
        if (string.Equals(Path.GetFullPath(originalPath), Path.GetFullPath(outputPath),
            StringComparison.OrdinalIgnoreCase) || File.Exists(outputPath))
        {
            reason = "Output must be a different, nonexistent file.";
            return false;
        }

        if (!TryPreparePatch(originalPath, latitude, longitude,
            out var patch, out reason))
            return false;

        var created = false;
        try
        {
            // Windows may offload this copy on capable SMB servers. We NEVER
            // assume it does: the benchmark includes this full operation.
            File.Copy(originalPath, outputPath, overwrite: false);
            created = true;
            using (var target = new FileStream(outputPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (target.Length != patch.Length)
                    throw new IOException("Cloned JPEG length changed.");
                target.Position = patch.PayloadOffset;
                target.Write(patch.Payload);
                target.Flush(flushToDisk: true);
            }
            // Preserve timestamp, matching production metadata-only writes.
            File.SetLastWriteTimeUtc(outputPath,
                new DateTime(patch.WriteTicks, DateTimeKind.Utc));
            var source = new FileInfo(originalPath);
            if (source.Length != patch.Length ||
                source.LastWriteTimeUtc.Ticks != patch.WriteTicks)
                throw new IOException("Original JPEG changed during prototype copy.");
            reason = "Patched a same-size EXIF GPS segment on a separate copy.";
            return true;
        }
        catch
        {
            if (created)
            {
                try { File.Delete(outputPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>SHA-256 of the raw SOS marker through end-of-file. Unlike a
    /// decoded-pixel comparison, this proves compressed image data is identical.</summary>
    public static byte[] CompressedImageHash(string jpegPath)
    {
        using var input = new FileStream(jpegPath, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        if (!TryFindExif(input, out _, out _, out var sosOffset, out var reason))
            throw new InvalidDataException(reason);
        input.Position = sosOffset;
        return SHA256.HashData(input);
    }

    private static bool TryPreparePatch(string path, double latitude, double longitude,
        out Patch patch, out string reason)
    {
        patch = null!;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        var length = input.Length;
        var writeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        if (!TryFindExif(input, out var payload, out var payloadOffset, out _,
            out reason))
            return false;
        if (payload is null)
        {
            reason = "JPEG has no existing EXIF APP1 segment.";
            return false;
        }
        if (!TryChangeExistingGps(payload, latitude, longitude, out reason))
            return false;
        patch = new Patch(payloadOffset, length, writeTicks, payload);
        return true;
    }

    private static bool TryFindExif(FileStream input, out byte[]? exif,
        out long exifOffset, out long sosOffset, out string reason)
    {
        exif = null;
        exifOffset = 0;
        sosOffset = 0;
        reason = "Unsupported or malformed JPEG.";
        if (input.ReadByte() != 0xff || input.ReadByte() != 0xd8)
            return false;
        for (var i = 0; i < MaxSegments &&
            input.Position < MaxHeaderBytes && input.Position < input.Length; i++)
        {
            var markerStart = input.Position;
            if (input.ReadByte() != 0xff) return false;
            int marker;
            do { marker = input.ReadByte(); }
            while (marker == 0xff && input.Position < MaxHeaderBytes);
            if (marker < 0 || marker is 0 or 0xd8 or 0xd9 or 0x01 ||
                marker is >= 0xd0 and <= 0xd7) return false;
            if (marker == 0xda)
            {
                sosOffset = markerStart;
                reason = "";
                return true;
            }
            var high = input.ReadByte();
            var low = input.ReadByte();
            if (high < 0 || low < 0) return false;
            var segmentSize = (high << 8) | low;
            if (segmentSize < 2 || input.Position + segmentSize - 2 > input.Length ||
                input.Position + segmentSize - 2 > MaxHeaderBytes) return false;
            var payloadOffset = input.Position;
            var payloadSize = segmentSize - 2;
            if (marker != 0xe1)
            {
                input.Position += payloadSize;
                continue;
            }
            var segment = new byte[payloadSize];
            input.ReadExactly(segment);
            if (segment.AsSpan().StartsWith(ExifSignature))
            {
                if (exif is not null)
                {
                    reason = "Multiple EXIF segments; unsafe to patch.";
                    return false;
                }
                exif = segment;
                exifOffset = payloadOffset;
            }
            else if (segment.AsSpan().StartsWith(XmpSignature))
            {
                // ExifTool's GPS writer removes stale XMP GPS to guarantee
                // one position. This narrow prototype refuses conflicting
                // XMP instead of silently producing two GPS positions.
                var text = System.Text.Encoding.UTF8.GetString(segment);
                if (text.Contains("GPSLatitude", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("GPSLongitude", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "XMP GPS exists; requires ExifTool normalization.";
                    return false;
                }
            }
        }
        return false;
    }

    private static bool TryChangeExistingGps(byte[] exif, double lat, double lon,
        out string reason)
    {
        reason = "EXIF GPS IFD is missing, malformed, or has unsupported types.";
        var t = 6; // TIFF starts after "Exif\0\0".
        if (exif.Length < 14) return false;
        var little = exif[t] == (byte)'I' && exif[t + 1] == (byte)'I';
        var big = exif[t] == (byte)'M' && exif[t + 1] == (byte)'M';
        if (!little && !big) return false;
        bool Valid(long offset, int count) =>
            offset >= t && count >= 0 && offset + count <= exif.Length;
        ushort U16(int at) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(exif.AsSpan(at, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(exif.AsSpan(at, 2));
        uint U32(int at) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(exif.AsSpan(at, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(exif.AsSpan(at, 4));
        void Put32(int at, uint value)
        {
            if (little) BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(at, 4), value);
            else BinaryPrimitives.WriteUInt32BigEndian(exif.AsSpan(at, 4), value);
        }
        if (U16(t + 2) != 42) return false;

        bool ReadEntries(uint relative, out Dictionary<ushort, int> entries)
        {
            entries = new Dictionary<ushort, int>();
            var start = t + (long)relative;
            if (!Valid(start, 2)) return false;
            var count = U16((int)start);
            if (count > 256 || !Valid(start + 2, count * 12 + 4)) return false;
            for (var i = 0; i < count; i++)
            {
                var entry = (int)(start + 2 + i * 12);
                if (!entries.TryAdd(U16(entry), entry))
                    return false; // duplicate tags are ambiguous
            }
            return true;
        }

        if (!ReadEntries(U32(t + 4), out var ifd0) ||
            !ifd0.TryGetValue(GpsInfoTag, out var gpsPointer) ||
            U16(gpsPointer + 2) != 4 || U32(gpsPointer + 4) != 1 ||
            !ReadEntries(U32(gpsPointer + 8), out var gps))
            return false;

        bool Ref(ushort tag, out int at)
        {
            at = 0;
            if (!gps.TryGetValue(tag, out var e) ||
                U16(e + 2) != 2 || U32(e + 4) != 2) return false;
            at = e + 8; // ASCII count=2 lives inline
            return Valid(at, 2) && exif[at + 1] == 0 &&
                exif[at] is (byte)'N' or (byte)'S' or (byte)'E' or (byte)'W';
        }
        bool Rational(ushort tag, out int at)
        {
            at = 0;
            if (!gps.TryGetValue(tag, out var e) ||
                U16(e + 2) != 5 || U32(e + 4) != 3)
                return false;
            var offset = t + (long)U32(e + 8);
            if (!Valid(offset, 24)) return false;
            at = (int)offset;
            for (var j = 0; j < 3; j++)
                if (U32(at + j * 8 + 4) == 0) return false;
            return true;
        }
        if (!Ref(1, out var latRef) || !Ref(3, out var lonRef) ||
            !Rational(2, out var latPos) || !Rational(4, out var lonPos) ||
            Math.Abs(latPos - lonPos) < 24 ||
            latRef >= latPos && latRef < latPos + 24 ||
            latRef >= lonPos && latRef < lonPos + 24 ||
            lonRef >= latPos && lonRef < latPos + 24 ||
            lonRef >= lonPos && lonRef < lonPos + 24)
            return false;

        // Six fixed-width unsigned rationals; no insertions, relocations,
        // MakerNotes rewrites, changed JPEG header length or pixel changes.
        // Round to microseconds of arc before splitting so 59.99999999s
        // carries correctly into the next minute or degree.
        void WriteCoordinate(int at, double degrees)
        {
            const long unitsPerSecond = 1_000_000;
            var total = checked((long)Math.Round(Math.Abs(degrees) * 3600d *
                unitsPerSecond, MidpointRounding.AwayFromZero));
            var wholeDegrees = total / (3600 * unitsPerSecond);
            var minutes = total / (60 * unitsPerSecond) % 60;
            var remainder = total % (60 * unitsPerSecond);
            Put32(at, checked((uint)wholeDegrees));
            Put32(at + 4, 1);
            Put32(at + 8, checked((uint)minutes));
            Put32(at + 12, 1);
            Put32(at + 16, checked((uint)remainder));
            Put32(at + 20, unitsPerSecond);
        }

        exif[latRef] = (byte)(lat < 0 ? 'S' : 'N');
        exif[lonRef] = (byte)(lon < 0 ? 'W' : 'E');
        WriteCoordinate(latPos, lat);
        WriteCoordinate(lonPos, lon);
        reason = "";
        return true;
    }
}
