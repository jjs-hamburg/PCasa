using System.IO;
using PCasa.App.Data;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed record CatalogInventoryCheckResult(
    int CheckedFiles, IReadOnlyList<BackgroundFolderChange> Changes);

// Read only directory entries and catalog records. There is intentionally
// no dependency on an image decoder, ExifTool or either thumbnail store.
public sealed class CatalogInventoryCheck(LocalDatabase database)
{
    internal sealed record FileStamp(long Size, long Modified, long? Created);

    // One directory's LOCAL catalog stamps. No source-file opens, thumbnail
    // probes, EXIF queries or per-image SQL lookups in the detection stage.
    internal static Dictionary<string, FileStamp> ReadDirectory(
        LocalDatabase database, long rootId, string directory, CancellationToken token)
    {
        var known = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT i.local_path,i.file_size,i.modified_utc_ticks,i.created_utc_ticks
            FROM photo_folders f JOIN images i ON i.photo_folder_id=f.id
            WHERE f.monitored_folder_id=$id AND f.local_path=$path COLLATE NOCASE
              AND i.folder_id=$id;
            """;
        cmd.Parameters.AddWithValue("$id", rootId);
        cmd.Parameters.AddWithValue("$path", directory);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            known[reader.GetString(0)] = new FileStamp(reader.GetInt64(1),
                reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
        }
        return known;
    }

    internal static bool CreationChanged(MonitoredFolder root, FileStamp old, FileInfo entry)
        => !root.IsNetwork && old.Created is long created && created > 0 &&
            entry.CreationTimeUtc.Ticks != created;

    internal static bool IsChanged(MonitoredFolder root, FileStamp old, FileInfo entry)
        => old.Size != entry.Length || old.Modified != entry.LastWriteTimeUtc.Ticks ||
            CreationChanged(root, old, entry);

    public CatalogInventoryCheckResult Check(MonitoredFolder root,
        IReadOnlyCollection<string> exclusions,
        IReadOnlyCollection<string> inclusions,
        CancellationToken token, Action<int, string>? progress = null,
        string? selectedDirectory = null,
        Action<BackgroundFolderChange>? importNewImmediately = null)
    {
        var scanRoot = Path.GetFullPath(selectedDirectory ?? root.LocalPath);
        if (!FolderExclusionRules.IsAtOrBelow(scanRoot, root.LocalPath))
            throw new ArgumentException("Inventory outside monitored root.");
        var changes = new List<BackgroundFolderChange>();
        var added = new List<BackgroundFolderChange>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        FileInfo? entry = null;
        string? activeDirectory = null;
        Dictionary<string, FileStamp>? known = null;
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void FinishDirectory(string directory)
        {
            visited.Add(FolderExclusionRules.Normalize(directory));
            if (FolderExclusionRules.IsExcluded(directory, exclusions, inclusions)) return;
            var remaining = string.Equals(activeDirectory, directory, StringComparison.OrdinalIgnoreCase)
                ? known! : ReadDirectory(database, root.Id, directory, token);
            foreach (var path in remaining.Keys) missing.Add(path);
            activeDirectory = null;
            known = null;
        }
        // Detection and indexing share exactly the same included-directory
        // traversal. P4 consumes only directory-entry metadata and queues
        // changes; it never opens an image, repairs a cache or decodes EXIF.
        foreach (var path in FolderScanner.EnumerateIncludedFiles(scanRoot,
            recursive: true, exclusions, inclusions, token,
            onDirectoryComplete: FinishDirectory,
            onFileInfo: file => entry = file))
        {
            token.ThrowIfCancellationRequested();
            if (path.Length == 0 || !CatalogMediaFormats.IsSupported(path)) continue;
            var file = entry ?? throw new InvalidOperationException("Missing directory entry.");
            count++;
            var directory = file.DirectoryName ?? scanRoot;
            if (!string.Equals(activeDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                known = ReadDirectory(database, root.Id, directory, token);
                activeDirectory = directory;
            }
            var found = known!.Remove(path, out var old);
            var creationChanged = found && CreationChanged(root, old!, file);
            if (!found || IsChanged(root, old!, file))
            {
                var change = new BackgroundFolderChange(root.Id, path,
                    BackgroundFolderChangeKind.ScanFile, false,
                    ForceExifRefresh: creationChanged);
                if (found) changes.Add(change);
                else if (importNewImmediately is not null)
                    importNewImmediately(change);
                else added.Add(change);
            }
            if (count % 128 == 0) progress?.Invoke(count, file.DirectoryName ?? scanRoot);
        }

        // An inaccessible or interrupted traversal never reaches this point.
        // Missing subtrees are submitted at their highest missing ancestor
        // so the existing removal handler can revalidate its reachable parent.
        // Only directory names are needed to find missing subtrees.
        var catalogDirectories = new List<string>();
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT local_path FROM photo_folders WHERE monitored_folder_id=$id;";
            cmd.Parameters.AddWithValue("$id", root.Id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                catalogDirectories.Add(reader.GetString(0));
            }
        }
        foreach (var path in catalogDirectories)
        {
            token.ThrowIfCancellationRequested();
            if (!FolderExclusionRules.IsAtOrBelow(path, scanRoot) ||
                FolderExclusionRules.IsExcluded(path, exclusions, inclusions) ||
                visited.Contains(FolderExclusionRules.Normalize(path))) continue;
            var candidate = path;
            var parent = Path.GetDirectoryName(candidate);
            while (parent is not null &&
                   FolderExclusionRules.IsAtOrBelow(parent, scanRoot) &&
                   !visited.Contains(FolderExclusionRules.Normalize(parent)))
            {
                candidate = parent;
                parent = Path.GetDirectoryName(candidate);
            }
            if (parent is not null && visited.Contains(FolderExclusionRules.Normalize(parent)))
                missing.Add(candidate);
        }
        if (!Directory.Exists(scanRoot))
            throw new DirectoryNotFoundException(scanRoot);
        token.ThrowIfCancellationRequested();
        foreach (var path in missing)
            changes.Add(new BackgroundFolderChange(root.Id, path,
                BackgroundFolderChangeKind.RemovePath, false));
        progress?.Invoke(count, scanRoot);
        added.AddRange(changes);
        return new CatalogInventoryCheckResult(count, added);
    }
}
