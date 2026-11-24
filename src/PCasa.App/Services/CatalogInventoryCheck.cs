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
        CancellationToken token, Action<int, string>? progress = null)
    {
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
                known[reader.GetString(0)] = new FileStamp(
                    reader.GetInt64(1), reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3));
            }
        }

        var changes = new List<BackgroundFolderChange>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>();
        directories.Push(root.LocalPath);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint
        };
        var count = 0;
        while (directories.TryPop(out var path))
        {
            token.ThrowIfCancellationRequested();
            var excluded = FolderExclusionRules.IsExcluded(path, exclusions, inclusions);
            if (excluded && !FolderExclusionRules.MayContainIncludedDescendant(path, inclusions))
                continue;
            var directory = new DirectoryInfo(path);
            foreach (var child in directory.EnumerateDirectories("*", options))
                directories.Push(child.FullName);
            visited.Add(FolderExclusionRules.Normalize(path));
            if (excluded) continue;
            foreach (var file in directory.EnumerateFiles("*", options))
            {
                token.ThrowIfCancellationRequested();
                if (!FolderScanner.IsSupportedImagePath(file.FullName)) continue;
                count++;
                var found = known.Remove(file.FullName, out var old);
                // Local creation stamps can detect same-size replacements.
                // Do not infer content changes from unreliable SMB birth times.
                var creationChanged = found && !root.IsNetwork &&
                    old!.Created is long created && created > 0 &&
                    file.CreationTimeUtc.Ticks != created;
                if (!found || old!.Size != file.Length ||
                    old.Modified != file.LastWriteTimeUtc.Ticks || creationChanged)
                    changes.Add(new BackgroundFolderChange(root.Id, file.FullName,
                        BackgroundFolderChangeKind.ScanFile, false,
                        ForceExifRefresh: creationChanged));
                if (count % 128 == 0) progress?.Invoke(count, path);
            }
        }

        // An inaccessible or interrupted traversal never reaches this point.
        // Missing subtrees are submitted at their highest missing ancestor
        // so the existing removal handler can revalidate its reachable parent.
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in known.Keys)
        {
            token.ThrowIfCancellationRequested();
            if (!FolderExclusionRules.IsAtOrBelow(path, root.LocalPath) ||
                FolderExclusionRules.IsExcluded(path, exclusions, inclusions)) continue;
            var candidate = path;
            var parent = Path.GetDirectoryName(candidate);
            while (parent is not null &&
                   FolderExclusionRules.IsAtOrBelow(parent, root.LocalPath) &&
                   !visited.Contains(FolderExclusionRules.Normalize(parent)))
            {
                candidate = parent;
                parent = Path.GetDirectoryName(candidate);
            }
            if (parent is not null && visited.Contains(FolderExclusionRules.Normalize(parent)))
                missing.Add(candidate);
        }
        if (!Directory.Exists(root.LocalPath))
            throw new DirectoryNotFoundException(root.LocalPath);
        token.ThrowIfCancellationRequested();
        foreach (var path in missing)
            changes.Add(new BackgroundFolderChange(root.Id, path,
                BackgroundFolderChangeKind.RemovePath, false));
        progress?.Invoke(count, root.LocalPath);
        return new CatalogInventoryCheckResult(count, changes);
    }
}
