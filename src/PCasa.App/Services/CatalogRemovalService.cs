using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed class CatalogRemovalService
{
    // Prevent compaction from racing the append-only browse-cache writer.
    internal static readonly object CacheLock = new();
    private readonly LocalDatabase _database;
    private readonly string _cacheBaseDirectory;

    public CatalogRemovalService(LocalDatabase database, string? cacheBaseDirectory = null)
    {
        _database = database;
        // Explicit root allows tests to prove cleanup without touching any
        // user's PCasa image/cache folders.
        _cacheBaseDirectory = cacheBaseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa");
    }

    public int RemovePath(long monitoredFolderId, string localPath, IProgress<string>? progress = null)
    {
        var normalized = Path.GetFullPath(localPath).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        return Remove(monitoredFolderId, normalized, prefix, removeRoot: false, progress);
    }

    public int RemoveFolder(long monitoredFolderId, IProgress<string>? progress = null)
        => Remove(monitoredFolderId, "", "", removeRoot: true, progress);

    // Removing one file changes only its own catalog entry. Using RemovePath
    // for a JPEG forces unrelated thumbnail-pack compaction in the foreground.
    // The deleted source path must match the image id, guarding against a
    // stale context-menu selection after background rescans.
    public bool DetachImage(long imageId, string expectedPath)
    {
        lock (CacheLock)
        {
            using var db = _database.OpenConnection();
            using var transaction = db.BeginTransaction();
            int removed;
            using (var image = db.CreateCommand())
            {
                image.Transaction = transaction;
                image.CommandText = """
                    DELETE FROM images
                    WHERE id = $id AND local_path = $path COLLATE NOCASE;
                    """;
                image.Parameters.AddWithValue("$id", imageId);
                image.Parameters.AddWithValue("$path", expectedPath);
                removed = image.ExecuteNonQuery();
            }
            if (removed == 0) return false;

            using (var folders = db.CreateCommand())
            {
                folders.Transaction = transaction;
                folders.CommandText = """
                    DELETE FROM photo_folders
                    WHERE NOT EXISTS (
                        SELECT 1 FROM images
                        WHERE photo_folder_id = photo_folders.id);
                    """;
                folders.ExecuteNonQuery();
            }
            using (var maintenance = db.CreateCommand())
            {
                maintenance.Transaction = transaction;
                maintenance.CommandText =
                    "UPDATE cache_maintenance SET cleanup_pending = 1 WHERE id = 1;";
                maintenance.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
    }

    // The same transactional pruning as RemovePath, but physical cache
    // maintenance is deferred. Used when a user excludes a subtree.
    public int DetachSubtree(long monitoredFolderId, string localPath,
        IProgress<string>? progress = null)
    {
        var normalized = Path.GetFullPath(localPath).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        return Remove(monitoredFolderId, normalized, prefix,
            removeRoot: false, progress, deferCleanup: true,
            requireExcludedPath: normalized);
    }

    // Removing a monitored root is a user-facing catalog operation. Do not
    // copy multi-gigabyte thumbnail packs while the user waits for its removal.
    // Foreign-key cascades remove all image, tag, job and thumbnail-index rows.
    // Actual disk reclamation is persisted and runs only when PCasa is idle.
    public void DetachMonitoredFolder(long monitoredFolderId)
    {
        lock (CacheLock)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            // Advance the persistent source generation in the same transaction
            // that detaches the monitored root and cascades its catalog rows.
            using (var source = connection.CreateCommand())
            {
                source.Transaction = transaction;
                source.CommandText = """
                    UPDATE scheduler_sources
                    SET generation = generation + 1,
                        desired_present = 0,
                        updated_utc = $now
                    WHERE canonical_path = (
                        SELECT canonical_path FROM monitored_folders WHERE id = $id
                    );
                    """;
                source.Parameters.AddWithValue("$id", monitoredFolderId);
                source.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                source.ExecuteNonQuery();
            }
            using (var remove = connection.CreateCommand())
            {
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM monitored_folders WHERE id = $id;";
                remove.Parameters.AddWithValue("$id", monitoredFolderId);
                if (remove.ExecuteNonQuery() == 0)
                    return;
            }
            using (var schedule = connection.CreateCommand())
            {
                schedule.Transaction = transaction;
                schedule.CommandText =
                    "UPDATE cache_maintenance SET cleanup_pending = 1 WHERE id = 1;";
                schedule.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public bool HasPendingMaintenance()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cleanup_pending FROM cache_maintenance WHERE id = 1;";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public bool RunPendingMaintenance(CancellationToken cancellationToken = default)
    {
        lock (CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasPendingMaintenance()) return false;

            // A thumbnail can be referenced by another catalog image; enumerate
            // the live set instead of deleting images by the removed folder path.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var connection = _database.OpenConnection())
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT thumbnail_path FROM images;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.IsDBNull(0))
                        referenced.Add(reader.GetString(0));
                }
            }

            var thumbnailRoot = Path.Combine(_cacheBaseDirectory, "thumbnails");
            if (Directory.Exists(thumbnailRoot))
            {
                foreach (var thumb in Directory.EnumerateFiles(
                             thumbnailRoot, "*.jpg", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!referenced.Contains(thumb))
                        TryDelete(thumb);
                }
            }

            // Pack compaction is restart-safe: each pack swap commits its new
            // offsets before the old file is removed. Cancellation leaves the
            // marker set so the next idle pass completes remaining packs.
            CompactBrowsePacks(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            using var done = _database.OpenConnection();
            using var update = done.CreateCommand();
            update.CommandText =
                "UPDATE cache_maintenance SET cleanup_pending = 0 WHERE id = 1;";
            update.ExecuteNonQuery();
            return true;
        }
    }

    // Called only after a complete and error-free directory enumeration.
    // Reuse the same deletion and cache cleanup as explicit folder removal.
    public int RemoveMissing(long folderId, string localDirectory,
        IReadOnlySet<string> currentPaths, IProgress<string>? progress = null,
        bool recursive = true, CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(localDirectory).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        return Remove(folderId, normalized, prefix, removeRoot: false, progress,
            currentPaths, recursive, cancellationToken);
    }

    private int Remove(long folderId, string path, string prefix, bool removeRoot,
        IProgress<string>? progress, IReadOnlySet<string>? currentPaths = null,
        bool recursive = true, CancellationToken cancellationToken = default,
        bool deferCleanup = false, string? requireExcludedPath = null)
    {
        lock (CacheLock)
        {
            // The scan may have been cancelled while waiting for the shared
            // catalog lock. No deletion is authorized in that case.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Bildkatalog bereinigen …");
            var legacyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int removed;
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                // Re-inclusion can overtake an already queued exclusion cleanup.
                // Check the current desired state inside the same SQLite write
                // transaction as the DELETE, not before acquiring CacheLock.
                if (requireExcludedPath is not null)
                {
                    using var guard = connection.CreateCommand();
                    guard.Transaction = transaction;
                    guard.CommandText = """
                        SELECT EXISTS(
                            SELECT 1 FROM excluded_folders
                            WHERE monitored_folder_id = $folderId
                              AND local_path = $path COLLATE NOCASE);
                        """;
                    guard.Parameters.AddWithValue("$folderId", folderId);
                    guard.Parameters.AddWithValue("$path", requireExcludedPath);
                    if (Convert.ToInt64(guard.ExecuteScalar()) == 0)
                        return 0;
                }

                if (currentPaths is not null)
                {
                    using var table = connection.CreateCommand();
                    table.Transaction = transaction;
                    table.CommandText = "CREATE TEMP TABLE IF NOT EXISTS scan_seen_paths(path TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID; DELETE FROM scan_seen_paths;";
                    table.ExecuteNonQuery();
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT OR IGNORE INTO scan_seen_paths(path) VALUES ($path);";
                    var seenParameter = insert.Parameters.Add("$path", SqliteType.Text);
                    foreach (var seen in currentPaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        seenParameter.Value = seen;
                        insert.ExecuteNonQuery();
                    }
                }

                var predicate = removeRoot
                    ? "folder_id = $folderId"
                    : @"folder_id = $folderId AND
                         (local_path = $path COLLATE NOCASE OR
                          substr(local_path, 1, length($prefix)) = $prefix COLLATE NOCASE)";
                if (currentPaths is not null)
                {
                    predicate += " AND NOT EXISTS (SELECT 1 FROM scan_seen_paths s WHERE s.path = images.local_path)";
                    // A non-recursive enumeration did not inspect child folders.
                    // Restrict its reconciliation to files directly in scanRoot.
                    if (!recursive)
                        predicate += " AND instr(substr(local_path, length($prefix) + 1), char(92)) = 0";
                }

                if (!deferCleanup)
                {
                    using var collect = connection.CreateCommand();
                    collect.Transaction = transaction;
                    collect.CommandText = $"SELECT thumbnail_path FROM images WHERE {predicate};";
                    Bind(collect, folderId, path, prefix);
                    using var reader = collect.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!reader.IsDBNull(0))
                            legacyPaths.Add(reader.GetString(0));
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM images WHERE {predicate};";
                    Bind(delete, folderId, path, prefix);
                    removed = delete.ExecuteNonQuery();
                }

                using (var folders = connection.CreateCommand())
                {
                    folders.Transaction = transaction;
                    folders.CommandText = @"DELETE FROM photo_folders
                        WHERE monitored_folder_id = $folderId
                          AND NOT EXISTS (SELECT 1 FROM images i
                                          WHERE i.photo_folder_id = photo_folders.id);";
                    folders.Parameters.AddWithValue("$folderId", folderId);
                    folders.ExecuteNonQuery();
                }

                if (deferCleanup && removed > 0)
                {
                    // The user action ends with this SQLite transaction.
                    // Cache compaction is not part of interactive removal.
                    using var schedule = connection.CreateCommand();
                    schedule.Transaction = transaction;
                    schedule.CommandText =
                        "UPDATE cache_maintenance SET cleanup_pending = 1 WHERE id = 1;";
                    schedule.ExecuteNonQuery();
                }

                // If cancellation arrives during the DELETE statements, the
                // uncommitted transaction must roll back instead of pruning
                // a catalog after its scan was interrupted.
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            if (removed == 0)
                return 0;

            if (deferCleanup)
            {
                progress?.Report($"{removed:N0} Bildeinträge entfernt · Cache wird im Leerlauf bereinigt");
                return removed;
            }

            progress?.Report($"{removed:N0} Bildeinträge entfernt · Einzel-Thumbnails löschen …");
            // Never delete an individual thumbnail still referenced by another image.
            using (var connection = _database.OpenConnection())
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = "SELECT COUNT(*) FROM images WHERE thumbnail_path = $path;";
                var parameter = exists.Parameters.Add("$path", SqliteType.Text);
                var cacheRoot = Path.Combine(_cacheBaseDirectory, "thumbnails");
                foreach (var thumbnail in legacyPaths)
                {
                    if (!IsInside(thumbnail, cacheRoot)) continue;
                    parameter.Value = thumbnail;
                    if (Convert.ToInt64(exists.ExecuteScalar()) != 0) continue;
                    TryDelete(thumbnail);
                }
            }

            progress?.Report("Browse-Cache bereinigen …");
            CompactBrowsePacks(progress);
            progress?.Report($"Bereinigung abgeschlossen · {removed:N0} Bilder entfernt");
            return removed;
        }
    }

    private static void Bind(SqliteCommand command, long id, string path, string prefix)
    {
        command.Parameters.AddWithValue("$folderId", id);
        if (path.Length == 0) return;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$prefix", prefix);
    }

    private void CompactBrowsePacks(IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(_cacheBaseDirectory, "browse-cache");
        if (!Directory.Exists(root)) return;

        using var connection = _database.OpenConnection();
        var packs = Directory.EnumerateFiles(root, "*.pack").ToArray();
        var packNumber = 0;
        foreach (var original in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Cache-Packdateien bereinigen: {++packNumber:N0} / {packs.Length:N0}");
            var fileName = Path.GetFileName(original);
            using var query = connection.CreateCommand();
            query.CommandText = @"SELECT image_id, tier, byte_offset, byte_length
                FROM thumbnail_entries WHERE pack_file = $pack
                ORDER BY byte_offset;";
            query.Parameters.AddWithValue("$pack", fileName);
            var rows = new List<(long ImageId, int Tier, long Offset, int Length)>();
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetInt32(1),
                              reader.GetInt64(2), reader.GetInt32(3)));

            if (rows.Count == 0)
            {
                TryDelete(original);
                continue;
            }

            var length = new FileInfo(original).Length;
            long expected = 0;
            var compact = false;
            foreach (var row in rows)
            {
                if (row.Length <= 0 || row.Offset < 0 || row.Offset > length - row.Length)
                    throw new InvalidDataException($"Ungültiger Cache-Eintrag in {fileName}");
                if (row.Offset != expected) compact = true;
                expected = row.Offset + row.Length;
            }
            if (!compact && expected == length) continue;

            var newName = "compact-" + Guid.NewGuid().ToString("N") + ".pack";
            var newPath = Path.Combine(root, newName);
            var updated = new List<(long ImageId, int Tier, long Offset)>(rows.Count);
            try
            {
                using (var input = new FileStream(original, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(newPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    foreach (var row in rows)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        input.Position = row.Offset;
                        var offset = output.Position;
                        var remaining = row.Length;
                        var buffer = new byte[Math.Min(65536, remaining)];
                        while (remaining > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                            if (read == 0) throw new EndOfStreamException(fileName);
                            output.Write(buffer, 0, read);
                            remaining -= read;
                        }
                        updated.Add((row.ImageId, row.Tier, offset));
                    }
                    output.Flush(flushToDisk: true);
                }

                using (var transaction = connection.BeginTransaction())
                using (var update = connection.CreateCommand())
                {
                    update.Transaction = transaction;
                    update.CommandText = @"UPDATE thumbnail_entries
                        SET pack_file = $newPack, byte_offset = $offset
                        WHERE image_id = $id AND tier = $tier AND pack_file = $oldPack;";
                    update.Parameters.AddWithValue("$newPack", newName);
                    update.Parameters.AddWithValue("$oldPack", fileName);
                    var offsetParam = update.Parameters.Add("$offset", SqliteType.Integer);
                    var idParam = update.Parameters.Add("$id", SqliteType.Integer);
                    var tierParam = update.Parameters.Add("$tier", SqliteType.Integer);
                    foreach (var row in updated)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        offsetParam.Value = row.Offset;
                        idParam.Value = row.ImageId;
                        tierParam.Value = row.Tier;
                        if (update.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("Cache-Index hat sich während der Bereinigung geändert.");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    transaction.Commit();
                }
                TryDelete(original);
            }
            catch
            {
                // If the transaction did not commit, the old pack is still valid.
                // If it did commit, the new pack is referenced and must be kept.
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM thumbnail_entries WHERE pack_file = $pack;";
                check.Parameters.AddWithValue("$pack", newName);
                if (Convert.ToInt64(check.ExecuteScalar()) == 0) TryDelete(newPath);
                throw;
            }
        }
    }

    private static bool IsInside(string path, string root)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return full.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* In-use files can be reclaimed on a subsequent cleanup. */ }
        catch (UnauthorizedAccessException) { /* Keep the database consistent. */ }
    }
}
