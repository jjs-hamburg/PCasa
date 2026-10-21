using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed class PackedThumbnailReader : IDisposable
{
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SafeFileHandle> _handles =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PackedThumbnailReader()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa",
            "browse-cache");
    }

    public BitmapSource? Load(
        BrowseThumbnailIndexEntry entry,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var handle = _handles.GetOrAdd(entry.PackFile, OpenPack);
        var bytes = new byte[entry.ByteLength];
        var totalRead = 0;

        while (totalRead < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = RandomAccess.Read(
                handle,
                bytes.AsSpan(totalRead),
                entry.ByteOffset + totalRead);
            if (read <= 0)
            {
                return null;
            }

            totalRead += read;
        }

        cancellationToken.ThrowIfCancellationRequested();

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

    private SafeFileHandle OpenPack(string packFile)
    {
        var path = Path.Combine(_cacheRoot, packFile);
        return File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
    }
}
