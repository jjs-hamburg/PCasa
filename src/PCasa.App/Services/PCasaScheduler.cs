namespace PCasa.App.Services;

public enum SchedulerPriority { ManualAction = 0, Interactive = 1, UserAction = 2, Background = 3, Idle = 4 }
public enum SchedulerJobState { Queued, Running, YieldRequested, Completed, Canceled, Failed }
public enum SchedulerWorkKind { Catalog, CatalogScan, CatalogRead, ViewerRead, ThumbnailRead, SourceProbe }

public sealed record SchedulerJobSnapshot(
    long Id, string Key, SchedulerPriority Priority, SchedulerJobState State,
    DateTimeOffset SubmittedAt, string? Error,
    DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null)
{
    public TimeSpan? Duration =>
        StartedAt is null ? null : (FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt;
}

/// <summary>
/// The one operation coordinator. Separate capacity lanes keep slow network
/// scans from blocking interactive catalog mutations, viewer originals, or
/// visible thumbnail reads. Catalog mutations still use the common short
/// pack/index lock and their persistent SQLite source-generation fences.
/// </summary>
public sealed class PCasaScheduler : IAsyncDisposable
{
    private const int MaximumHistory = 512;
    private const int LaneBackground = 0;
    private const int LaneUserAction = 1;
    private const int LaneManual = 2;
    private const int LaneInteractive = 3;
    private const int LaneViewer = 4;
    private const int LaneThumbnail = 5;
    private const int LaneCatalogRead = 6;
    private const int LaneSourceProbe = 7;
    private readonly object _gate = new();
    private readonly List<WorkItem> _pending = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    private readonly SemaphoreSlim[] _signals =
        Enumerable.Range(0, 8).Select(_ => new SemaphoreSlim(0)).ToArray();
    private readonly WorkItem?[] _runningPriorityJobs = new WorkItem?[3];
    private int _catalogForegroundRunning;
    private int _catalogManualRunning;
    private int _catalogInteractiveRunning;
    private readonly CancellationTokenSource?[] _runningYield = new CancellationTokenSource?[3];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _nextId;
    private bool _disposed;

    public PCasaScheduler()
    {
        // File reading is bounded. UI painting and hit testing never enter a lane.
        _workers = new[]
        {
            Task.Run(() => RunAsync(LaneBackground)),
            Task.Run(() => RunAsync(LaneUserAction)),
            // A manually requested scan must not wait for blocked initial SMB I/O.
            Task.Run(() => RunAsync(LaneManual)),
            Task.Run(() => RunAsync(LaneInteractive)),
            Task.Run(() => RunAsync(LaneViewer)),
            Task.Run(() => RunAsync(LaneViewer)),
            Task.Run(() => RunAsync(LaneThumbnail)),
            Task.Run(() => RunAsync(LaneThumbnail)),
            Task.Run(() => RunAsync(LaneCatalogRead)),
            Task.Run(() => RunAsync(LaneCatalogRead)),
            Task.Run(() => RunAsync(LaneSourceProbe)),
            Task.Run(() => RunAsync(LaneSourceProbe))
        };
    }

    private static int LaneFor(SchedulerPriority priority, SchedulerWorkKind kind)
        => kind switch
        {
            SchedulerWorkKind.ViewerRead => LaneViewer,
            SchedulerWorkKind.CatalogRead => LaneCatalogRead,
            SchedulerWorkKind.ThumbnailRead => LaneThumbnail,
            SchedulerWorkKind.SourceProbe => LaneSourceProbe,
            _ => priority switch
            {
                SchedulerPriority.ManualAction => LaneManual,
                SchedulerPriority.Interactive => LaneInteractive,
                SchedulerPriority.UserAction => LaneUserAction,
                _ => LaneBackground
            }
        };

    // Typed scheduler results use exactly the same queue, priority,
    // cancellation and lifetime bookkeeping as catalog mutations.
    public async Task<TResult> RunAsync<TResult>(
        string key, SchedulerPriority priority,
        Func<CancellationToken, TResult> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.CatalogRead)
    {
        TResult? result = default;
        await EnqueueAsync(key, priority,
            token =>
            {
                token.ThrowIfCancellationRequested();
                result = operation(token);
                return Task.CompletedTask;
            }, isCurrent, cancellationToken, workKind);
        return result!;
    }

    public IReadOnlyList<SchedulerJobSnapshot> Snapshot()
    {
        lock (_gate) return _history.ToArray();
    }

    public Task EnqueueAsync(string key, SchedulerPriority priority,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(operation);
        Task completion;
        List<CancellationTokenSource> yieldRequests = new();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = _pending.FirstOrDefault(x =>
                x.Kind == workKind && x.Key == key &&
                !x.CancellationToken.IsCancellationRequested);
            if (existing is not null)
            {
                if (priority < existing.Priority)
                {
                    existing.Priority = priority;
                    SetStateUnsafe(existing, SchedulerJobState.Queued);
                    _signals[LaneFor(existing.Priority, existing.Kind)].Release();
                }
                completion = existing.Completion.Task;
            }
            else
            {
                var item = new WorkItem(++_nextId, key, priority, workKind,
                    operation, isCurrent, cancellationToken);
                _pending.Add(item);
                _history.Add(new SchedulerJobSnapshot(item.Id, key, priority,
                    SchedulerJobState.Queued, DateTimeOffset.UtcNow, null));
                TrimHistoryUnsafe();
                _signals[LaneFor(priority, workKind)].Release();
                completion = item.Completion.Task;
            }

            // A running background or bulk-user request can yield, but a slow
            // SMB/decoder call never holds the dispatcher or a different lane.
            if (workKind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan)
            {
                for (var lane = 0; lane <= LaneManual; lane++)
                {
                    var active = _runningPriorityJobs[lane];
                    // An incoming user file mutation supersedes a longer
                    // manual SCAN in the same lane. Two manual mutations
                    // never cancel one another.
                    var supersedesManualScan =
                        priority == SchedulerPriority.ManualAction &&
                        workKind == SchedulerWorkKind.Catalog &&
                        active?.Priority == SchedulerPriority.ManualAction &&
                        active.Kind == SchedulerWorkKind.CatalogScan;
                    if (active is not null &&
                        (priority < active.Priority || supersedesManualScan) &&
                        !active.YieldRequested)
                    {
                        active.YieldRequested = true;
                        SetStateUnsafe(active, SchedulerJobState.YieldRequested);
                        if (_runningYield[lane] is { } token)
                            yieldRequests.Add(token);
                    }
                }
            }
        }
        // Cancellation callbacks may re-enter the scheduler: never invoke
        // them under _gate (including from the WPF UI thread).
        foreach (var yield in yieldRequests)
        {
            try { yield.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        return completion;
    }

    private async Task RunAsync(int lane)
    {
        try
        {
            while (true)
            {
                await _signals[lane].WaitAsync(_shutdown.Token);
                WorkItem? item;
                CancellationTokenSource? yieldRequest = null;
                lock (_gate)
                {
                    // A free background worker must not pick the next job while
                    // a higher-priority catalog request is pending or running.
                    // Otherwise parallel workers can reverse the logical order.
                    var blockedByHigherPriority =
                        lane == LaneBackground &&
                        (_catalogForegroundRunning > 0 ||
                         _pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                             x.Priority <= SchedulerPriority.UserAction)) ||
                        lane == LaneUserAction &&
                        (_catalogManualRunning > 0 || _catalogInteractiveRunning > 0 ||
                         _pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                             x.Priority <= SchedulerPriority.Interactive));
                    item = blockedByHigherPriority ? null :
                        _pending.Where(x => LaneFor(x.Priority, x.Kind) == lane)
                            .OrderBy(x => x.Priority).ThenBy(x => x.Id).FirstOrDefault();
                    if (item is not null)
                    {
                        _pending.Remove(item);
                        item.YieldRequested = false;
                        if (item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan)
                        {
                            if (lane == LaneUserAction) _catalogForegroundRunning++;
                            if (lane == LaneManual)
                            {
                                _catalogForegroundRunning++;
                                _catalogManualRunning++;
                            }
                            if (lane == LaneInteractive)
                            {
                                _catalogForegroundRunning++;
                                _catalogInteractiveRunning++;
                            }
                        }
                        if (lane <= LaneManual)
                        {
                            yieldRequest = new CancellationTokenSource();
                            _runningPriorityJobs[lane] = item;
                            _runningYield[lane] = yieldRequest;
                        }
                        SetStateUnsafe(item, SchedulerJobState.Running);
                    }
                }
                if (item is null) continue;
                using (yieldRequest)
                using (var linked = yieldRequest is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token, item.CancellationToken)
                    : CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token, item.CancellationToken, yieldRequest.Token))
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
                        if (item.YieldRequested && !_shutdown.IsCancellationRequested &&
                            !item.CancellationToken.IsCancellationRequested)
                        {
                            try { valid = item.IsCurrent is null || item.IsCurrent(); }
                            catch { /* Startup/monitor reconciliation can retry. */ }
                        }
                        lock (_gate)
                        {
                            if (valid && !_disposed && !_shutdown.IsCancellationRequested &&
                                !item.CancellationToken.IsCancellationRequested)
                            {
                                item.YieldRequested = false;
                                _pending.Add(item);
                                SetStateUnsafe(item, SchedulerJobState.Queued);
                                _signals[LaneFor(item.Priority, item.Kind)].Release();
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
                            if (lane <= LaneManual &&
                                ReferenceEquals(_runningPriorityJobs[lane], item))
                            {
                                _runningPriorityJobs[lane] = null;
                                _runningYield[lane] = null;
                            }
                            if (item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan)
                            {
                                if (lane == LaneUserAction) _catalogForegroundRunning--;
                                if (lane == LaneManual)
                                {
                                    _catalogForegroundRunning--;
                                    _catalogManualRunning--;
                                }
                                if (lane == LaneInteractive)
                                {
                                    _catalogForegroundRunning--;
                                    _catalogInteractiveRunning--;
                                }
                            }
                            // A blocked lane consumed a signal without taking
                            // a job. Wake it when higher priority work drains.
                            if (!_shutdown.IsCancellationRequested)
                            {
                                if (_pending.Any(x => LaneFor(x.Priority, x.Kind) == LaneManual))
                                    _signals[LaneManual].Release();
                                if (_catalogInteractiveRunning == 0 &&
                                    _catalogManualRunning == 0 &&
                                    !_pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                                        x.Priority <= SchedulerPriority.Interactive) &&
                                    _pending.Any(x => LaneFor(x.Priority, x.Kind) == LaneUserAction))
                                    _signals[LaneUserAction].Release();
                                if (_catalogForegroundRunning == 0 &&
                                    !_pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                                        x.Priority <= SchedulerPriority.UserAction) &&
                                    _pending.Any(x => LaneFor(x.Priority, x.Kind) == LaneBackground))
                                    _signals[LaneBackground].Release();
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
        _shutdown.Cancel();
        await Task.WhenAll(_workers);
        _shutdown.Dispose();
        foreach (var signal in _signals) signal.Dispose();
    }

    private sealed class WorkItem(
        long id, string key, SchedulerPriority priority, SchedulerWorkKind kind,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent, CancellationToken cancellationToken)
    {
        public long Id { get; } = id;
        public string Key { get; } = key;
        public SchedulerPriority Priority { get; set; } = priority;
        public SchedulerWorkKind Kind { get; } = kind;
        public bool YieldRequested { get; set; }
        public Func<CancellationToken, Task> Operation { get; } = operation;
        public Func<bool>? IsCurrent { get; } = isCurrent;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
