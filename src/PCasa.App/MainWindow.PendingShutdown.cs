using System.IO;
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

    // Already accepted original writes are durable scheduler work, not
    // unsaved editor drafts. On Exit they checkpoint and resume on startup;
    // deliberate cancellation remains exclusively in the Taskmanager.
    private List<ShutdownPendingOperation> SnapshotShutdownOperations(
        IReadOnlyList<TagImageChange> draftTags, long? editId)
    {
        var needed = new HashSet<long>(draftTags.Select(tag => tag.ImageId));
        if (editId is long edited) needed.Add(edited);
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
