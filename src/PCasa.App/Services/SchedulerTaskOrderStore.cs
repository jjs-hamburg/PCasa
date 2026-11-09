using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed record SchedulerTaskOrderItem(string Key,
    SchedulerPriority Priority);

public sealed record SchedulerStoredTaskOrder(
    long Order, SchedulerPriority? PriorityOverride);

/// <summary>
/// The scheduler's single durable user-defined dispatch sequence. Taskmanager
/// never owns a second ranking table. An explicit mouse/button move overrides
/// default priority for the currently accepted work, regardless of original
/// package type, priority class or physical executor lane.
/// </summary>
public sealed class SchedulerTaskOrderStore
{
    private readonly LocalDatabase _database;

    public SchedulerTaskOrderStore(LocalDatabase database)
    {
        _database = database;
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS scheduler_task_order (
                task_key TEXT PRIMARY KEY,
                dispatch_order INTEGER NOT NULL,
                priority_override INTEGER NULL,
                paused INTEGER NOT NULL DEFAULT 0,
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        // Additive migration: existing saved user ranks from the previous
        // preview release must not automatically promote every root to P0.
        using var schema = db.CreateCommand();
        schema.CommandText = "PRAGMA table_info(scheduler_task_order);";
        var hasPriority = false;
        using (var reader = schema.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == "priority_override")
                    hasPriority = true;
        if (!hasPriority)
        {
            using var migrate = db.CreateCommand();
            migrate.CommandText =
                "ALTER TABLE scheduler_task_order ADD COLUMN priority_override INTEGER NULL;";
            migrate.ExecuteNonQuery();
        }
        using var pausedCheck = db.CreateCommand();
        pausedCheck.CommandText = "PRAGMA table_info(scheduler_task_order);";
        var hasPaused = false;
        using (var reader = pausedCheck.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(1) == "paused") hasPaused = true;
        if (!hasPaused)
        {
            using var migrate = db.CreateCommand();
            migrate.CommandText =
                "ALTER TABLE scheduler_task_order ADD COLUMN paused INTEGER NOT NULL DEFAULT 0;";
            migrate.ExecuteNonQuery();
        }
    }

    public IReadOnlySet<string> ReadPaused()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT task_key FROM scheduler_task_order WHERE paused=1;";
        using var reader = cmd.ExecuteReader();
        var paused = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) paused.Add(reader.GetString(0));
        return paused;
    }

    // External scan intents use the SAME persisted scheduler control ledger
    // as their user-defined order. The physical scanner cursor stays with
    // its existing owner; a pause is NOT a canceled or new scan request.
    public bool SetPaused(string key, bool paused)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = paused ? """
            INSERT INTO scheduler_task_order
                (task_key,dispatch_order,paused,updated_utc)
            VALUES ($key,9223372036854775807,1,$now)
            ON CONFLICT(task_key) DO UPDATE SET
                paused=1,updated_utc=excluded.updated_utc;
            """ : """
            UPDATE scheduler_task_order
               SET paused=0,updated_utc=$now
             WHERE task_key=$key AND paused=1;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var changed = cmd.ExecuteNonQuery() == 1;
        if (!paused && changed)
        {
            using var cleanup = db.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM scheduler_task_order
                 WHERE task_key=$key AND paused=0
                   AND dispatch_order=9223372036854775807
                   AND priority_override IS NULL;
                """;
            cleanup.Parameters.AddWithValue("$key", key);
            cleanup.ExecuteNonQuery();
        }
        return changed;
    }

    public IReadOnlyDictionary<string, SchedulerStoredTaskOrder> ReadAll()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT task_key,dispatch_order,priority_override
              FROM scheduler_task_order;
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<string,SchedulerStoredTaskOrder>(
            StringComparer.Ordinal);
        while (reader.Read())
            rows.Add(reader.GetString(0),
                new SchedulerStoredTaskOrder(
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null :
                        (SchedulerPriority)reader.GetInt32(2)));
        return rows;
    }

    // The complete accepted task list is supplied by the scheduler view.
    // It must be UNFILTERED; hidden state filters never change priorities.
    // All ranks are committed in ONE SQLite transaction.
    public IReadOnlyDictionary<string,SchedulerStoredTaskOrder>? Reorder(
        IReadOnlyList<SchedulerTaskOrderItem> orderedItems,
        string movedKey, string targetKey)
    {
        if (movedKey == targetKey) return null;
        if (orderedItems.Count != orderedItems.Select(x=>x.Key)
                .Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Doppelte Auftragsidentitäten.");
        var list = orderedItems.Select(item => item.Key).ToList();
        var priorities = orderedItems.ToDictionary(
            item => item.Key, item => item.Priority, StringComparer.Ordinal);
        var source = list.IndexOf(movedKey);
        var destination = list.IndexOf(targetKey);
        if (source < 0 || destination < 0)
            return null;
        list.RemoveAt(source);
        // Reordering DOWN inserts AFTER the target; reordering UP inserts
        // BEFORE it. Without this distinction the Down button was a no-op.
        var newTarget = list.IndexOf(targetKey);
        list.Insert(source < destination ? newTarget + 1 : newTarget, movedKey);
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        var existingOverrides = new Dictionary<string,SchedulerPriority>(
            StringComparer.Ordinal);
        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = """
                SELECT task_key,priority_override FROM scheduler_task_order
                WHERE priority_override IS NOT NULL;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
                existingOverrides.Add(reader.GetString(0),
                    (SchedulerPriority)reader.GetInt32(1));
        }
        // Only the MOVED job adopts the target's effective priority.
        // Other P4 scans must NOT become manual P0 work simply because
        // the user rearranged two unrelated GPS jobs.
        existingOverrides[movedKey] = priorities[targetKey];
        var changed =
            new Dictionary<string,SchedulerStoredTaskOrder>(StringComparer.Ordinal);
        for (var i=0; i<list.Count; i++)
        {
            var rank = (long)(i + 1);
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO scheduler_task_order
                    (task_key,dispatch_order,priority_override,updated_utc)
                VALUES ($key,$rank,$priority,$when)
                ON CONFLICT(task_key) DO UPDATE SET
                    dispatch_order=excluded.dispatch_order,
                    priority_override=excluded.priority_override,
                    updated_utc=excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$key",list[i]);
            cmd.Parameters.AddWithValue("$rank",rank);
            cmd.Parameters.AddWithValue("$priority",
                existingOverrides.TryGetValue(list[i],out var priority)
                    ? (object)(int)priority : DBNull.Value);
            cmd.Parameters.AddWithValue("$when",DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            changed.Add(list[i],new SchedulerStoredTaskOrder(
                rank,existingOverrides.TryGetValue(list[i],out var effective)
                    ? effective : null));
        }
        tx.Commit();
        return changed;
    }
}
