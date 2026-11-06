using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

// One durable scheduler ledger. It stores data, not serialised delegates.
// Business steps are reconstructed by registered, versioned package factories.
public sealed record DurablePackageRecord(
    string Key, string Type, string Payload, SchedulerPriority Priority,
    SchedulerWorkKind WorkKind, long Sequence, int CompletedSteps,
    string State, string? Error)
{
    public long QueueOrder { get; init; }
}

public sealed class DurablePackageJournal
{
    private readonly LocalDatabase _database;

    public DurablePackageJournal(LocalDatabase database)
    {
        _database = database;
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduler_packages (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                package_key TEXT NOT NULL UNIQUE,
                package_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                priority INTEGER NOT NULL,
                work_kind INTEGER NOT NULL,
                completed_steps INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL DEFAULT 'queued',
                error TEXT,
                retry_attempts INTEGER NOT NULL DEFAULT 0,
                retry_after_utc TEXT,
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_scheduler_packages_active
                ON scheduler_packages(state, priority, sequence);
            """;
        cmd.ExecuteNonQuery();
        // Older PCasa installations already have scheduler_packages. Migrate
        // its durable ledger in place; do not rebuild or discard pending work.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var schema = db.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(scheduler_packages);";
            using var reader = schema.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("retry_attempts"))
        {
            using var add = db.CreateCommand();
            add.CommandText = "ALTER TABLE scheduler_packages ADD COLUMN retry_attempts INTEGER NOT NULL DEFAULT 0;";
            add.ExecuteNonQuery();
        }
        if (!columns.Contains("retry_after_utc"))
        {
            using var add = db.CreateCommand();
            add.CommandText = "ALTER TABLE scheduler_packages ADD COLUMN retry_after_utc TEXT;";
            add.ExecuteNonQuery();
        }
        // Legacy LIFO for P0, FIFO for other work. From now on these ranks
        // are the persisted ordering authority, not a separate UI list.
        if (!columns.Contains("queue_order"))
        {
            using var add = db.CreateCommand();
            add.CommandText = "ALTER TABLE scheduler_packages ADD COLUMN queue_order INTEGER;";
            add.ExecuteNonQuery();
        }
        using (var migrate = db.CreateCommand())
        {
            migrate.CommandText = """
                UPDATE scheduler_packages
                   SET queue_order=CASE WHEN priority=0 THEN -sequence ELSE sequence END
                 WHERE queue_order IS NULL;
                CREATE INDEX IF NOT EXISTS idx_scheduler_packages_order
                ON scheduler_packages(priority,work_kind,queue_order);
                """;
            migrate.ExecuteNonQuery();
        }
    }

    public DurablePackageRecord Put(string key, string type, string payload,
        SchedulerPriority priority, SchedulerWorkKind workKind)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scheduler_packages
                (package_key,package_type,payload_json,priority,work_kind,updated_utc)
            VALUES ($key,$type,$payload,$priority,$kind,$now)
            ON CONFLICT(package_key) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.Parameters.AddWithValue("$priority", (int)priority);
        cmd.Parameters.AddWithValue("$kind", (int)workKind);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        using (var rank = db.CreateCommand())
        {
            rank.CommandText = """
                UPDATE scheduler_packages
                   SET queue_order=CASE WHEN priority=0 THEN -sequence ELSE sequence END
                 WHERE package_key=$key AND queue_order IS NULL;
                """;
            rank.Parameters.AddWithValue("$key", key);
            rank.ExecuteNonQuery();
        }
        using var lookup = db.CreateCommand();
        lookup.CommandText = """
            SELECT sequence,priority,work_kind,completed_steps,state,error,
                   package_type,payload_json,COALESCE(queue_order,CASE WHEN priority=0 THEN -sequence ELSE sequence END)
              FROM scheduler_packages WHERE package_key=$key;
            """;
        lookup.Parameters.AddWithValue("$key", key);
        using var reader = lookup.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Paketjournal konnte nicht erstellt werden.");
        // An existing key may only refer to the identical immutable recipe.
        // Never attach a new caller to a different payload or silently change
        // its priority; doing so could replay destructive steps on wrong files.
        if (!string.Equals(reader.GetString(6), type, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(7), payload, StringComparison.Ordinal) ||
            reader.GetInt32(1) != (int)priority ||
            reader.GetInt32(2) != (int)workKind)
            throw new InvalidOperationException(
                "Paketidentität kollidiert mit einem anderen unveränderlichen Auftrag: " + key);
        return new DurablePackageRecord(key, reader.GetString(6), reader.GetString(7),
            (SchedulerPriority)reader.GetInt32(1), (SchedulerWorkKind)reader.GetInt32(2),
            reader.GetInt64(0), reader.GetInt32(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5))
        { QueueOrder = reader.GetInt64(8) };
    }

    public IReadOnlyList<DurablePackageRecord> Pending()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_key,package_type,payload_json,priority,work_kind,
                   sequence,completed_steps,state,error,COALESCE(queue_order,CASE WHEN priority=0 THEN -sequence ELSE sequence END)
              FROM scheduler_packages WHERE state IN ('queued','running','deferred')
             ORDER BY priority ASC, queue_order ASC, sequence ASC;
            """;
        using var reader = cmd.ExecuteReader();
        var results = new List<DurablePackageRecord>();
        while (reader.Read())
            results.Add(new DurablePackageRecord(reader.GetString(0),
                reader.GetString(1), reader.GetString(2),
                (SchedulerPriority)reader.GetInt32(3),
                (SchedulerWorkKind)reader.GetInt32(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))
                { QueueOrder = reader.GetInt64(9) });
        return results;
    }

    // Recent completion is optional presentation history, never a replay
    // source. Old records are not deleted to support diagnostics.
    public IReadOnlyList<DurablePackageRecord> RecentCompleted(int limit = 80)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_key,package_type,payload_json,priority,work_kind,
                   sequence,completed_steps,state,error,COALESCE(queue_order,CASE WHEN priority=0 THEN -sequence ELSE sequence END)
              FROM scheduler_packages
             WHERE state='completed'
             ORDER BY updated_utc DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = cmd.ExecuteReader();
        var rows = new List<DurablePackageRecord>();
        while (reader.Read())
            rows.Add(new DurablePackageRecord(reader.GetString(0),
                reader.GetString(1), reader.GetString(2),
                (SchedulerPriority)reader.GetInt32(3),
                (SchedulerWorkKind)reader.GetInt32(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))
                { QueueOrder = reader.GetInt64(9) });
        return rows;
    }

    // One persisted ordering authority for the scheduler AND taskmanager.
    // Reorder by rotating existing ranks inside one SQLite transaction: no
    // parallel UI priority queue, no rank collisions and no replay of steps.
    // A running task may be displaced; the scheduler must then yield it
    // cooperatively before selecting the promoted queued task.
    public IReadOnlyDictionary<string, long>? Reorder(string key, string targetKey)
    {
        if (string.Equals(key, targetKey, StringComparison.Ordinal)) return null;
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        var rows = new List<(string Key, long Rank, SchedulerPriority Priority,
            SchedulerWorkKind Kind, string State)>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT package_key,
                       COALESCE(queue_order,CASE WHEN priority=0 THEN -sequence ELSE sequence END),
                       priority, work_kind, state
                  FROM scheduler_packages
                 WHERE state IN ('queued','running','deferred')
                 ORDER BY priority ASC,queue_order ASC,sequence ASC;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetInt64(1),
                    (SchedulerPriority)reader.GetInt32(2),
                    (SchedulerWorkKind)reader.GetInt32(3), reader.GetString(4)));
        }
        var source = rows.FindIndex(item => item.Key == key);
        var target = rows.FindIndex(item => item.Key == targetKey);
        if (source < 0 || target < 0) return null;
        var selected = rows[source];
        var destination = rows[target];
        if (selected.Priority != destination.Priority ||
            !SameQueueLane(selected.Kind, destination.Kind))
            return null;
        // A moving source must still be queued (or a yielded worker already
        // back in the queue). The scheduler additionally rejects a live source.
        var ordered = rows.Where(row =>
                row.Priority == selected.Priority &&
                SameQueueLane(row.Kind, selected.Kind))
            .ToList();
        source = ordered.FindIndex(row => row.Key == key);
        target = ordered.FindIndex(row => row.Key == targetKey);
        if (source == target) return null;
        var ranks = ordered.Select(row => row.Rank).ToArray();
        ordered.RemoveAt(source);
        ordered.Insert(target, selected);
        var changed = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Rank == ranks[i]) continue;
            using var update = db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE scheduler_packages
                   SET queue_order=$rank, updated_utc=$now
                 WHERE package_key=$key AND state IN ('queued','running','deferred');
                """;
            update.Parameters.AddWithValue("$rank", ranks[i]);
            update.Parameters.AddWithValue("$key", ordered[i].Key);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (update.ExecuteNonQuery() != 1)
                throw new IOException("Task-Reihenfolge konnte nicht atomar gespeichert werden.");
            changed.Add(ordered[i].Key, ranks[i]);
        }
        tx.Commit();
        return changed;
    }

    // A Catalog task and a CatalogScan task share the same worker lane; a
    // source probe or initial import does not. Preserve that distinction.
    private static bool SameQueueLane(SchedulerWorkKind a, SchedulerWorkKind b)
        => a == b || (a is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan &&
                      b is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan);

    // Keep the existing adjacent API, but route through the same atomic
    // reorder as mouse drag/drop. There is only ONE mutation code path.
    public (string OtherKey, long SelectedOrder, long OtherOrder)? Move(
        string key, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var all = Pending();
        var source = all.FirstOrDefault(row => row.Key == key);
        if (source is null) return null;
        var lane = all.Where(row =>
                row.Priority == source.Priority &&
                SameQueueLane(row.WorkKind, source.WorkKind)).ToList();
        var i = lane.FindIndex(row => row.Key == key);
        if (i < 0 || i + direction < 0 || i + direction >= lane.Count)
            return null;
        var neighbor = lane[i + direction];
        var changed = Reorder(key, neighbor.Key);
        if (changed is null || !changed.TryGetValue(key, out var selectedRank) ||
            !changed.TryGetValue(neighbor.Key, out var neighborRank))
            return null;
        return (neighbor.Key, selectedRank, neighborRank);
    }

    public bool Cancel(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages SET state='canceled',
                   retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state IN ('queued','running','deferred');
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public DurablePackageRecord? RestartDeferred(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages SET state='queued',error=NULL,
                retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state='deferred';
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1
            ? Pending().FirstOrDefault(p => p.Key == key) : null;
    }

    // Old one-intent reconciliation packages were created with a NEW GUID
    // on every startup. An ambiguous filesystem state was logged and the
    // package marked completed, while the actual relocation intent remained
    // pending. Each later startup spawned another P0 probe and stalled scans.
    //
    // Retire only those obsolete *probe packages*. The authoritative
    // file_transfer_intents rows and their image identities remain untouched
    // for explicit review. Do not classify this as a successful file move.
    // The same migration is safe to invoke on every startup.
    public int RetireLegacyTransferReconciliationPackages()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state='failed',
                   error='Automatische Alt-Übertragungsprüfung ausgesetzt; '
                         || 'Quelldatei und Ziel müssen vor einer Katalogänderung '
                         || 'manuell geprüft werden.',
                   retry_after_utc=NULL,
                   updated_utc=$now
             WHERE package_type='transfer-reconcile-v1'
               AND state IN ('queued','running','deferred');
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var retired = cmd.ExecuteNonQuery();
        if (retired > 0)
            PCasaDiagnostics.WriteMessage("Scheduler.LegacyTransferRecovery",
                $"Retired {retired} old startup reconciliation probes; " +
                "all physical relocation intents preserved for review.");
        return retired;
    }

    // One-time versioned migration for already journaled V1 recovery.
    // V1 stored a fixed image list and a position within that list. A single
    // offline NAS image blocks all later LOCAL images in that same package.
    // V2 instead uses pending_tag_edits (the authoritative, durable list of
    // still-unconfirmed original writes) and retries every independent image.
    // Preserve original key, sequence, priority and work kind transactionally;
    // ONLY the obsolete recipe encoding/cursor are replaced. No user edit or
    // confirmed file write is invented or removed by this migration.
    public int UpgradeLegacyTagRecovery()
    {
        using var db = _database.OpenConnection();
        using var transaction = db.BeginTransaction();
        var legacy = new List<(string Key, string Payload, int Cursor)>();
        using (var query = db.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT package_key,payload_json,completed_steps
                FROM scheduler_packages
                WHERE package_type='tag-recovery-v1'
                  AND state IN ('queued','running','deferred');
                """;
            using var reader = query.ExecuteReader();
            while (reader.Read())
                legacy.Add((reader.GetString(0), reader.GetString(1),
                    reader.GetInt32(2)));
        }
        var migrated = 0;
        foreach (var (key, payload, cursor) in legacy)
        {
            // Corrupt or unsupported recipes are left untouched for
            // diagnosis. Never silently acknowledge an unreadable journal.
            long[] ids;
            try
            {
                ids = System.Text.Json.JsonSerializer.Deserialize<long[]>(payload)
                    ?? throw new InvalidDataException("Leeres V1-Tag-Paket.");
                if (cursor < 0 || cursor > ids.Length)
                    throw new InvalidDataException("Ungültiger V1-Schrittcursor.");
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Scheduler.LegacyTagMigration:" + key, ex);
                continue;
            }
            using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE scheduler_packages
                SET package_type='tag-recovery-v2',
                    payload_json='{}',
                    completed_steps=0,
                    state='queued',
                    retry_attempts=0,
                    retry_after_utc=NULL,
                    error=NULL,
                    updated_utc=$now
                WHERE package_key=$key AND package_type='tag-recovery-v1'
                  AND state IN ('queued','running','deferred');
                """;
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$key", key);
            migrated += update.ExecuteNonQuery();
        }
        transaction.Commit();
        if (migrated > 0)
            PCasaDiagnostics.WriteMessage("Scheduler.LegacyTagMigration",
                $"Converted {migrated} V1 packages to V2 in place. " +
                "Original priority/order preserved; pending edits untouched.");
        return migrated;
    }

    // The idle scheduler gate uses ALL outstanding non-idle packages,
    // including temporarily deferred NAS packages. The Taskmanager must
    // not display 'Wartet' for P0 while starting an unrelated P4 review.
    // A deliberately canceled/completed/failed package is not outstanding.
    // The persisted user priority override is authoritative.
    public bool HasOutstandingNonIdleTasks()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_packages p
                LEFT JOIN scheduler_task_order o ON o.task_key=p.package_key
                WHERE COALESCE(o.priority_override,p.priority) < $idle
                  AND p.state IN ('queued','running','deferred')
            );
            """;
        cmd.Parameters.AddWithValue("$idle", (int)SchedulerPriority.Idle);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    // Only runnable P0 packets prevent automatic work from starting.
    // A deferred original-file write is still in Pending() and retains its
    // priority/cursor, but cannot hold the scanner hostage until NAS returns.
    public bool HasPendingManualActions()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_packages
                WHERE priority=$priority AND state IN ('queued','running')
            );
            """;
        cmd.Parameters.AddWithValue("$priority", (int)SchedulerPriority.ManualAction);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    // Global Taskmanager moves may explicitly demote an old P0 package.
    // The background guard must obey the same scheduler-owned effective
    // priority; testing the original package priority would deadlock a
    // user-demoted P0 job behind itself.
    public bool HasPendingEffectiveManualActions()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_packages p
                LEFT JOIN scheduler_task_order o ON o.task_key=p.package_key
                WHERE COALESCE(o.priority_override,p.priority)=0
                  AND p.state IN ('queued','running')
            );
            """;
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    // A failed attempt is not a finished or canceled user action. The
    // next attempt is durable, bounded, and owned by the existing scheduler.
    // Backoff stops a permanently unavailable NAS monopolizing the worker.
    public void Defer(string key, int completed, string error,
        TimeSpan? firstAttemptDelay = null)
    {
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        long attempts;
        using (var lookup = db.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText =
                "SELECT retry_attempts FROM scheduler_packages WHERE package_key=$key;";
            lookup.Parameters.AddWithValue("$key", key);
            var previous = lookup.ExecuteScalar();
            if (previous is null) throw new InvalidOperationException(
                "Unbekannter persistenter Auftrag: " + key);
            attempts = Convert.ToInt64(previous);
        }
        var seconds = Math.Min(120, 5 * (1L << (int)Math.Min(attempts, 5)));
        var retryAfter = DateTimeOffset.UtcNow +
            (firstAttemptDelay ?? TimeSpan.FromSeconds(seconds));
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                UPDATE scheduler_packages
                   SET state='deferred',
                       completed_steps=MAX(completed_steps,$step),
                       retry_attempts=retry_attempts+1,
                       retry_after_utc=$retry,
                       error=$error, updated_utc=$now
                 WHERE package_key=$key
                   AND state NOT IN ('completed','failed','canceled');
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$step", completed);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$retry", retryAfter.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<string> DueDeferredKeys()
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT package_key
              FROM scheduler_packages
             WHERE state='deferred'
               AND (retry_after_utc IS NULL OR retry_after_utc <= $now)
             ORDER BY priority ASC, sequence ASC;
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    // Called only by the one scheduler retry coordinator; SQLite CAS keeps
    // two racing observations or restarts from scheduling duplicate writers.
    public DurablePackageRecord? ActivateDueDeferred(string key)
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE scheduler_packages
               SET state='queued',retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state='deferred'
               AND (retry_after_utc IS NULL OR retry_after_utc <= $now);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        if (command.ExecuteNonQuery() == 0) return null;
        return Pending().FirstOrDefault(p => p.Key == key);
    }

    public void SetState(string key, string state, int? completed = null,
        string? error = null)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state=$state, completed_steps=MAX(completed_steps,COALESCE($step,completed_steps)),
                   error=$error,updated_utc=$now
             WHERE package_key=$key
               AND state NOT IN ('completed','failed','canceled');
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$step", (object?)completed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
