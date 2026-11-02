using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// A deliberately narrow NAS optimization for two-image JPEG/MPF (including
/// ISO 21496 HDR gain maps). The whole original is downloaded ONCE to a
/// disposable LOCAL working JPEG. ExifTool edits that local file, not SMB.
/// Both MPF offsets and the complete encoded picture stream are checked
/// before a separately named remote candidate may atomically replace the
/// source. No second scheduler, no sidecars, no in-place header patches.
/// If this format cannot be proven safe, return false while the original
/// remains untouched and let the existing native writer handle it.
/// </summary>
public static class JpegMpfStagedWriter
{
    private static long _attempts;

    public static bool TryWrite(string path, ExifToolKeywordWriter.Session session,
        IReadOnlyList<string> keywords, bool updateTags,
        double? latitude, double? longitude, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var seq = Interlocked.Increment(ref _attempts);
        var local = Path.Combine(Path.GetTempPath(),
            "PCasa-mpf-" + Guid.NewGuid().ToString("N") + ".jpg");
        var remote = Path.Combine(Path.GetDirectoryName(path)!,
            ".pcasa-mpf-" + Guid.NewGuid().ToString("N") + ".jpg");
        var original = new FileInfo(path);
        var size = original.Length;
        var modified = original.LastWriteTimeUtc;
        var created = original.CreationTimeUtc;
        long download = 0, edit = 0, upload = 0, verify = 0;

        void Log(string outcome)
        {
            if (seq > 4 && seq % 25 != 0 && clock.ElapsedMilliseconds < 10000)
                return;
            PCasaDiagnostics.WriteMessage("JPEG.MPF.Staging",
                $"Mode={(updateTags ? "Tags" : "GPS")}; Result={outcome}; " +
                $"DownloadMs={download}; LocalEditMs={edit}; " +
                $"UploadMs={upload}; VerifyMs={verify}; " +
                $"TotalMs={clock.ElapsedMilliseconds}; " +
                $"File={Path.GetFileName(path)}");
        }

        try
        {
            token.ThrowIfCancellationRequested();
            File.Copy(path, local, overwrite: false);
            download = clock.ElapsedMilliseconds;

            if (!TryInspect(local, out var before))
            {
                Log("UnsupportedMPF");
                return false;
            }
            using var reader = new ImageExifReader();
            var originalExif = reader.Read(local, token);
            var originalTags = session.ReadSnapshot(local, token);
            var oldTailHash = JpegGpsPatchPrototype.CompressedImageHash(local);

            if (updateTags) session.Write(local, keywords, token);
            else session.WriteGps(local, latitude, longitude, token);
            edit = clock.ElapsedMilliseconds - download;

            if (!TryInspect(local, out var after) ||
                before.ImageCount != after.ImageCount ||
                before.SecondaryBytes != after.SecondaryBytes ||
                !before.SecondaryHash.SequenceEqual(after.SecondaryHash) ||
                !before.Iso21496Hash.SequenceEqual(after.Iso21496Hash) ||
                !before.ProtectedAppHash.SequenceEqual(after.ProtectedAppHash) ||
                !oldTailHash.SequenceEqual(
                    JpegGpsPatchPrototype.CompressedImageHash(local)))
            {
                Log("PictureOrMpfMismatch");
                return false;
            }

            // The same metadata contracts as ordinary fast JPEG editing.
            var newExif = reader.Read(local, token);
            var newTags = session.ReadSnapshot(local, token);
            var expected = updateTags ? keywords.ToHashSet(
                StringComparer.OrdinalIgnoreCase) :
                originalTags.Combined.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!newTags.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(updateTags ? expected : originalTags.Xmp.ToHashSet(
                        StringComparer.OrdinalIgnoreCase)) ||
                !newTags.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(updateTags ? expected : originalTags.Iptc.ToHashSet(
                        StringComparer.OrdinalIgnoreCase)) ||
                !newTags.Combined.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(expected) ||
                (updateTags
                    ? !JpegMetadataOriginalWriter.SameGps(originalExif, newExif)
                    : (latitude.HasValue
                        ? !JpegMetadataOriginalWriter.Near(newExif.GpsLatitude, latitude) ||
                          !JpegMetadataOriginalWriter.Near(newExif.GpsLongitude, longitude)
                        : newExif.GpsLatitude is not null ||
                          newExif.GpsLongitude is not null)) ||
                !JpegMetadataOriginalWriter.SameUnchangedExif(
                    originalExif.Json, newExif.Json, ignoreGps: !updateTags))
            {
                Log("MetadataMismatch");
                return false;
            }

            // The remote candidate is in the same directory as the source.
            // Never overwrite the source with a streaming copy.
            token.ThrowIfCancellationRequested();
            File.Copy(local, remote, overwrite: false);
            upload = clock.ElapsedMilliseconds - download - edit;

            // Independently verify bytes that reached the NAS.
            // The original source remains untouched up to the final commit.
            var localHash = HashFile(local);
            var remoteHash = HashFile(remote);
            if (!localHash.SequenceEqual(remoteHash))
                throw new IOException("Remote MPF candidate differs from verified local JPEG.");
            var current = new FileInfo(path);
            if (current.Length != size || current.LastWriteTimeUtc != modified)
                throw new IOException("MPF original changed during local metadata edit.");
            token.ThrowIfCancellationRequested(); // final safe cancellation point
            File.SetCreationTimeUtc(remote, created);
            File.SetLastWriteTimeUtc(remote, modified);
            verify = clock.ElapsedMilliseconds - download - edit - upload;

            File.Replace(remote, path, destinationBackupFileName: null);
            if (File.GetLastWriteTimeUtc(path) != modified)
                File.SetLastWriteTimeUtc(path, modified);
            Log("VerifiedMpfStage");
            return true;
        }
        finally
        {
            // Local edits and failed NAS candidates are disposable;
            // the original is never deleted or opened for modification.
            try { if (File.Exists(local)) File.Delete(local); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { if (File.Exists(remote)) File.Delete(remote); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        return SHA256.HashData(stream);
    }

    private sealed record MpfSnapshot(int ImageCount, int SecondaryBytes,
        byte[] SecondaryHash, byte[] Iso21496Hash, byte[] ProtectedAppHash);

    private static bool TryInspect(string path, out MpfSnapshot snapshot)
    {
        snapshot = null!;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8)
            return false;
        int imageCount = 0, secondSize = 0;
        long secondStart = -1;
        byte[]? isoHash = null;
        var protectedApp = new List<string>();
        var foundMpf = false;
        var foundSos = false;
        for (var n = 0; n < 512 && file.Position < 16 * 1024 * 1024; n++)
        {
            if (file.ReadByte() != 0xff) return false;
            int marker;
            do { marker = file.ReadByte(); } while (marker == 0xff);
            if (marker == 0xda) { foundSos = true; break; }
            if (marker <= 0 || marker is 0xd8 or 0xd9 ||
                marker is >= 0xd0 and <= 0xd7) return false;
            var hi = file.ReadByte();
            var lo = file.ReadByte();
            var size = (hi << 8) + lo - 2;
            if (hi < 0 || lo < 0 || size < 0 ||
                file.Position + size > file.Length) return false;
            if (marker is >= 0xe0 and <= 0xef)
            {
                var at = file.Position;
                var chunk = new byte[size];
                file.ReadExactly(chunk);
                var isMpf = marker == 0xe2 &&
                    chunk.AsSpan().StartsWith("MPF\0"u8);
                var isIso21496 = marker == 0xe2 &&
                    chunk.AsSpan().StartsWith(
                        "urn:iso:std:iso:ts:21496:"u8);
                if (isMpf)
                {
                    if (foundMpf ||
                        !TryDecodeMpf(chunk, at + 4, out imageCount,
                            out secondSize, out secondStart))
                        return false;
                    foundMpf = true;
                }
                else if (isIso21496)
                {
                    if (isoHash is not null) return false; // narrow profile only
                    isoHash = SHA256.HashData(chunk);
                }
                else
                {
                    // Exif/XMP APP1 and Photoshop/IPTC APP13 may change.
                    // Preserve everything else verbatim, including ICC
                    // profiles, Samsung APP4 and unfamiliar camera blocks.
                    var editable = marker == 0xe1 &&
                        (chunk.AsSpan().StartsWith("Exif\0\0"u8) ||
                         chunk.AsSpan().StartsWith(
                             "http://ns.adobe.com/xap/1.0/"u8)) ||
                        marker == 0xed &&
                        chunk.AsSpan().StartsWith("Photoshop 3.0\0"u8);
                    if (!editable)
                    {
                        var tagged = new byte[chunk.Length + 1];
                        tagged[0] = (byte)marker;
                        chunk.CopyTo(tagged.AsSpan(1));
                        protectedApp.Add(Convert.ToHexString(
                            SHA256.HashData(tagged)));
                    }
                }
            }
            else file.Position += size;
        }
        if (!foundSos || !foundMpf || imageCount != 2 ||
            secondSize < 10 || secondStart < file.Position ||
            secondStart > file.Length - secondSize)
            return false;
        file.Position = secondStart;
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8)
            return false;
        file.Position = secondStart + secondSize - 2;
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd9)
            return false;
        file.Position = secondStart;
        using var limited = new BoundedReadStream(file, secondSize);
        protectedApp.Sort(StringComparer.Ordinal);
        snapshot = new MpfSnapshot(imageCount, secondSize,
            SHA256.HashData(limited),
            isoHash ?? Array.Empty<byte>(),
            SHA256.HashData(Encoding.ASCII.GetBytes(
                string.Join("|", protectedApp))));
        return true;
    }

    private static bool TryDecodeMpf(byte[] data, long tiffStart,
        out int count, out int secondSize, out long secondStart)
    {
        count = 0;
        secondSize = 0;
        secondStart = -1;
        if (data.Length < 4 + 8 + 2 + 3 * 12 + 4 + 32 ||
            !data.AsSpan().StartsWith("MPF\0"u8)) return false;
        var tiff = data.AsSpan(4);
        var little = tiff.StartsWith("II"u8);
        if (!little && !tiff.StartsWith("MM"u8)) return false;
        ushort U16(ReadOnlySpan<byte> b) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(b)
            : BinaryPrimitives.ReadUInt16BigEndian(b);
        uint U32(ReadOnlySpan<byte> b) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(b)
            : BinaryPrimitives.ReadUInt32BigEndian(b);
        if (U16(tiff.Slice(2, 2)) != 42) return false;
        var offset = U32(tiff.Slice(4, 4));
        if (offset > tiff.Length - 2) return false;
        var entries = U16(tiff.Slice((int)offset, 2));
        if (entries is < 3 or > 32 ||
            offset + 2 + entries * 12 + 4 > tiff.Length) return false;
        uint mpEntryOffset = 0;
        var mpEntryBytes = 0;
        for (var j = 0; j < entries; j++)
        {
            var e = tiff.Slice((int)offset + 2 + j * 12, 12);
            var tag = U16(e.Slice(0, 2));
            if (tag == 0xb001)
            {
                if (U16(e.Slice(2, 2)) != 4 ||
                    U32(e.Slice(4, 4)) != 1) return false;
                count = checked((int)U32(e.Slice(8, 4)));
            }
            if (tag == 0xb002)
            {
                if (U16(e.Slice(2, 2)) != 7) return false;
                mpEntryBytes = checked((int)U32(e.Slice(4, 4)));
                mpEntryOffset = U32(e.Slice(8, 4));
            }
        }
        if (count != 2 || mpEntryBytes != 32 ||
            mpEntryOffset > tiff.Length - 32) return false;
        var mp = tiff.Slice((int)mpEntryOffset, 32);
        var primary = U32(mp.Slice(4, 4));
        var embedded = U32(mp.Slice(16 + 4, 4));
        var relativeOffset = U32(mp.Slice(16 + 8, 4));
        if (primary == 0 || embedded == 0 || relativeOffset == 0 ||
            embedded > int.MaxValue) return false;
        secondSize = (int)embedded;
        secondStart = tiffStart + relativeOffset;
        return true;
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _left;
        public BoundedReadStream(Stream inner, long bytes) =>
            (_inner, _left) = (inner, bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }
        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= n;
            return n;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
