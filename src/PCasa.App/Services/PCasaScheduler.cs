namespace PCasa.App.Services;

public enum SchedulerPriority { Interactive = 0, UserAction = 1, Background = 2, Idle = 3 }
public enum SchedulerJobState { Queued, Running, YieldRequested, Completed, Canceled, Failed }

public sealed record SchedulerJobSnapshot(
    long Id, string Key, SchedulerPriority Priority, SchedulerJobState State,
    DateTimeOffset SubmittedAt, string? Error,
    DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null)
{
    public TimeSpan? Duration =>
        StartedAt is null ? null : (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

/// <summary>
/// The single catalog/cache operation lane. Higher-priority work requests a
/// cooperative yield of running background/idle work. Yielded work is retried
/// using its original completion task: callers never lose interrupted jobs.
/// Source generations and SQLite commit fences remain the authority for writes.
/// </summary>
public sealed class PCasaScheduler : IAsyncDisposable
{
    private const int MaximumHistory = 512;
    private readonly object _gate = new();
    private readonly List<WorkItem> _pending = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private WorkItem? _running;
    private CancellationTokenSource? _runningYield;
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
        Task completion;
        CancellationTokenSource? yieldRequest = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = _pending.FirstOrDefault(x =>
                // Keys already include the source generation for all
                // generation-scoped jobs. Never synchronously call a SQLite
                // validity predicate while enqueuing from the WPF dispatcher.
                x.Key == key && !x.CancellationToken.IsCancellationRequested);
            if (existing is not null)
            {
                if (priority < existing.Priority)
                {
                    existing.Priority = priority;
                    SetStateUnsafe(existing, SchedulerJobState.Queued);
                }
                completion = existing.Completion.Task;
            }
            else
            {
                var item = new WorkItem(++_nextId, key, priority, operation,
                    isCurrent, cancellationToken);
                _pending.Add(item);
                _history.Add(new SchedulerJobSnapshot(item.Id, key, priority,
                    SchedulerJobState.Queued, DateTimeOffset.UtcNow, null));
                TrimHistoryUnsafe();
                _signal.Release();
                completion = item.Completion.Task;
            }

            // Cancellation is cooperative. A non-interruptible SMB or decoder
            // call finishes first, but the dispatcher never waits for it.
            if (_running is { } active &&
                priority < active.Priority && !active.YieldRequested)
            {
                active.YieldRequested = true;
                SetStateUnsafe(active, SchedulerJobState.YieldRequested);
                yieldRequest = _runningYield;
            }
        }
        // Never call Cancel while holding _gate: user token registrations
        // may re-enter scheduler code.
        try { yieldRequest?.Cancel(); }
        catch (ObjectDisposedException) { /* The running job finished. */ }
        return completion;
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token);
                WorkItem? item;
                CancellationTokenSource? yieldRequest = null;
                lock (_gate)
                {
                    item = _pending.OrderBy(x => x.Priority)
                        .ThenBy(x => x.Id).FirstOrDefault();
                    if (item is not null)
                    {
                        _pending.Remove(item);
                        item.YieldRequested = false;
                        yieldRequest = new CancellationTokenSource();
                        _running = item;
                        _runningYield = yieldRequest;
                        SetStateUnsafe(item, SchedulerJobState.Running);
                    }
                }
                if (item is null) continue;
                using (yieldRequest!)
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token, item.CancellationToken, yieldRequest!.Token))
                {
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (item.IsCurrent is not null && !item.IsCurrent())
                            throw new OperationCanceledException();
                        await item.Operation(linked.Token);
                        linked.Token.ThrowIfCancellationRequested();
                        if (item.IsCurrent is not null && !item.IsCurrent())
                            throw new OperationCanceledException();
                        SetState(item, SchedulerJobState.Completed);
                        item.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        var valid = false;
                        if (item.YieldRequested &&
                            !_shutdown.IsCancellationRequested &&
                            !item.CancellationToken.IsCancellationRequested)
                        {
                            try { valid = item.IsCurrent is null || item.IsCurrent(); }
                            catch { /* Reconciliation will resubmit if needed. */ }
                        }
                        lock (_gate)
                        {
                            if (valid && !_disposed &&
                                !_shutdown.IsCancellationRequested &&
                                !item.CancellationToken.IsCancellationRequested)
                            {
                                item.YieldRequested = false;
                                _pending.Add(item);
                                SetStateUnsafe(item, SchedulerJobState.Queued);
                                _signal.Release();
                            }
                            else
                            {
                                SetStateUnsafe(item, SchedulerJobState.Canceled);
                                item.Completion.TrySetCanceled();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SetState(item, SchedulerJobState.Failed, ex.Message);
                        item.Completion.TrySetException(ex);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_running, item))
                            {
                                _running = null;
                                _runningYield = null;
                            }
                        }
                    }
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
        if (index < 0) return;
        var now = DateTimeOffset.UtcNow;
        _history[index] = _history[index] with
        {
            State = state, Priority = item.Priority, Error = error,
            StartedAt = state == SchedulerJobState.Running
                ? _history[index].StartedAt ?? now : _history[index].StartedAt,
            FinishedAt = state is SchedulerJobState.Completed or
                SchedulerJobState.Failed or SchedulerJobState.Canceled ? now : null
        };
        TrimHistoryUnsafe();
    }

    private void TrimHistoryUnsafe()
    {
        while (_history.Count > MaximumHistory)
        {
            var index = _history.FindIndex(x =>
                x.State is SchedulerJobState.Completed or SchedulerJobState.Canceled
                    or SchedulerJobState.Failed);
            if (index < 0) break;
            _history.RemoveAt(index);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Never execute cancellation registrations while holding _gate.
        _shutdown.Cancel();
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
        public bool YieldRequested { get; set; }
        public Func<CancellationToken, Task> Operation { get; } = operation;
        public Func<bool>? IsCurrent { get; } = isCurrent;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
