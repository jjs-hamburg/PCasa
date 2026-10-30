using Microsoft.Data.Sqlite;
using System.IO;

namespace PCasa.App.Data;

/// <summary>
/// One durable error ledger for original-file writes (GPS and tags).
/// NEVER a job queue: the Scheduler still owns retries, ordering and steps,
/// and pending_tag_edits remains the sole authoritative tag intent table.
/// </summary>
public sealed class OriginalWriteFailureRepository
{
    private readonly LocalDatabase database;

    public OriginalWriteFailureRepository(LocalDatabase database)
    {
        this.database = database;
        using var connection = database.OpenConnection();
        EnsureSchema(connection); // one migration on service creation, never per photo
    }

    public const string Gps = "gps";
    public const string Tags = "tags";
    public const int MaximumAttempts = 3;

    public sealed record FailedFile(string Kind, string PackageKey, long ImageId,
        string Path, int Attempts, string Error, string LastAttemptUtc);

    private static void EnsureSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS original_write_failures (
                kind TEXT NOT NULL,
                image_id INTEGER NOT NULL,
                package_key TEXT NOT NULL,
                file_path TEXT NOT NULL,
                attempts INTEGER NOT NULL CHECK(attempts >= 0),
                last_error TEXT NOT NULL,
                last_attempt_utc TEXT NOT NULL,
                resolved INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (kind,image_id)
            );
            CREATE INDEX IF NOT EXISTS idx_original_write_failures_open
                ON original_write_failures(resolved,kind,attempts);
            """;
        cmd.ExecuteNonQuery();
        // Migration of the previously shipped GPS-only ledger is repeatable.
        // A newer active shared record must never be overwritten by an old one.
        using var table = db.CreateCommand();
        table.CommandText = """
            SELECT EXISTS(SELECT 1 FROM sqlite_master
                WHERE type='table' AND name='gps_write_failures');
            """;
        if (Convert.ToInt32(table.ExecuteScalar()) == 0) return;
        using var migrate = db.CreateCommand();
        migrate.CommandText = """
            INSERT OR IGNORE INTO original_write_failures
                (kind,image_id,package_key,file_path,attempts,
                 last_error,last_attempt_utc,resolved)
            SELECT 'gps',image_id,package_key,file_path,attempts,
                   last_error,last_attempt_utc,resolved
            FROM gps_write_failures;
            """;
        migrate.ExecuteNonQuery();
        // Once copied, retire legacy unresolved rows so a later intentional
        // Clear/Discard does not resurrect them during the next schema check.
        using var retire = db.CreateCommand();
        retire.CommandText = "UPDATE gps_write_failures SET resolved=1 WHERE resolved=0;";
        retire.ExecuteNonQuery();
    }

    public int RecordFailure(string kind, string packageKey, long imageId, Exception exception,
        bool permanent = false)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO original_write_failures
                (kind,image_id,package_key,file_path,attempts,
                 last_error,last_attempt_utc,resolved)
            VALUES ($kind,$id,$key,
                COALESCE((SELECT local_path FROM images WHERE id=$id),'(missing file)'),
                $first,$error,$at,0)
            ON CONFLICT(kind,image_id) DO UPDATE SET
                package_key=excluded.package_key,
                file_path=excluded.file_path,
                attempts=CASE
                    WHEN original_write_failures.package_key=excluded.package_key
                      THEN MIN(original_write_failures.attempts+1,$max)
                    ELSE $first END,
                last_error=excluded.last_error,
                last_attempt_utc=excluded.last_attempt_utc,
                resolved=0
            RETURNING attempts;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$key", packageKey);
        cmd.Parameters.AddWithValue("$max", MaximumAttempts);
        cmd.Parameters.AddWithValue("$first", permanent ? MaximumAttempts : 1);
        cmd.Parameters.AddWithValue("$error", exception.GetType().Name + ": " +
            (exception.Message.Length > 1500 ? exception.Message[..1500] : exception.Message));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool IsParked(string kind, long imageId, string? packageKey = null)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM original_write_failures
                WHERE kind=$kind AND image_id=$id AND resolved=0
                  AND attempts >= $maximum
                  AND ($key IS NULL OR package_key=$key));
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$maximum", MaximumAttempts);
        cmd.Parameters.AddWithValue("$key", (object?)packageKey ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    public void Clear(string kind, long imageId)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM original_write_failures
             WHERE kind=$kind AND image_id=$id;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.ExecuteNonQuery();
    }

    // A superseded older GPS package may finish AFTER a newer package
    // already failed. It must never clear the newer package's error.
    public void ClearIfOwned(string kind, long imageId, string packageKey)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM original_write_failures
             WHERE kind=$kind AND image_id=$id AND package_key=$key;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$key", packageKey);
        cmd.ExecuteNonQuery();
    }

    // Normal scheduling only sees exhausted (parked) failures. The explicit
    // error-review dialog can also display the FIRST failed attempt, before
    // the central scheduler's automatic retries finish.
    public IReadOnlyList<FailedFile> Unresolved(bool includeRetrying = false)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT kind,package_key,image_id,file_path,attempts,
                   last_error,last_attempt_utc
              FROM original_write_failures
             WHERE resolved=0 AND (attempts >= $max OR $showRetrying = 1)
             ORDER BY last_attempt_utc DESC;
            """;
        cmd.Parameters.AddWithValue("$max", MaximumAttempts);
        cmd.Parameters.AddWithValue("$showRetrying", includeRetrying ? 1 : 0);
        var result = new List<FailedFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(new FailedFile(reader.GetString(0),reader.GetString(1),
                reader.GetInt64(2),reader.GetString(3),reader.GetInt32(4),
                reader.GetString(5),reader.GetString(6)));
        return result;
    }

    // The immutable GPS intent is still owned by the existing scheduler
    // journal. No duplicate recipe is stored in the error ledger.
    public (string Type, string Payload, long Sequence)? OriginalRecipe(string packageKey)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_type,payload_json,sequence
              FROM scheduler_packages WHERE package_key=$key;
            """;
        cmd.Parameters.AddWithValue("$key", packageKey);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0),reader.GetString(1),reader.GetInt64(2))
            : null;
    }

    // A newer GPS write for this same image invalidates earlier failed work.
    // Earlier coordinates must never overwrite an intentional newer position.
    public bool HasNewerGpsIntent(string packageKey, long imageId)
    {
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM scheduler_packages newer
                JOIN scheduler_packages original ON original.package_key=$key
                WHERE newer.package_type='gps-edit-v1'
                  AND newer.sequence > original.sequence
                  AND newer.state NOT IN ('canceled','failed')
                  AND EXISTS (SELECT 1 FROM json_each(newer.payload_json,'$.ImageIds')
                              WHERE CAST(value AS INTEGER)=$id));
            """;
        cmd.Parameters.AddWithValue("$key", packageKey);
        cmd.Parameters.AddWithValue("$id", imageId);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }
}
