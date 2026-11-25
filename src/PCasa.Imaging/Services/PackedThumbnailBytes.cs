using System.Collections.Concurrent;
using System.IO;
using Microsoft.Win32.SafeHandles;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed class PackedThumbnailBytes : IDisposable
{
    private readonly string _cacheRoot;
    private readonly ConcurrentDictionary<string, SafeFileHandle> _handles =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PackedThumbnailBytes(string? cacheRoot = null)
    {
        _cacheRoot = cacheRoot ?? Path.Combine(PcasaDataPaths.SharedDataRoot,
            "browse-cache");
    }

    public byte[]? Load(BrowseThumbnailIndexEntry entry,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.ByteLength <= 0 || string.IsNullOrWhiteSpace(entry.PackFile))
            return null;
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

        return bytes;
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
