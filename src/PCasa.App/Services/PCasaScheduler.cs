using System.Collections.Concurrent;

namespace PCasa.App.Services;

public enum SchedulerPriority { Interactive = 0, UserAction = 1, Background = 2, Idle = 3 }
public enum SchedulerJobState { Queued, Running, Completed, Canceled, Failed }

public sealed record SchedulerJobSnapshot(
    long Id, string Key, SchedulerPriority Priority, SchedulerJobState State,
    DateTimeOffset SubmittedAt, string? Error);

/// <summary>
/// Single serialized execution lane for catalog/cache maintenance.
/// Interactive work takes priority over queued background work. Identical
/// pending keys coalesce; the caller can invalidate a source generation
/// before execution using the validity predicate.
/// </summary>
public sealed class PCasaScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<WorkItem> _pending = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _nextId;
    private bool _disposed;

    public PCasaScheduler() => _worker = Task.Run(RunAsync);

    public IReadOnlyList<SchedulerJobSnapshot> Snapshot()
    {
        lock (_gate) return _history.ToArray();
    }

    public Task EnqueueAsync(string key, SchedulerPriority priority,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A canceled request must never absorb a fresh request for the
            // same key (for example after a folder is removed and re-added).
            // Keep the canceled entry queued so the worker records its final
            // state, but do not coalesce new work into it.
            var existing = _pending.FirstOrDefault(x =>
                x.Key == key && !x.CancellationToken.IsCancellationRequested &&
                (x.IsCurrent is null || x.IsCurrent()));
            if (existing is not null)
            {
                if (priority < existing.Priority)
                {
                    existing.Priority = priority;
                    SetStateUnsafe(existing, SchedulerJobState.Queued);
                }
                return existing.Completion.Task;
            }

            var item = new WorkItem(++_nextId, key, priority, operation,
                isCurrent, cancellationToken);
            _pending.Add(item);
            _history.Add(new SchedulerJobSnapshot(item.Id, key, priority,
                SchedulerJobState.Queued, DateTimeOffset.UtcNow, null));
            _signal.Release();
            return item.Completion.Task;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token);
                WorkItem? item;
                lock (_gate)
                {
                    item = _pending.OrderBy(x => x.Priority)
                        .ThenBy(x => x.Id).FirstOrDefault();
                    if (item is not null) _pending.Remove(item);
                }
                if (item is null) continue;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token, item.CancellationToken);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (item.IsCurrent is not null && !item.IsCurrent())
                        throw new OperationCanceledException();
                    SetState(item, SchedulerJobState.Running);
                    await item.Operation(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    SetState(item, SchedulerJobState.Completed);
                    item.Completion.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    SetState(item, SchedulerJobState.Canceled);
                    item.Completion.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    SetState(item, SchedulerJobState.Failed, ex.Message);
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            lock (_gate)
            {
                foreach (var item in _pending)
                {
                    SetStateUnsafe(item, SchedulerJobState.Canceled);
                    item.Completion.TrySetCanceled();
                }
                _pending.Clear();
            }
        }
    }

    private void SetState(WorkItem item, SchedulerJobState state, string? error = null)
    {
        lock (_gate) SetStateUnsafe(item, state, error);
    }

    private void SetStateUnsafe(WorkItem item, SchedulerJobState state, string? error = null)
    {
        var index = _history.FindIndex(x => x.Id == item.Id);
        if (index >= 0) _history[index] = _history[index] with
        { State = state, Priority = item.Priority, Error = error };
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
        }
        await _worker;
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private sealed class WorkItem(
        long id, string key, SchedulerPriority priority,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent, CancellationToken cancellationToken)
    {
        public long Id { get; } = id;
        public string Key { get; } = key;
        public SchedulerPriority Priority { get; set; } = priority;
        public Func<CancellationToken, Task> Operation { get; } = operation;
        public Func<bool>? IsCurrent { get; } = isCurrent;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
