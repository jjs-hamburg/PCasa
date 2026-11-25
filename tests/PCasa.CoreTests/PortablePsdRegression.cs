using System.Buffers.Binary;
using System.IO.Compression;
using PCasa.App.Services;

internal static class PortablePsdRegression
{
    public static void Run(string directory)
    {
        // Two columns, three rows. Planar RGBA; each channel distinct.
        var channels = new byte[][]
        {
            new byte[] { 10, 11, 12, 13, 14, 15 },
            new byte[] { 30, 31, 32, 33, 34, 35 },
            new byte[] { 50, 51, 52, 53, 54, 55 },
            new byte[] { 70, 71, 72, 73, 74, 75 }
        };
        foreach (ushort compression in new ushort[] { 0, 1, 2, 3 })
        {
            var fixture = CreatePsd(channels, compression);
            using var input = new MemoryStream(fixture);
            var decoded = PsdCompositeDecoder.DecodeComposite(input);
            if (decoded.Width != 2 || decoded.Height != 3 || decoded.Bgra.Length != 24)
                throw new Exception("PSD portrait dimensions changed.");
            for (var i = 0; i < 6; i++)
                if (!decoded.Bgra.AsSpan(i * 4, 4).SequenceEqual(new byte[]
                    { channels[2][i], channels[1][i], channels[0][i], channels[3][i] }))
                    throw new Exception("PSD channel or compression decoding changed.");
            var path = Path.Combine(directory, $"fixture-{compression}.psd");
            File.WriteAllBytes(path, fixture);
            if (PsdCompositeDecoder.GetDimensions(path) != (2, 3))
                throw new Exception("PSD source dimensions changed.");
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var input = new MemoryStream(CreatePsd(channels, 0));
            PsdCompositeDecoder.DecodeComposite(input, cancelled.Token);
            throw new Exception("PSD decoder ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static byte[] CreatePsd(byte[][] channels, ushort compression)
    {
        using var output = new MemoryStream();
        output.Write("8BPS"u8);
        U16(output, 1);
        output.Write(new byte[6]);
        U16(output, 4);
        U32(output, 3);
        U32(output, 2);
        U16(output, 8);
        U16(output, 3);
        U32(output, 0);
        U32(output, 0);
        U32(output, 0);
        U16(output, compression);
        if (compression == 1)
        {
            // One two-byte literal per row, including PackBits control byte.
            for (var i = 0; i < 12; i++) U16(output, 3);
            foreach (var channel in channels)
                for (var y = 0; y < 3; y++)
                {
                    output.WriteByte(1);
                    output.Write(channel, y * 2, 2);
                }
        }
        else if (compression is 2 or 3)
        {
            using var zip = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true);
            foreach (var channel in channels)
                for (var y = 0; y < 3; y++)
                {
                    var x = y * 2;
                    zip.WriteByte(channel[x]);
                    zip.WriteByte(compression == 3
                        ? unchecked((byte)(channel[x + 1] - channel[x]))
                        : channel[x + 1]);
                }
        }
        else
            foreach (var channel in channels) output.Write(channel);
        return output.ToArray();
    }

    private static void U16(Stream output, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        output.Write(buffer);
    }

    private static void U32(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        output.Write(buffer);
    }
}
