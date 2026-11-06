using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

// Read-only projection of the existing automatic scan queue and current
// scheduler execution. The taskmanager never owns a second scanner or queue.
public sealed record AutomaticScanTask(
    string Key, string Title, string Directory, string Progress,
    string Status, int Priority, long Order, bool CanStop = true);

public partial class MainWindow
{
    private TaskManagerWindow? _taskManagerWindow;

    private static bool TryInitialTaskId(string key, out long folderId)
    {
        folderId = 0;
        return key.StartsWith("initial:", StringComparison.Ordinal) &&
            long.TryParse(key.AsSpan("initial:".Length), out folderId);
    }

    private static bool TryRootReviewTaskId(string key, out long folderId)
    {
        folderId = 0;
        if (!key.StartsWith("automatic:", StringComparison.Ordinal))
            return false;
        var rest = key.AsSpan("automatic:".Length);
        var delimiter = rest.IndexOf(':');
        return delimiter > 0 &&
            long.TryParse(rest[..delimiter], out folderId) &&
            rest[(delimiter + 1)..].StartsWith(
                "RescanRoot:", StringComparison.Ordinal);
    }

    // A UI reorder acknowledges the durable scheduler decision immediately.
    // Waiting for a NAS scanner's cancellation callbacks here kept the
    // Taskmanager's _acting flag raised and disabled EVERY action button,
    // even though the new rank was already committed to SQLite.
    private static void RequestTaskManagerYield(CancellationTokenSource cancellation)
    {
        if (cancellation.IsCancellationRequested) return;
        try
        {
            _ = cancellation.CancelAsync().ContinueWith(task =>
            {
                if (task.Exception is not null)
                    PCasaDiagnostics.Write("Taskmanager.Yield", task.Exception);
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException) { }
    }

    private async Task ObserveTaskManagerOwnerUpdateAsync(Task<bool> action)
    {
        try
        {
            if (!await action && !Dispatcher.HasShutdownStarted)
                StatusText.Text = UiLanguage.T(
                    "Taskmanager: Auftrag konnte nicht aktiviert werden.");
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Taskmanager.OwnerReorder", ex);
            if (!Dispatcher.HasShutdownStarted)
                StatusText.Text = UiLanguage.T(
                    "Taskmanager: Auftrag konnte nicht aktiviert werden.") +
                    " " + ex.Message;
        }
    }

    // The ONE scheduler owns priority and relative dispatch order for
    // every active task key, independent of original work type. These are
    // owner notifications for the existing physical scanner cursors, not
    // a second Taskmanager queue or a reordered copy of the rows.
    private async Task<bool> MoveGlobalTaskAsync(string key, string targetKey,
        IReadOnlyList<SchedulerTaskOrderItem> allAcceptedKeys)
    {
        var moved = await Task.Run(() =>
            _scheduler.MoveTaskGlobally(key, targetKey, allAcceptedKeys));
        if (!moved) return false;

        // A parked original mutation may now be ahead of other jobs, but
        // needs its actual deferred recipe restarted before it can run.
        // Never implicitly replay an already completed write.
        if (key.StartsWith("gps:edit:", StringComparison.Ordinal) ||
            key.StartsWith("tags:", StringComparison.Ordinal))
        {
            // Already-queued steps were reordered inside the scheduler.
            // Explicit Retry remains the way to unpark failed originals.
        }

        if (TryInitialTaskId(key, out var importId))
        {
            var unpaused = await Task.Run(() =>
                _scheduler.ChangeTaskIntent(() =>
                    _folderRepository.SetPendingFirstImportPaused(
                        importId, false)));
            if (unpaused)
            {
                _initialScanAttempted.Remove(importId);
                if (_initialScanActive && _activeScanFolderId != importId &&
                    _initialScanCancellation is
                        { IsCancellationRequested: false } running)
                    RequestTaskManagerYield(running);
                QueuePendingInitialScans();
            }
        }

        // Every automatic scan (including ScanFile/"Import file") belongs
        // to the SAME scan owner. Previously only full-root reviews received
        // the reorder notification, so moving a waiting ScanFile above other
        // work changed its displayed rank without changing the in-flight
        // batch. This does NOT create or duplicate any scanner work.
        var changesAutomaticOrder =
            key.StartsWith("automatic:", StringComparison.Ordinal) ||
            targetKey.StartsWith("automatic:", StringComparison.Ordinal);
        if (changesAutomaticOrder)
        {
            foreach (var item in allAcceptedKeys.Where(item =>
                TryIsRootReview(item.Key)))
            {
                if (!TryRootReviewTaskId(item.Key, out var rootId)) continue;
                var effective = _scheduler.EffectiveTaskPlacement(
                    item.Key, item.Priority, long.MaxValue).Priority;
                if (effective < SchedulerPriority.Idle)
                    _backgroundUrgentReviews.Add(rootId);
                else
                    _backgroundUrgentReviews.Remove(rootId);
            }
            if (TryRootReviewTaskId(key, out _) &&
                _scheduler.EffectiveTaskPlacement(
                    key, SchedulerPriority.Idle, long.MaxValue).Priority <
                    SchedulerPriority.Idle)
                _ = ObserveTaskManagerOwnerUpdateAsync(
                    StartAutomaticScanNowAsync(key));
            if (_backgroundInFlight is { Length: > 0 })
            {
                _backgroundOrderChanged = true;
                if (_backgroundCurrentScanCancellation is
                    { IsCancellationRequested: false } active)
                    RequestTaskManagerYield(active);
            }
            if (_backgroundScanInitialized && !_backgroundMonitorClosing)
                RefreshBackgroundMonitor();
            _backgroundDebounceTimer?.Start();
        }
        _scheduler.NotifyTaskViewChanged();
        return true;
    }

    private static bool TryIsRootReview(string key)
        => TryRootReviewTaskId(key, out _);

    private async Task<bool> MoveAutomaticScanAsync(
        string sourceKey, string targetKey)
    {
        // The two Verify-catalog rows represent different monitored roots
        // of the SAME P4 review queue. Persist their order in SQLite, and
        // update the ONE existing background scan dispatcher immediately.
        if (TryRootReviewTaskId(sourceKey, out var sourceRoot) &&
            TryRootReviewTaskId(targetKey, out var targetRoot))
        {
            var (moved, ordered) = await _scheduler.RunAsync(
                $"taskmanager:review-reorder:{Guid.NewGuid():N}",
                SchedulerPriority.UserAction,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    var ok = _folderRepository.MoveRootReview(sourceRoot, targetRoot);
                    return (ok, ok ? _folderRepository.GetRootReviewQueueOrder()
                        : Array.Empty<long>());
                }, workKind: SchedulerWorkKind.CatalogRead);
            if (!moved) return false;

            _backgroundReviewOrder.Clear();
            for (var index = 0; index < ordered.Count; index++)
                _backgroundReviewOrder[ordered[index]] = index + 1L;

            if (_backgroundInFlight is { } inFlight &&
                _backgroundInFlightIndex < inFlight.Length)
            {
                if (inFlight[_backgroundInFlightIndex].Kind ==
                        BackgroundFolderChangeKind.RescanRoot)
                {
                    // The current review may be waiting in the scheduler
                    // behind a manual scan. Cancel its queued attempt and
                    // requeue ALL remaining requests with their confirmed
                    // file/directory cursors, in the persisted new order.
                    _backgroundOrderChanged = true;
                    if (_backgroundCurrentScanCancellation is
                        { IsCancellationRequested: false } active)
                        try { await active.CancelAsync(); }
                        catch (ObjectDisposedException) { }
                }
                else
                {
                    // A running P3 concrete discovery is untouchable.
                    // Reorder only requests that have not been started.
                    var start = _backgroundInFlightIndex + 1;
                    var reordered = inFlight.Skip(start)
                        .OrderBy(request => request.Kind ==
                                BackgroundFolderChangeKind.RescanRoot ? 1 : 0)
                        .ThenBy(request => request.Kind ==
                                BackgroundFolderChangeKind.RescanRoot
                                ? _backgroundReviewOrder.GetValueOrDefault(
                                    request.MonitoredFolderId, long.MaxValue)
                                : long.MaxValue)
                        .ThenBy(request => request.Path,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    Array.Copy(reordered, 0, inFlight, start, reordered.Length);
                }
            }
            if (_backgroundPending.Count > 0 && !_backgroundWorkerRunning)
                _backgroundDebounceTimer?.Start();
            return true;
        }

        if (!TryInitialTaskId(sourceKey, out var source) ||
            !TryInitialTaskId(targetKey, out var target))
            return false;

        var (changed, nextFolderId) = await _scheduler.RunAsync(
            $"taskmanager:first-import-reorder:{Guid.NewGuid():N}",
            SchedulerPriority.UserAction,
            token =>
            {
                token.ThrowIfCancellationRequested();
                var moved = _folderRepository.MovePendingFirstImport(source, target);
                var first = moved
                    ? _folderRepository.GetPendingFirstImportQueue()
                        .FirstOrDefault(row => !row.Paused)?.FolderId
                    : null;
                return (moved, first);
            }, workKind: SchedulerWorkKind.CatalogRead);
        if (!changed) return false;
        // A direct user move is a new explicit request. It also overrides
        // the session-only guard for an earlier failed automatic attempt;
        // otherwise moving the task would change its visible rank but not
        // actually cause the scanner to execute it.
        _initialScanAttempted.Remove(source);

        // A newly promoted first import must actually overtake a running
        // older import. Yield at the scanner's normal cancellation boundary;
        // its confirmed SQLite receipts remain ready for later resumption.
        if (_initialScanActive && _activeScanFolderId is long activeId &&
            activeId != source &&
            _initialScanCancellation is
                { IsCancellationRequested: false } running)
        {
            if (nextFolderId == source)
                try { await running.CancelAsync(); }
                catch (ObjectDisposedException) { }
        }
        QueuePendingInitialScans();
        return true;
    }

    // "Jetzt ausführen" promotes the ORIGINAL queued root review from P4
    // to the scheduler's P2 user-action lane. P0 tag/GPS writes and manual
    // scans still win; no second scanner or duplicate inventory is created.
    private async Task<bool> StartAutomaticScanNowAsync(string key)
    {
        if (!TryRootReviewTaskId(key, out var rootId)) return false;
        // This is a scheduler CONTROL-plane mutation. Waiting for an
        // additional P2 worker here would defeat the user's attempt to
        // move a P4 scan ahead of the currently busy work queue.
        var promoted = await Task.Run(() =>
            _scheduler.ChangeTaskIntent(() =>
                _folderRepository.RequestRootReviewNow(rootId)));
        if (!promoted) return false;
        _backgroundUrgentReviews.Add(rootId);

        // Reuse the SAME queued watcher intent and existing live iterator.
        // Never create another task for an already queued or running review.
        var queued = _backgroundPending.Values.Any(task =>
            task.MonitoredFolderId == rootId &&
            task.Kind == BackgroundFolderChangeKind.RescanRoot);
        var inFlight = _backgroundInFlight is { } batch &&
            batch.Skip(_backgroundInFlightIndex).Any(task =>
                task.MonitoredFolderId == rootId &&
                task.Kind == BackgroundFolderChangeKind.RescanRoot);
        if (!queued && !inFlight)
        {
            var root = _folderRepository.GetAll()
                .FirstOrDefault(item => item.Id == rootId);
            if (root is not null)
                QueueBackgroundChange(new BackgroundFolderChange(
                    root.Id, root.LocalPath,
                    BackgroundFolderChangeKind.RescanRoot, true));
        }
        if (inFlight)
        {
            // A different root currently owns the scan attempt. Yield it
            // cooperatively and requeue its confirmed cursor behind the P2
            // promotion; do not interrupt a completed file write.
            _backgroundOrderChanged = true;
            if (_backgroundCurrentScanCancellation is
                { IsCancellationRequested: false } running)
                RequestTaskManagerYield(running);
        }
        _backgroundDebounceTimer?.Stop();
        _backgroundDebounceTimer?.Start();
        _scheduler.NotifyTaskViewChanged();
        return true;
    }

    private async Task<bool> ResumeAutomaticScanAsync(string key)
    {
        if (!TryInitialTaskId(key, out var folderId)) return false;
        var restored = await _scheduler.RunAsync(
            $"taskmanager:first-import-resume:{Guid.NewGuid():N}",
            SchedulerPriority.UserAction,
            token =>
            {
                token.ThrowIfCancellationRequested();
                return _folderRepository.SetPendingFirstImportPaused(folderId, false);
            }, workKind: SchedulerWorkKind.CatalogRead);
        if (restored)
        {
            _initialScanAttempted.Remove(folderId);
            QueuePendingInitialScans();
        }
        return restored;
    }

    private void TaskManagerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_taskManagerWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var manager = new TaskManagerWindow(_scheduler, _database,
            RetryOriginalWriteAsync, DiscardOriginalWriteAsync,
            SnapshotAutomaticScans, StopAutomaticScanAsync,
            () => _backgroundIdleStatus,
            MoveAutomaticScanAsync, ResumeAutomaticScanAsync,
            StartAutomaticScanNowAsync, MoveGlobalTaskAsync)
        {
            Owner = this
        };
        _taskManagerWindow = manager;
        manager.Closed += (_, _) =>
        {
            if (ReferenceEquals(_taskManagerWindow, manager))
                _taskManagerWindow = null;
        };
        manager.Show();
    }
}
