using System.IO;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Single-image actions are session-bound packages in the ONE scheduler.
    // No extra busy flag, maintenance lease or competing direct-write slot.
    private Task StartDirectImageWrite(string key, string title, string path,
        SchedulerTaskPackage work, long folderId, bool reindexOriginal)
    {
        if (_shutdownStarted || _databaseMaintenancePending ||
            _databaseBackupTask is { IsCompleted: false })
            throw new InvalidOperationException(UiLanguage.T("Datenbankverwaltung läuft."));
        return _scheduler.EnqueueSessionWriteAsync(key, async token =>
        {
            token.ThrowIfCancellationRequested();
            _scheduler.ReportProgress(key, title, 0, Math.Max(1, work.TotalSteps), path);
            // This accepted atomic write is never preempted halfway through.
            // The scheduler, not a second queue, admits the next P0.
            await work.ExecuteAsync(CancellationToken.None);
            if (!reindexOriginal) return;
            try
            {
                var root = _folderRepository.GetAll().FirstOrDefault(folder => folder.Id == folderId)
                    ?? throw new IOException("Monitored folder unavailable.");
                var result = _folderScanner.ScanFile(root, path,
                    publishCatalogChange: false, forceExifRefresh: true);
                if (result.Failed > 0 || result.ImageFilesSeen == 0)
                    throw new IOException("Image catalog reconciliation failed.");
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Image.Queued.Reindex", ex);
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                        QueueBackgroundChange(new BackgroundFolderChange(folderId,
                            path, BackgroundFolderChangeKind.ScanFile, false,
                            ForceExifRefresh: true))));
            }
        });
    }
}
