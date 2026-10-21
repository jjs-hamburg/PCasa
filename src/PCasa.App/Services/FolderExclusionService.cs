using PCasa.App.Data;

namespace PCasa.App.Services;

/// <summary>
/// Single orchestration for both UI entry points. An exclusion is persisted,
/// watchers/scans are notified, and only then catalog/cache cleanup is run.
/// Re-inclusion queues a rescan through the same notification.
/// </summary>
public sealed class FolderExclusionService
{
    private readonly FolderRepository _folders;
    private readonly CatalogRemovalService _removal;
    private readonly PCasaScheduler? _scheduler;

    public FolderExclusionService(FolderRepository folders, LocalDatabase database,
        PCasaScheduler? scheduler = null)
    {
        _folders = folders;
        _removal = new CatalogRemovalService(database);
        _scheduler = scheduler;
    }

    public async Task<int> ChangeAsync(long folderId, string path, bool excluded,
        Action<long, string, bool>? onChanged = null,
        IProgress<string>? progress = null)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        await Task.Run(() => _folders.SetExcluded(folderId, normalized, excluded));
        onChanged?.Invoke(folderId, normalized, excluded);
        if (!excluded) return 0;
        if (_scheduler is null)
            return await Task.Run(() => _removal.DetachSubtree(folderId, normalized, progress));
        var folder = _folders.GetAll().FirstOrDefault(f => f.Id == folderId);
        if (folder is null) return 0;
        var state = _folders.GetSourceState(folder.CanonicalPath);
        if (state is not { DesiredPresent: true }) return 0;
        var removed = 0;
        try
        {
            await _scheduler.EnqueueAsync(
                $"folder-exclude:{folderId}:{state.Value.Generation}:{normalized}",
                SchedulerPriority.UserAction,
                token =>
                {
                    removed = _removal.DetachSubtree(folderId, normalized, progress, token);
                    return Task.CompletedTask;
                },
                () => _folders.IsCurrentSourceGeneration(
                    folder.CanonicalPath, state.Value.Generation, true));
        }
        catch (OperationCanceledException)
        {
            return 0; // A newer source generation superseded this cleanup.
        }
        return removed;
    }
}
