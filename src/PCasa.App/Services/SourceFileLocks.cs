using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Bounded per-source locks for in-place metadata writes, source decoding,
/// duplication and deletion. File operations on unrelated photos must never
/// hold CatalogRemovalService.CacheLock across slow SMB / ExifTool calls.
/// A fixed stripe set avoids retaining 500,000 path objects indefinitely.
/// </summary>
public static class SourceFileLocks
{
    private static readonly object[] Gates =
        Enumerable.Range(0, 1021).Select(_ => new object()).ToArray();

    // Yield requests must interrupt a scanner waiting for a different image's
    // stripe. Monitor.Enter/lock cannot observe a scheduler cancellation until
    // the lock is released; a short bounded wait makes the same lock preemptible.
    // This lease is ONLY for synchronous work on its acquiring worker thread.
    public static IDisposable Enter(string path, CancellationToken token)
    {
        var gate = ForPath(path);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Monitor.TryEnter(gate, millisecondsTimeout: 25))
                continue;
            if (token.IsCancellationRequested)
            {
                Monitor.Exit(gate);
                token.ThrowIfCancellationRequested();
            }
            return new Lease(gate);
        }
    }

    private sealed class Lease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    public static object ForPath(string path)
    {
        var canonical = Path.GetFullPath(path).Replace('/', '\\');
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(canonical) & int.MaxValue;
        return Gates[hash % Gates.Length];
    }
}
