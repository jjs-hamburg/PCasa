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

    private List<ShutdownPendingOperation> SnapshotShutdownOperations(
        IReadOnlyList<TagImageChange> draftTags, long? editId)
    {
        // Use only the local catalog, journal and local thumbnail paths.
        // Building the dialog may not read or inspect source files on NAS.
        var records = new DurablePackageJournal(_database).Pending()
            .Where(record => record.Type is not
                ("manual-folder-scan-v1" or "manual-scan-all-v1")).ToArray();
        var idSets = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var pathsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var needed = new HashSet<long>(draftTags.Select(tag => tag.ImageId));
        if (editId is long edited) needed.Add(edited);
        foreach (var record in records)
        {
            var ids = new HashSet<long>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var payload = JsonDocument.Parse(record.Payload);
                FindReferencedImages(payload.RootElement, ids, paths);
            }
            catch (JsonException) { }
            idSets[record.Key] = ids;
            pathsByKey[record.Key] = paths;
            needed.UnionWith(ids);
        }

        var images = new Dictionary<long, ShutdownPendingFile>();
        using var db = _database.OpenConnection();
        foreach (var group in needed.Chunk(400))
        {
            using var command = db.CreateCommand();
            var args = group.Select((_, i) => "$id" + i).ToArray();
            for (var i = 0; i < group.Length; i++)
                command.Parameters.AddWithValue(args[i], group[i]);
            command.CommandText = "SELECT id,local_path,thumbnail_path FROM images WHERE id IN (" +
                string.Join(",", args) + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                images[reader.GetInt64(0)] = new ShutdownPendingFile(
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        var result = new List<ShutdownPendingOperation>();
        foreach (var draft in draftTags)
            if (images.TryGetValue(draft.ImageId, out var file))
                result.Add(new ShutdownPendingOperation("draft:tag:" + draft.ImageId,
                    UiLanguage.T("Tags"), UiLanguage.T("Nicht gespeichert"),
                    [file], draft.ImageId));
        if (editId is long editor && images.TryGetValue(editor, out var editedFile))
            result.Add(new ShutdownPendingOperation("draft:edit",
                UiLanguage.T("Bildbearbeitung speichern"),
                UiLanguage.T("Nicht gespeichert"), [editedFile]));
        foreach (var record in records)
        {
            var files = idSets[record.Key]
                .Where(images.ContainsKey).Select(id => images[id]).ToList();
            foreach (var path in pathsByKey[record.Key])
                if (!files.Any(file => file.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                    files.Add(new ShutdownPendingFile(path, null));
            if (files.Count == 0)
                files.Add(new ShutdownPendingFile(record.Key, null));
            result.Add(new ShutdownPendingOperation(record.Key,
                ShutdownOperationCaption(record.Type),
                record.State switch
                {
                    "running" => UiLanguage.T("Läuft"),
                    "deferred" => UiLanguage.T("Fehler"),
                    "paused" => UiLanguage.T("Angehalten"),
                    _ => UiLanguage.T("Wartet")
                }, files));
        }
        return result;
    }

    private static string ShutdownOperationCaption(string type) => type switch
    {
        "image-edit-v1" or "image-crop-v1" => UiLanguage.T("Bildbearbeitung speichern"),
        "tag-edit-v1" or "tag-edit-v2" or "tag-recovery-v1" or
            "tag-recovery-v2" => UiLanguage.T("Tags"),
        "gps-edit-v1" or "gps-edit-v2" or "gpx-write-v1" => "GPS",
        "gallery-delete-v1" => UiLanguage.T("Löschen"),
        "gallery-transfer-v1" => UiLanguage.T("Verschieben"),
        "undo-restore-v1" => UiLanguage.T("Bild wiederherstellen"),
        "create-gallery-folder-v1" => UiLanguage.T("Neuen Unterordner anlegen …"),
        _ => type
    };

    private static void FindReferencedImages(JsonElement element,
        HashSet<long> ids, HashSet<string> paths)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("ImageId") &&
                    property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt64(out var id) && id > 0)
                    ids.Add(id);
                if ((property.NameEquals("ImageIds") ||
                     property.NameEquals("SelectedImageIds")) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in property.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number &&
                            item.TryGetInt64(out var next) && next > 0)
                            ids.Add(next);
                if (property.Name is "Path" or "Source" or "OriginalPath" or
                    "SourcePath" or "DestinationPath" &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.GetString() is { } path &&
                    Path.IsPathFullyQualified(path))
                    paths.Add(path);
                FindReferencedImages(property.Value, ids, paths);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                FindReferencedImages(item, ids, paths);
    }

    private async Task<bool> CancelShutdownOperationAsync(
        ShutdownPendingOperation item)
    {
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
