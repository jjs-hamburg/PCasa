using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Resolve a Windows Shell video frame to a local, versioned JPEG preview.
/// Never create files beside the original; never modify the video.
/// Shell codec failures result in a clearly recognizable video placeholder,
/// not a deleted or silently ignored catalog entry.
/// </summary>
public static class VideoThumbnailSource
{
    private const int Side = 512;
    private static readonly Guid FactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    public static string Resolve(string path, CancellationToken token = default)
    {
        if (!VideoFormats.IsVideo(path)) return path;
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var key = $"{Path.GetFullPath(path)}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var root = Path.Combine(PcasaDataPaths.SharedDataRoot, "video-previews", hash[..2]);
        var target = Path.Combine(root, hash + ".jpg");
        {
            if (File.Exists(target) && new FileInfo(target).Length > 0)
                return target;
            Directory.CreateDirectory(root);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                token.ThrowIfCancellationRequested();
                BitmapSource frame;
                try { frame = GetWindowsThumbnail(path); }
                catch (Exception ex) when (ex is COMException or NotSupportedException
                    or InvalidOperationException or ExternalException)
                {
                    // The source remains in the catalog even when its codec is absent.
                    frame = CreatePlaceholder();
                }
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(frame));
                using (var output = File.Create(temporary)) encoder.Save(output);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: true);
                return target;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static BitmapSource GetWindowsThumbnail(string path)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = FactoryId;
            SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero,
                ref iid, out factory);
            factory.GetImage(new NativeSize(Side, Side), 0x08, out hbitmap);
            if (hbitmap == IntPtr.Zero)
                throw new NotSupportedException("Kein Video-Thumbnail verfügbar.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource CreatePlaceholder()
    {
        const int w = 512, h = 288;
        var bytes = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var offset = (y * w + x) * 4;
                var shade = (byte)(32 + 28 * y / h);
                bytes[offset] = shade;
                bytes[offset + 1] = shade;
                bytes[offset + 2] = shade;
                bytes[offset + 3] = 255;
                // Play triangle, visible even without color.
                if (x >= 222 && x <= 300 && Math.Abs(y - 144) <= (x - 222) * 0.57)
                    bytes[offset] = bytes[offset + 1] = bytes[offset + 2] = 238;
            }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32,
            null, bytes, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext,
        [In] ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
