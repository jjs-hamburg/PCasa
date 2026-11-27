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

// Presentation adapter remains in the host. The portable coordinator has
// no dependency on translated UI rows or the Windows application assembly.
public static class SchedulerTaskViewExtensions
{
    public static SchedulerTaskView CreateTaskView(this PCasaScheduler scheduler,
        LocalDatabase database, bool canMoveAutomatic, bool canResumeAutomatic,
        bool canStopAutomatic, bool canRetryFailed, bool canDiscardFailed)
        => new(scheduler, database, canMoveAutomatic, canResumeAutomatic,
            canStopAutomatic, canRetryFailed, canDiscardFailed);
}

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
                        "paused" => "Angehalten",
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
                        ? UiLanguage.Format("{0:N0} Bilder",
                            scanProgress.CountCataloguedInDirectory(path))
                        : snapshot is { Total: > 0 }
                            ? $"{snapshot.Processed:N0}/{snapshot.Total:N0}"
                            : total > 0
                                ? $"{Math.Clamp(record.CompletedSteps - (record.Type.StartsWith("tag-edit-", StringComparison.Ordinal) ? 1 : 0), 0, total):N0}/{total:N0}"
                                : record.CompletedSteps > 0
                                    ? record.CompletedSteps.ToString("N0") : "–";
                    var isWaiting = status == "Wartet";
                    var movable = isWaiting;
                    var retryable = status == "Fehler" && record.State == "deferred";
                    // Current tag packages can cancel their own staged revisions.
                    // Legacy recovery recipes have no exclusive ownership.
                    var deletable = record.State is "queued" or "deferred" or "running" or "paused" &&
                        !(record.Type != "tag-edit-v2" &&
                            record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
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
                        scan.Directory, first is not null
                            ? UiLanguage.Format("{0:N0} Bilder", first.CataloguedCount)
                            : scan.CatalogDirectory is string directory
                                ? UiLanguage.Format("{0:N0} Bilder",
                                    scanProgress.CountCataloguedInDirectory(directory))
                                : scan.Progress, scan.Status,
                        null, first is not null ? (int)SchedulerPriority.ManualAction : scan.Priority,
                        first?.QueueOrder ?? scan.Order,
                        false, _canMoveAutomatic &&
                            ((first is not null) ||
                             (scan.Priority == 4 && IsRootReview(scan.Key) &&
                              scan.Status == "Wartet")),
                        false, _canStopAutomatic && scan.CanStop));
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
                        first.Directory, UiLanguage.Format("{0:N0} Bilder", first.CataloguedCount),
                        first.Paused ? "Angehalten" : "Wartet",
                        null, (int)SchedulerPriority.ManualAction, first.QueueOrder,
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
                    var task = first.Kind switch
                    {
                        OriginalWriteFailureRepository.Gps => UiLanguage.T("GPS schreiben"),
                        OriginalWriteFailureRepository.CaptureDate => UiLanguage.T("Aufnahmedatum aus Dateinamen übernehmen"),
                        _ => UiLanguage.T("Tags schreiben")
                    };
                    rows.Add(new SchedulerTaskViewItem(key, task,
                        CurrentDirectory(first.Path),
                        UiLanguage.Format("{0:N0} fehlerhaft", failures.Count),
                        "Fehler", first.Error, 0, long.MaxValue / 4,
                        false, false, _canRetryFailed,
                        _canDiscardFailed));
                }
                // External processes are user interactions, not scheduler workers.
                // Project the persisted single session read-only so waiting for
                // Topaz never looks like an idle PCasa; no shadow task is queued.
                try
                {
                    var external = ExternalEditTransaction.FindPending();
                    if (external is not null)
                        rows.Add(new SchedulerTaskViewItem(
                            "external:session:" + external.Id.ToString("N"),
                            UiLanguage.T("Externe Bearbeitung"),
                            external.OriginalPath,
                            UiLanguage.T(external.Finalizing is null
                                ? "Warte auf Ergebnis oder Entscheidung"
                                : "Speicherentscheidung wird abgeschlossen"),
                            "Wartet", null, 0, long.MinValue,
                            false, false, false, false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or InvalidDataException)
                {
                    rows.Add(new SchedulerTaskViewItem(
                        "external:session:invalid",
                        UiLanguage.T("Externe Sitzung prüfen"),
                        ExternalEditTransaction.DirectoryPath, "–",
                        "Fehler", ex.Message, 0, long.MinValue,
                        false, false, false, false));
                }
                // Non-durable external stage/commit/refresh/cleanup work is
                // already owned by the ONE scheduler. Previously the Taskmanager
                // only displayed durable packages and a few special jobs,
                // leaving the external edit invisible while bytes were copied.
                foreach (var job in latest.Values.Where(job =>
                    job.Key.StartsWith("external:", StringComparison.Ordinal) &&
                    !job.Key.StartsWith("external:session:", StringComparison.Ordinal) &&
                    !job.Key.StartsWith("external:cleanup:", StringComparison.Ordinal) &&
                    (job.State is SchedulerJobState.Queued or SchedulerJobState.Running
                        or SchedulerJobState.YieldRequested or SchedulerJobState.Failed ||
                     (showCompleted && job.State == SchedulerJobState.Completed))))
                {
                    var label = job.Key.StartsWith("external:stage:", StringComparison.Ordinal)
                        ? "Arbeitskopie erstellen"
                        : job.Key.StartsWith("external:commit:", StringComparison.Ordinal)
                            ? "Externes Ergebnis übernehmen"
                            : job.Key.StartsWith("external:catalog:", StringComparison.Ordinal)
                                ? "Externes Ergebnis katalogisieren"
                                : "Temporäre Arbeitsdateien bereinigen";
                    var status = job.State switch
                    {
                        SchedulerJobState.Running => "Läuft",
                        SchedulerJobState.Failed => "Fehler",
                        SchedulerJobState.Completed => "Abgeschlossen",
                        _ => "Wartet"
                    };
                    rows.Add(new SchedulerTaskViewItem(job.Key,
                        UiLanguage.T(label),
                        job.CurrentPath ?? ExternalEditTransaction.DirectoryPath,
                        job.Phase is { Length: > 0 } phase
                            ? UiLanguage.T(phase) +
                              (job.Total > 0 ? $" · {job.Processed:N0}/{job.Total:N0}" : "")
                            : job.Total > 0 ? $"{job.Processed:N0}/{job.Total:N0}" : "–",
                        status, job.Error, (int)job.Priority, job.Id,
                        false, false, false, false, job.WorkKind));
                }

                // Accepted single-image writes have the same real queue rank
                // and lifecycle as other user work. No separate direct slot.
                foreach (var job in latest.Values.Where(job => job.SessionWrite &&
                    (job.State is SchedulerJobState.Queued or SchedulerJobState.Running
                        or SchedulerJobState.YieldRequested or SchedulerJobState.Failed ||
                     (showCompleted && job.State == SchedulerJobState.Completed))))
                {
                    if (rows.Any(row => row.Key == job.Key)) continue;
                    var status = job.State switch
                    {
                        SchedulerJobState.Running => "Läuft",
                        SchedulerJobState.YieldRequested => "Läuft",
                        SchedulerJobState.Failed => "Fehler",
                        SchedulerJobState.Completed => "Abgeschlossen",
                        _ => job.Paused ? "Angehalten" : "Wartet"
                    };
                    rows.Add(new SchedulerTaskViewItem(job.Key,
                        UiLanguage.T(job.Phase ?? "Dateiaktion"),
                        CurrentDirectory(job.CurrentPath),
                        job.Total > 0 ? $"{job.Processed:N0}/{job.Total:N0}" : "–",
                        status, job.Error, (int)job.Priority, job.QueueOrder,
                        false, true, false, job.State is SchedulerJobState.Queued or
                            SchedulerJobState.Running or SchedulerJobState.YieldRequested or SchedulerJobState.Failed, job.WorkKind));
                }

                // Actual running work first, then the scheduler's effective
                // pending priority/order. This is a single scheduler-owned
                // projection, not a second dispatch queue. Reordering still
                // modifies the real persisted scheduler ranks.
                var positioned = _scheduler.OrderTaskView(
                    rows, row => row.Key,
                    row => (SchedulerPriority)row.Priority,
                    row => row.Order,
                    row => row.Status == "Abgeschlossen",
                    (row, priority, order) => row with
                    {
                        Priority = (int)priority,
                        Order = order
                    },
                    row => row.Status == "Läuft");
                var ordered = positioned.Where(row =>
                    row.Key != PCasaScheduler.ContinuousReviewKey).Concat(
                    positioned.Where(row => row.Key == PCasaScheduler.ContinuousReviewKey));
                return new SchedulerTaskViewResult(ordered.ToList(), failedGroups);
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

    internal static string GalleryRotationCaption => UiLanguage.T("Bilder drehen");

    internal static string TaskCaption(DurablePackageRecord item)
        => item.Type switch
        {
            "gps-edit-v1" or "gps-edit-v2" => item.Payload.Contains("\"Mode\":2", StringComparison.Ordinal)
                ? UiLanguage.T("GPS entfernen") : UiLanguage.T("GPS schreiben"),
            "tag-edit-v1" or "tag-edit-v2" => UiLanguage.T("Tags schreiben"),
            "capture-date-from-name-v1" => UiLanguage.T("Aufnahmedatum aus Dateinamen übernehmen"),
            "explicit-exif-dates-v1" => UiLanguage.T("EXIF-Datumsfelder speichern"),
            "gallery-rotation-v1" => GalleryRotationCaption,
            "image-edit-v1" or "image-edit-v2" => UiLanguage.T("Bildbearbeitung speichern"),
            "undo-restore-v1" or "undo-restore-v2" => UiLanguage.T("Bild wiederherstellen"),
            "image-rename-v1" => UiLanguage.T("Bild umbenennen"),
            "folder-rename-v1" => UiLanguage.T("Ordner umbenennen"),
            "tag-recovery-v1" or "tag-recovery-v2" => UiLanguage.T("Tags wiederherstellen"),
            "manual-folder-scan-v1" => UiLanguage.T("Ordner aktualisieren"),
            "import-error-retry-v1" => UiLanguage.T("Erneut einlesen"),
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
