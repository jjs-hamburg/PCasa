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
        // Upgrade in-flight Alpha.33 databases: old image edits already parked
        // with a retry timestamp must NOT keep retrying after this version
        // starts. The durable recipe, cursor and last error remain intact.
        using (var park = db.CreateCommand())
        {
            park.CommandText = """
                UPDATE scheduler_packages
                   SET retry_after_utc=NULL
                 WHERE package_type='image-edit-v1' AND state='deferred';
                """;
            park.ExecuteNonQuery();
        }
    }

    // Only scans have a durable execution lifetime. An image, tag, GPS,
    // transfer or other file mutation must never reappear as a runnable job
    // after the PCasa process has ended. Keep its history and last error;
    // do NOT delete the DB record or imply that a partial file write finished.
    // Existing persisted numeric 0/3/4 values remain compatible with
    // older libraries. Only legacy intermediate user ranks are migrated.
    public void NormalizeLegacyJobPriorities()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET priority=0,
                   queue_order=CASE WHEN queue_order<0 THEN queue_order ELSE -sequence END
             WHERE priority IN (1,2)
               AND state IN ('queued','running','deferred','paused');
            """;
        cmd.ExecuteNonQuery();
    }

    public static bool SurvivesSession(string type) => type is
        "manual-folder-scan-v1" or "manual-scan-all-v1" or "import-error-retry-v1";

    public int RetireUnfinishedFileJobs()
    {
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        // A staged tag projection is not proof of an original-file write.
        // Preserve its revisions for explicit review and prevent the
        // automatic tag drain from recreating the abandoned job.
        using (var park = db.CreateCommand())
        {
            park.Transaction = tx;
            park.CommandText = """
                INSERT INTO original_write_failures
                    (kind,image_id,package_key,file_path,attempts,
                     last_error,last_attempt_utc,resolved)
                SELECT DISTINCT 'tags',p.image_id,
                    'tags:session-ended:' || p.image_id,i.local_path,$max,
                    $error,$now,0
                FROM pending_tag_edits p JOIN images i ON i.id=p.image_id
                WHERE 1=1
                ON CONFLICT(kind,image_id) DO UPDATE SET
                    attempts=MAX(original_write_failures.attempts,excluded.attempts),
                    resolved=0;
                """;
            park.Parameters.AddWithValue("$max", OriginalWriteFailureRepository.MaximumAttempts);
            park.Parameters.AddWithValue("$error", "Dateioperation mit der PCasa-Sitzung beendet.");
            park.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            park.ExecuteNonQuery();
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE scheduler_packages
               SET state='canceled', retry_after_utc=NULL,
                   error=CASE WHEN error IS NULL OR error=''
                       THEN 'Dateioperation mit der PCasa-Sitzung beendet.'
                       ELSE error END,
                   updated_utc=$now
             WHERE package_type NOT IN
                   ('manual-folder-scan-v1','manual-scan-all-v1','import-error-retry-v1')
               AND state IN ('queued','running','deferred','paused');
            """;
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var retired = command.ExecuteNonQuery();
        tx.Commit();
        return retired;
    }

    public DurablePackageRecord Put(string key, string type, string payload,
        SchedulerPriority priority, SchedulerWorkKind workKind)
    {
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
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
        var inserted = cmd.ExecuteNonQuery() != 0;
        var acceptance = inserted && priority == SchedulerPriority.ManualAction
            ? LocalDatabase.NextSchedulerAcceptanceSequence(db, tx) : 0L;
        using (var rank = db.CreateCommand())
        {
            rank.Transaction = tx;
            rank.CommandText = """
                UPDATE scheduler_packages
                   SET queue_order=CASE WHEN priority=0
                       THEN CASE WHEN $accepted>0 THEN -$accepted ELSE -sequence END
                       ELSE sequence END
                 WHERE package_key=$key AND queue_order IS NULL;
                """;
            rank.Parameters.AddWithValue("$accepted", acceptance);
            rank.Parameters.AddWithValue("$key", key);
            rank.ExecuteNonQuery();
        }
        using var lookup = db.CreateCommand();
        lookup.Transaction = tx;
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
        var result = new DurablePackageRecord(key, reader.GetString(6), reader.GetString(7),
            (SchedulerPriority)reader.GetInt32(1), (SchedulerWorkKind)reader.GetInt32(2),
            reader.GetInt64(0), reader.GetInt32(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5))
        { QueueOrder = reader.GetInt64(8) };
        reader.Close();
        tx.Commit();
        return result;
    }

    public IReadOnlyList<DurablePackageRecord> Pending()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_key,package_type,payload_json,priority,work_kind,
                   sequence,completed_steps,state,error,COALESCE(queue_order,CASE WHEN priority=0 THEN -sequence ELSE sequence END)
              FROM scheduler_packages WHERE state IN ('queued','running','deferred','paused')
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

    public bool Cancel(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages SET state='canceled',
                   retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state IN ('queued','running','deferred','paused');
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    // A paused tag job has reached its safe file boundary. Remove only its
    // current, unconfirmed revisions and restore their previous membership.
    // A superseding owner and a confirmed native write are never reversed.
    // The projection, pending intents and terminal job state commit together.
    public bool CancelOwnedTagEdits(string key)
    {
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        using (var job = db.CreateCommand())
        {
            job.Transaction = tx;
            job.CommandText = """
                UPDATE scheduler_packages SET state='canceled',retry_after_utc=NULL,
                    updated_utc=$now
                WHERE package_key=$key AND package_type='tag-edit-v2'
                  AND state='paused';
                """;
            job.Parameters.AddWithValue("$key", key);
            job.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (job.ExecuteNonQuery() != 1) return false;
        }
        var edits = new List<(long Id, string Tag, bool BasePresent)>();
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT image_id,tag,base_present FROM pending_tag_edits
                WHERE owner_key=$key ORDER BY image_id;
                """;
            read.Parameters.AddWithValue("$key", key);
            using var rows = read.ExecuteReader();
            while (rows.Read())
            {
                if (rows.IsDBNull(2))
                    throw new InvalidOperationException(
                        "Dieser Tag-Auftrag enthält bereits vorgemerkte Änderungen. " +
                        "Er darf erst nach deren sicherem Abgleich oder Verwerfen gelöscht werden.");
                edits.Add((rows.GetInt64(0), rows.GetString(1), rows.GetBoolean(2)));
            }
        }
        foreach (var image in edits.GroupBy(edit => edit.Id))
        {
            string? words;
            using (var read = db.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT keywords FROM image_tags WHERE image_id=$id;";
                read.Parameters.AddWithValue("$id", image.Key);
                words = read.ExecuteScalar() as string;
            }
            var tags = new HashSet<string>((words ?? string.Empty).Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (var edit in image)
            {
                if (edit.BasePresent) tags.Add(edit.Tag);
                else tags.Remove(edit.Tag);
            }
            using (var update = db.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = "UPDATE image_tags SET keywords=$words WHERE image_id=$id;";
                update.Parameters.AddWithValue("$words", string.Join("\n", tags));
                update.Parameters.AddWithValue("$id", image.Key);
                update.ExecuteNonQuery();
            }
            using (var remove = db.CreateCommand())
            {
                remove.Transaction = tx;
                remove.CommandText =
                    "DELETE FROM pending_tag_edits WHERE owner_key=$key AND image_id=$id;";
                remove.Parameters.AddWithValue("$key", key);
                remove.Parameters.AddWithValue("$id", image.Key);
                remove.ExecuteNonQuery();
            }
            using (var clear = db.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = """
                    DELETE FROM original_write_failures
                    WHERE kind='tags' AND image_id=$id
                      AND (package_key=$key OR NOT EXISTS
                         (SELECT 1 FROM pending_tag_edits WHERE image_id=$id));
                    """;
                clear.Parameters.AddWithValue("$key", key);
                clear.Parameters.AddWithValue("$id", image.Key);
                clear.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return true;
    }

    // A real persisted HOLD, distinct from a failure/backoff and from
    // cancellation. No recovery controller may reactivate this state.
    public bool Pause(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state='paused',retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state IN ('queued','running','deferred');
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    // Alpha.63: an ordinary manual hold lasts for this application session.
    // Recovery preserves the original key, queue position, recipe and cursor.
    public void ReleaseStartupPauses()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state='queued',error=NULL,retry_after_utc=NULL,updated_utc=$now
             WHERE state='paused';
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public DurablePackageRecord? ResumePaused(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state='queued',error=NULL,retry_after_utc=NULL,updated_utc=$now
             WHERE package_key=$key AND state='paused';
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1
            ? Pending().FirstOrDefault(p => p.Key == key) : null;
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

    // Only runnable packages may prevent the continuous inventory review.
    // Deferred packages retain their identity and journal position, but
    // cannot block an unrelated reachable source while awaiting retry.
    public bool HasRunnableNonIdleTasks()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_packages p
                LEFT JOIN scheduler_task_order o ON o.task_key=p.package_key
                WHERE COALESCE(o.priority_override,p.priority) < $idle
                  AND p.state IN ('queued','running')
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
        string type;
        using (var lookup = db.CreateCommand())
        {
            lookup.Transaction = tx;
            lookup.CommandText =
                "SELECT retry_attempts,package_type FROM scheduler_packages WHERE package_key=$key;";
            lookup.Parameters.AddWithValue("$key", key);
            using var reader = lookup.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException(
                "Unbekannter persistenter Auftrag: " + key);
            attempts = reader.GetInt64(0);
            type = reader.GetString(1);
        }
        var seconds = Math.Min(120, 5 * (1L << (int)Math.Min(attempts, 5)));
        // An image file locked by an editor or OneDrive cannot be written
        // again automatically. Park the durable recipe for an EXPLICIT
        // Taskmanager Retry after the user has released the lock.
        DateTimeOffset? retryAfter = type == "image-edit-v1" ? null :
            DateTimeOffset.UtcNow + (firstAttemptDelay ?? TimeSpan.FromSeconds(seconds));
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
                   AND state NOT IN ('completed','failed','canceled','paused');
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$step", completed);
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$retry",
                retryAfter is { } next ? next.UtcDateTime.ToString("O") : DBNull.Value);
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
               AND (package_type <> 'image-edit-v1' OR retry_after_utc IS NOT NULL)
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
               AND (package_type <> 'image-edit-v1' OR retry_after_utc IS NOT NULL)
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
               SET state=CASE WHEN state='paused' THEN 'paused' ELSE $state END,
                   completed_steps=MAX(completed_steps,COALESCE($step,completed_steps)),
                   error=CASE WHEN state='paused' THEN error ELSE $error END,
                   updated_utc=$now
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
