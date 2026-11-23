using System.Buffers.Binary;
using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Lossless, length-preserving repair of EXIF ASCII date values in an
/// already staged JPEG. Some scanners write non-canonical EXIF date strings
/// such as yyyy/MM/dd. ExifTool normalizes them while copying tags, so
/// writing the string back through ExifTool still cannot reproduce the
/// original bytes. Never alter the source, JPEG pixels, TIFF offsets or
/// metadata other than the nine specifically protected EXIF date values.
/// The caller must perform full ExifTool verification after this repair.
/// </summary>
internal static class JpegExifVerbatimDateRestorer
{
    private static readonly (ushort Id, bool ExifIfd, string Name)[] DateTags =
    [
        (0x0132, false, "EXIF:ModifyDate"),
        (0x9003, true, "EXIF:DateTimeOriginal"),
        (0x9004, true, "EXIF:CreateDate"),
        (0x9290, true, "EXIF:SubSecTime"),
        (0x9291, true, "EXIF:SubSecTimeOriginal"),
        (0x9292, true, "EXIF:SubSecTimeDigitized"),
        (0x9010, true, "EXIF:OffsetTime"),
        (0x9011, true, "EXIF:OffsetTimeOriginal"),
        (0x9012, true, "EXIF:OffsetTimeDigitized")
    ];

    private sealed record Segment(long FileOffset, byte[] Bytes,
        bool LittleEndian, int TiffBase = 6);

    private readonly record struct Entry(int ValuePosition, int Count);

    internal static IReadOnlyList<string> Restore(string original, string staged)
    {
        if (!IsJpeg(original) || !IsJpeg(staged) ||
            !TryOpenExif(original, out var source) ||
            !TryOpenExif(staged, out var candidate))
            return [];

        var repaired = new List<string>();
        foreach (var (tag, exifIfd, name) in DateTags)
        {
            if (!TryReadAscii(source, tag, exifIfd, out var from) ||
                !TryReadAscii(candidate, tag, exifIfd, out var to) ||
                from.Count != to.Count)
                continue;

            var sourceValue = source.Bytes.AsSpan(from.ValuePosition, from.Count);
            var candidateValue = candidate.Bytes.AsSpan(to.ValuePosition, to.Count);
            if (sourceValue.SequenceEqual(candidateValue)) continue;
            // This copies the original ASCII bytes including any unconventional
            // date separators and the terminator. It never invokes ExifTool's
            // date parser, normalizer or value formatter.
            sourceValue.CopyTo(candidateValue);
            repaired.Add(name);
        }
        if (repaired.Count == 0) return [];

        // The staged photo is a private, UUID-named file and is not visible
        // to the gallery until this method and strict verification succeed.
        // JPEG APP1/TIFF sizes, offsets, pixel payload and byte order stay
        // identical. Never write to the original photograph.
        using var target = new FileStream(staged, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        target.Position = candidate.FileOffset;
        target.Write(candidate.Bytes);
        target.Flush(flushToDisk: true);
        return repaired;
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe";

    private static bool TryOpenExif(string path, out Segment segment)
    {
        segment = null!;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) return false;
        while (true)
        {
            if (stream.ReadByte() != 0xff) return false;
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda) return false;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
            var high = stream.ReadByte();
            var low = stream.ReadByte();
            if (high < 0 || low < 0) return false;
            var length = (high << 8) | low;
            if (length < 2) return false;
            var count = length - 2;
            var position = stream.Position;
            if (marker != 0xe1 || count < 14)
            {
                if (stream.Length - stream.Position < count) return false;
                stream.Position += count;
                continue;
            }

            var bytes = new byte[count];
            stream.ReadExactly(bytes);
            if (bytes[0] != (byte)'E' || bytes[1] != (byte)'x' ||
                bytes[2] != (byte)'i' || bytes[3] != (byte)'f' ||
                bytes[4] != 0 || bytes[5] != 0)
                continue;

            var little = bytes[6] == (byte)'I' && bytes[7] == (byte)'I';
            var big = bytes[6] == (byte)'M' && bytes[7] == (byte)'M';
            if (!little && !big) return false;
            var probe = new Segment(position, bytes, little);
            if (U16(probe, 8) != 42) return false;
            segment = probe;
            return true;
        }
    }

    private static bool TryReadAscii(Segment segment, ushort tag,
        bool exifIfd, out Entry value)
    {
        value = default;
        if (segment.Bytes.Length < 14) return false;
        var root = U32(segment, 10);
        if (root > 65535) return false;
        var ifd = (int)root + segment.TiffBase;
        if (exifIfd)
        {
            if (!TryFind(segment, ifd, 0x8769, out var pointer) ||
                U16(segment, pointer) != 0x8769 ||
                U16(segment, pointer + 2) != 4 ||
                U32(segment, pointer + 4) != 1)
                return false;
            var exifOffset = U32(segment, pointer + 8);
            if (exifOffset > 65535) return false;
            ifd = (int)exifOffset + segment.TiffBase;
        }
        if (!TryFind(segment, ifd, tag, out var entry) ||
            U16(segment, entry + 2) != 2)
            return false;
        var count = U32(segment, entry + 4);
        if (count == 0 || count > 1024) return false;
        var data = count <= 4
            ? entry + 8
            : (long)segment.TiffBase + U32(segment, entry + 8);
        if (!Within(segment, data, count)) return false;
        value = new Entry((int)data, (int)count);
        return true;
    }

    private static bool TryFind(Segment s, int ifd, ushort tag, out int entry)
    {
        entry = 0;
        if (!Within(s, ifd, 2)) return false;
        var count = U16(s, ifd);
        if (count > 1024 || !Within(s, (long)ifd + 2, (long)count * 12))
            return false;
        for (var index = 0; index < count; index++)
        {
            var offset = ifd + 2 + index * 12;
            if (U16(s, offset) != tag) continue;
            entry = offset;
            return true;
        }
        return false;
    }

    private static bool Within(Segment s, long offset, long length) =>
        offset >= 0 && length >= 0 &&
        offset <= s.Bytes.Length && length <= s.Bytes.Length - offset;

    private static ushort U16(Segment s, int position)
    {
        if (!Within(s, position, 2)) return 0;
        var bytes = s.Bytes.AsSpan(position, 2);
        return s.LittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static uint U32(Segment s, int position)
    {
        if (!Within(s, position, 4)) return 0;
        var bytes = s.Bytes.AsSpan(position, 4);
        return s.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
