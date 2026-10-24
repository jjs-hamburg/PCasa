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
        => EnterGate(ForPath(path), token);

    private sealed class Lease(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    // A transfer changes TWO paths. A still-running scanner or metadata
    // writer must not read the destination while it is being replaced.
    // Always acquire stripes in the same index order; opposite-direction
    // moves cannot deadlock, including hash-stripe collisions.
    public static IDisposable EnterPair(string source, string destination,
        CancellationToken token)
    {
        var firstIndex = GateIndex(source);
        var secondIndex = GateIndex(destination);
        if (firstIndex == secondIndex)
            return EnterGate(Gates[firstIndex], token);
        var lower = Math.Min(firstIndex, secondIndex);
        var upper = Math.Max(firstIndex, secondIndex);
        var first = EnterGate(Gates[lower], token);
        try
        {
            var second = EnterGate(Gates[upper], token);
            return new PairLease(first, second);
        }
        catch
        {
            first.Dispose();
            throw;
        }
    }

    private sealed class PairLease(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose()
        {
            second.Dispose();
            first.Dispose();
        }
    }

    private static IDisposable EnterGate(object gate, CancellationToken token)
    {
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

    private static int GateIndex(string path)
    {
        var canonical = Path.GetFullPath(path).Replace('/', '\\');
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(canonical) & int.MaxValue;
        return hash % Gates.Length;
    }

    public static object ForPath(string path) => Gates[GateIndex(path)];
}
