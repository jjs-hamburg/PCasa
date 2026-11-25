using System.IO;
using System.Windows.Media.Imaging;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed class PackedThumbnailReader : IDisposable
{
    private readonly PackedThumbnailBytes _packed = new();
    private bool _disposed;

    public BitmapSource? Load(
        BrowseThumbnailIndexEntry entry,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        // The gallery catalog includes every committed photo, even if its
        // packed browse tier is still awaiting background conversion.
        // Read the existing local legacy thumbnail for that image instead.
        // Never synchronously fetch a NAS original on the WPF dispatcher.
        if (entry.ByteLength <= 0 || string.IsNullOrWhiteSpace(entry.PackFile))
        {
            if (string.IsNullOrWhiteSpace(entry.LegacyThumbnailPath) ||
                !File.Exists(entry.LegacyThumbnailPath))
                return null;
            using var fallback = new FileStream(entry.LegacyThumbnailPath,
                FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            cancellationToken.ThrowIfCancellationRequested();
            var legacy = new BitmapImage();
            legacy.BeginInit();
            legacy.CacheOption = BitmapCacheOption.OnLoad;
            legacy.DecodePixelWidth = Math.Max(48, entry.Tier);
            legacy.StreamSource = fallback;
            legacy.EndInit();
            legacy.Freeze();
            return legacy;
        }

        var bytes = _packed.Load(entry, cancellationToken);
        if (bytes is null) return null;

        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _packed.Dispose();
    }
}
