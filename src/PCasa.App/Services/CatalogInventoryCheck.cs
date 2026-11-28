using System.Diagnostics;
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

    internal sealed record Difference(string Path, bool IsNew, bool ForceExifRefresh);

    // The only output of this iterator is a genuine difference. A known
    // unchanged entry never enters the source processor, never emits a
    // filename progress event, and never becomes a per-file scheduler step.
    // The remaining local names at a successful directory boundary are A-B.
    // B-A and changed A intersect B are emitted immediately, not buffered
    // until the end of the directory or root.
    internal static IEnumerable<Difference> EnumerateDifferences(
        LocalDatabase database, MonitoredFolder root, string scanRoot, bool recursive,
        IReadOnlyCollection<string> exclusions, IReadOnlyCollection<string> inclusions,
        Func<CancellationToken> getToken, Action<string> onUnchanged,
        Action<string, IReadOnlySet<string>> onDirectoryComplete,
        IReadOnlySet<string>? completedDirectories = null,
        IReadOnlyList<string>? preferredDirectories = null)
    {
        FileInfo? entry = null;
        string? activeDirectory = null;
        Dictionary<string, FileStamp>? known = null;
        var measurement = Guid.NewGuid().ToString("N")[..8];
        var listing = 0L;
        var catalog = 0L;
        var comparing = 0L;
        var stamps = 0L;
        var directoryCompletion = 0L;
        var boundaryInsideListing = 0L;
        var entries = 0;
        var supported = 0;
        var differences = 0;
        var directories = 0;
        var complete = false;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        Dictionary<string, FileStamp> LoadDirectory(string directory)
        {
            var started = Stopwatch.GetTimestamp();
            try { return ReadDirectory(database, root.Id, directory, getToken()); }
            finally { catalog += Stopwatch.GetTimestamp() - started; }
        }
        void FinishDirectory(string directory)
        {
            var started = Stopwatch.GetTimestamp();
            var catalogBefore = catalog;
            try { FinishDirectoryCore(directory); }
            finally
            {
                var elapsed = Stopwatch.GetTimestamp() - started;
                boundaryInsideListing += elapsed;
                directoryCompletion += elapsed - (catalog - catalogBefore);
                directories++;
            }
        }
        void FinishDirectoryCore(string directory)
        {
            getToken().ThrowIfCancellationRequested();
            var skipped = completedDirectories?.Contains(directory) == true ||
                FolderExclusionRules.IsExcluded(directory, exclusions, inclusions);
            var remaining = skipped
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>((string.Equals(activeDirectory, directory,
                    StringComparison.OrdinalIgnoreCase) ? known! :
                    LoadDirectory(directory)).Keys,
                    StringComparer.OrdinalIgnoreCase);
            activeDirectory = null;
            known = null;
            onDirectoryComplete(directory, remaining);
        }
        using var iterator = FolderScanner.EnumerateIncludedFiles(scanRoot, recursive,
            exclusions, inclusions, CancellationToken.None,
            completedDirectories: completedDirectories, preferredDirectories: preferredDirectories,
            onDirectoryComplete: FinishDirectory, onFileInfo: file => entry = file,
            reportCompletedDirectories: true).GetEnumerator();
        try
        {
            while (true)
            {
                var listingStart = Stopwatch.GetTimestamp();
                var boundaryBefore = boundaryInsideListing;
                bool next;
                try { next = iterator.MoveNext(); }
                finally
                {
                    listing += Stopwatch.GetTimestamp() - listingStart -
                        (boundaryInsideListing - boundaryBefore);
                }
                if (!next) { complete = true; break; }
                var path = iterator.Current;
                getToken().ThrowIfCancellationRequested();
                if (path.Length == 0) continue;
                entries++;
                if (!CatalogMediaFormats.IsSupported(path)) continue;
                supported++;
                var compareStart = Stopwatch.GetTimestamp();
                var catalogBefore = catalog;
                var stampBefore = stamps;
                Difference? difference = null;
                try
                {
                    var file = entry ?? throw new InvalidOperationException("Missing directory entry.");
                    var directory = file.DirectoryName ?? scanRoot;
                    if (!string.Equals(activeDirectory, directory, StringComparison.OrdinalIgnoreCase))
                    {
                        known = LoadDirectory(directory);
                        activeDirectory = directory;
                    }
                    if (!known!.Remove(path, out var old))
                        difference = new Difference(path, true, false);
                    else
                    {
                        var stampStart = Stopwatch.GetTimestamp();
                        try
                        {
                            if (IsChanged(root, old, file))
                                difference = new Difference(path, false, CreationChanged(root, old, file));
                        }
                        finally { stamps += Stopwatch.GetTimestamp() - stampStart; }
                    }
                    if (difference is null) onUnchanged(directory);
                }
                finally
                {
                    comparing += Stopwatch.GetTimestamp() - compareStart -
                        (catalog - catalogBefore) - (stamps - stampBefore);
                }
                if (difference is not null)
                {
                    differences++;
                    if (differences == 1)
                        PCasaDiagnostics.WriteMessage("Scan.InventoryFirstDifference",
                            $"Measurement={measurement}; Root={scanRoot}; Entries={entries}; " +
                            $"ListingMs={Ms(listing):F1}; CatalogMs={Ms(catalog):F1}; " +
                            $"CompareMs={Ms(comparing):F1}; StampMs={Ms(stamps):F1}; " +
                            $"DirectoryCompletionMs={Ms(directoryCompletion):F1}");
                    // Do not time the image processing while this iterator is suspended.
                    yield return difference;
                }
            }
        }
        finally
        {
            PCasaDiagnostics.WriteMessage("Scan.InventoryTiming",
                $"Measurement={measurement}; Root={scanRoot}; Complete={complete}; " +
                $"Entries={entries}; Supported={supported}; Differences={differences}; Directories={directories}; " +
                $"ListingMs={Ms(listing):F1}; CatalogMs={Ms(catalog):F1}; " +
                $"CompareMs={Ms(comparing):F1}; StampMs={Ms(stamps):F1}; " +
                $"DirectoryCompletionMs={Ms(directoryCompletion):F1}");
        }
    }

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
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Count(string directory)
        {
            if (++count % 128 == 0) progress?.Invoke(count, directory);
        }
        foreach (var difference in EnumerateDifferences(database, root, scanRoot, true,
            exclusions, inclusions, () => token, Count,
            (directory, remaining) =>
            {
                visited.Add(FolderExclusionRules.Normalize(directory));
                missing.UnionWith(remaining);
            }))
        {
            Count(Path.GetDirectoryName(difference.Path) ?? scanRoot);
            var change = new BackgroundFolderChange(root.Id, difference.Path,
                BackgroundFolderChangeKind.ScanFile, false,
                ForceExifRefresh: difference.ForceExifRefresh);
            if (!difference.IsNew) changes.Add(change);
            else if (importNewImmediately is not null) importNewImmediately(change);
            else added.Add(change);
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
