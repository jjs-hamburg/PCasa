using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Render the MERGED image-data section of a layered Photoshop document,
/// never a layer, tiny EXIF thumbnail, or a libvips/ImageMagick first page.
/// It is strictly a read-only preview: the layered PSD stays byte-for-byte
/// unchanged. Current decoder supports RGB/grayscale 8/16-bit PSD with
/// raw/PackBits/ZIP compression, and 8-bit ZIP prediction.
/// </summary>
public static class PsdCompositePreview
{
    private const int MaximumPreviewSide = 4096;
    private static string PreviewRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "psd-composites");

    public static string Resolve(string psd, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(psd);
        if (!info.Exists) throw new FileNotFoundException("PSD nicht gefunden.", psd);
        var signature = Path.GetFullPath(psd) + "\n" + info.Length + "\n" +
            info.LastWriteTimeUtc.Ticks + "\nmerged-v1";
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        var destination = Path.Combine(PreviewRoot, key[..2], key + ".png");
        using var lease = SourceFileLocks.Enter(psd, token);
        if (File.Exists(destination) && new FileInfo(destination).Length > 64)
            return destination;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = new FileStream(psd, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
                       FileOptions.SequentialScan))
            {
                var image = DecodeComposite(input, token);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                encoder.Save(output);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>PSD source dimensions; never substitute downsampled preview dimensions.</summary>
    public static (int Width, int Height) GetDimensions(string psd)
    {
        using var source = new FileStream(psd, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[26];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8) ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2)) != 1)
            throw new InvalidDataException("PSD-Signatur oder Version ungültig.");
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(14, 4)));
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(18, 4)));
        if (width < 1 || height < 1 || width > 300000 || height > 300000)
            throw new InvalidDataException("Ungültige PSD-Abmessungen.");
        return (width, height);
    }

    /// <summary>Pure PSD merged-composite decoder; exposed to regression tests.</summary>
    public static BitmapSource DecodeComposite(Stream input,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[26];
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8) ||
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2)) != 1)
            throw new InvalidDataException("PSD-Signatur oder Version ungültig.");
        var channels = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(12, 2));
        var sourceHeight = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            header.Slice(14, 4)));
        var sourceWidth = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            header.Slice(18, 4)));
        var bitDepth = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(22, 2));
        var colorMode = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(24, 2));
        if (sourceWidth < 1 || sourceHeight < 1 ||
            sourceWidth > 300000 || sourceHeight > 300000 || channels is < 1 or > 56)
            throw new InvalidDataException("Ungültige PSD-Abmessungen/Kanäle.");
        if ((colorMode != 3 || channels < 3) &&
            (colorMode != 1 || channels < 1))
            throw new NotSupportedException(
                "Für diese PSD-Farbart ist keine verlässliche Gesamtvorschau verfügbar.");
        if (bitDepth is not (8 or 16))
            throw new NotSupportedException(
                "Diese PSD-Bittiefe wird für die Gesamtvorschau nicht unterstützt.");

        SkipSection(input); // Color mode data
        SkipSection(input); // Image resources
        SkipSection(input); // Layer & mask information
        var compression = ReadU16(input);
        if (compression > 3)
            throw new NotSupportedException(
                "Die PSD-Kompression der Gesamtvorschau wird nicht unterstützt.");

        var scale = Math.Min(1.0, (double)MaximumPreviewSide /
            Math.Max(sourceWidth, sourceHeight));
        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bytesPerSample = bitDepth / 8;
        var rowSize = checked(sourceWidth * bytesPerSample);
        var row = new byte[rowSize];

        // The merged composite is PLANAR, not interleaved. Each entire R
        // channel is followed by the G and B channels, then optional alpha.
        // Process at most four channels and downsample while reading rows;
        // large layered PSDs never allocate the original full-size bitmap.
        int[]? rleRowSizes = null;
        if (compression == 1)
        {
            rleRowSizes = new int[checked(sourceHeight * channels)];
            for (var i = 0; i < rleRowSizes.Length; i++)
                rleRowSizes[i] = ReadU16(input);
        }

        using var zipped = compression is 2 or 3
            ? new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true)
            : null;
        var source = (Stream?)zipped ?? input;
        var count = Math.Min(channels, colorMode == 3 ? 4 : 2);
        for (var channel = 0; channel < count; channel++)
        {
            var nextOutputY = 0;
            for (var y = 0; y < sourceHeight; y++)
            {
                if ((y & 0x7F) == 0) token.ThrowIfCancellationRequested();
                switch (compression)
                {
                    case 0:
                    case 2:
                    case 3:
                        source.ReadExactly(row);
                        if (compression == 3)
                        {
                            // Photoshop 8-bit prediction uses byte deltas;
                            // 16-bit prediction uses big-endian SAMPLE
                            // deltas, not byte-wise deltas.
                            if (bitDepth == 8)
                            {
                                for (var x = 1; x < row.Length; x++)
                                    row[x] = unchecked((byte)(row[x] + row[x - 1]));
                            }
                            else
                            {
                                for (var x = 2; x < row.Length; x += 2)
                                {
                                    var prior = BinaryPrimitives.ReadUInt16BigEndian(
                                        row.AsSpan(x - 2, 2));
                                    var delta = BinaryPrimitives.ReadUInt16BigEndian(
                                        row.AsSpan(x, 2));
                                    BinaryPrimitives.WriteUInt16BigEndian(
                                        row.AsSpan(x, 2),
                                        unchecked((ushort)(prior + delta)));
                                }
                            }
                        }
                        break;
                    case 1:
                        DecodePackBitsRow(source, row,
                            rleRowSizes![channel * sourceHeight + y]);
                        break;
                }
                if (nextOutputY >= height ||
                    y != SamplePosition(nextOutputY, sourceHeight, height))
                    continue;
                var offset = nextOutputY * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var sample = row[SamplePosition(x, sourceWidth, width) *
                        bytesPerSample]; // 16-bit PSD: most-significant byte
                    var outputOffset = offset + x * 4;
                    if (colorMode == 1 && channel == 0)
                        pixels[outputOffset] = pixels[outputOffset + 1] =
                            pixels[outputOffset + 2] = sample;
                    else if (colorMode == 3 && channel <= 2)
                        pixels[outputOffset + 2 - channel] = sample;
                    else if ((colorMode == 3 && channel == 3) ||
                             (colorMode == 1 && channel == 1))
                        pixels[outputOffset + 3] = sample;
                }
                nextOutputY++;
            }
        }
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }

    private static int SamplePosition(int destination, int sourceSize, int outputSize)
        => Math.Min(sourceSize - 1,
            (int)(((long)destination * 2 + 1) * sourceSize /
                ((long)outputSize * 2)));

    private static ushort ReadU16(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[2];
        stream.ReadExactly(bytes);
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    private static void SkipSection(Stream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        stream.ReadExactly(bytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (length > stream.Length - stream.Position)
            throw new InvalidDataException("PSD enthält einen ungültigen Abschnitt.");
        stream.Seek(length, SeekOrigin.Current);
    }

    private static void DecodePackBitsRow(Stream stream, byte[] destination,
        int compressedSize)
    {
        if (compressedSize == 0 || compressedSize > 65535)
            throw new InvalidDataException("PSD enthält eine ungültige RLE-Zeile.");
        var encoded = new byte[compressedSize];
        stream.ReadExactly(encoded);
        var read = 0;
        var write = 0;
        while (read < encoded.Length)
        {
            var n = unchecked((sbyte)encoded[read++]);
            if (n >= 0)
            {
                var count = n + 1;
                if (read + count > encoded.Length ||
                    write + count > destination.Length)
                    throw new InvalidDataException("PSD-RLE-Literal beschädigt.");
                Array.Copy(encoded, read, destination, write, count);
                read += count;
                write += count;
            }
            else if (n != -128)
            {
                var count = 1 - n;
                if (read >= encoded.Length || write + count > destination.Length)
                    throw new InvalidDataException("PSD-RLE-Lauf beschädigt.");
                Array.Fill(destination, encoded[read++], write, count);
                write += count;
            }
        }
        if (write != destination.Length)
            throw new InvalidDataException("PSD-RLE-Zeile ist unvollständig.");
    }
}
