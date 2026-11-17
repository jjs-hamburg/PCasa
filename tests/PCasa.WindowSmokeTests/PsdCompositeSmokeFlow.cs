using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class PsdCompositeSmokeFlow
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "pcasa-psd-merged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var method in new ushort[] { 0, 1, 2, 3 })
            {
                var psd = Path.Combine(root, "merged-" + method + ".psd");
                WriteRgbPsd(psd, method, 8, 8, 8, [210, 60, 30]);
                var original = File.ReadAllBytes(psd);
                var decoded = PsdCompositePreview.DecodeComposite(
                    new MemoryStream(original));
                CheckColor(decoded, 210, 60, 30);
                var preview = RawImageSource.Resolve(psd);
                if (!preview.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(preview) ||
                    !File.ReadAllBytes(psd).SequenceEqual(original))
                    throw new Exception("Merged PSD preview did not preserve the layered original.");
                using var input = File.OpenRead(preview);
                var png = BitmapDecoder.Create(input,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
                CheckColor(png, 210, 60, 30);
                if (PsdCompositePreview.GetDimensions(psd) != (8, 8))
                    throw new Exception("PSD dimensions were taken from a thumbnail.");
            }

            var depth16 = Path.Combine(root, "merged-16bit.psd");
            WriteRgbPsd(depth16, 0, 8, 8, 16, [96, 165, 33]);
            CheckColor(PsdCompositePreview.DecodeComposite(
                new MemoryStream(File.ReadAllBytes(depth16))), 96, 165, 33);
            if (!ExternalEditTransaction.RequiresNewFile(
                    "source.arw", "output.psd") ||
                ExternalEditTransaction.RequiresNewFile(
                    "source.arw", "output.arw") ||
                ExternalEditTransaction.RequiresNewFile(
                    "source.psd", "output.psd") ||
                !ExternalEditTransaction.RequiresNewFile(
                    "source.psd", "output.tif"))
                throw new Exception("External PSD/RAW Save format safety rules broken.");
            var rawApp = new ExternalEditorProgram(
                "Camera RAW", "editor.exe", "photo", Raw: true);
            var generic = new ExternalEditorProgram(
                "Generic", "editor.exe", "*");
            if (!rawApp.Matches("sony.arw") ||
                generic.Matches("sony.arw") ||
                generic.Matches("layers.psd") ||
                !new ExternalEditorProgram("Layers", "editor.exe",
                    "photo", Psd: true).Matches("layers.psd"))
                throw new Exception("RAW/PSD app flags are not enforced.");
            Console.WriteLine("PASS: Native PSD merged composite RLE/ZIP/raw/16-bit, no layer flattening, RAW/PSD Save policy.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void CheckColor(BitmapSource image, byte r, byte g, byte b)
    {
        var frame = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var buffer = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(buffer, frame.PixelWidth * 4, 0);
        var index = (frame.PixelHeight / 2 * frame.PixelWidth +
            frame.PixelWidth / 2) * 4;
        if (buffer[index] != b || buffer[index + 1] != g ||
            buffer[index + 2] != r || buffer[index + 3] != 255)
            throw new Exception("PSD composite does not match merged RGB pixels: " +
                $"{buffer[index + 2]}/{buffer[index + 1]}/{buffer[index]} expected {r}/{g}/{b}");
    }

    private static void WriteRgbPsd(string path, ushort compression,
        int width, int height, ushort depth, byte[] color)
    {
        using var file = File.Create(path);
        file.Write("8BPS"u8);
        U16(file, 1);
        file.Write(new byte[6]); // reserved
        U16(file, 3);
        U32(file, (uint)height);
        U32(file, (uint)width);
        U16(file, depth);
        U16(file, 3); // RGB
        U32(file, 0); // color-mode section
        U32(file, 0); // image resources
        // Nonzero layer/mask data distinct from the merged composite; the
        // preview must ignore it, not misinterpret it as an image layer.
        U32(file, 6);
        file.Write(new byte[] { 0, 0, 0, 2, 0, 0 });
        U16(file, compression);

        var planes = new MemoryStream();
        var perRow = width * (depth / 8);
        var rleSizes = new List<ushort>();
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                var row = new byte[perRow];
                for (var x = 0; x < width; x++)
                    row[x * (depth / 8)] = color[channel];
                if (compression == 3)
                    for (var x = row.Length - 1; x > 0; x--)
                        row[x] = unchecked((byte)(row[x] - row[x - 1]));
                if (compression == 1)
                {
                    // PackBits literal row (one run, 8 pixels, 8/16bit).
                    var encoded = new byte[1 + perRow];
                    encoded[0] = (byte)(perRow - 1);
                    Array.Copy(row, 0, encoded, 1, perRow);
                    rleSizes.Add((ushort)encoded.Length);
                    planes.Write(encoded);
                }
                else planes.Write(row);
            }
        }
        if (compression == 1)
            foreach (var bytes in rleSizes) U16(file, bytes);
        if (compression is 2 or 3)
        {
            planes.Position = 0;
            using var zipped = new ZLibStream(file,
                CompressionLevel.Fastest, leaveOpen: true);
            planes.CopyTo(zipped);
        }
        else
        {
            planes.Position = 0;
            planes.CopyTo(file);
        }
    }

    private static void U16(Stream stream, ushort n)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, n);
        stream.Write(bytes);
    }

    private static void U32(Stream stream, uint n)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, n);
        stream.Write(bytes);
    }
}
