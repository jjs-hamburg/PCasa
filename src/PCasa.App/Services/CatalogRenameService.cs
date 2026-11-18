using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;
using PCasa.App.Models;

namespace PCasa.App.Services;

/// <summary>
/// A same-parent folder rename must keep the IDs of ALL existing images and
/// photo folders. Metadata, tags, embedded GPS and packed thumbnails are keyed
/// by image ID and therefore must not be recreated or reread.
/// </summary>
public sealed class CatalogRenameService(LocalDatabase database)
{
    private static bool Under(string path, string root) =>
        FolderExclusionRules.IsAtOrBelow(path, root);

    // Fence an existing scan BEFORE the directory changes on disk. A stale
    // scanner cannot commit images or prune missing files against its older
    // source generation while Windows is renaming the tree.
    public void InvalidateScans(string sourceDirectory)
    {
        using var connection = database.OpenConnection();
        using var tx = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT local_path, canonical_path FROM monitored_folders";
        var roots = new List<string>();
        using (var reader = read.ExecuteReader())
            while (reader.Read())
                if (Under(sourceDirectory, reader.GetString(0)) ||
                    Under(reader.GetString(0), sourceDirectory))
                    roots.Add(reader.GetString(1));
        foreach (var canonicalRoot in roots)
        {
            using var update = connection.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE scheduler_sources
                SET generation=generation+1, updated_utc=$now
                WHERE canonical_path=$canonical COLLATE NOCASE;
                """;
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$canonical", canonicalRoot);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "Scanner source generation could not be fenced.");
        }
        tx.Commit();
    }

    public sealed record PendingRename(string Id, string Source, string Target,
        string CanonicalSource, string CanonicalTarget, bool CatalogApplied);

    /// <summary>
    /// Durable receipt precedes the physical Directory.Move. It is preserved
    /// until all path-dependent state (SQLite, Undo, appearance) is updated.
    /// If the process exits at any point, RecoverPendingRenames can distinguish
    /// an unexecuted operation from a moved directory and finish the latter.
    /// </summary>
    public void PrepareDirectoryRename(string operationId, string source,
        string target, string canonicalSource, string canonicalTarget)
    {
        InvalidateScans(source);
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO folder_rename_intents
                (operation_id,source_path,target_path,canonical_source,
                 canonical_target,catalog_applied,created_utc)
            VALUES ($id,$source,$target,$cs,$ct,0,$now);
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$cs", canonicalSource);
        command.Parameters.AddWithValue("$ct", canonicalTarget);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingRename> PendingRenames()
    {
        var result = new List<PendingRename>();
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT operation_id,source_path,target_path,canonical_source,
                   canonical_target,catalog_applied
            FROM folder_rename_intents ORDER BY created_utc,operation_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new PendingRename(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5) != 0));
        return result;
    }

    /// <summary>
    /// Do not guess when both paths exist or neither can be reached (NAS
    /// offline). Such intents remain durable and inspectable; startup must
    /// never erase the identity of an unverified rename.
    /// </summary>
    public (IReadOnlyList<PendingRename> Applied,
        IReadOnlyList<PendingRename> Unresolved) RecoverPendingRenames()
    {
        var applied = new List<PendingRename>();
        var unresolved = new List<PendingRename>();
        foreach (var receipt in PendingRenames())
        {
            try
            {
                var sourceExists = Directory.Exists(receipt.Source);
                var targetExists = Directory.Exists(receipt.Target);
                if (targetExists && !sourceExists)
                {
                    if (!receipt.CatalogApplied)
                        RebaseDirectory(receipt.Source, receipt.Target,
                            receipt.CanonicalSource, receipt.CanonicalTarget,
                            receipt.Id);
                    applied.Add(receipt with { CatalogApplied = true });
                }
                else if (sourceExists && !targetExists && !receipt.CatalogApplied)
                {
                    // Power loss before Directory.Move, or a successful
                    // filesystem rollback after a failed database operation.
                    AbortDirectoryRename(receipt.Id);
                }
                else
                {
                    // Both paths present, both missing, or a previously
                    // committed rename apparently reverted outside PCasa.
                    unresolved.Add(receipt);
                }
            }
            catch (Exception ex) when (ex is IOException or
                UnauthorizedAccessException or SqliteException)
            {
                PCasaDiagnostics.Write("Rename.FolderRecovery", ex);
                unresolved.Add(receipt);
            }
        }
        return (applied, unresolved);
    }

    public void CompleteDirectoryRename(string operationId)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM folder_rename_intents
            WHERE operation_id=$id AND catalog_applied=1;
            """;
        command.Parameters.AddWithValue("$id", operationId);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(
                "Completed folder rename receipt not found.");
    }

    public void AbortDirectoryRename(string operationId)
    {
        using var db = database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM folder_rename_intents
            WHERE operation_id=$id AND catalog_applied=0;
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.ExecuteNonQuery();
    }

    public void RebaseDirectory(string sourcePath, string targetPath,
        string canonicalSource, string canonicalTarget,
        string? operationId = null)
    {
        using var db = database.OpenConnection();
        using var tx = db.BeginTransaction();
        // Path columns are rewritten in ONE SQLite transaction. Numeric IDs,
        // content fingerprints, original timestamps, thumbnail paths and
        // thumbnail_entries are unchanged. Prefixes include a slash boundary,
        // so moving C:\Album cannot affect C:\AlbumExtra.
        foreach (var (table, column, canonical) in new (string, string, bool)[]
        {
            ("images", "local_path", false),
            ("images", "canonical_path", true),
            ("photo_folders", "local_path", false),
            ("photo_folders", "canonical_path", true),
            ("monitored_folders", "local_path", false),
            ("monitored_folders", "canonical_path", true),
            ("created_photo_directories", "local_path", false),
            ("excluded_folders", "local_path", false),
            ("initial_scan_directories", "local_path", false),
            ("initial_scan_file_cursors", "local_directory", false),
            ("scan_resume_jobs", "scan_root", false),
            ("scan_resume_directories", "local_directory", false),
            ("scan_directory_first_pass", "local_directory", false),
            ("scan_directory_verified", "local_directory", false),
            ("original_write_failures", "file_path", false),
            ("file_transfer_intents", "source_path", false),
            ("file_transfer_intents", "destination_path", false),
            ("scheduler_sources", "canonical_path", true)
        })
        {
            // Table/column names are constants, never untrusted input.
            var previous = canonical ? canonicalSource : sourcePath;
            var replacement = canonical ? canonicalTarget : targetPath;
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"""
                UPDATE {table}
                SET {column} = $new || substr({column},length($old)+1)
                WHERE {column}=$old COLLATE NOCASE
                    OR substr({column},1,length($old)+1)
                       = ($old || char(92)) COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$old", previous);
            command.Parameters.AddWithValue("$new", replacement);
            command.ExecuteNonQuery();
        }
        using (var folderName = db.CreateCommand())
        {
            folderName.Transaction = tx;
            folderName.CommandText = """
                UPDATE photo_folders SET display_name=$name
                WHERE local_path=$new COLLATE NOCASE;
                """;
            folderName.Parameters.AddWithValue("$new", targetPath);
            folderName.Parameters.AddWithValue("$name",
                Path.GetFileName(Path.TrimEndingDirectorySeparator(targetPath)));
            folderName.ExecuteNonQuery();
        }
        // Mark the SQLite part complete IN THE SAME transaction as every
        // path rewrite. Recovery can then finish Undo / appearance without
        // mistakenly replaying a previously committed path change.
        if (operationId is not null)
        {
            using var receipt = db.CreateCommand();
            receipt.Transaction = tx;
            receipt.CommandText = """
                UPDATE folder_rename_intents SET catalog_applied=1
                WHERE operation_id=$id AND catalog_applied=0;
                """;
            receipt.Parameters.AddWithValue("$id", operationId);
            if (receipt.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "Folder rename receipt disappeared before commit.");
        }
        // Reuse the same primary keys. Neither image_tags, image_exif,
        // raw_gps_overrides, image thumbnails nor packs need touching.
        tx.Commit();
    }

    public static string CanonicalTarget(MonitoredFolder owner,
        string oldPath, string newPath)
    {
        if (string.Equals(owner.LocalPath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            // Renaming the monitored root itself changes the prefix the
            // scanner uses for this drive/share. UNC paths are preserved.
            return Path.Combine(Path.GetDirectoryName(owner.CanonicalPath)!,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(newPath)));
        }
        return FolderScanner.ResolveCanonicalFullPath(owner, newPath);
    }
}
