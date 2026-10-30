using Microsoft.Data.Sqlite;
using System.IO;

namespace PCasa.App.Data;

/// <summary>
/// Durable, file-specific GPS-write failures. The scheduler owns the package
/// cursor and retries; this ledger only distinguishes a repeatedly bad file
/// from a temporarily unavailable source. Never changes originals or creates
/// a second worker.
/// </summary>
public sealed class GpsWriteFailureRepository(LocalDatabase database)
{
    public sealed record FailedFile(string PackageKey, long ImageId, string Path,
        int Attempts, string Error, string LastAttemptUtc);

    private static void EnsureSchema(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gps_write_failures (
                package_key TEXT NOT NULL,
                image_id INTEGER NOT NULL,
                file_path TEXT NOT NULL,
                attempts INTEGER NOT NULL CHECK(attempts > 0),
                last_error TEXT NOT NULL,
                last_attempt_utc TEXT NOT NULL,
                resolved INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(package_key,image_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public int RecordFailure(string packageKey, long imageId, Exception failure)
    {
        using var db = database.OpenConnection();
        EnsureSchema(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gps_write_failures
                (package_key,image_id,file_path,attempts,last_error,last_attempt_utc,resolved)
            VALUES ($key,$id,
                COALESCE((SELECT local_path FROM images WHERE id=$id),'(missing file)'),
                1,$error,$at,0)
            ON CONFLICT(package_key,image_id) DO UPDATE SET
                attempts=attempts+1,
                last_error=excluded.last_error,
                last_attempt_utc=excluded.last_attempt_utc,
                resolved=0
            RETURNING attempts;
            """;
        cmd.Parameters.AddWithValue("$key", packageKey);
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.Parameters.AddWithValue("$error",
            failure.GetType().Name + ": " +
            (failure.Message.Length > 1500 ? failure.Message[..1500] : failure.Message));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Called only AFTER the original's GPS metadata was verified and its
    // catalog transaction committed. Fixing the source retires old warnings
    // for the same image regardless of which later package succeeded.
    public void MarkResolved(long imageId)
    {
        using var db = database.OpenConnection();
        EnsureSchema(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE gps_write_failures SET resolved=1
            WHERE image_id=$id AND resolved=0;
            """;
        cmd.Parameters.AddWithValue("$id", imageId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FailedFile> Unresolved(string? packageKey = null)
    {
        using var db = database.OpenConnection();
        EnsureSchema(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT package_key,image_id,file_path,attempts,last_error,last_attempt_utc
            FROM gps_write_failures
            WHERE resolved=0 AND attempts >= 3
              AND ($key IS NULL OR package_key=$key)
            ORDER BY last_attempt_utc DESC;
            """;
        cmd.Parameters.AddWithValue("$key", (object?)packageKey ?? DBNull.Value);
        var list = new List<FailedFile>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
            list.Add(new FailedFile(rows.GetString(0), rows.GetInt64(1),
                rows.GetString(2), rows.GetInt32(3), rows.GetString(4),
                rows.GetString(5)));
        return list;
    }
}
