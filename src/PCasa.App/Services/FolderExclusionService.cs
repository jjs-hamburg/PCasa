using PCasa.App.Data;

namespace PCasa.App.Services;

/// <summary>
/// Sole scheduler-owned context-menu exclusion operation. The domain commit
/// is shared with the folder manager and is atomic across rules, source
/// generation and catalog pruning. An optional UI publication is awaited
/// before the same scheduler job can report Completed.
/// </summary>
public sealed class FolderExclusionService
{
    private readonly FolderRepository _folders;
    private readonly FolderManagerSelectionService _selection;
    private readonly PCasaScheduler _scheduler;

    public FolderExclusionService(FolderRepository folders, LocalDatabase database,
        PCasaScheduler scheduler)
    {
        _folders = folders;
        _selection = new FolderManagerSelectionService(database);
        _scheduler = scheduler;
    }

    public async Task<int> ChangeAsync(long folderId, string path, bool excluded,
        Action<long, string, bool>? onChanged = null,
        IProgress<string>? progress = null,
        Func<long, string, bool, IReadOnlyList<string>, Task>? publish = null,
        string? operationKey = null)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        FolderManagerSelectionService.ExclusionTransition transition = default;
        var key = operationKey ?? $"folder-exclusion:{folderId}:{Guid.NewGuid():N}";
        await _scheduler.EnqueueAsync(
            key, SchedulerPriority.ManualAction,
            async token =>
            {
                _scheduler.ReportProgress(key,
                    excluded ? "Unterordner entfernen" : "Unterordner aufnehmen",
                    0, 1, normalized);
                transition = _selection.ChangeExclusion(
                    folderId, normalized, excluded, token);
                if (!transition.Changed)
                {
                    _scheduler.ReportProgress(key, "Ordner unverändert", 1, 1, normalized);
                    return;
                }
                progress?.Report(excluded
                    ? $"{transition.Removed:N0} Bildeinträge ausgeschlossen"
                    : "Unterordner wieder in die Überwachung aufgenommen");
                if (publish is not null)
                {
                    // Snapshot comes from the same durable SQLite authority;
                    // the callback may switch to the UI Dispatcher. The
                    // scheduler owns completion until publication finishes.
                    var currentRules = _folders.GetExcludedPaths(folderId);
                    await publish(folderId, normalized, excluded, currentRules);
                }
                _scheduler.ReportProgress(key,
                    excluded ? "Unterordner entfernt" : "Unterordner aufgenommen",
                    1, 1, normalized);
            });

        // Post-completion observers (e.g. tests) may enqueue another decision
        // without deadlocking the manual lane. Production WPF publication is
        // awaited INSIDE that lane using the explicit publish callback above.
        if (transition.Changed)
            onChanged?.Invoke(folderId, normalized, excluded);
        return transition.Removed;
    }
}
