using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed class CatalogRemovalService
{
    // Prevent compaction from racing the append-only browse-cache writer.
    internal static readonly object CacheLock = new();
    // Only maintenance jobs serialize for their whole lifetime; interactive
    // writers acquire CacheLock for individual, bounded index commits.
    private static readonly object MaintenanceLock = new();
    private readonly LocalDatabase _database;
    private readonly string _cacheBaseDirectory;
    private readonly Action? _beforePackCopy;

    public CatalogRemovalService(LocalDatabase database, string? cacheBaseDirectory = null,
        Action? beforePackCopy = null)
    {
        _database = database;
        // Optional synchronization gate for deterministic cache-race tests.
        _beforePackCopy = beforePackCopy;
        // Explicit root allows tests to prove cleanup without touching any
        // user's PCasa image/cache folders.
        _cacheBaseDirectory = cacheBaseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa");
    }

    public int RemovePath(long monitoredFolderId, string localPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        string? sourceCanonicalPath = null, long? expectedGeneration = null,
        bool deferCleanup = false)
    {
        var normalized = Path.GetFullPath(localPath).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        return Remove(monitoredFolderId, normalized, prefix, removeRoot: false, progress,
            cancellationToken: cancellationToken, deferCleanup: deferCleanup,
            sourceCanonicalPath: sourceCanonicalPath, expectedGeneration: expectedGeneration);
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
                    "UPDATE cache_maintenance SET cleanup_pending = 1, request_generation = request_generation + 1 WHERE id = 1;";
                maintenance.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
    }

    // The same transactional pruning as RemovePath, but physical cache
    // maintenance is deferred. Used when a user excludes a subtree.
    public int DetachSubtree(long monitoredFolderId, string localPath,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        string? sourceCanonicalPath = null, long? expectedGeneration = null)
    {
        var normalized = Path.GetFullPath(localPath).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        return Remove(monitoredFolderId, normalized, prefix,
            removeRoot: false, progress, deferCleanup: true,
            requireExcludedPath: normalized, cancellationToken: cancellationToken,
            sourceCanonicalPath: sourceCanonicalPath, expectedGeneration: expectedGeneration);
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
                    "UPDATE cache_maintenance SET cleanup_pending = 1, request_generation = request_generation + 1 WHERE id = 1;";
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
        // Slow disk traversal, pack copying and JPEG cleanup must never
        // monopolize the append/index lock or postpone a manual folder action.
        lock (MaintenanceLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _database.OpenConnection();
            long requestedGeneration;
            using (var state = connection.CreateCommand())
            {
                state.CommandText =
                    "SELECT request_generation FROM cache_maintenance WHERE id = 1 AND cleanup_pending = 1;";
                var pending = state.ExecuteScalar();
                if (pending is null) return false;
                requestedGeneration = Convert.ToInt64(pending);
            }

            var complete = ReclaimUnusedLegacyThumbnails(cancellationToken);
            if (!CompactBrowsePacks(null, cancellationToken))
                complete = false;

            cancellationToken.ThrowIfCancellationRequested();
            if (complete)
            {
                // A delete/add occurring during cleanup sets a newer request.
                // Never acknowledge work belonging to that newer generation.
                using var done = connection.CreateCommand();
                done.CommandText = """
                    UPDATE cache_maintenance SET cleanup_pending = 0
                    WHERE id = 1 AND request_generation = $generation;
                    """;
                done.Parameters.AddWithValue("$generation", requestedGeneration);
                done.ExecuteNonQuery();
            }
            return true;
        }
    }

    private bool ReclaimUnusedLegacyThumbnails(CancellationToken cancellationToken)
    {
        var root = Path.Combine(_cacheBaseDirectory, "thumbnails");
        if (!Directory.Exists(root)) return true;

        // Snapshot is only a fast rejection filter, not authority to delete.
        // The final DB reference check is made under the same short lock as
        // scanner catalog commits. Skip fresh/uncommitted thumbnail files.
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

        var complete = true;
        var safeBefore = DateTime.UtcNow - TimeSpan.FromMinutes(30);
        foreach (var thumb in Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (referenced.Contains(thumb)) continue;
            // .tmp.jpg is owned by a concurrent image encoder.
            if (thumb.EndsWith(".tmp.jpg", StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.GetLastWriteTimeUtc(thumb) > safeBefore)
            {
                complete = false;
                continue;
            }

            lock (CacheLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var db = _database.OpenConnection();
                using var count = db.CreateCommand();
                count.CommandText = "SELECT EXISTS(SELECT 1 FROM images WHERE thumbnail_path = $path);";
                count.Parameters.AddWithValue("$path", thumb);
                if (Convert.ToInt32(count.ExecuteScalar()) == 0)
                {
                    TryDelete(thumb);
                    if (File.Exists(thumb)) complete = false;
                }
            }
        }
        return complete;
    }

    // Called only after a complete and error-free directory enumeration.
    // Reuse the same deletion and cache cleanup as explicit folder removal.
    public int RemoveMissing(long folderId, string localDirectory,
        IReadOnlySet<string> currentPaths, IProgress<string>? progress = null,
        bool recursive = true, CancellationToken cancellationToken = default,
        string? sourceCanonicalPath = null, long? expectedGeneration = null,
        long? expectedScanEpoch = null)
    {
        var normalized = Path.GetFullPath(localDirectory).Replace('/', '\\');
        var prefix = Path.TrimEndingDirectorySeparator(normalized) + "\\";
        // Reconciliation is a catalog operation, not a pack-compaction job.
        // Schedule physical cache reclamation for the idle worker; a full scan
        // must never monopolize CacheLock while copying large pack files.
        return Remove(folderId, normalized, prefix, removeRoot: false, progress,
            currentPaths, recursive, cancellationToken, deferCleanup: true,
            sourceCanonicalPath: sourceCanonicalPath,
            expectedGeneration: expectedGeneration,
            expectedScanEpoch: expectedScanEpoch);
    }

    private int Remove(long folderId, string path, string prefix, bool removeRoot,
        IProgress<string>? progress, IReadOnlySet<string>? currentPaths = null,
        bool recursive = true, CancellationToken cancellationToken = default,
        bool deferCleanup = false, string? requireExcludedPath = null,
        string? sourceCanonicalPath = null, long? expectedGeneration = null,
        long? expectedScanEpoch = null)
    {
        // A complete scan may have observed hundreds of thousands of paths.
        // Filling this connection-local TEMP table is preparation, not a
        // shared pack/index mutation. Do it outside CacheLock, leaving the
        // generation check and DELETE inside one short SQLite commit boundary.
        using var reconciliationConnection = _database.OpenConnection();
        if (currentPaths is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var table = reconciliationConnection.CreateCommand())
            {
                table.CommandText = "CREATE TEMP TABLE IF NOT EXISTS scan_seen_paths(path TEXT PRIMARY KEY COLLATE NOCASE) WITHOUT ROWID; DELETE FROM scan_seen_paths;";
                table.ExecuteNonQuery();
            }
            using var insert = reconciliationConnection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO scan_seen_paths(path) VALUES ($path);";
            var seenParameter = insert.Parameters.Add("$path", SqliteType.Text);
            foreach (var seen in currentPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                seenParameter.Value = seen;
                insert.ExecuteNonQuery();
            }
        }

        lock (CacheLock)
        {
            // Source generation is checked in the same transaction as the
            // catalog deletion even if user actions ran during preparation.
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Bildkatalog bereinigen …");
            var legacyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int removed;
            using (var transaction = reconciliationConnection.BeginTransaction())
            {
                var connection = reconciliationConnection;
                // Re-inclusion can overtake an already queued exclusion cleanup.
                // Check the current desired state inside the same SQLite write
                // transaction as the DELETE, not before acquiring CacheLock.
                if (sourceCanonicalPath is not null)
                {
                    if (expectedGeneration is null)
                        throw new ArgumentException("Missing source generation.");
                    using var guard = connection.CreateCommand();
                    guard.Transaction = transaction;
                    guard.CommandText = """
                        SELECT EXISTS(
                            SELECT 1 FROM scheduler_sources s
                            JOIN monitored_folders m ON m.canonical_path = s.canonical_path
                            WHERE m.id = $folderId AND s.canonical_path = $path
                              AND s.generation = $generation AND s.desired_present = 1);
                        """;
                    guard.Parameters.AddWithValue("$folderId", folderId);
                    guard.Parameters.AddWithValue("$path", sourceCanonicalPath);
                    guard.Parameters.AddWithValue("$generation", expectedGeneration.Value);
                    if (Convert.ToInt32(guard.ExecuteScalar()) != 1)
                        throw new OperationCanceledException("Old scan may not prune catalog.");
                    if (expectedScanEpoch is long epoch)
                    {
                        // A newer scan may have started during a paused old scan.
                        // Its catalogue is safe; the old inventory no longer
                        // has permission to DELETE, but may finish normally.
                        // The check and DELETE share this write transaction.
                        using var epochGuard = connection.CreateCommand();
                        epochGuard.Transaction = transaction;
                        epochGuard.CommandText = "SELECT scan_epoch FROM scheduler_sources WHERE canonical_path = $path;";
                        epochGuard.Parameters.AddWithValue("$path", sourceCanonicalPath);
                        if (Convert.ToInt64(epochGuard.ExecuteScalar()) != epoch)
                            return 0;
                    }
                }
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
                        "UPDATE cache_maintenance SET cleanup_pending = 1, request_generation = request_generation + 1 WHERE id = 1;";
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

            // Never compact multi-megabyte pack files while holding CacheLock.
            using (var db = _database.OpenConnection())
            using (var schedule = db.CreateCommand())
            {
                schedule.CommandText = """
                    UPDATE cache_maintenance
                    SET cleanup_pending = 1, request_generation = request_generation + 1
                    WHERE id = 1;
                    """;
                schedule.ExecuteNonQuery();
            }
            progress?.Report($"Katalog bereinigt · {removed:N0} Bilder entfernt; physische Bereinigung im Leerlauf");
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

    private bool CompactBrowsePacks(IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(_cacheBaseDirectory, "browse-cache");
        if (!Directory.Exists(root)) return true;

        // Pack bytes are append-only. Readers may hold old pack handles, and
        // every new writer uses a non-recycled filename. Build the replacement
        // OUTSIDE CacheLock. Move index entries in bounded SQLite commits.
        var complete = true;
        var packs = Directory.EnumerateFiles(root, "*.pack").ToArray();
        var packNumber = 0;
        foreach (var original in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Cache-Packdateien bereinigen: {++packNumber:N0} / {packs.Length:N0}");
            if (!File.Exists(original)) continue;
            var fileName = Path.GetFileName(original);
            using var connection = _database.OpenConnection();
            using var query = connection.CreateCommand();
            query.CommandText = """
                SELECT image_id, tier, byte_offset, byte_length
                FROM thumbnail_entries WHERE pack_file = $pack
                ORDER BY byte_offset;
                """;
            query.Parameters.AddWithValue("$pack", fileName);
            var rows = new List<(long ImageId, int Tier, long Offset, int Length)>();
            using (var reader = query.ExecuteReader())
                while (reader.Read())
                    rows.Add((reader.GetInt64(0), reader.GetInt32(1),
                        reader.GetInt64(2), reader.GetInt32(3)));

            if (rows.Count == 0)
            {
                // An append might have committed after our SELECT. Recheck
                // under CacheLock before reclaiming any unreferenced pack.
                lock (CacheLock)
                {
                    if (PackReferenceCount(connection, fileName) == 0)
                        TryDelete(original);
                    if (File.Exists(original)) complete = false;
                }
                continue;
            }

            long length;
            try { length = new FileInfo(original).Length; }
            catch (FileNotFoundException) { complete = false; continue; }

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
            var updated = new List<(long ImageId, int Tier, long OldOffset, int Length, long NewOffset)>(rows.Count);
            try
            {
                // A concurrent writer may append to original, never modify
                // existing bytes. The replacement holds only snapshot rows.
                _beforePackCopy?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
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
                        updated.Add((row.ImageId, row.Tier, row.Offset, row.Length, offset));
                    }
                    output.Flush(flushToDisk: true);
                }

                // Each batch has a separate, short transaction. If canceled or
                // crashed between batches, both packs remain valid; the durable
                // maintenance request causes a later reconciliation to finish.
                const int commitBatchSize = 128;
                for (var start = 0; start < updated.Count; start += commitBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (CacheLock)
                    {
                        using var transaction = connection.BeginTransaction();
                        using var update = connection.CreateCommand();
                        update.Transaction = transaction;
                        update.CommandText = """
                            UPDATE thumbnail_entries
                            SET pack_file = $newPack, byte_offset = $newOffset
                            WHERE image_id = $id AND tier = $tier
                              AND pack_file = $oldPack
                              AND byte_offset = $oldOffset AND byte_length = $length;
                            """;
                        update.Parameters.AddWithValue("$newPack", newName);
                        update.Parameters.AddWithValue("$oldPack", fileName);
                        var newOffset = update.Parameters.Add("$newOffset", SqliteType.Integer);
                        var oldOffset = update.Parameters.Add("$oldOffset", SqliteType.Integer);
                        var id = update.Parameters.Add("$id", SqliteType.Integer);
                        var tier = update.Parameters.Add("$tier", SqliteType.Integer);
                        var byteLength = update.Parameters.Add("$length", SqliteType.Integer);
                        foreach (var row in updated.Skip(start).Take(commitBatchSize))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            newOffset.Value = row.NewOffset;
                            oldOffset.Value = row.OldOffset;
                            id.Value = row.ImageId;
                            tier.Value = row.Tier;
                            byteLength.Value = row.Length;
                            // A deleted/rebuilt row is intentionally skipped.
                            // Its old bytes may become unreferenced but a newer
                            // version must not be overwritten by compaction.
                            update.ExecuteNonQuery();
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        transaction.Commit();
                    }
                }

                lock (CacheLock)
                {
                    // A writer may have appended new references while copying.
                    // Leave the old pack intact until no index row references it.
                    // Retire old, formerly indexed packs on the NEXT idle
                    // pass, after the UI has refreshed its loaded index. A
                    // page queued with the old index may still open the file.
                    // The next pass sees zero references and reclaims it.
                    complete = false;
                    if (PackReferenceCount(connection, newName) == 0)
                        TryDelete(newPath);
                }
            }
            catch
            {
                // Partially committed replacement rows remain readable.
                // Delete the new pack ONLY if it is not referenced.
                lock (CacheLock)
                {
                    if (PackReferenceCount(connection, newName) == 0)
                        TryDelete(newPath);
                }
                throw;
            }
        }
        return complete;
    }

    private static long PackReferenceCount(SqliteConnection connection, string packFile)
    {
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM thumbnail_entries WHERE pack_file = $pack;";
        query.Parameters.AddWithValue("$pack", packFile);
        return Convert.ToInt64(query.ExecuteScalar());
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
