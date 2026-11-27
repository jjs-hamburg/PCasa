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
    private sealed record FileStamp(long Size, long Modified, long? Created);

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
        var known = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT local_path,file_size,modified_utc_ticks,created_utc_ticks
                  FROM images WHERE folder_id=$id;
                """;
            cmd.Parameters.AddWithValue("$id", root.Id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!FolderExclusionRules.IsAtOrBelow(reader.GetString(0), scanRoot)) continue;
                known[reader.GetString(0)] = new FileStamp(
                    reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3));
            }
        }

        var changes = new List<BackgroundFolderChange>();
        var added = new List<BackgroundFolderChange>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        FileInfo? entry = null;
        // Detection and indexing share exactly the same included-directory
        // traversal. P4 consumes only directory-entry metadata and queues
        // changes; it never opens an image, repairs a cache or decodes EXIF.
        foreach (var path in FolderScanner.EnumerateIncludedFiles(scanRoot,
            recursive: true, exclusions, inclusions, token,
            onDirectoryComplete: directory => visited.Add(FolderExclusionRules.Normalize(directory)),
            onFileInfo: file => entry = file))
        {
            token.ThrowIfCancellationRequested();
            if (path.Length == 0 || !CatalogMediaFormats.IsSupported(path)) continue;
            var file = entry ?? throw new InvalidOperationException("Missing directory entry.");
            count++;
            var found = known.Remove(path, out var old);
            var creationChanged = found && !root.IsNetwork &&
                old!.Created is long created && created > 0 &&
                file.CreationTimeUtc.Ticks != created;
            if (!found || old!.Size != file.Length ||
                old.Modified != file.LastWriteTimeUtc.Ticks || creationChanged)
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
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in known.Keys)
        {
            token.ThrowIfCancellationRequested();
            if (!FolderExclusionRules.IsAtOrBelow(path, scanRoot) ||
                FolderExclusionRules.IsExcluded(path, exclusions, inclusions)) continue;
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
