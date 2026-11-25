using System.Security.Cryptography;
using System.Text;
using NetVips;

namespace PCasa.App.Services;

// The cache key, source lock and atomic publication are identical on every
// host. Windows may retain its existing PNG encoder without owning decoding.
public static class PsdCompositeCache
{
    private static string PreviewRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "psd-composites");

    public static string Resolve(string psd, CancellationToken token = default,
        string? cacheRoot = null)
        => Resolve(psd, WritePng, token, cacheRoot);

    public static string Resolve(string psd, Action<string, PsdCompositePixels, CancellationToken> encode,
        CancellationToken token = default, string? cacheRoot = null)
    {
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(psd);
        if (!info.Exists) throw new FileNotFoundException("PSD nicht gefunden.", psd);
        var signature = Path.GetFullPath(psd) + "\n" + info.Length + "\n" +
            info.LastWriteTimeUtc.Ticks + "\nmerged-v1";
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
        var destination = Path.Combine(cacheRoot ?? PreviewRoot, key[..2], key + ".png");
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
                var image = PsdCompositeDecoder.DecodeComposite(input, token);
                encode(temporary, image, token);
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

    private static void WritePng(string temporary, PsdCompositePixels pixels,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        NativeImageCachePolicy.EnsureConfigured();
        // libvips expects RGBA; the decoder returns the established BGRA layout.
        var rgba = new byte[pixels.Bgra.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = pixels.Bgra[i + 2];
            rgba[i + 1] = pixels.Bgra[i + 1];
            rgba[i + 2] = pixels.Bgra[i];
            rgba[i + 3] = pixels.Bgra[i + 3];
        }
        using var image = Image.NewFromMemory(rgba, pixels.Width, pixels.Height,
            4, Enums.BandFormat.Uchar);
        using var srgb = image.Copy(interpretation: Enums.Interpretation.Srgb);
        srgb.SetProgress(new Progress<int>(_ => { }), token);
        srgb.Pngsave(temporary);
        token.ThrowIfCancellationRequested();
        using var output = new FileStream(temporary, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        output.Flush(true);
    }
}
