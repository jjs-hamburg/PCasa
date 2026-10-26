using PCasa.App.Data;

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
    public string? Phase { get; init; }
    public int Processed { get; init; }
    public int Total { get; init; }
    public string? CurrentPath { get; init; }
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
    private const int LaneManualSourceProbe = 8;
    private const int LaneInteractiveSourceProbe = 9;
    private readonly object _gate = new();
    private DurablePackageJournal? _durableJournal;
    private readonly HashSet<Task> _durableCompletions = new();
    private readonly Dictionary<string, Func<string, string, SchedulerTaskPackage>> _packageFactories = new(StringComparer.Ordinal);
    private readonly List<WorkItem> _pending = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    private readonly SemaphoreSlim[] _signals =
        Enumerable.Range(0, 10).Select(_ => new SemaphoreSlim(0)).ToArray();
    private readonly WorkItem?[] _runningPriorityJobs = new WorkItem?[3];
    private int _catalogForegroundRunning;
    private int _catalogManualRunning;
    private int _catalogInteractiveRunning;
    private int _manualSourceProbeRunning;
    private WorkItem? _runningManualSourceProbe;
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
            Task.Run(() => RunAsync(LaneSourceProbe)),
            // A disconnected NAS can occupy both automatic probe workers.
            // User-initiated source I/O has its own bounded capacity but
            // remains under the SAME scheduler queue and job lifecycle.
            Task.Run(() => RunAsync(LaneManualSourceProbe)),
            Task.Run(() => RunAsync(LaneInteractiveSourceProbe))
        };
    }

    public void EnableDurablePackages(LocalDatabase database)
    {
        if (_durableJournal is not null)
            throw new InvalidOperationException("Paketjournal bereits aktiviert.");
        _durableJournal = new DurablePackageJournal(database);
    }

    public bool HasActiveDurablePackageType(string type)
        => _durableJournal?.Pending().Any(p => p.Type == type) == true;

    public bool HasActiveDurablePackage(string type, string payload)
        => _durableJournal?.Pending().Any(p =>
            p.Type == type && p.Payload == payload) == true;

    public void RegisterDurablePackageType(string type,
        Func<string, string, SchedulerTaskPackage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        _packageFactories.Add(type, factory);
    }

    // Replay ONLY registered types. Unknown versions stay durable and visible;
    // never silently discard them or pretend their source steps ran.
    public IReadOnlyList<DurablePackageRecord> RecoverDurablePackages()
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Paketjournal nicht aktiviert.");
        var unresolved = new List<DurablePackageRecord>();
        foreach (var record in journal.Pending())
        {
            if (!_packageFactories.TryGetValue(record.Type, out var factory))
            {
                unresolved.Add(record);
                continue;
            }
            try
            {
                var package = factory(record.Key, record.Payload);
                // A dynamically built package must rebuild the identical
                // step sequence before a saved cursor can be applied.
                package.RestoreCursor(record.CompletedSteps);
                TrackDurableCompletion(QueueDurableExisting(package, record));
            }
            catch (Exception ex)
            {
                journal.SetState(record.Key, "failed", error: ex.Message);
                unresolved.Add(record);
            }
        }
        return unresolved;
    }

    public Task EnqueueDurablePackageAsync(SchedulerTaskPackage package,
        string type, string payload, SchedulerPriority priority,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog,
        CancellationToken cancellationToken = default)
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Paketjournal nicht aktiviert.");
        if (!_packageFactories.ContainsKey(type))
            throw new InvalidOperationException("Pakettyp ohne Wiederherstellungsfabrik: " + type);
        var record = journal.Put(package.Key, type, payload, priority, workKind);
        if (record.State is "completed" or "failed")
            throw new InvalidOperationException("Paketidentität bereits abgeschlossen.");
        package.RestoreCursor(record.CompletedSteps);
        return TrackDurableCompletion(QueueDurableExisting(package, record, cancellationToken));
    }

    // Durable ledger commits are part of scheduler shutdown, not detached
    // fire-and-forget continuations that outlive the database owner.
    private Task TrackDurableCompletion(Task completion)
    {
        lock (_gate) _durableCompletions.Add(completion);
        _ = completion.ContinueWith(task =>
        {
            // Observe exceptions, but retain the original Task for callers.
            _ = task.Exception;
            lock (_gate) _durableCompletions.Remove(task);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion;
    }

    private async Task QueueDurableExisting(SchedulerTaskPackage package,
        DurablePackageRecord record, CancellationToken cancellationToken = default)
    {
        var journal = _durableJournal!;
        package.OnStepCompleted = step => journal.SetState(record.Key, "running", step);
        try
        {
            // Durable identity/priority come only from the journal; never
            // substitute a default background priority on restart.
            await EnqueuePackageAsync(package, record.Priority,
                cancellationToken, workKind: record.WorkKind);
            journal.SetState(record.Key, "completed", package.CompletedSteps);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is a yield, user cancellation is final. Never
            // resurrect a deliberately canceled destructive operation.
            journal.SetState(record.Key,
                _shutdown.IsCancellationRequested ? "queued" : "canceled",
                package.CompletedSteps);
            throw;
        }
        catch (Exception ex)
        {
            journal.SetState(record.Key, "failed", package.CompletedSteps, ex.Message);
            throw;
        }
    }

    private static int LaneFor(SchedulerPriority priority, SchedulerWorkKind kind)
        => kind switch
        {
            SchedulerWorkKind.ViewerRead => LaneViewer,
            SchedulerWorkKind.CatalogRead => LaneCatalogRead,
            SchedulerWorkKind.ThumbnailRead => LaneThumbnail,
            SchedulerWorkKind.SourceProbe => priority switch
            {
                SchedulerPriority.ManualAction => LaneManualSourceProbe,
                SchedulerPriority.Interactive => LaneInteractiveSourceProbe,
                _ => LaneSourceProbe
            },
            _ => priority switch
            {
                SchedulerPriority.ManualAction => LaneManual,
                SchedulerPriority.Interactive => LaneInteractive,
                SchedulerPriority.UserAction => LaneUserAction,
                _ => LaneBackground
            }
        };

    // Call ONLY under _gate. A manual network mutation has to suspend bulk
    // scanner scheduling for its entire operation, not merely while queued.
    private bool HasManualSourceProbeUnsafe()
        => _manualSourceProbeRunning > 0 ||
            _pending.Any(item => item.Kind == SchedulerWorkKind.SourceProbe &&
                item.Priority == SchedulerPriority.ManualAction);

    // A package is an open-ended sequence of reusable steps. It keeps its
    // cursor when a newer manual package interrupts it, and preserves the
    // SAME scheduler identity and priority through indexing and publication.
    public Task EnqueuePackageAsync(SchedulerTaskPackage package,
        SchedulerPriority priority, CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog)
    {
        ArgumentNullException.ThrowIfNull(package);
        return EnqueueAsync(package.Key, priority, package.ExecuteAsync,
            cancellationToken: cancellationToken, workKind: workKind);
    }

    // A dismissed read-only projection must leave no queued scheduler work.
    // The owner cancels its token first; a worker already blocked in native
    // filesystem I/O cannot be forcibly aborted and finishes at its next
    // cancellation boundary. Queued work, however, is retired immediately.
    public void CancelPending(string key, SchedulerWorkKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            for (var index = _pending.Count - 1; index >= 0; index--)
            {
                var item = _pending[index];
                if (item.Kind != kind ||
                    !string.Equals(item.Key, key, StringComparison.Ordinal))
                    continue;
                _pending.RemoveAt(index);
                SetStateUnsafe(item, SchedulerJobState.Canceled);
                item.Completion.TrySetCanceled();
            }
        }
    }

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

    // Progress is part of the SAME scheduler snapshot as priority/status.
    // Presenters observe this event; they never create a second task state.
    public event Action<SchedulerJobSnapshot>? ProgressChanged;

    public void ReportProgress(string key, string phase, int processed,
        int total, string? currentPath = null)
    {
        SchedulerJobSnapshot? snapshot;
        var publish = false;
        lock (_gate)
        {
            var index = _history.FindLastIndex(x =>
                x.Key == key && x.State is SchedulerJobState.Running or
                    SchedulerJobState.YieldRequested);
            if (index < 0) return;
            var previous = _history[index];
            snapshot = previous with
            {
                Phase = phase,
                Processed = Math.Max(0, processed),
                Total = Math.Max(0, total),
                CurrentPath = currentPath
            };
            _history[index] = snapshot;

            var work = _runningPriorityJobs.FirstOrDefault(item =>
                item?.Key == key);
            var now = Environment.TickCount64;
            // Every file updates the authoritative snapshot, but only phase
            // transitions / final counts / <=10Hz publish to WPF. A batch
            // of many thousands of files cannot flood the dispatcher.
            publish = work is null ||
                !string.Equals(previous.Phase, phase, StringComparison.Ordinal) ||
                (previous.CurrentPath is null && currentPath is { Length: > 0 }) ||
                processed >= total ||
                now - work.LastProgressNoticeTick >= 100;
            if (publish && work is not null)
                work.LastProgressNoticeTick = now;
        }
        if (publish)
        {
            // Observers (including WPF) run outside _gate.
            try { ProgressChanged?.Invoke(snapshot); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
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
        long incomingId;
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
                incomingId = existing.Id;
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
                incomingId = item.Id;
            }

            // A running background or bulk-user request can yield, but a slow
            // SMB/decoder call never holds the dispatcher or a different lane.
            if (workKind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan ||
                (workKind == SchedulerWorkKind.SourceProbe &&
                 priority == SchedulerPriority.ManualAction))
            {
                for (var lane = 0; lane <= LaneManual; lane++)
                {
                    var active = _runningPriorityJobs[lane];
                    // An incoming user file mutation supersedes a longer
                    // manual SCAN in the same lane. Two manual mutations
                    // never cancel one another.
                    var supersedesOlderManual =
                        priority == SchedulerPriority.ManualAction &&
                        active?.Priority == SchedulerPriority.ManualAction &&
                        active.Id < incomingId &&
                        workKind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan or SchedulerWorkKind.SourceProbe;
                    var supersedesManualScan =
                        priority == SchedulerPriority.ManualAction &&
                        workKind is SchedulerWorkKind.Catalog or SchedulerWorkKind.SourceProbe &&
                        active?.Priority == SchedulerPriority.ManualAction &&
                        active.Kind == SchedulerWorkKind.CatalogScan;
                    if (active is not null &&
                        (priority < active.Priority || supersedesManualScan ||
                         supersedesOlderManual) &&
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
                         HasManualSourceProbeUnsafe() ||
                         _pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                             x.Priority <= SchedulerPriority.UserAction)) ||
                        lane == LaneUserAction &&
                        (_catalogManualRunning > 0 || _catalogInteractiveRunning > 0 ||
                         HasManualSourceProbeUnsafe() ||
                         _pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                             x.Priority <= SchedulerPriority.Interactive));
                    var candidate = _pending.Where(x =>
                            LaneFor(x.Priority, x.Kind) == lane)
                            .OrderBy(x => x.Priority)
                            // Manual mutations are P0 and a resumed scan is
                            // P2 within the shared manual lane. Otherwise an
                            // old yielded scan wins by ID and starves the
                            // exact user move that requested its preemption.
                            .ThenBy(x => lane == LaneManual ? 0 :
                                x.Kind == SchedulerWorkKind.CatalogScan ? 1 : 0)
                            // Manual packages form a stack: newest request
                            // resumes first, older ones retain their cursor.
                            .ThenBy(x => lane is LaneManual or LaneManualSourceProbe
                                ? -x.Id : x.Id)
                            .FirstOrDefault();
                    // Manual source probes have a separate executor so blocked
                    // SMB calls do not freeze the manual catalog worker. Yet an
                    // older yielded manual package must NOT resume while a
                    // newer manual probe is queued or running.
                    var newerManualProbe = lane == LaneManual &&
                        candidate is not null &&
                        ((_runningManualSourceProbe?.Id ?? 0) > candidate.Id ||
                         _pending.Any(x => x.Kind == SchedulerWorkKind.SourceProbe &&
                             x.Priority == SchedulerPriority.ManualAction &&
                             x.Id > candidate.Id));
                    item = blockedByHigherPriority || newerManualProbe ? null : candidate;
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
                        if (lane == LaneManualSourceProbe)
                        {
                            _manualSourceProbeRunning++;
                            _runningManualSourceProbe = item;
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
                            if (lane == LaneManualSourceProbe)
                            {
                                _manualSourceProbeRunning--;
                                if (ReferenceEquals(_runningManualSourceProbe, item))
                                    _runningManualSourceProbe = null;
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
                                    !HasManualSourceProbeUnsafe() &&
                                    !_pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                                        x.Priority <= SchedulerPriority.Interactive) &&
                                    _pending.Any(x => LaneFor(x.Priority, x.Kind) == LaneUserAction))
                                    _signals[LaneUserAction].Release();
                                if (_catalogForegroundRunning == 0 &&
                                    !HasManualSourceProbeUnsafe() &&
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
        // Worker completion can unblock the journal continuation AFTER the
        // worker exits. Do not close the app while a checkpoint is in flight.
        Task[] durable;
        lock (_gate) durable = _durableCompletions.ToArray();
        try { await Task.WhenAll(durable); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
        public long LastProgressNoticeTick { get; set; }
        public Func<CancellationToken, Task> Operation { get; } = operation;
        public Func<bool>? IsCurrent { get; } = isCurrent;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
