using System.IO;
using System.Text.Json;
using PCasa.App;
using PCasa.App.Data;

namespace PCasa.App.Services;

// The scheduler owns task identities, lifecycle, ordering and snapshots.
// Sources can supply accepted scan intents, but the UI never joins or sorts
// those sources itself. This is a read-only scheduler projection, not a
// second queue and not a second executor.
public sealed record SchedulerTaskViewItem(
    string Key, string Title, string Directory, string Progress,
    string Status, string? Error, int Priority, long Order,
    bool Durable, bool Movable, bool Retryable, bool Deletable,
    SchedulerWorkKind WorkKind = SchedulerWorkKind.Catalog)
{
    // Internal state remains stable German tokens for actions and filters.
    // Only the visible cell is translated at the presentation boundary.
    public string LocalizedStatus => UiLanguage.T(Status);
}

public sealed record SchedulerTaskViewResult(
    List<SchedulerTaskViewItem> Rows,
    Dictionary<string, IReadOnlyList<OriginalWriteFailureRepository.FailedFile>> Failed);

public sealed class SchedulerTaskView
{
    private readonly PCasaScheduler _scheduler;
    private readonly LocalDatabase _database;
    private readonly DurablePackageJournal _journal;
    private readonly OriginalWriteFailureRepository _originalFailures;
    private readonly Dictionary<string,string> _initialDirectoryByKey =
        new(StringComparer.Ordinal);
    private readonly bool _canMoveAutomatic, _canResumeAutomatic,
        _canStopAutomatic, _canRetryFailed, _canDiscardFailed;

    public SchedulerTaskView(PCasaScheduler scheduler, LocalDatabase database,
        bool canMoveAutomatic, bool canResumeAutomatic,
        bool canStopAutomatic, bool canRetryFailed, bool canDiscardFailed)
    {
        _scheduler = scheduler;
        _database = database;
        _journal = new DurablePackageJournal(database);
        _originalFailures = new OriginalWriteFailureRepository(database);
        _canMoveAutomatic = canMoveAutomatic;
        _canResumeAutomatic = canResumeAutomatic;
        _canStopAutomatic = canStopAutomatic;
        _canRetryFailed = canRetryFailed;
        _canDiscardFailed = canDiscardFailed;
    }

    public SchedulerTaskViewResult Snapshot(
        IReadOnlyList<AutomaticScanTask> automaticScans, bool includeCompleted)
    {
        var snapshots = _scheduler.Snapshot();
        var showCompleted = includeCompleted;
                var active = _journal.Pending();
                var done = showCompleted
                    ? _journal.RecentCompleted()
                    : Array.Empty<DurablePackageRecord>();
                // Resolve paths for all newly accepted GPS/tag packages with
                // bounded SQL batches. A per-package SQLite connection here
                // made the Taskmanager unresponsive with thousands of jobs.
                var imagePaths = LoadFirstImagePaths(active.Concat(done));
                var latest = snapshots
                    .GroupBy(s => s.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First());
                var rows = new List<SchedulerTaskViewItem>();
                var scanProgress = new FolderRepository(_database);
                var firstImports = scanProgress.GetPendingFirstImportQueue();
                var continuedImports = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var record in active.Concat(done))
                {
                    // Old transfer-reconcile-v1 records were temporary
                    // repair probes, not user-owned transfers. They have
                    // no meaningful restart, delete or priority action.
                    if (record.Type == "transfer-reconcile-v1") continue;
                    latest.TryGetValue(record.Key, out var snapshot);
                    var status = record.State switch
                    {
                        "completed" => "Abgeschlossen",
                        "deferred" => "Fehler",
                        _ when snapshot?.State == SchedulerJobState.Running => "Läuft",
                        _ when snapshot?.State == SchedulerJobState.Failed => "Fehler",
                        _ => "Wartet"
                    };
                    var path = DirectoryFor(record, snapshot?.CurrentPath, imagePaths);
                    var continuingImport = record.State != "completed" &&
                        AcknowledgesPendingImport(record) &&
                        firstImports.Any(first => string.Equals(
                            first.Directory, path, StringComparison.OrdinalIgnoreCase));
                    if (continuingImport) continuedImports.Add(path);
                    var work = continuingImport
                        ? UiLanguage.T("Bilder einlesen") : TaskCaption(record);
                    var total = CountRecipeImages(record.Payload);
                    // One confirmed scan counter, read from the SAME durable
                    // receipt as the floating progress window. A queued job
                    // restored after restart has no in-memory snapshot yet.
                    var progress = record.Type == "manual-folder-scan-v1"
                        ? UiLanguage.Format("{0:N0} geprüft",
                            Math.Max(snapshot?.Processed ?? 0,
                                scanProgress.GetConfirmedRegularScanCount(record.Key)))
                        : snapshot is { Total: > 0 }
                            ? $"{snapshot.Processed:N0}/{snapshot.Total:N0}"
                            : total > 0
                                ? $"{Math.Clamp(record.CompletedSteps - (record.Type.StartsWith("tag-edit-", StringComparison.Ordinal) ? 1 : 0), 0, total):N0}/{total:N0}"
                                : record.CompletedSteps > 0
                                    ? record.CompletedSteps.ToString("N0") : "–";
                    var isWaiting = status == "Wartet";
                    var movable = isWaiting;
                    var retryable = status == "Fehler" && record.State == "deferred";
                    // Tag write intents may already be staged in SQLite. The
                    // scheduler must protect them against accidental deletion;
                    // an unmanageable active task is not shown as actionable.
                    var deletable = record.State is "queued" or "deferred" or "running" &&
                        !(record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
                            (record.CompletedSteps > 0 || record.State == "running" ||
                             record.Type.StartsWith("tag-recovery-", StringComparison.Ordinal)));
                    // Running but protected GPS/tag writes are STILL jobs.
                    // Action restrictions must never hide the task itself.
                    rows.Add(new SchedulerTaskViewItem(record.Key, work, path, progress,
                        status, record.Error ?? snapshot?.Error,
                        (int)record.Priority, record.QueueOrder, true,
                        movable, retryable, deletable, record.WorkKind));
                }
                // Automatic discoveries and full-root reviews are REAL
                // scanner requests, not transient maintenance probes.
                // They run under this scheduler and can be stopped through
                // the original background owner without deleting catalog data.
                var firstByKey = firstImports.ToDictionary(
                    first => $"initial:{first.FolderId}", StringComparer.Ordinal);
                var liveKeys = new HashSet<string>(
                    automaticScans.Select(scan => scan.Key),
                    StringComparer.Ordinal);
                foreach (var scan in automaticScans)
                {
                    firstByKey.TryGetValue(scan.Key, out var first);
                    rows.Add(new SchedulerTaskViewItem(scan.Key, scan.Title,
                        scan.Directory, scan.Progress, scan.Status,
                        null, scan.Priority, first?.QueueOrder ?? scan.Order,
                        false, _canMoveAutomatic &&
                            ((first is not null) ||
                             (scan.Priority == 4 && IsRootReview(scan.Key) &&
                              scan.Status == "Wartet")),
                        false, _canStopAutomatic && scan.CanStop));
                }

                // An in-process database backup is a real scheduler
                // job; show its queued/running lifecycle directly from
                // scheduler snapshots. No duplicate UI task is created.
                foreach (var backup in latest.Values.Where(job =>
                    job.Key.StartsWith("database:backup:", StringComparison.Ordinal) &&
                    job.State is SchedulerJobState.Queued or
                        SchedulerJobState.Running or SchedulerJobState.YieldRequested))
                {
                    var destination = backup.Key.Length >
                        "database:backup:".Length + 33
                        ? backup.Key[("database:backup:".Length + 33)..]
                        : "–";
                    var fileProgress = backup.Total > 0
                        ? UiLanguage.Format("{0:N0}/{1:N0} Dateien",
                            backup.Processed, backup.Total)
                        : "–";
                    rows.Add(new SchedulerTaskViewItem(backup.Key,
                        UiLanguage.T("Datenbank sichern"), destination,
                        fileProgress,
                        backup.State == SchedulerJobState.Running
                            ? "Läuft" : "Wartet",
                        backup.Error, (int)backup.Priority, backup.Id,
                        false, false, false, _canStopAutomatic,
                        backup.WorkKind));
                }

                // The pending initial-import flag is a durable job from
                // the moment the source was selected, NOT merely when
                // the UI scan pump gets around to starting it. Paused
                // imports remain visible without executing at startup.
                foreach (var first in firstImports)
                {
                    var key = $"initial:{first.FolderId}";
                    // One root, one import: a manually continued full-root
                    // scan is the real active scheduler job. Do not show its
                    // old pending marker as an unrelated second task.
                    if (liveKeys.Contains(key) ||
                        continuedImports.Contains(first.Directory)) continue;
                    rows.Add(new SchedulerTaskViewItem(key, UiLanguage.T("Bilder einlesen"),
                        first.Directory, "–",
                        first.Paused ? "Angehalten" : "Wartet",
                        null, 2, first.QueueOrder,
                        false, _canMoveAutomatic,
                        first.Paused && _canResumeAutomatic,
                        _canStopAutomatic));
                }

                // Unrelated transient scheduler snapshots (historical source
                // probes, cache refresh etc.) have no user action.
                // Parked individual originals are real errors, but NOT
                // additional scheduler tasks. Group by their original P0
                // package and delegate Retry/Discard to the SAME existing
                // write-error actions used by the error dialog.
                var failedGroups = _originalFailures.Unresolved()
                    .GroupBy(issue => issue.PackageKey, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key,
                        group => (IReadOnlyList<OriginalWriteFailureRepository.FailedFile>)
                            group.ToArray(), StringComparer.Ordinal);
                foreach (var (key, failures) in failedGroups)
                {
                    if (!_canRetryFailed && !_canDiscardFailed)
                        continue; // no control is available in this window
                    var existingIndex = rows.FindIndex(item =>
                        string.Equals(item.Key, key, StringComparison.Ordinal));
                    if (existingIndex >= 0)
                    {
                        // The original durable package IS the failed-file owner.
                        // Never show a second synthetic task for its error rows.
                        var original = rows[existingIndex];
                        if (original.Status == "Abgeschlossen")
                            rows[existingIndex] = original with
                            {
                                Status = "Fehler",
                                Retryable = _canRetryFailed,
                                Deletable = _canDiscardFailed,
                                Error = failures[0].Error
                            };
                        continue;
                    }
                    var first = failures[0];
                    var task = first.Kind == OriginalWriteFailureRepository.Gps
                        ? UiLanguage.T("GPS schreiben") : UiLanguage.T("Tags schreiben");
                    rows.Add(new SchedulerTaskViewItem(key, task,
                        CurrentDirectory(first.Path),
                        UiLanguage.Format("{0:N0} fehlerhaft", failures.Count),
                        "Fehler", first.Error, 0, long.MaxValue / 4,
                        false, false, _canRetryFailed,
                        _canDiscardFailed));
                }
                // Actual execution first. A paused high-priority task
                // cannot precede a running lower-priority task on screen.
                // One effective order comes from the scheduler. The display
                // does not invent a separate status-first queue: moving a
                // waiting task above a running one must be VISIBLE at once.
                var positioned = _scheduler.OrderTaskView(
                    rows, row => row.Key,
                    row => (SchedulerPriority)row.Priority,
                    row => row.Order,
                    row => row.Status == "Abgeschlossen",
                    (row, priority, order) => row with
                    {
                        Priority = (int)priority,
                        Order = order
                    });
                return new SchedulerTaskViewResult(positioned.ToList(), failedGroups);
    }

    private IReadOnlyDictionary<long,string> LoadFirstImagePaths(
        IEnumerable<DurablePackageRecord> records)
    {
        var ids = records
            .Where(item => !_initialDirectoryByKey.ContainsKey(item.Key))
            .Select(item => FirstImageId(item.Payload))
            .Where(id => id > 0).Distinct().ToArray();
        var result = new Dictionary<long,string>();
        if (ids.Length == 0) return result;
        using var db = _database.OpenConnection();
        foreach (var chunk in ids.Chunk(300))
        {
            using var cmd = db.CreateCommand();
            var names = chunk.Select((_, index) => "$i" + index).ToArray();
            cmd.CommandText = "SELECT id,local_path FROM images WHERE id IN (" +
                string.Join(",", names) + ");";
            for (var i=0; i<chunk.Length; i++)
                cmd.Parameters.AddWithValue(names[i], chunk[i]);
            using var rows = cmd.ExecuteReader();
            while (rows.Read())
                result[rows.GetInt64(0)] = rows.GetString(1);
        }
        return result;
    }

    private string DirectoryFor(DurablePackageRecord item,
        string? currentPath, IReadOnlyDictionary<long,string> imagePaths)
    {
        // The authoritative root of a manual scan is immutable in the
        // durable recipe. A yield/restart clears CurrentPath by design; it
        // must never erase the folder shown in this task's tile.
        if (item.Type == "manual-folder-scan-v1" &&
            DirectoryFromRecipe(item.Payload) is string root)
        {
            _initialDirectoryByKey[item.Key] = root;
            return root;
        }
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var current = CurrentDirectory(currentPath);
            if (current.Length > 0 && current != "–")
                _initialDirectoryByKey[item.Key] = current;
            return current;
        }
        if (_initialDirectoryByKey.TryGetValue(item.Key, out var cached))
            return cached;
        var path = FirstPathFromRecipe(item.Payload);
        if (path is null)
        {
            var id = FirstImageId(item.Payload);
            if (id > 0) imagePaths.TryGetValue(id, out path);
        }
        var dir = CurrentDirectory(path);
        if (dir.Length > 0)
            _initialDirectoryByKey[item.Key] = dir;
        return dir;
    }

    private static string CurrentDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "–";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) &&
                extension.Length <= 8 &&
                extension.All(ch => char.IsLetterOrDigit(ch) || ch == '.')
                ? Path.GetDirectoryName(path) ?? path : path;
        }
        catch { return path; }
    }

    private static bool AcknowledgesPendingImport(DurablePackageRecord record)
    {
        if (record.Type != "manual-folder-scan-v1") return false;
        try
        {
            using var json = JsonDocument.Parse(record.Payload);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("AcknowledgeTargetedPending", out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string? DirectoryFromRecipe(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("Directory", out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                var path = element.GetString();
                if (!string.IsNullOrWhiteSpace(path) &&
                    Path.IsPathFullyQualified(path))
                    return path;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? FirstPathFromRecipe(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            return Search(json.RootElement);
        }
        catch (JsonException) { return null; }

        static string? Search(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in node.EnumerateObject())
                    if (member.Value.ValueKind == JsonValueKind.String &&
                        (member.Name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                             member.Name.Contains("directory", StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = member.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value) &&
                            Path.IsPathFullyQualified(value)) return value;
                    }
                foreach (var member in node.EnumerateObject())
                    if (Search(member.Value) is { } found) return found;
            }
            if (node.ValueKind == JsonValueKind.Array)
                foreach (var member in node.EnumerateArray())
                    if (Search(member) is { } found) return found;
            return null;
        }
    }

    private static long FirstImageId(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ImageIds", out var ids) &&
                ids.ValueKind == JsonValueKind.Array &&
                ids.GetArrayLength() > 0 &&
                ids[0].TryGetInt64(out var id)) return id;
        }
        catch (JsonException) { }
        return 0;
    }

    private static int CountRecipeImages(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("ImageIds", out var ids) &&
                   ids.ValueKind == JsonValueKind.Array
                ? ids.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static bool IsRootReview(string key)
        => key.StartsWith("automatic:", StringComparison.Ordinal) &&
           key.Contains(":RescanRoot:", StringComparison.Ordinal);

    private static string TaskCaption(DurablePackageRecord item)
        => item.Type switch
        {
            "gps-edit-v1" or "gps-edit-v2" => item.Payload.Contains("\"Mode\":2", StringComparison.Ordinal)
                ? UiLanguage.T("GPS entfernen") : UiLanguage.T("GPS schreiben"),
            "tag-edit-v1" or "tag-edit-v2" => UiLanguage.T("Tags schreiben"),
            "tag-recovery-v1" or "tag-recovery-v2" => UiLanguage.T("Tags wiederherstellen"),
            "manual-folder-scan-v1" => UiLanguage.T("Ordner aktualisieren"),
            "manual-scan-all-v1" => UiLanguage.T("Alle Ordner einlesen"),
            "gallery-transfer-v1" or "transfer-reconcile-v1" => UiLanguage.T("Dateien übertragen"),
            "gallery-delete-v1" => UiLanguage.T("Dateien löschen"),
            "create-gallery-folder-v1" => UiLanguage.T("Ordner anlegen"),
            _ => item.Type
        };

    private static string BackgroundCaption(string key)
        => key switch
        {
            var value when value.StartsWith("catalog:", StringComparison.Ordinal) =>
                UiLanguage.T("Katalog aktualisieren"),
            var value when value.StartsWith("tags:", StringComparison.Ordinal) =>
                UiLanguage.T("Tags überprüfen"),
            var value when value.Contains("cache", StringComparison.OrdinalIgnoreCase) =>
                UiLanguage.T("Vorschaubilder aktualisieren"),
            _ => UiLanguage.T("Hintergrundarbeit")
        };

}
