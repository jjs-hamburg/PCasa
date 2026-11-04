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
        => long.TryParse(key.AsSpan("initial:".Length), out folderId) &&
            key.StartsWith("initial:", StringComparison.Ordinal);

    private async Task<bool> MoveAutomaticScanAsync(
        string sourceKey, string targetKey)
    {
        if (!TryInitialTaskId(sourceKey, out var source) ||
            !TryInitialTaskId(targetKey, out var target))
            return false;

        var changed = await _scheduler.RunAsync(
            $"taskmanager:first-import-reorder:{Guid.NewGuid():N}",
            SchedulerPriority.UserAction,
            token =>
            {
                token.ThrowIfCancellationRequested();
                return _folderRepository.MovePendingFirstImport(source, target);
            }, workKind: SchedulerWorkKind.CatalogRead);
        if (!changed) return false;

        // A newly promoted first import must actually overtake a running
        // older import. Yield at the scanner's normal cancellation boundary;
        // its confirmed SQLite receipts remain ready for later resumption.
        if (_initialScanActive && _activeScanFolderId is long activeId &&
            activeId != source &&
            _initialScanCancellation is
                { IsCancellationRequested: false } running)
        {
            var first = _folderRepository.GetPendingFirstImportQueue()
                .FirstOrDefault(row => !row.Paused);
            if (first?.FolderId == source)
                try { await running.CancelAsync(); }
                catch (ObjectDisposedException) { }
        }
        QueuePendingInitialScans();
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
            MoveAutomaticScanAsync, ResumeAutomaticScanAsync)
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
