using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// The original-file package owns its own metadata re-indexing. FileSystemWatcher
/// notifications produced by that same write are observations, not new work.
/// An unrelated external modification is still forwarded to the scanner:
/// post-commit receipts match both length and last-write timestamp.
/// </summary>
public static class OriginalFileMutationReceipts
{
    private sealed class Entry
    {
        public int Active;
        public long Length;
        public long LastWriteUtcTicks;
        public DateTimeOffset ConfirmedUntil;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan EventDeliveryWindow = TimeSpan.FromSeconds(20);

    public sealed class Mutation : IDisposable
    {
        private readonly string _path;
        private bool _disposed;

        internal Mutation(string path) => _path = path;

        // Only call after the source metadata has been verified and its
        // matching catalog transaction has committed. A failed write leaves
        // no successful receipt.
        public void Confirm(long length, long lastWriteUtcTicks)
        {
            lock (Gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Mutation));
                if (!Entries.TryGetValue(_path, out var entry)) return;
                entry.Length = length;
                entry.LastWriteUtcTicks = lastWriteUtcTicks;
                entry.ConfirmedUntil = DateTimeOffset.UtcNow + EventDeliveryWindow;
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (!Entries.TryGetValue(_path, out var entry)) return;
                entry.Active--;
                if (entry.Active == 0 && entry.ConfirmedUntil <= DateTimeOffset.UtcNow)
                    Entries.Remove(_path);
            }
        }
    }

    public static Mutation Begin(string path)
    {
        var key = Path.GetFullPath(path);
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry))
                Entries[key] = entry = new Entry();
            entry.Active++;
            // The last write's receipt must not hide an external change
            // when a new operation on the same path begins.
            entry.ConfirmedUntil = DateTimeOffset.MinValue;
            // Keep the memory bounded for large photo libraries.
            if (Entries.Count > 4096)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var stale in Entries.Where(item => item.Value.Active == 0 &&
                             item.Value.ConfirmedUntil <= now)
                         .Select(item => item.Key).ToArray())
                    Entries.Remove(stale);
            }
        }
        return new Mutation(key);
    }

    // Invoked on the watcher's worker thread, never the WPF dispatcher.
    // A changed file signature is NOT our own write and must be rescanned.
    public static bool IsOwnedNotification(string path)
    {
        string key;
        try { key = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                     or PathTooLongException) { return false; }
        long length, ticks;
        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry)) return false;
            if (entry.Active > 0) return true;
            if (entry.ConfirmedUntil <= DateTimeOffset.UtcNow)
            {
                Entries.Remove(key);
                return false;
            }
            length = entry.Length;
            ticks = entry.LastWriteUtcTicks;
        }
        try
        {
            var info = new FileInfo(key);
            if (!info.Exists || info.Length != length ||
                info.LastWriteTimeUtc.Ticks != ticks)
            {
                lock (Gate)
                    if (Entries.TryGetValue(key, out var entry) && entry.Active == 0 &&
                        entry.Length == length && entry.LastWriteUtcTicks == ticks)
                        Entries.Remove(key);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // Offline file: do not silence its eventual reconciliation.
        }
    }
}
