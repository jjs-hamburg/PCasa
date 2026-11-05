using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

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
                updated_utc TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, long> ReadAll()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT task_key,dispatch_order FROM scheduler_task_order;
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<string,long>(StringComparer.Ordinal);
        while (reader.Read()) rows.Add(reader.GetString(0),reader.GetInt64(1));
        return rows;
    }

    // The complete accepted task list is supplied by the scheduler view.
    // It must be UNFILTERED; hidden state filters never change priorities.
    // All ranks are committed in ONE SQLite transaction.
    public IReadOnlyDictionary<string,long>? Reorder(
        IReadOnlyList<string> orderedKeys, string movedKey, string targetKey)
    {
        if (movedKey == targetKey) return null;
        if (orderedKeys.Count != orderedKeys.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Doppelte Auftragsidentitäten.");
        var list = orderedKeys.ToList();
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
        var changed = new Dictionary<string,long>(StringComparer.Ordinal);
        for (var i=0; i<list.Count; i++)
        {
            var rank = (long)(i + 1);
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO scheduler_task_order(task_key,dispatch_order,updated_utc)
                VALUES ($key,$rank,$when)
                ON CONFLICT(task_key) DO UPDATE SET
                    dispatch_order=excluded.dispatch_order,
                    updated_utc=excluded.updated_utc;
                """;
            cmd.Parameters.AddWithValue("$key",list[i]);
            cmd.Parameters.AddWithValue("$rank",rank);
            cmd.Parameters.AddWithValue("$when",DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
            changed.Add(list[i],rank);
        }
        tx.Commit();
        return changed;
    }
}
