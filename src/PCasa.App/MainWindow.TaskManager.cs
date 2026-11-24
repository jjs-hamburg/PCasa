using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

// Read-only projection of the existing automatic scan queue and current
// scheduler execution. The taskmanager never owns a second scanner or queue.
public sealed record AutomaticScanTask(
    string Key, string Title, string Directory, string Progress,
    string Status, int Priority, long Order, bool CanStop = true,
    string? CatalogDirectory = null);

public partial class MainWindow
{
    private TaskManagerWindow? _taskManagerWindow;

    private static bool TryInitialTaskId(string key, out long folderId)
    {
        folderId = 0;
        return key.StartsWith("initial:", StringComparison.Ordinal) &&
            long.TryParse(key.AsSpan("initial:".Length), out folderId);
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

    // The ONE scheduler owns priority and relative dispatch order for
    // every active task key, independent of original work type. These are
    // owner notifications for the existing physical scanner cursors, not
    // a second Taskmanager queue or a reordered copy of the rows.
    private async Task<bool> MoveGlobalTaskAsync(string key, string targetKey,
        IReadOnlyList<SchedulerTaskOrderItem> allAcceptedKeys)
    {
        if (key == PCasaScheduler.ContinuousReviewKey ||
            targetKey == PCasaScheduler.ContinuousReviewKey) return false;
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
            // Reordering an explicitly paused import changes only its rank.
            // It must not silently resume the import: "Fortsetzen" and
            // "Jetzt ausführen" are separate user commands.
            var paused = _folderRepository.GetPendingFirstImportQueue()
                .Any(item => item.FolderId == importId && item.Paused);
            if (!paused)
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

    private async Task<bool> PauseInitialImportAsync(string key)
    {
        if (!TryInitialTaskId(key, out var folderId)) return false;
        // Persist the pause first. Otherwise cancellation is mistaken for a
        // scheduler yield and the initial-import pump can restart immediately.
        var paused = await Task.Run(() =>
            _scheduler.ChangeTaskIntent(() =>
                _folderRepository.SetPendingFirstImportPaused(folderId, true)));
        if (!paused) return false;
        _initialScanAttempted.Add(folderId);
        if (_initialScanActive && _activeScanFolderId == folderId &&
            _initialScanCancellation is { IsCancellationRequested: false } active)
        {
            _initialScanStopRequested = true;
            RequestTaskManagerYield(active);
        }
        _scheduler.NotifyTaskViewChanged();
        return true;
    }

    private Task<bool> StartTaskNowAsync(string key)
    {
        if (key == PCasaScheduler.ContinuousReviewKey) return Task.FromResult(false);
        if (!TryInitialTaskId(key, out var folderId))
        {
            if (!key.StartsWith("automatic:", StringComparison.Ordinal))
                return Task.FromResult(false);
            if (!_scheduler.PromoteTaskNow(key)) return Task.FromResult(false);
            _backgroundOrderChanged = _backgroundInFlight is { Length: > 0 };
            if (_backgroundOrderChanged &&
                _backgroundCurrentScanCancellation is
                    { IsCancellationRequested: false } running)
                RequestTaskManagerYield(running);
            _backgroundDebounceTimer?.Start();
            _scheduler.NotifyTaskViewChanged();
            return Task.FromResult(true);
        }
        // The initial import owns the same confirmed scan cursor and key.
        if (!_scheduler.PromoteTaskNow(key)) return Task.FromResult(false);
        _initialScanAttempted.Remove(folderId);
        if (_initialScanActive && _activeScanFolderId != folderId &&
            _initialScanCancellation is { IsCancellationRequested: false } other)
            RequestTaskManagerYield(other);
        QueuePendingInitialScans();
        _scheduler.NotifyTaskViewChanged();
        return Task.FromResult(true);
    }

    private Task<bool> PauseAutomaticTaskAsync(string key)
    {
        if (key == PCasaScheduler.ContinuousReviewKey)
        {
            if (!_scheduler.SetExternalTaskPaused(key, true)) return Task.FromResult(false);
            foreach (var queued in _backgroundPending.Where(entry =>
                entry.Value.Kind == BackgroundFolderChangeKind.RescanRoot).ToArray())
            {
                queued.Value.Resume?.Dispose();
                _backgroundPending.Remove(queued.Key);
            }
            if (_backgroundActiveScanKind == BackgroundFolderChangeKind.RescanRoot &&
                _backgroundCurrentScanCancellation is { IsCancellationRequested: false } review)
                RequestTaskManagerYield(review);
            _scheduler.NotifyTaskViewChanged();
            return Task.FromResult(true);
        }
        if (!key.StartsWith("automatic:", StringComparison.Ordinal))
            return Task.FromResult(false);
        var paused = _scheduler.SetExternalTaskPaused(key, true);
        if (!paused) return Task.FromResult(false);
        if (_backgroundInFlight is { } inflight &&
            inflight.Skip(_backgroundInFlightIndex).Any(scan =>
                string.Equals(AutomaticTaskKey(scan), key, StringComparison.Ordinal)))
        {
            _backgroundOrderChanged = true;
            if (_backgroundCurrentScanCancellation is
                { IsCancellationRequested: false } running)
                RequestTaskManagerYield(running);
        }
        _scheduler.NotifyTaskViewChanged();
        return Task.FromResult(true);
    }

    private Task<bool> ResumePausedTaskAsync(string key)
    {
        if (TryInitialTaskId(key, out _))
            return ResumeAutomaticScanAsync(key);
        if (!key.StartsWith("automatic:", StringComparison.Ordinal))
            return Task.FromResult(false);
        var resumed = _scheduler.SetExternalTaskPaused(key, false);
        if (resumed)
        {
            _backgroundDebounceTimer?.Start();
            if (_backgroundScanInitialized && !_backgroundMonitorClosing)
                RefreshBackgroundMonitor();
        }
        return Task.FromResult(resumed);
    }

    private Task<bool> PauseTaskAsync(string key)
        => TryInitialTaskId(key, out _) ? PauseInitialImportAsync(key)
         : PauseAutomaticTaskAsync(key);

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
            resumeAutomaticScan: ResumeAutomaticScanAsync,
            startAutomaticNow: StartTaskNowAsync,
            pauseInitialImport: PauseTaskAsync,
            resumePausedTask: ResumePausedTaskAsync,
            moveGlobalTask: MoveGlobalTaskAsync)
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
