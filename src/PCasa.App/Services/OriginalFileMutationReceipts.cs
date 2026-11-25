using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

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
    // Delayed SMB watcher notifications may survive a process restart.
    // Keep only the latest verified fingerprint per image. The bounded
    // notification window is not a scan/retry schedule: old receipts must
    // not mask external edits which deliberately preserve timestamps.
    private static readonly TimeSpan DurableDeliveryWindow = TimeSpan.FromMinutes(2);

    public static void RecordConfirmed(SqliteConnection db, SqliteTransaction tx,
        long imageId, FileInfo after)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO metadata_write_receipts
                (image_id,file_size,modified_utc_ticks,created_utc_ticks,confirmed_utc_ticks)
            SELECT id,$size,$modified,$created,$now FROM images
             WHERE id=$id AND file_size=$size AND modified_utc_ticks=$modified
               AND created_utc_ticks=$created
            ON CONFLICT(image_id) DO UPDATE SET
                file_size=excluded.file_size,modified_utc_ticks=excluded.modified_utc_ticks,
                created_utc_ticks=excluded.created_utc_ticks,
                confirmed_utc_ticks=excluded.confirmed_utc_ticks;
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$size", after.Length);
        cmd.Parameters.AddWithValue("$modified", after.LastWriteTimeUtc.Ticks);
        cmd.Parameters.AddWithValue("$created", after.CreationTimeUtc.Ticks);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        cmd.ExecuteNonQuery();
    }

    public static bool IsConfirmedNotification(LocalDatabase database, string path)
    {
        // No file-system access unless the LOCAL catalog has a recent,
        // matching acknowledged write. A stale receipt is not ownership.
        try
        {
            using var db = database.OpenConnection();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT r.file_size,r.modified_utc_ticks,r.created_utc_ticks
                  FROM metadata_write_receipts r JOIN images i ON i.id=r.image_id
                 WHERE i.local_path=$path COLLATE NOCASE
                   AND r.confirmed_utc_ticks >= $since
                   AND i.file_size=r.file_size
                   AND i.modified_utc_ticks=r.modified_utc_ticks
                   AND i.created_utc_ticks=r.created_utc_ticks;
                """;
            cmd.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            cmd.Parameters.AddWithValue("$since", DateTime.UtcNow.Subtract(DurableDeliveryWindow).Ticks);
            long size, modified, created;
            using (var row = cmd.ExecuteReader())
            {
                if (!row.Read()) return false;
                size = row.GetInt64(0);
                modified = row.GetInt64(1);
                created = row.GetInt64(2);
            }
            var info = new FileInfo(path);
            return info.Exists && info.Length == size &&
                info.LastWriteTimeUtc.Ticks == modified &&
                info.CreationTimeUtc.Ticks == created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or SqliteException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

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
