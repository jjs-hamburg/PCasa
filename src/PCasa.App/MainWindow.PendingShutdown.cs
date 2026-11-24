using System.IO;
using System.Text.Json;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _shutdownReviewOpen;

    private bool ConfirmPendingShutdown()
    {
        if (_shutdownReviewOpen) return false;
        _shutdownReviewOpen = true;
        try
        {
            var tagDrafts = _tagDraft.Snapshot();
            var editId = _photoEditSession is { HasChanges: true } edit
                ? (long?)edit.ImageId : null;
            // Query only the local SQLite journal/catalog. No network image
            // reads are performed. The idle Close() path remains synchronous.
            var pending = SnapshotShutdownOperations(tagDrafts, editId);
            if (_panoramaSession is { } panorama)
                pending.Add(new ShutdownPendingOperation("panorama:active",
                    UiLanguage.T(panorama.IsPanorama
                        ? "Panorama erstellen" : "Mehrfachbearbeitung"),
                    UiLanguage.T(_externalBatchReview is { Outputs.Length: > 0 }
                        ? "Entscheidung offen" : "Läuft"),
                    panorama.Sources.Select(path =>
                        new ShutdownPendingFile(path, null)).ToArray(),
                    CanCancel: _externalBatchReview?.Busy != true));
            if (_externalEditReview is { } review)
            {
                // The session is independent of PhotoCraft's process lifetime.
                // Report a pending decision, not a supposedly running editor.
                var onDisk = ExternalEditTransaction.FindPending(
                    review.Snapshot.OriginalPath);
                if (onDisk is not null)
                {
                    var finalizing = onDisk.Finalizing;
                    pending.Add(new ShutdownPendingOperation("external:active",
                        UiLanguage.T("Externe Bearbeitung"),
                        review.Busy ? UiLanguage.T("Abschluss läuft") :
                            finalizing is not null ? UiLanguage.T("Abschluss fortsetzen") :
                            review.ResultPath is not null ? UiLanguage.T("Ergebnis bereit") :
                            UiLanguage.T("Entscheidung offen"),
                        [new ShutdownPendingFile(review.Snapshot.OriginalPath, null)],
                        CanCancel: !review.Busy,
                        ActionLabel: review.Busy ? "Abschluss läuft" :
                            finalizing is not null ? "Abschluss fortsetzen" : null));
                }
            }
            if (pending.Count == 0) return true;
            var window = new ShutdownPendingWorkWindow(pending,
                CancelShutdownOperationAsync) { Owner = this };
            return window.ShowDialog() == true;
        }
        finally { _shutdownReviewOpen = false; }
    }

    // Show both unsubmitted drafts and accepted file operations. Scan
    // intents are excluded: their progress survives a normal shutdown.
    private List<ShutdownPendingOperation> SnapshotShutdownOperations(
        IReadOnlyList<TagImageChange> draftTags, long? editId)
    {
        var needed = new HashSet<long>(draftTags.Select(tag => tag.ImageId));
        if (editId is long edited) needed.Add(edited);
        var operations = new DurablePackageJournal(_database).Pending()
            .Where(record => record.Type is not
                ("manual-folder-scan-v1" or "manual-scan-all-v1" or
                 "import-error-retry-v1" or "transfer-reconcile-v1"))
            .ToArray();
        var targets = operations.ToDictionary(record => record.Key,
            record => ShutdownRecipeTargets(record.Payload), StringComparer.Ordinal);
        foreach (var target in targets.Values)
            needed.UnionWith(target.Ids);
        var images = new Dictionary<long, ShutdownPendingFile>();
        using var db = _database.OpenConnection();
        foreach (var group in needed.Chunk(400))
        {
            using var command = db.CreateCommand();
            var args = group.Select((_, i) => "$id" + i).ToArray();
            for (var i = 0; i < group.Length; i++)
                command.Parameters.AddWithValue(args[i], group[i]);
            command.CommandText =
                "SELECT id,local_path,thumbnail_path FROM images WHERE id IN (" +
                string.Join(",", args) + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                images[reader.GetInt64(0)] = new ShutdownPendingFile(
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        var result = new List<ShutdownPendingOperation>();
        // Resolve explicit source paths using the catalog, not the original
        // files. The dialog must also work with disconnected NAS sources.
        var previews = images.Values.DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var unresolvedPaths = targets.Values.SelectMany(target => target.Paths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !previews.ContainsKey(path)).ToArray();
        foreach (var group in unresolvedPaths.Chunk(300))
        {
            using var command = db.CreateCommand();
            var args = group.Select((_, i) => "$path" + i).ToArray();
            for (var i = 0; i < group.Length; i++)
                command.Parameters.AddWithValue(args[i], group[i]);
            command.CommandText =
                "SELECT local_path,thumbnail_path FROM images WHERE local_path COLLATE NOCASE IN (" +
                string.Join(",", args) + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                previews[reader.GetString(0)] = new ShutdownPendingFile(
                    reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
        }
        foreach (var record in operations)
        {
            var target = targets[record.Key];
            var files = target.Ids.Where(images.ContainsKey).Select(id => images[id])
                .Concat(target.Paths.Select(path => previews.GetValueOrDefault(path)
                    ?? new ShutdownPendingFile(path, null)))
                .DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var status = record.State switch
            {
                "running" => "Läuft",
                "deferred" => "Fehler",
                "paused" => "Angehalten",
                _ => "Wartet"
            };
            // An already staged tag transaction is protected by the same
            // scheduler cancellation rules as in Taskmanager.
            var protectedTag = record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
                (record.CompletedSteps > 0 || record.State == "running" ||
                 record.Type.StartsWith("tag-recovery-", StringComparison.Ordinal));
            result.Add(new ShutdownPendingOperation(record.Key,
                SchedulerTaskView.TaskCaption(record), UiLanguage.T(status), files,
                CanCancel: !protectedTag));
        }
        if (_directImageWriteProgress is { CurrentPath: { } path } direct)
        {
            using var preview = db.CreateCommand();
            preview.CommandText = "SELECT thumbnail_path FROM images WHERE local_path=$path COLLATE NOCASE LIMIT 1;";
            preview.Parameters.AddWithValue("$path", path);
            result.Add(new ShutdownPendingOperation(direct.Key,
                direct.Phase ?? UiLanguage.T("Bildbearbeitung speichern"),
                UiLanguage.T(_directImageWriteStarted ? "Läuft" : "Wartet"),
                [new ShutdownPendingFile(path, preview.ExecuteScalar() as string)],
                CanCancel: !_directImageWriteStarted));
        }
        foreach (var draft in draftTags)
            if (images.TryGetValue(draft.ImageId, out var file))
                result.Add(new ShutdownPendingOperation("draft:tag:" + draft.ImageId,
                    UiLanguage.T("Tags"), UiLanguage.T("Nicht gespeichert"),
                    [file], draft.ImageId));
        if (editId is long editor && images.TryGetValue(editor, out var editedFile))
            result.Add(new ShutdownPendingOperation("draft:edit",
                UiLanguage.T("Bildbearbeitung speichern"),
                UiLanguage.T("Nicht gespeichert"), [editedFile]));
        return result;
    }

    internal static (HashSet<long> Ids, HashSet<string> Paths) ShutdownRecipeTargets(string payload)
    {
        var ids = new HashSet<long>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var json = JsonDocument.Parse(payload);
            Visit(json.RootElement);
        }
        catch (JsonException) { }
        return (ids, paths);

        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in node.EnumerateArray()) Visit(element);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            foreach (var property in node.EnumerateObject())
            {
                if ((property.Name is "ImageId" or "Id") &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out var id) && id > 0)
                    ids.Add(id);
                else if (property.Name == "ImageIds" &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in property.Value.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.Number &&
                            value.TryGetInt64(out var imageId) && imageId > 0)
                            ids.Add(imageId);
                }
                else if (property.Name == "Path" &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { Length: > 0 } path)
                    paths.Add(path);
                else if (property.Name == "Sources" &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in property.Value.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String &&
                            value.GetString() is { Length: > 0 } source)
                            paths.Add(source);
                }
                else if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    Visit(property.Value);
            }
        }
    }

    private async Task<bool> CancelShutdownOperationAsync(
        ShutdownPendingOperation item)
    {
        if (_directImageWriteProgress?.Key == item.Key)
        {
            if (_directImageWriteStarted) return false;
            _directImageWriteCancellation?.Cancel();
            return true;
        }
        if (item.Key.StartsWith("draft:tag:", StringComparison.Ordinal) &&
            item.DraftImageId is long imageId)
        {
            if (_tagDraft.DiscardImage(imageId))
            {
                RefreshTagDraftControls();
                await RefreshTagSidebarAsync();
            }
            return true;
        }
        if (item.Key == "draft:edit")
        {
            CancelEditSession();
            return true;
        }
        if (item.Key == "panorama:active" && _panoramaSession is { } session)
        {
            if (_externalBatchReview?.Busy == true) return false;
            _panoramaSession = null;
            _externalBatchReview = null;
            await Task.Run(() => PanoramaTransaction.Cleanup(session));
            ResumeSchedulerAfterExternalEdit();
            UpdateEditControls();
            return true;
        }
        if (item.Key == "external:active")
        {
            var review = _externalEditReview;
            if (review is null) return true;
            if (review.Busy) return false;
            var persisted = ExternalEditTransaction.FindPending(
                review.Snapshot.OriginalPath);
            if (persisted is null)
            {
                _externalEditReview = null;
                ResumeSchedulerAfterExternalEdit();
                UpdateEditControls();
                return true;
            }
            // Once a Save or Copy has been selected and durably recorded,
            // 'Cancel' can no longer roll it back. Complete the SAME choice.
            return await CompleteExternalReviewAsync(
                persisted.Finalizing ?? ExternalEditDecision.Cancel);
        }
        if (new DurablePackageJournal(_database).Pending()
            .Any(record => record.Key == item.Key))
            return await _scheduler.DeleteDurableTaskAsync(item.Key);
        return await _scheduler.CancelActiveTaskAsync(item.Key);
    }
}
