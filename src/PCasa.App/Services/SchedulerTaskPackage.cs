namespace PCasa.App.Services;

/// <summary>
/// Dynamically assembled, resumable scheduler task. An operation may append
/// dependent steps while executing a preceding step. Only successfully finished
/// steps advance the cursor. Scheduler yield/cancellation never starts a
/// completed step over, and all steps inherit one job ID and priority.
///
/// A step which mutates the filesystem must leave it in a recoverable state
/// before returning or throwing; the scheduler cannot roll back external I/O.
/// </summary>
public sealed class SchedulerTaskPackage
{
    private readonly List<Func<CancellationToken, Task>> _steps = new();
    private int _nextIndex;
    internal Action<int>? OnStepCompleted { get; set; }

    internal void RestoreCursor(int step)
    {
        if (step < 0 || step > _steps.Count)
            throw new InvalidOperationException("Ungültige Paket-Wiederaufnahmeposition.");
        _nextIndex = step;
    }

    public SchedulerTaskPackage(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
    }

    public string Key { get; }
    public int CompletedSteps => _nextIndex;
    public int TotalSteps => _steps.Count;

    public void AddStep(Func<CancellationToken, Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
    }

    // Used for work discovered by the current step (e.g. a new file needs
    // EXIF and thumbnail ingestion before we proceed to the next copy).
    public void InsertNext(Func<CancellationToken, Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        // A durable cursor refers to the original factory step positions.
        // Dynamic insertion would shift those positions after a restart and
        // could repeat destructive work or skip a required catalog commit.
        if (OnStepCompleted is not null)
            throw new InvalidOperationException(
                "Persistente Pakete benötigen eine feste, rekonstruierbare Schrittfolge.");
        _steps.Insert(_nextIndex + 1, step);
    }

    internal async Task ExecuteAsync(CancellationToken token)
    {
        while (_nextIndex < _steps.Count)
        {
            token.ThrowIfCancellationRequested();
            await _steps[_nextIndex](token);
            _nextIndex++;
            OnStepCompleted?.Invoke(_nextIndex);
        }
    }
}
