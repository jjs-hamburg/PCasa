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

    public FolderExclusionService(FolderRepository folders, LocalDatabase database)
    {
        _folders = folders;
        _removal = new CatalogRemovalService(database);
    }

    public async Task<int> ChangeAsync(long folderId, string path, bool excluded,
        Action<long, string, bool>? onChanged = null,
        IProgress<string>? progress = null)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        _folders.SetExcluded(folderId, normalized, excluded);
        onChanged?.Invoke(folderId, normalized, excluded);
        if (!excluded) return 0;
        return await Task.Run(() => _removal.DetachSubtree(folderId, normalized, progress));
    }
}
