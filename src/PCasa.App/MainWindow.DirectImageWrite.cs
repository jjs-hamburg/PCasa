using System.IO;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Session-bound single-image writes are deliberately NOT durable jobs.
    // There is no second work queue. One operation owns the safe-boundary
    // lease; the existing progress presenter and shutdown review observe it.
    private Task? _directImageWriteTask;
    private CancellationTokenSource? _directImageWriteCancellation;
    private SchedulerJobSnapshot? _directImageWriteProgress;
    private bool _directImageWriteStarted;

    private Task StartDirectImageWrite(string key, string title, string path,
        SchedulerTaskPackage work, long folderId, bool reindexOriginal)
    {
        if (_shutdownStarted || _databaseMaintenancePending ||
            _databaseBackupTask is { IsCompleted: false } ||
            _directImageWriteTask is { IsCompleted: false })
            throw new InvalidOperationException(UiLanguage.T("Eine Dateiaktion läuft bereits."));
        var cancellation = new CancellationTokenSource();
        _directImageWriteCancellation = cancellation;
        _directImageWriteStarted = false;
        var submitted = DateTimeOffset.UtcNow;
        _directImageWriteProgress = new SchedulerJobSnapshot(-1, key,
            SchedulerPriority.ManualAction, SchedulerJobState.Queued, submitted, null)
            { Phase = title, Total = Math.Max(1, work.TotalSteps), CurrentPath = path };
        var task = RunDirectImageWriteAsync(key, title, path, work, folderId,
            reindexOriginal, cancellation, submitted);
        _directImageWriteTask = task;
        return task;
    }

    private async Task RunDirectImageWriteAsync(string key, string title,
        string path, SchedulerTaskPackage work, long folderId,
        bool reindexOriginal, CancellationTokenSource cancellation,
        DateTimeOffset submitted)
    {
        try
        {
            using var lease = await _scheduler.AcquireMaintenancePauseAsync(
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _directImageWriteStarted = true;
            _directImageWriteProgress = new SchedulerJobSnapshot(-1, key,
                SchedulerPriority.ManualAction, SchedulerJobState.Running,
                submitted, null, StartedAt: DateTimeOffset.UtcNow)
                { Phase = title, Total = Math.Max(1, work.TotalSteps), CurrentPath = path };
            DisplayManualOperation(key);
            // Reuse the step completion notification without creating a
            // scheduler history row or a persistent job.
            work.OnStepCompleted = completed =>
            {
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_directImageWriteProgress is not { } current ||
                        current.Key != key || _shutdownStarted) return;
                    var progress = current with { Processed = completed };
                    _directImageWriteProgress = progress;
                    if (_visibleManualActiveKey == key)
                        _visibleManualWindow?.UpdateManualOperationWithContext(
                            progress, title);
                }));
            };
            // Once an original write begins, let the atomic writer finish.
            // Closing the app may cancel a wait, never interrupt File.Replace.
            await Task.Run(() => work.ExecuteAsync(CancellationToken.None));
            if (reindexOriginal)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var root = _folderRepository.GetAll().FirstOrDefault(
                            folder => folder.Id == folderId)
                            ?? throw new IOException("Monitored folder unavailable.");
                        var result = _folderScanner.ScanFile(root, path,
                            publishCatalogChange: false, forceExifRefresh: true);
                        if (result.Failed > 0 || result.ImageFilesSeen == 0)
                            throw new IOException("Image catalog reconciliation failed.");
                    });
                }
                catch (Exception ex)
                {
                    // The original was already committed. Retry only the
                    // catalog update, never the destructive image operation.
                    PCasaDiagnostics.Write("Image.Direct.Reindex", ex);
                    QueueBackgroundChange(new BackgroundFolderChange(folderId,
                        path, BackgroundFolderChangeKind.ScanFile, false,
                        ForceExifRefresh: true));
                }
            }
        }
        finally
        {
            _directImageWriteStarted = false;
            _directImageWriteProgress = null;
            _directImageWriteCancellation = null;
            cancellation.Dispose();
        }
    }
}
