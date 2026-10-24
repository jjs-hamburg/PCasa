using Microsoft.Data.Sqlite;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App.Data;

public sealed class FolderRepository
{
    private readonly LocalDatabase _database;

    public FolderRepository(LocalDatabase database)
    {
        _database = database;
    }

    public IReadOnlyList<MonitoredFolder> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, local_path, canonical_path, is_network, server_sync_enabled
            FROM monitored_folders
            ORDER BY local_path COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var result = new List<MonitoredFolder>();

        while (reader.Read())
        {
            result.Add(new MonitoredFolder
            {
                Id = reader.GetInt64(0),
                LocalPath = reader.GetString(1),
                CanonicalPath = reader.GetString(2),
                IsNetwork = reader.GetBoolean(3),
                ServerSyncEnabled = reader.GetBoolean(4)
            });
        }

        return result;
    }

    // File creation and durable folder-tree registration are one scheduled
    // operation. A client restart must not erase an empty NAS directory from
    // the tree merely because no image has been indexed there yet.
    public void RegisterCreatedDirectory(long monitoredFolderId, string localPath)
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO created_photo_directories
                (monitored_folder_id, local_path, created_utc)
            VALUES ($id, $path, $now);
            """;
        command.Parameters.AddWithValue("$id", monitoredFolderId);
        command.Parameters.AddWithValue("$path", FolderExclusionRules.Normalize(localPath));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    // Read on the scheduler catalog lane, then publish into the UI's
    // already-existing folder-tree snapshot. Never probe SMB from WPF.
    public IReadOnlyList<string> GetCreatedDirectories()
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.local_path FROM created_photo_directories c
            JOIN monitored_folders m ON m.id = c.monitored_folder_id
            WHERE NOT EXISTS (
                SELECT 1 FROM excluded_folders e
                WHERE e.monitored_folder_id = c.monitored_folder_id
                  AND (c.local_path = e.local_path COLLATE NOCASE
                    OR substr(c.local_path, 1, length(e.local_path) + 1) =
                        e.local_path || char(92) COLLATE NOCASE)
            )
            ORDER BY c.local_path COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    // The file system owns existence. A directory made through PCasa is
    // only a persistent tree projection while it actually exists. An
    // authoritative, successful manual inventory may prune absent paths.
    // Do NOT prune an unavailable NAS root: the user could simply be offline.
    // Disk probes remain in the scheduler's source worker, never in WPF.
    public IReadOnlyList<string> PruneMissingCreatedDirectories(
        long monitoredFolderId, string scannedDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(scannedDirectory))
            return Array.Empty<string>();

        using var db = _database.OpenConnection();
        using var read = db.CreateCommand();
        read.CommandText = """
            SELECT local_path FROM created_photo_directories
            WHERE monitored_folder_id = $owner;
            """;
        read.Parameters.AddWithValue("$owner", monitoredFolderId);
        var candidates = new List<string>();
        using (var reader = read.ExecuteReader())
            while (reader.Read()) candidates.Add(reader.GetString(0));

        var missing = new List<string>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FolderExclusionRules.IsAtOrBelow(candidate, scannedDirectory) ||
                Directory.Exists(candidate))
                continue;
            // Never conclude an entire SMB subtree is deleted solely from
            // an unresponsive path. Require a reachable surviving ancestor
            // inside the successfully scanned directory.
            var ancestor = Path.GetDirectoryName(candidate);
            while (!string.IsNullOrWhiteSpace(ancestor) &&
                   FolderExclusionRules.IsAtOrBelow(ancestor, scannedDirectory) &&
                   !Directory.Exists(ancestor))
                ancestor = Path.GetDirectoryName(ancestor);
            if (string.IsNullOrWhiteSpace(ancestor) ||
                !FolderExclusionRules.IsAtOrBelow(ancestor, scannedDirectory) ||
                !Directory.Exists(ancestor))
                continue;
            missing.Add(candidate);
        }
        if (missing.Count == 0) return Array.Empty<string>();

        using var transaction = db.BeginTransaction();
        using var remove = db.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = """
            DELETE FROM created_photo_directories
            WHERE monitored_folder_id = $owner
              AND local_path = $path COLLATE NOCASE;
            """;
        remove.Parameters.AddWithValue("$owner", monitoredFolderId);
        var pathParam = remove.Parameters.Add("$path", SqliteType.Text);
        foreach (var candidate in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck immediately before deletion, in case another manual
            // packet recreated the path between the scan and this commit.
            if (Directory.Exists(candidate)) continue;
            pathParam.Value = candidate;
            remove.ExecuteNonQuery();
        }
        transaction.Commit();
        return missing.Where(path => !Directory.Exists(path)).ToArray();
    }

    public void Add(string path)
    {
        var classification = PathClassifier.Classify(path);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO monitored_folders
                (local_path, canonical_path, is_network, server_sync_enabled,
                 created_utc, initial_scan_pending)
            VALUES
                ($localPath, $canonicalPath, $isNetwork, 0, $createdUtc, 1);
            """;
        command.Parameters.AddWithValue("$localPath", classification.LocalPath);
        command.Parameters.AddWithValue("$canonicalPath", classification.CanonicalPath);
        command.Parameters.AddWithValue("$isNetwork", classification.IsNetwork ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
        var inserted = command.ExecuteNonQuery();
        if (inserted > 0)
        {
            // Source identity is independent of the replaceable monitored-folder ID.
            // Only an actual new inclusion advances the durable generation.
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = """
                INSERT INTO scheduler_sources
                    (canonical_path, generation, desired_present, updated_utc)
                VALUES ($path, 1, 1, $now)
                ON CONFLICT(canonical_path) DO UPDATE SET
                    generation = generation + 1,
                    desired_present = 1,
                    updated_utc = excluded.updated_utc;
                """;
            source.Parameters.AddWithValue("$path", classification.CanonicalPath);
            source.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            source.ExecuteNonQuery();
        }
        // Selecting an already-monitored root again is also an explicit
        // request to read it, even if the old first-scan request was lost.
        using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText = """
                UPDATE monitored_folders SET initial_scan_pending = 1
                WHERE local_path = $path COLLATE NOCASE;
                """;
            pending.Parameters.AddWithValue("$path", classification.LocalPath);
            pending.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    // A durable first-import request survives cancellation, app restart and
    // a temporarily unreachable SMB share. It is not conditional on the
    // optional background folder watcher being enabled.
    public IReadOnlyList<MonitoredFolder> GetPendingInitialScans()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT local_path FROM monitored_folders
                WHERE initial_scan_pending = 1;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }
        return GetAll().Where(folder => paths.Contains(folder.LocalPath)).ToArray();
    }

    public bool CompleteInitialScan(long folderId, long expectedGeneration,
        long? expectedScanEpoch = null)
    {
        using var connection = _database.OpenConnection();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE monitored_folders SET initial_scan_pending = 0
            WHERE id = $id AND initial_scan_pending = 1
              AND EXISTS (
                  SELECT 1 FROM scheduler_sources s
                  WHERE s.canonical_path = monitored_folders.canonical_path
                    AND s.generation = $generation AND s.desired_present = 1
                    AND ($scanEpoch IS NULL OR s.scan_epoch = $scanEpoch));
            """;
        update.Parameters.AddWithValue("$id", folderId);
        update.Parameters.AddWithValue("$generation", expectedGeneration);
        update.Parameters.AddWithValue("$scanEpoch", (object?)expectedScanEpoch ?? DBNull.Value);
        return update.ExecuteNonQuery() == 1;
    }

    public void Remove(long id, IProgress<string>? progress = null)
    {
        // User-visible removal is a single transactional catalog change.
        // Expensive physical thumbnail cleanup is persisted and runs at idle,
        // not while the folder manager is waiting.
        progress?.Report("Ordner aus dem Katalog entfernen …");
        new CatalogRemovalService(_database).DetachMonitoredFolder(id);
        progress?.Report("Ordner entfernt · Cache-Bereinigung erfolgt im Leerlauf");
    }

    public IReadOnlyList<string> GetExcludedPaths(long monitoredFolderId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_path FROM excluded_folders
            WHERE monitored_folder_id = $id
            ORDER BY local_path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", monitoredFolderId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public long SetExcluded(long monitoredFolderId, string subfolderPath, bool excluded)
    {
        var monitored = GetAll().FirstOrDefault(item => item.Id == monitoredFolderId)
            ?? throw new InvalidOperationException("Der überwachte Ordner existiert nicht mehr.");
        var normalized = FolderExclusionRules.Normalize(subfolderPath);
        if (!FolderExclusionRules.IsAtOrBelow(normalized, monitored.LocalPath))
        {
            throw new InvalidOperationException(
                "Ordner liegt nicht innerhalb der überwachten Wurzel.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = excluded
            ? """
              INSERT OR IGNORE INTO excluded_folders
                  (monitored_folder_id, local_path, created_utc)
              VALUES ($id, $path, $now);
              """
            : """
              DELETE FROM excluded_folders
              WHERE monitored_folder_id = $id AND local_path = $path COLLATE NOCASE;
              """;
        command.Parameters.AddWithValue("$id", monitoredFolderId);
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var changed = command.ExecuteNonQuery();

        if (excluded)
        {
            // Redundant exclusions nested under a newly excluded parent are
            // removed so re-including the parent behaves predictably.
            using var descendants = connection.CreateCommand();
            descendants.Transaction = transaction;
            descendants.CommandText = """
                SELECT local_path FROM excluded_folders
                WHERE monitored_folder_id = $id;
                """;
            descendants.Parameters.AddWithValue("$id", monitoredFolderId);
            using var reader = descendants.ExecuteReader();
            var redundant = new List<string>();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (!string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) &&
                    FolderExclusionRules.IsAtOrBelow(path, normalized))
                {
                    redundant.Add(path);
                }
            }

            reader.Close();
            foreach (var path in redundant)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM excluded_folders
                    WHERE monitored_folder_id = $id AND local_path = $path;
                    """;
                delete.Parameters.AddWithValue("$id", monitoredFolderId);
                delete.Parameters.AddWithValue("$path", path);
                delete.ExecuteNonQuery();
            }
        }

        if (changed > 0)
        {
            // A change to exclusions invalidates old scans in the SAME transaction.
            using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = """
                UPDATE scheduler_sources
                SET generation = generation + 1, updated_utc = $now
                WHERE canonical_path = $path AND desired_present = 1;
                """;
            source.Parameters.AddWithValue("$path", monitored.CanonicalPath);
            source.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (source.ExecuteNonQuery() != 1)
                throw new OperationCanceledException("Source removed during exclusion.");
        }
        // Capture the generation inside the decision's own write transaction.
        // A later lookup can observe a different request for this same path.
        using var readGeneration = connection.CreateCommand();
        readGeneration.Transaction = transaction;
        readGeneration.CommandText = """
            SELECT generation FROM scheduler_sources
            WHERE canonical_path = $path AND desired_present = 1;
            """;
        readGeneration.Parameters.AddWithValue("$path", monitored.CanonicalPath);
        var generation = readGeneration.ExecuteScalar();
        if (generation is null)
            throw new OperationCanceledException("Monitored source was removed.");
        transaction.Commit();
        return Convert.ToInt64(generation);
    }

    // Starting a newer scan invalidates ONLY the older inventory's authority
    // to prune missing paths. It must not invalidate already completed EXIF,
    // thumbnails, or the enumeration cursor of a suspended older scan.
    // Source generation remains reserved for remove/re-add/exclusion changes.
    public long BeginManualScan(string canonicalPath)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE scheduler_sources
            SET scan_epoch = scan_epoch + 1, updated_utc = $now
            WHERE canonical_path = $path AND desired_present = 1
            RETURNING scan_epoch;
            """;
        update.Parameters.AddWithValue("$path", canonicalPath);
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var generation = update.ExecuteScalar();
        if (generation is null)
            throw new OperationCanceledException(
                "Der überwachte Ordner wurde inzwischen entfernt.");
        transaction.Commit();
        return Convert.ToInt64(generation);
    }

    // Every asynchronous operation captures this token before starting work.
    // A token is valid only while the same source is still desired at the
    // same generation. No old scan or cleanup may commit after re-inclusion.
    public long GetScanEpoch(string canonicalPath)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT scan_epoch FROM scheduler_sources WHERE canonical_path = $path AND desired_present = 1;";
        command.Parameters.AddWithValue("$path", canonicalPath);
        return command.ExecuteScalar() is { } epoch
            ? Convert.ToInt64(epoch)
            : throw new OperationCanceledException("The monitored source was removed.");
    }

    public (long Generation, bool DesiredPresent)? GetSourceState(string canonicalPath)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT generation, desired_present
            FROM scheduler_sources
            WHERE canonical_path = $path;
            """;
        command.Parameters.AddWithValue("$path", canonicalPath);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt64(0), reader.GetInt64(1) != 0);
    }

    public bool IsCurrentSourceGeneration(string canonicalPath, long generation, bool desiredPresent)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_sources
                WHERE canonical_path = $path
                  AND generation = $generation
                  AND desired_present = $desired
            );
            """;
        command.Parameters.AddWithValue("$path", canonicalPath);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$desired", desiredPresent ? 1 : 0);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    public void SetServerSync(long id, bool enabled)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE monitored_folders
            SET server_sync_enabled = CASE
                WHEN is_network = 1 AND substr(canonical_path, 1, 2) = '\\' THEN $enabled
                ELSE 0
            END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
