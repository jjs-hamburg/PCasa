using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

// This is NOT a second scheduler. These are the per-file receipts for one
// immutable, durable scheduler recipe. No OS file operation and SQLite commit
// can be atomic together. A receipt never licenses an ambiguous retry.
public sealed record GalleryTransferReceipt(
    string Source, string Destination, bool Move, bool Replace,
    long TargetRootId, long? SourceImageId, long? ReplacedImageId,
    string? RelocationId, string Phase);

public sealed class DurableGalleryTransferJournal
{
    private readonly LocalDatabase _database;

    public DurableGalleryTransferJournal(LocalDatabase database)
    {
        _database = database;
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gallery_transfer_receipts (
                package_key TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                source_path TEXT NOT NULL,
                destination_path TEXT NOT NULL,
                move_file INTEGER NOT NULL,
                replace_target INTEGER NOT NULL,
                target_root_id INTEGER NOT NULL,
                source_image_id INTEGER,
                replaced_image_id INTEGER,
                relocation_id TEXT,
                phase TEXT NOT NULL CHECK (phase IN
                    ('planned','executing','materialized','indexed','skipped')),
                PRIMARY KEY (package_key, ordinal)
            );
            CREATE INDEX IF NOT EXISTS idx_gallery_receipts_phase
                ON gallery_transfer_receipts(package_key,phase);
            """;
        cmd.ExecuteNonQuery();
    }

    public GalleryTransferReceipt? Get(string key, int ordinal)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source_path,destination_path,move_file,replace_target,
                   target_root_id,source_image_id,replaced_image_id,
                   relocation_id,phase
              FROM gallery_transfer_receipts
             WHERE package_key=$key AND ordinal=$ordinal;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new GalleryTransferReceipt(
            reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2) != 0, reader.GetInt32(3) != 0,
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8));
    }

    public GalleryTransferReceipt Prepare(string key, int ordinal,
        GalleryTransferReceipt proposed)
    {
        using (var db = _database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO gallery_transfer_receipts(
                    package_key,ordinal,source_path,destination_path,
                    move_file,replace_target,target_root_id,source_image_id,
                    replaced_image_id,relocation_id,phase)
                VALUES ($key,$ordinal,$source,$destination,$move,$replace,$root,
                        $image,$old,$intent,$phase)
                ON CONFLICT(package_key,ordinal) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$ordinal", ordinal);
            cmd.Parameters.AddWithValue("$source", proposed.Source);
            cmd.Parameters.AddWithValue("$destination", proposed.Destination);
            cmd.Parameters.AddWithValue("$move", proposed.Move ? 1 : 0);
            cmd.Parameters.AddWithValue("$replace", proposed.Replace ? 1 : 0);
            cmd.Parameters.AddWithValue("$root", proposed.TargetRootId);
            cmd.Parameters.AddWithValue("$image", (object?)proposed.SourceImageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$old", (object?)proposed.ReplacedImageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$intent", (object?)proposed.RelocationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$phase", proposed.Phase);
            cmd.ExecuteNonQuery();
        }
        var actual = Get(key, ordinal) ??
            throw new InvalidOperationException("Datei-Checkpoint konnte nicht gespeichert werden.");
        if (!string.Equals(actual.Source, proposed.Source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.Destination, proposed.Destination, StringComparison.OrdinalIgnoreCase) ||
            actual.Move != proposed.Move || actual.Replace != proposed.Replace ||
            actual.TargetRootId != proposed.TargetRootId ||
            actual.SourceImageId != proposed.SourceImageId ||
            actual.ReplacedImageId != proposed.ReplacedImageId ||
            !string.Equals(actual.RelocationId, proposed.RelocationId, StringComparison.Ordinal))
            throw new InvalidOperationException("Datei-Checkpoint passt nicht zum Paket.");
        return actual;
    }

    public void Advance(string key, int ordinal, string from, string to)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE gallery_transfer_receipts SET phase=$next
             WHERE package_key=$key AND ordinal=$ordinal AND phase=$previous;
            """;
        cmd.Parameters.AddWithValue("$next", to);
        cmd.Parameters.AddWithValue("$previous", from);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                "Datei-Checkpoint hat unerwarteten Zustand: " + key + "/" + ordinal);
    }

    public HashSet<string> ReservedDestinations(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT destination_path FROM gallery_transfer_receipts
             WHERE package_key=$key AND phase<>'skipped';
            """;
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public HashSet<long> MovedImageIds(string key)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source_image_id FROM gallery_transfer_receipts
             WHERE package_key=$key AND move_file=1 AND phase='indexed'
               AND source_image_id IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    // A receipt must remain inspectable while the scheduler outcome is not
    // terminal. Only a confirmed, durable COMPLETED package may be pruned.
    // A crash between completion and this cleanup is harmless on next start.
    public void PruneCompleted()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM gallery_transfer_receipts
             WHERE package_key IN (
                SELECT package_key FROM scheduler_packages WHERE state='completed'
             );
            """;
        cmd.ExecuteNonQuery();
    }

    public bool OwnsRelocationIntent(string intentId)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM gallery_transfer_receipts
             WHERE relocation_id=$intent LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$intent", intentId);
        return cmd.ExecuteScalar() is not null;
    }

    // Preserve the existing image relocation intent contract. The deterministic
    // ID makes a repeated invocation idempotent and validates its exact recipe.
    public void EnsureRelocationIntent(GalleryTransferReceipt item)
    {
        if (!item.Move || item.SourceImageId is not long imageId ||
            item.RelocationId is not string intentId) return;
        using var db = _database.OpenConnection();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO file_transfer_intents(
                    operation_id,image_id,source_path,destination_path,
                    target_root_id,created_utc)
                VALUES ($id,$image,$src,$dst,$root,$time)
                ON CONFLICT(operation_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$id", intentId);
            cmd.Parameters.AddWithValue("$image", imageId);
            cmd.Parameters.AddWithValue("$src", item.Source);
            cmd.Parameters.AddWithValue("$dst", item.Destination);
            cmd.Parameters.AddWithValue("$root", item.TargetRootId);
            cmd.Parameters.AddWithValue("$time", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        using var verify = db.CreateCommand();
        verify.CommandText = """
            SELECT image_id,source_path,destination_path,target_root_id
              FROM file_transfer_intents WHERE operation_id=$id;
            """;
        verify.Parameters.AddWithValue("$id", intentId);
        using var reader = verify.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != imageId ||
            !string.Equals(reader.GetString(1), item.Source, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(reader.GetString(2), item.Destination, StringComparison.OrdinalIgnoreCase) ||
            reader.GetInt64(3) != item.TargetRootId)
            throw new InvalidOperationException("Widersprüchliche Dateiübertragungs-Identität.");
    }
}
