using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Services;

namespace PCasa.App.Data;

/// <summary>
/// Sole durable commit boundary for an entire folder-manager OK decision.
/// No dialog checkbox writes to the database or starts a scanner. Root,
/// exclusion and source-generation changes are atomic; foreign-key cascades
/// remove obsolete catalog rows in this same transaction.
/// </summary>
public sealed class FolderManagerSelectionService
{
    private readonly LocalDatabase _database;

    public FolderManagerSelectionService(LocalDatabase database) => _database = database;

    public bool Apply(FolderManagerSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // UNC classification may consult a network provider: do it BEFORE
        // holding the catalog/cache commit lock.
        var desired = selection.Roots.Select(choice => (
                Choice: choice,
                Classified: PathClassifier.Classify(choice.LocalPath)))
            .ToArray();
        if (desired.Select(x => x.Classified.LocalPath).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != desired.Length)
            throw new ArgumentException("Ein Überwachungsordner wurde mehrfach ausgewählt.");

        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = new Dictionary<string, (long Id, string Canonical)>(
                StringComparer.OrdinalIgnoreCase);

            using (var all = Command(connection, transaction, """
                SELECT id, local_path, canonical_path FROM monitored_folders;
                """))
            using (var reader = all.ExecuteReader())
            {
                while (reader.Read())
                    existing.Add(reader.GetString(1), (reader.GetInt64(0), reader.GetString(2)));
            }

            var selectedPaths = desired.Select(x => x.Classified.LocalPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var modified = false;
            var reclaim = false;
            var timestamp = DateTime.UtcNow.ToString("O");

            foreach (var (path, old) in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selectedPaths.Contains(path)) continue;

                using (var stale = Command(connection, transaction, """
                    UPDATE scheduler_sources
                    SET generation = generation + 1, desired_present = 0,
                        updated_utc = $now
                    WHERE canonical_path = $path;
                    """))
                {
                    stale.Parameters.AddWithValue("$path", old.Canonical);
                    stale.Parameters.AddWithValue("$now", timestamp);
                    stale.ExecuteNonQuery();
                }
                using (var delete = Command(connection, transaction,
                    "DELETE FROM monitored_folders WHERE id = $id;"))
                {
                    delete.Parameters.AddWithValue("$id", old.Id);
                    delete.ExecuteNonQuery();
                }
                reclaim = modified = true;
            }

            foreach (var (choice, classified) in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id;
                if (existing.TryGetValue(classified.LocalPath, out var current))
                    id = current.Id;
                else
                {
                    using (var insert = Command(connection, transaction, """
                        INSERT INTO monitored_folders (
                            local_path, canonical_path, is_network,
                            server_sync_enabled, created_utc, initial_scan_pending)
                        VALUES ($local, $canonical, $network, $sync, $now, 1)
                        RETURNING id;
                        """))
                    {
                        insert.Parameters.AddWithValue("$local", classified.LocalPath);
                        insert.Parameters.AddWithValue("$canonical", classified.CanonicalPath);
                        insert.Parameters.AddWithValue("$network", classified.IsNetwork ? 1 : 0);
                        insert.Parameters.AddWithValue("$sync",
                            classified.IsNetwork && classified.CanonicalPath.StartsWith(@"\\",
                                StringComparison.Ordinal) && choice.ServerSyncEnabled ? 1 : 0);
                        insert.Parameters.AddWithValue("$now", timestamp);
                        id = Convert.ToInt64(insert.ExecuteScalar());
                    }

                    using (var source = Command(connection, transaction, """
                        INSERT INTO scheduler_sources (
                            canonical_path, generation, desired_present, updated_utc)
                        VALUES ($path, 1, 1, $now)
                        ON CONFLICT(canonical_path) DO UPDATE SET
                            generation = generation + 1,
                            desired_present = 1,
                            updated_utc = excluded.updated_utc;
                        """))
                    {
                        source.Parameters.AddWithValue("$path", classified.CanonicalPath);
                        source.Parameters.AddWithValue("$now", timestamp);
                        source.ExecuteNonQuery();
                    }
                    modified = true;
                }

                var wanted = choice.ExcludedPaths.Select(FolderExclusionRules.Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var excluded in wanted)
                    if (!FolderExclusionRules.IsAtOrBelow(excluded, classified.LocalPath))
                        throw new ArgumentException($"Ausschluss liegt außerhalb der Wurzel: {excluded}");

                var previouslyExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var fetch = Command(connection, transaction, """
                    SELECT local_path FROM excluded_folders
                    WHERE monitored_folder_id = $id;
                    """))
                {
                    fetch.Parameters.AddWithValue("$id", id);
                    using var reader = fetch.ExecuteReader();
                    while (reader.Read()) previouslyExcluded.Add(reader.GetString(0));
                }

                if (!previouslyExcluded.SetEquals(wanted))
                {
                    using (var removeOld = Command(connection, transaction, """
                        DELETE FROM excluded_folders WHERE monitored_folder_id = $id;
                        """))
                    {
                        removeOld.Parameters.AddWithValue("$id", id);
                        removeOld.ExecuteNonQuery();
                    }
                    foreach (var excluded in wanted)
                    {
                        using var add = Command(connection, transaction, """
                            INSERT INTO excluded_folders (monitored_folder_id, local_path, created_utc)
                            VALUES ($id, $path, $now);
                            """);
                        add.Parameters.AddWithValue("$id", id);
                        add.Parameters.AddWithValue("$path", excluded);
                        add.Parameters.AddWithValue("$now", timestamp);
                        add.ExecuteNonQuery();
                    }

                    // A completed old scan cannot commit over a new checkbox
                    // decision. Re-inclusion requests a full prioritized scan.
                    using (var advance = Command(connection, transaction, """
                        UPDATE scheduler_sources SET generation = generation + 1,
                            updated_utc = $now
                        WHERE canonical_path = $canonical AND desired_present = 1;
                        """))
                    {
                        advance.Parameters.AddWithValue("$canonical", classified.CanonicalPath);
                        advance.Parameters.AddWithValue("$now", timestamp);
                        if (advance.ExecuteNonQuery() != 1)
                            throw new OperationCanceledException("Überwachungsordner nicht mehr aktiv.");
                    }

                    if (previouslyExcluded.Except(wanted, StringComparer.OrdinalIgnoreCase).Any())
                    {
                        using var pending = Command(connection, transaction, """
                            UPDATE monitored_folders SET initial_scan_pending = 1 WHERE id = $id;
                            """);
                        pending.Parameters.AddWithValue("$id", id);
                        pending.ExecuteNonQuery();
                    }

                    // Prune excluded photos in the same transaction as the
                    // exclusions. No scanner can repopulate them from an old
                    // source generation after OK.
                    foreach (var excluded in wanted)
                    {
                        using var prune = Command(connection, transaction, """
                            DELETE FROM images WHERE folder_id = $id
                            AND (local_path = $path COLLATE NOCASE OR
                                 substr(local_path, 1, length($prefix)) = $prefix COLLATE NOCASE);
                            """);
                        prune.Parameters.AddWithValue("$id", id);
                        prune.Parameters.AddWithValue("$path", excluded);
                        prune.Parameters.AddWithValue("$prefix",
                            Path.TrimEndingDirectorySeparator(excluded) + "\\");
                        if (prune.ExecuteNonQuery() > 0) reclaim = true;
                    }
                    using (var empty = Command(connection, transaction, """
                        DELETE FROM photo_folders
                        WHERE monitored_folder_id = $id
                        AND NOT EXISTS (SELECT 1 FROM images
                            WHERE photo_folder_id = photo_folders.id);
                        """))
                    {
                        empty.Parameters.AddWithValue("$id", id);
                        empty.ExecuteNonQuery();
                    }
                    modified = true;
                }

                using (var sync = Command(connection, transaction, """
                    UPDATE monitored_folders SET server_sync_enabled = $enabled
                    WHERE id = $id AND server_sync_enabled != $enabled;
                    """))
                {
                    sync.Parameters.AddWithValue("$id", id);
                    sync.Parameters.AddWithValue("$enabled",
                        classified.IsNetwork &&
                        classified.CanonicalPath.StartsWith(@"\\", StringComparison.Ordinal) &&
                        choice.ServerSyncEnabled ? 1 : 0);
                    if (sync.ExecuteNonQuery() > 0) modified = true;
                }
            }

            if (reclaim)
            {
                using var maintenance = Command(connection, transaction, """
                    UPDATE cache_maintenance
                    SET cleanup_pending = 1, request_generation = request_generation + 1
                    WHERE id = 1;
                    """);
                maintenance.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return modified;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection,
        SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
