using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

// One durable scheduler ledger. It stores data, not serialised delegates.
// Business steps are reconstructed by registered, versioned package factories.
public sealed record DurablePackageRecord(
    string Key, string Type, string Payload, SchedulerPriority Priority,
    SchedulerWorkKind WorkKind, long Sequence, int CompletedSteps,
    string State, string? Error);

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
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_scheduler_packages_active
                ON scheduler_packages(state, priority, sequence);
            """;
        cmd.ExecuteNonQuery();
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
        using var lookup = db.CreateCommand();
        lookup.CommandText = """
            SELECT sequence,priority,work_kind,completed_steps,state,error,
                   package_type,payload_json
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
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public IReadOnlyList<DurablePackageRecord> Pending()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_key,package_type,payload_json,priority,work_kind,
                   sequence,completed_steps,state,error
              FROM scheduler_packages WHERE state IN ('queued','running')
             ORDER BY priority ASC, sequence ASC;
            """;
        using var reader = cmd.ExecuteReader();
        var results = new List<DurablePackageRecord>();
        while (reader.Read())
            results.Add(new DurablePackageRecord(reader.GetString(0),
                reader.GetString(1), reader.GetString(2),
                (SchedulerPriority)reader.GetInt32(3),
                (SchedulerWorkKind)reader.GetInt32(4), reader.GetInt64(5),
                reader.GetInt32(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return results;
    }

    public void SetState(string key, string state, int? completed = null,
        string? error = null)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE scheduler_packages
               SET state=$state, completed_steps=COALESCE($step,completed_steps),
                   error=$error,updated_utc=$now
             WHERE package_key=$key;
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$step", (object?)completed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }
}
