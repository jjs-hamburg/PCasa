using PCasa.App.Data;

namespace PCasa.App.Services;

/// <summary>
/// Shared exclusion workflow. Cleanup belongs to the exact source generation
/// committed with the decision, not just to an exclusion path that still exists.
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
        var generation = await Task.Run(
            () => _folders.SetExcluded(folderId, normalized, excluded));
        onChanged?.Invoke(folderId, normalized, excluded);
        if (!excluded) return 0;

        var folder = _folders.GetAll().FirstOrDefault(f => f.Id == folderId);
        if (folder is null ||
            !_folders.IsCurrentSourceGeneration(folder.CanonicalPath, generation, true))
            return 0;

        int Delete(CancellationToken token) =>
            _removal.DetachSubtree(folderId, normalized, progress, token,
                sourceCanonicalPath: folder.CanonicalPath,
                expectedGeneration: generation);

        if (_scheduler is null)
        {
            try { return await Task.Run(() => Delete(CancellationToken.None)); }
            catch (OperationCanceledException) { return 0; }
        }

        var removed = 0;
        try
        {
            await _scheduler.EnqueueAsync(
                $"folder-exclude:{folderId}:{generation}:{normalized}",
                SchedulerPriority.UserAction,
                token =>
                {
                    removed = Delete(token);
                    return Task.CompletedTask;
                },
                () => _folders.IsCurrentSourceGeneration(
                    folder.CanonicalPath, generation, true));
        }
        catch (OperationCanceledException) { return 0; }
        return removed;
    }
}
