using System.IO;
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

    // A checkpoint is valid only for the same source membership and scan epoch.
    // Stale entries remain harmless and are ignored without touching catalog data.
    // Read only the already committed directory receipts of currently
    // pending first imports. Old scan epochs and changed root selections are
    // excluded in the DB; an indexed picture alone is NOT proof of a scan.
    // The existing scheduler catalog-read lane owns this snapshot.
    public IReadOnlyList<string> GetVerifiedPendingInitialDirectories()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT d.local_path
            FROM initial_scan_directories d
            JOIN monitored_folders m ON m.id = d.monitored_folder_id
            JOIN scheduler_sources s ON s.canonical_path = m.canonical_path
            WHERE m.initial_scan_pending=1
              AND s.desired_present=1
              AND d.source_generation=s.generation
              AND d.scan_epoch=s.scan_epoch;
            """;
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public HashSet<string> GetCompletedInitialScanDirectories(
        long folderId, long generation, long epoch)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT d.local_path FROM initial_scan_directories d
            JOIN monitored_folders m ON m.id = d.monitored_folder_id
            JOIN scheduler_sources s ON s.canonical_path = m.canonical_path
            WHERE d.monitored_folder_id = $id AND m.initial_scan_pending = 1
              AND d.source_generation = $generation AND d.scan_epoch = $epoch
              AND s.generation = $generation AND s.scan_epoch = $epoch
              AND s.desired_present = 1;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$epoch", epoch);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) completed.Add(reader.GetString(0));
        return completed;
    }

    public Dictionary<string, string> GetInitialScanFileCursors(
        long folderId, long generation, long epoch)
    {
        var cursors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.local_directory, c.last_file_name
            FROM initial_scan_file_cursors c
            JOIN monitored_folders m ON m.id = c.monitored_folder_id
            JOIN scheduler_sources s ON s.canonical_path = m.canonical_path
            WHERE c.monitored_folder_id = $id AND m.initial_scan_pending = 1
              AND c.source_generation = $generation AND c.scan_epoch = $epoch
              AND s.generation = $generation AND s.scan_epoch = $epoch
              AND s.desired_present = 1;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$epoch", epoch);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cursors[reader.GetString(0)] = reader.GetString(1);
        return cursors;
    }

    public sealed record PendingAutomaticInventory(
        long MonitoredFolderId, string ScanKey, string ScanRoot,
        long SourceGeneration);

    // A full-root review is a bounded cycle, never a perpetual "start again"
    // loop. Every source gets one turn, oldest/never-verified first; another
    // cycle is only due after its minimum interval. Directory timestamps are
    // kept independently for inspection and future subdirectory scheduling.
    public IReadOnlyList<long> GetDueRootReviewIds(
        DateTime utcNow, TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT m.id
              FROM monitored_folders m
              JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
              LEFT JOIN scan_root_review r ON r.monitored_folder_id=m.id
             WHERE s.desired_present=1 AND m.initial_scan_pending=0
               AND (r.monitored_folder_id IS NULL
                    OR r.source_generation != s.generation
                    OR r.retry_after_utc IS NULL OR r.retry_after_utc <= $now)
               AND (r.monitored_folder_id IS NULL
                    OR r.source_generation != s.generation
                    OR r.last_completed_utc IS NULL OR r.last_completed_utc <= $cutoff)
             ORDER BY COALESCE(r.last_completed_utc,'') ASC,
                      m.local_path COLLATE NOCASE ASC;
            """;
        cmd.Parameters.AddWithValue("$now", utcNow.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$cutoff",
            (utcNow.ToUniversalTime() - minimumInterval).ToString("O"));
        using var reader = cmd.ExecuteReader();
        var due = new List<long>();
        while (reader.Read()) due.Add(reader.GetInt64(0));
        return due;
    }

    public DateTime? GetLastCompletedRootReview(long monitoredFolderId)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT last_completed_utc FROM scan_root_review
             WHERE monitored_folder_id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", monitoredFolderId);
        var value = cmd.ExecuteScalar();
        return value is string date &&
            DateTime.TryParse(date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed : null;
    }

    public IReadOnlyDictionary<string, DateTime> GetVerifiedDirectories(
        long monitoredFolderId, long sourceGeneration)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT local_directory,verified_utc FROM scan_directory_verified
             WHERE monitored_folder_id=$id AND source_generation=$generation;
            """;
        cmd.Parameters.AddWithValue("$id", monitoredFolderId);
        cmd.Parameters.AddWithValue("$generation", sourceGeneration);
        var rows = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows[reader.GetString(0)] = DateTime.Parse(reader.GetString(1),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
        return rows;
    }

    // Unreachable/invalid roots must not be picked again every 30 seconds.
    // No source/image/scan receipt is cleared. Its saved cursor remains ready
    // when the next retry window arrives or the user explicitly requests it.
    public void DeferRootReview(long folderId, long generation,
        DateTime retryAfterUtc)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scan_root_review(monitored_folder_id,source_generation,
                                         last_completed_utc,retry_after_utc)
            SELECT m.id,$generation,NULL,$retry
              FROM monitored_folders m
              JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
             WHERE m.id=$id AND s.desired_present=1 AND s.generation=$generation
            ON CONFLICT(monitored_folder_id) DO UPDATE SET
                last_completed_utc=CASE
                    WHEN scan_root_review.source_generation=excluded.source_generation
                    THEN scan_root_review.last_completed_utc ELSE NULL END,
                source_generation=excluded.source_generation,
                retry_after_utc=excluded.retry_after_utc;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$retry", retryAfterUtc.ToUniversalTime().ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // An unfinished automatic scan is a durable identity, not just a progress
    // counter. Startup must recover this exact key before considering a fresh
    // root inventory, otherwise a new request rechecks already committed files.
    // Only receipts still valid for the source generation AND scan epoch count.
    public IReadOnlyList<PendingAutomaticInventory> GetPendingAutomaticInventories()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT j.monitored_folder_id, j.scan_key, j.scan_root,
                   j.source_generation
            FROM scan_resume_jobs j
            JOIN monitored_folders m ON m.id=j.monitored_folder_id
            JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
            WHERE j.scan_key LIKE 'background:inventory:%'
              AND j.source_generation=s.generation
              AND j.scan_epoch=s.scan_epoch
              AND s.desired_present=1 AND m.initial_scan_pending=0
            ORDER BY j.started_utc, j.scan_key;
            """;
        var result = new List<PendingAutomaticInventory>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new PendingAutomaticInventory(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt64(3)));
        return result;
    }

    public sealed record RegularScanCheckpoint(
        long Generation, long Epoch, int Checked,
        HashSet<string> Completed, Dictionary<string, string> Cursors);

    // Read-only scan progress for every UI projection. The taskmanager and
    // floating progress window must not use different zero-based counters
    // after a process restart. The durable scan receipt is authoritative.
    public int GetConfirmedRegularScanCount(string scanKey)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT checked_count FROM scan_resume_jobs
            WHERE scan_key=$key LIMIT 1;
            """;
        command.Parameters.AddWithValue("$key", scanKey);
        return command.ExecuteScalar() is { } count
            ? Convert.ToInt32(count) : 0;
    }

    // One scan identity owns its receipts throughout a restart. A *new*
    // manual request has a new scheduler package key and creates a fresh
    // inventory epoch. Recovered packages reuse their old identity/epoch.
    public RegularScanCheckpoint BeginOrResumeRegularScan(
        string scanKey, long folderId, string scanRoot,
        string sourceCanonicalPath, bool beginManualEpoch)
    {
        using var connection = _database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var source = connection.CreateCommand();
        source.Transaction = tx;
        source.CommandText = """
            SELECT generation, scan_epoch FROM scheduler_sources
            WHERE canonical_path=$path AND desired_present=1;
            """;
        source.Parameters.AddWithValue("$path", sourceCanonicalPath);
        long generation, currentEpoch;
        using (var reader = source.ExecuteReader())
        {
            if (!reader.Read())
                throw new OperationCanceledException("Scan source no longer monitored.");
            generation = reader.GetInt64(0);
            currentEpoch = reader.GetInt64(1);
        }

        long epoch;
        int checkedCount;
        using (var previous = connection.CreateCommand())
        {
            previous.Transaction = tx;
            previous.CommandText = """
                SELECT monitored_folder_id, scan_root, source_generation,
                       scan_epoch, checked_count
                FROM scan_resume_jobs WHERE scan_key=$key;
                """;
            previous.Parameters.AddWithValue("$key", scanKey);
            using var reader = previous.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetInt64(0) != folderId ||
                    !string.Equals(reader.GetString(1), scanRoot,
                        StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt64(2) != generation)
                    throw new OperationCanceledException(
                        "Obsolete scan receipt belongs to another source generation.");
                epoch = reader.GetInt64(3);
                checkedCount = reader.GetInt32(4);
            }
            else
            {
                epoch = -1;
                checkedCount = 0;
            }
        }
        if (epoch < 0)
        {
            epoch = currentEpoch;
            if (beginManualEpoch)
            {
                using var advance = connection.CreateCommand();
                advance.Transaction = tx;
                advance.CommandText = """
                    UPDATE scheduler_sources
                    SET scan_epoch=scan_epoch+1, updated_utc=$now
                    WHERE canonical_path=$path AND generation=$generation
                      AND desired_present=1 RETURNING scan_epoch;
                    """;
                advance.Parameters.AddWithValue("$path", sourceCanonicalPath);
                advance.Parameters.AddWithValue("$generation", generation);
                advance.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                epoch = advance.ExecuteScalar() is { } next
                    ? Convert.ToInt64(next)
                    : throw new OperationCanceledException("Scan source changed.");
            }
            using var create = connection.CreateCommand();
            create.Transaction = tx;
            create.CommandText = """
                INSERT INTO scan_resume_jobs
                    (scan_key,monitored_folder_id,scan_root,source_generation,
                     scan_epoch,checked_count,started_utc)
                VALUES ($key,$id,$root,$generation,$epoch,0,$now);
                """;
            create.Parameters.AddWithValue("$key", scanKey);
            create.Parameters.AddWithValue("$id", folderId);
            create.Parameters.AddWithValue("$root", scanRoot);
            create.Parameters.AddWithValue("$generation", generation);
            create.Parameters.AddWithValue("$epoch", epoch);
            create.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            create.ExecuteNonQuery();
        }
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var rows = connection.CreateCommand())
        {
            rows.Transaction = tx;
            rows.CommandText = """
                SELECT local_directory,last_file_name,completed
                FROM scan_resume_directories WHERE scan_key=$key;
                """;
            rows.Parameters.AddWithValue("$key", scanKey);
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                var directory = reader.GetString(0);
                if (reader.GetInt64(2) != 0) completed.Add(directory);
                else if (!reader.IsDBNull(1))
                    cursors[directory] = reader.GetString(1);
            }
        }
        tx.Commit();
        return new RegularScanCheckpoint(generation, epoch, checkedCount,
            completed, cursors);
    }

    // This is the durable *first-pass* frontier per physical directory.
    // Unlike scan_resume_jobs it deliberately outlives a completed package.
    // Only first imports/manual continuation read it; later low-priority
    // background reconciliation still does a full source inventory.
    public sealed record DirectoryFirstPass(
        HashSet<string> Completed, Dictionary<string, string> Cursors);

    public DirectoryFirstPass GetDirectoryFirstPass(
        long folderId, long generation, string scanRoot)
    {
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT local_directory,last_file_name,completed
              FROM scan_directory_first_pass
             WHERE monitored_folder_id=$id AND source_generation=$generation;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$generation", generation);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var directory = reader.GetString(0);
            if (!FolderExclusionRules.IsAtOrBelow(directory, scanRoot))
                continue;
            if (reader.GetInt32(2) != 0)
                completed.Add(directory);
            else if (!reader.IsDBNull(1))
                cursors[directory] = reader.GetString(1);
        }
        return new DirectoryFirstPass(completed, cursors);
    }

    // A monotonic prefix may advance only after a file's catalog commit (or
    // an explicitly logged skipped-file failure) and the regular scan receipt.
    // A change in source membership always invalidates the old frontier.
    private const string FirstPassUpsert = """
        ON CONFLICT(monitored_folder_id,local_directory) DO UPDATE SET
            source_generation=excluded.source_generation,
            completed=CASE
                WHEN excluded.source_generation >
                     scan_directory_first_pass.source_generation
                THEN excluded.completed
                ELSE MAX(scan_directory_first_pass.completed,excluded.completed)
            END,
            last_file_name=CASE
                WHEN excluded.source_generation >
                     scan_directory_first_pass.source_generation
                THEN excluded.last_file_name
                WHEN scan_directory_first_pass.completed=1 OR excluded.completed=1
                THEN NULL
                WHEN scan_directory_first_pass.last_file_name IS NULL
                THEN excluded.last_file_name
                WHEN excluded.last_file_name IS NULL
                THEN scan_directory_first_pass.last_file_name
                WHEN excluded.last_file_name COLLATE NOCASE >
                     scan_directory_first_pass.last_file_name COLLATE NOCASE
                THEN excluded.last_file_name
                ELSE scan_directory_first_pass.last_file_name
            END,
            updated_utc=excluded.updated_utc
        WHERE excluded.source_generation >=
              scan_directory_first_pass.source_generation
        """;

    public bool SaveDirectoryFirstPass(long folderId, long generation,
        string directory, string? fileName, bool completed)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO scan_directory_first_pass(
                monitored_folder_id,local_directory,source_generation,
                last_file_name,completed,updated_utc)
            SELECT $id,$directory,$generation,$file,$completed,$now
             WHERE EXISTS(
                 SELECT 1 FROM monitored_folders m
                   JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
                  WHERE m.id=$id AND s.desired_present=1
                    AND s.generation=$generation)
            {FirstPassUpsert};
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$directory", directory);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$file", (object?)fileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    // Upgrade ALL unfinished old scans belonging to the same monitored
    // source, not just the currently selected new job ID. This imports
    // already confirmed subdirectories from older P0 and initial restore
    // attempts without replaying any images or changing those original jobs.
    public void ImportFirstPassFromRegularScans(
        long folderId, long generation)
    {
        using var connection = _database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO scan_directory_first_pass(
                monitored_folder_id,local_directory,source_generation,
                last_file_name,completed,updated_utc)
            SELECT j.monitored_folder_id,d.local_directory,j.source_generation,
                   d.last_file_name,d.completed,$now
              FROM scan_resume_directories d
              JOIN scan_resume_jobs j ON j.scan_key=d.scan_key
              JOIN monitored_folders m ON m.id=j.monitored_folder_id
              JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
             WHERE j.monitored_folder_id=$id
               AND j.source_generation=$generation
               AND s.generation=$generation AND s.desired_present=1
            {FirstPassUpsert};
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    // A file receipt is durable only AFTER the image's catalog transaction
    // committed. Both the file cursor and its visible progress count are
    // persisted in this single short transaction.
    public bool SaveRegularScanFile(string scanKey, string directory,
        string fileName, int checkedCount, long generation, long epoch)
    {
        return SaveRegularScanReceipt(scanKey, directory,
            fileName, completed: false, checkedCount, generation, epoch);
    }

    public bool CompleteRegularScanDirectory(string scanKey, string directory,
        int checkedCount, long generation, long epoch)
    {
        return SaveRegularScanReceipt(scanKey, directory,
            null, completed: true, checkedCount, generation, epoch);
    }

    private bool SaveRegularScanReceipt(string scanKey, string directory,
        string? fileName, bool completed, int checkedCount,
        long generation, long epoch)
    {
        using var connection = _database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var validate = connection.CreateCommand();
        validate.Transaction = tx;
        validate.CommandText = """
            UPDATE scan_resume_jobs
            SET checked_count=MAX(checked_count,$count)
            WHERE scan_key=$key AND source_generation=$generation AND scan_epoch=$epoch
              AND EXISTS (
                SELECT 1 FROM monitored_folders m
                JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
                WHERE m.id=scan_resume_jobs.monitored_folder_id
                  AND s.generation=$generation AND s.desired_present=1);
            """;
        validate.Parameters.AddWithValue("$key", scanKey);
        validate.Parameters.AddWithValue("$generation", generation);
        validate.Parameters.AddWithValue("$epoch", epoch);
        validate.Parameters.AddWithValue("$count", checkedCount);
        if (validate.ExecuteNonQuery() != 1)
        {
            tx.Rollback();
            return false;
        }
        using var receipt = connection.CreateCommand();
        receipt.Transaction = tx;
        receipt.CommandText = """
            INSERT INTO scan_resume_directories
                (scan_key,local_directory,last_file_name,completed)
            VALUES ($key,$dir,$file,$completed)
            ON CONFLICT(scan_key,local_directory) DO UPDATE SET
                last_file_name=excluded.last_file_name,
                completed=excluded.completed;
            """;
        receipt.Parameters.AddWithValue("$key", scanKey);
        receipt.Parameters.AddWithValue("$dir", directory);
        receipt.Parameters.AddWithValue("$file", (object?)fileName ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        receipt.ExecuteNonQuery();

        // A directory timestamp is proof of a successfully enumerated
        // directory only, never of a whole-root cycle. The same source/epoch
        // fence and transaction guard both the checkpoint and timestamp.
        if (completed && scanKey.StartsWith("background:inventory:",
                StringComparison.Ordinal))
        {
            using var verified = connection.CreateCommand();
            verified.Transaction = tx;
            verified.CommandText = """
                INSERT INTO scan_directory_verified
                    (monitored_folder_id,local_directory,
                     source_generation,verified_utc)
                SELECT j.monitored_folder_id,$directory,j.source_generation,$now
                  FROM scan_resume_jobs j
                  JOIN monitored_folders m ON m.id=j.monitored_folder_id
                  JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
                 WHERE j.scan_key=$key AND j.source_generation=$generation
                   AND j.scan_epoch=$epoch AND s.generation=$generation
                   AND s.scan_epoch=$epoch AND s.desired_present=1
                ON CONFLICT(monitored_folder_id,local_directory) DO UPDATE SET
                    source_generation=excluded.source_generation,
                    verified_utc=excluded.verified_utc;
                """;
            verified.Parameters.AddWithValue("$directory", directory);
            verified.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            verified.Parameters.AddWithValue("$key", scanKey);
            verified.Parameters.AddWithValue("$generation", generation);
            verified.Parameters.AddWithValue("$epoch", epoch);
            verified.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public void FinishRegularScan(string scanKey, long generation, long epoch)
    {
        using var connection = _database.OpenConnection();
        using var tx = connection.BeginTransaction();
        // Mark a *root-wide* cycle complete only after the caller has
        // enumerated every included subdirectory and reached the scanner's
        // normal success path. The directory receipt itself was written
        // earlier, but is not proof that the subtree was finished.
        long? completedRoot = null;
        if (scanKey.StartsWith("background:inventory:", StringComparison.Ordinal) &&
            scanKey.Contains(":RescanRoot:True:", StringComparison.Ordinal))
        {
            using var root = connection.CreateCommand();
            root.Transaction = tx;
            root.CommandText = """
                SELECT j.monitored_folder_id,j.scan_root,m.local_path
                  FROM scan_resume_jobs j
                  JOIN monitored_folders m ON m.id=j.monitored_folder_id
                  JOIN scheduler_sources s ON s.canonical_path=m.canonical_path
                 WHERE j.scan_key=$key AND j.source_generation=$generation
                   AND j.scan_epoch=$epoch AND s.generation=$generation
                   AND s.scan_epoch=$epoch AND s.desired_present=1;
                """;
            root.Parameters.AddWithValue("$key", scanKey);
            root.Parameters.AddWithValue("$generation", generation);
            root.Parameters.AddWithValue("$epoch", epoch);
            using var reader = root.ExecuteReader();
            if (reader.Read() &&
                string.Equals(FolderExclusionRules.Normalize(reader.GetString(1)),
                    FolderExclusionRules.Normalize(reader.GetString(2)),
                    StringComparison.OrdinalIgnoreCase))
                completedRoot = reader.GetInt64(0);
        }
        if (completedRoot is not null)
        {
            using var verified = connection.CreateCommand();
            verified.Transaction = tx;
            verified.CommandText = """
                INSERT INTO scan_root_review
                    (monitored_folder_id,source_generation,
                     last_completed_utc,retry_after_utc)
                VALUES ($id,$generation,$now,NULL)
                ON CONFLICT(monitored_folder_id) DO UPDATE SET
                    source_generation=excluded.source_generation,
                    last_completed_utc=excluded.last_completed_utc,
                    retry_after_utc=NULL;
                """;
            verified.Parameters.AddWithValue("$id", completedRoot.Value);
            verified.Parameters.AddWithValue("$generation", generation);
            verified.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            verified.ExecuteNonQuery();
        }

        using var clear = connection.CreateCommand();
        clear.Transaction = tx;
        clear.CommandText = """
            DELETE FROM scan_resume_directories WHERE scan_key=$key
              AND EXISTS (SELECT 1 FROM scan_resume_jobs
                  WHERE scan_key=$key AND source_generation=$generation
                    AND scan_epoch=$epoch);
            """;
        clear.Parameters.AddWithValue("$key", scanKey);
        clear.Parameters.AddWithValue("$generation", generation);
        clear.Parameters.AddWithValue("$epoch", epoch);
        clear.ExecuteNonQuery();
        using var finish = connection.CreateCommand();
        finish.Transaction = tx;
        finish.CommandText = """
            DELETE FROM scan_resume_jobs WHERE scan_key=$key
              AND source_generation=$generation AND scan_epoch=$epoch;
            """;
        finish.Parameters.AddWithValue("$key", scanKey);
        finish.Parameters.AddWithValue("$generation", generation);
        finish.Parameters.AddWithValue("$epoch", epoch);
        finish.ExecuteNonQuery();
        tx.Commit();
    }

    public bool SaveInitialScanFileCursor(
        long folderId, string directory, string fileName,
        long generation, long epoch)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO initial_scan_file_cursors
                (monitored_folder_id, local_directory, last_file_name,
                 source_generation, scan_epoch, updated_utc)
            SELECT m.id, $directory, $file, $generation, $epoch, $now
            FROM monitored_folders m
            JOIN scheduler_sources s ON s.canonical_path = m.canonical_path
            WHERE m.id = $id AND m.initial_scan_pending = 1
              AND s.generation = $generation AND s.scan_epoch = $epoch
              AND s.desired_present = 1
            ON CONFLICT(monitored_folder_id, local_directory) DO UPDATE SET
                last_file_name = excluded.last_file_name,
                source_generation = excluded.source_generation,
                scan_epoch = excluded.scan_epoch,
                updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$directory", directory);
        cmd.Parameters.AddWithValue("$file", fileName);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$epoch", epoch);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool CompleteInitialScanDirectory(
        long folderId, string path, long generation, long epoch)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO initial_scan_directories
                (monitored_folder_id, local_path, source_generation, scan_epoch, completed_utc)
            SELECT m.id, $path, $generation, $epoch, $now
            FROM monitored_folders m
            JOIN scheduler_sources s ON s.canonical_path = m.canonical_path
            WHERE m.id = $id AND m.initial_scan_pending = 1
              AND s.generation = $generation AND s.scan_epoch = $epoch
              AND s.desired_present = 1
            ON CONFLICT(monitored_folder_id, local_path) DO UPDATE SET
                source_generation = excluded.source_generation,
                scan_epoch = excluded.scan_epoch,
                completed_utc = excluded.completed_utc;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$generation", generation);
        cmd.Parameters.AddWithValue("$epoch", epoch);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool CompleteInitialScan(long folderId, long expectedGeneration,
        long? expectedScanEpoch = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
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
        var completed = update.ExecuteNonQuery() == 1;
        if (completed)
        {
            // Checkpoints have no purpose once the initial import is done.
            // Remove them atomically with the durable completion marker.
            using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                "DELETE FROM initial_scan_directories WHERE monitored_folder_id = $id;";
            cleanup.Parameters.AddWithValue("$id", folderId);
            cleanup.ExecuteNonQuery();
            using var clearCursors = connection.CreateCommand();
            clearCursors.Transaction = transaction;
            clearCursors.CommandText =
                "DELETE FROM initial_scan_file_cursors WHERE monitored_folder_id = $id;";
            clearCursors.Parameters.AddWithValue("$id", folderId);
            clearCursors.ExecuteNonQuery();
        }
        transaction.Commit();
        return completed;
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
