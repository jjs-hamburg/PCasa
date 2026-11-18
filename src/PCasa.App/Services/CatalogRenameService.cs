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

    public void RebaseDirectory(string sourcePath, string targetPath,
        string canonicalSource, string canonicalTarget)
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
