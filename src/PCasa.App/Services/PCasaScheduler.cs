using PCasa.App.Data;

namespace PCasa.App.Services;

public enum SchedulerPriority { ManualAction = 0, Interactive = 1, UserAction = 2, Background = 3, Idle = 4 }
public enum SchedulerJobState { Queued, Running, YieldRequested, Completed, Canceled, Failed }
public enum SchedulerWorkKind { Catalog, CatalogScan, CatalogRead, ViewerRead, ThumbnailRead, SourceProbe, InitialImport }

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
    // First imports can wait on a particular NAS/file lock without
    // blocking reconciliation of unrelated, already-imported roots.
    private const int LaneInitialImport = 10;
    private readonly object _gate = new();
    private DurablePackageJournal? _durableJournal;
    private readonly HashSet<Task> _durableCompletions = new();
    private bool _restoringDurablePackages;
    private Task? _deferredRetryWorker;
    private Action<string, Task>? _durableRestoreObserver;
    private readonly Dictionary<string, Func<string, string, SchedulerTaskPackage>> _packageFactories = new(StringComparer.Ordinal);
    private readonly List<WorkItem> _pending = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    private readonly SemaphoreSlim[] _signals =
        Enumerable.Range(0, 11).Select(_ => new SemaphoreSlim(0)).ToArray();
    // Track the dedicated first-import executor as well. Otherwise an
    // already-running initial scan never receives the scheduler's yield
    // cancellation when a new manual P0 package arrives.
    private readonly WorkItem?[] _runningPriorityJobs =
        new WorkItem?[LaneInitialImport + 1];
    private int _catalogForegroundRunning;
    private int _catalogManualRunning;
    private int _catalogInteractiveRunning;
    private int _manualSourceProbeRunning;
    private WorkItem? _runningManualSourceProbe;
    private readonly CancellationTokenSource?[] _runningYield =
        new CancellationTokenSource?[LaneInitialImport + 1];
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
            // Same coordinator, same durable/source fences, separate bounded
            // capacity. An unfinished first import owns only its own root.
            Task.Run(() => RunAsync(LaneInitialImport)),
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
    public IReadOnlyList<DurablePackageRecord> RecoverDurablePackages(
        Action<string, Task>? onRestored = null)
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Paketjournal nicht aktiviert.");
        var unresolved = new List<DurablePackageRecord>();
        _durableRestoreObserver = onRestored;
        var pendingAtStartup = journal.Pending();
        if (pendingAtStartup.Count > 0)
        {
            var overview = string.Join("; ", pendingAtStartup
                .GroupBy(p => (p.Type, p.State))
                .Select(g => g.Key.Type + "/" + g.Key.State + "=" + g.Count()));
            PCasaDiagnostics.WriteMessage("Scheduler.Recovery",
                $"Restoring {pendingAtStartup.Count} durable packages: {overview}");
        }
        // The manual executor must NOT start the oldest recovered package
        // while newer durable P0 packages are still being reconstructed.
        lock (_gate) _restoringDurablePackages = true;
        try
        {
            foreach (var record in pendingAtStartup)
            {
                // A deferred package is durable, but not currently executable.
                // The central retry coordinator owns its next activation.
                if (record.State == "deferred") continue;
                if (!_packageFactories.TryGetValue(record.Type, out var factory))
                {
                    journal.Defer(record.Key, record.CompletedSteps,
                        "Wiederherstellungsfabrik fehlt: " + record.Type);
                    unresolved.Add(record);
                    continue;
                }
                try
                {
                    var package = factory(record.Key, record.Payload);
                    package.RestoreCursor(record.CompletedSteps);
                    var completion = TrackDurableCompletion(
                        QueueDurableExisting(package, record));
                    // Observe the SAME scheduler lifetime, not a second
                    // presentation queue. Subscribers may render progress or
                    // clear the view when the original task completes.
                    try { onRestored?.Invoke(record.Key, completion); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
                }
                catch (Exception ex)
                {
                    // An unreadable recipe is not a completed operation.
                    journal.Defer(record.Key, record.CompletedSteps, ex.Message);
                    unresolved.Add(record);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _restoringDurablePackages = false;
                // Workers may have consumed signals during recovery.
                foreach (var lane in new[] { LaneManual, LaneManualSourceProbe })
                    if (_pending.Any(p => LaneFor(p.Priority, p.Kind) == lane))
                        _signals[lane].Release();
            }
        }
        // One retry coordinator owned by this scheduler, not a second
        // Tag, transfer, or scan queue. Start only after recovery is staged.
        lock (_gate)
            _deferredRetryWorker ??= Task.Run(RetryDeferredPackagesAsync);
        return unresolved;
    }

    private async Task RetryDeferredPackagesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                var journal = _durableJournal;
                if (journal is null) continue;
                try
                {
                    foreach (var key in journal.DueDeferredKeys())
                    {
                        _shutdown.Token.ThrowIfCancellationRequested();
                        var record = journal.Pending().FirstOrDefault(p => p.Key == key);
                        if (record is null || record.State != "deferred") continue;
                        if (!_packageFactories.TryGetValue(record.Type, out var factory))
                        {
                            journal.Defer(key, record.CompletedSteps,
                                "Wiederherstellungsfabrik fehlt: " + record.Type);
                            continue;
                        }
                        SchedulerTaskPackage package;
                        try
                        {
                            package = factory(record.Key, record.Payload);
                            package.RestoreCursor(record.CompletedSteps);
                        }
                        catch (Exception ex)
                        {
                            journal.Defer(key, record.CompletedSteps, ex.Message);
                            PCasaDiagnostics.Write("Scheduler.RetryRecipe:" + key, ex);
                            continue;
                        }
                        var activated = journal.ActivateDueDeferred(key);
                        if (activated is null) continue;
                        // SAME journal identity, step cursor and P0 priority;
                        // ordinary worker lanes still own every execution.
                        var completion = TrackDurableCompletion(
                            QueueDurableExisting(package, activated));
                        try { _durableRestoreObserver?.Invoke(key, completion); }
                        catch (Exception ex)
                        {
                            PCasaDiagnostics.Write("Scheduler.RetryObserver", ex);
                        }
                        PCasaDiagnostics.WriteMessage("Scheduler.Retry",
                            $"Key={key}; Type={record.Type}; Step={record.CompletedSteps}");
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Scheduler.RetryLoop", ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
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
        if (record.State is "completed" or "failed" or "canceled")
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
        catch (DurablePackageRetryException ex)
        {
            PCasaDiagnostics.WriteMessage("Scheduler.Deferred",
                $"Key={record.Key}; Type={record.Type}; Step={package.CompletedSteps}; {ex.Message}");
            // The original priority and cursor survive. An offline source
            // cannot block every subsequent scheduler job until app restart.
            journal.Defer(record.Key, package.CompletedSteps, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            // A durable step failing is not proof that the user's operation
            // should be abandoned. Filesystem/network faults and old recipe
            // limitations must remain recoverable, without holding all lower
            // priority jobs hostage between bounded retries.
            PCasaDiagnostics.Write("Scheduler.PackageDeferred:" + record.Key, ex);
            journal.Defer(record.Key, package.CompletedSteps, ex.Message);
            throw;
        }
        finally
        {
            // A background worker may have consumed its wakeup while the
            // P0 journal record was still "running". Recheck its durable
            // state after the final checkpoint, without any second queue.
            if (!_shutdown.IsCancellationRequested)
                WakeDeferredAutomaticWork();
        }
    }

    private void WakeDeferredAutomaticWork()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var lane in new[] { LaneBackground, LaneInitialImport,
                         LaneUserAction })
                if (_pending.Any(item => LaneFor(item.Priority, item.Kind) == lane))
                    _signals[lane].Release();
        }
    }

    private static int LaneFor(SchedulerPriority priority, SchedulerWorkKind kind)
        => kind switch
        {
            SchedulerWorkKind.ViewerRead => LaneViewer,
            SchedulerWorkKind.CatalogRead => LaneCatalogRead,
            SchedulerWorkKind.ThumbnailRead => LaneThumbnail,
            SchedulerWorkKind.InitialImport => LaneInitialImport,
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

    // A deferred durable record can remain queued in SQLite after its
    // last in-memory executor has failed. Only the scheduler may determine
    // whether a live execution for that identity still exists.
    public bool HasLiveExecution(string key)
    {
        lock (_gate)
            return _pending.Any(item => item.Key == key) ||
                _runningPriorityJobs.Any(item => item?.Key == key) ||
                _runningManualSourceProbe?.Key == key;
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
                foreach (var lane in new[] { LaneBackground, LaneUserAction,
                             LaneManual, LaneInitialImport })
                {
                    var active = _runningPriorityJobs[lane];
                    // An incoming user file mutation supersedes a longer
                    // manual SCAN in the same lane. Two manual mutations
                    // never cancel one another.
                    var supersedesOlderManual =
                        priority == SchedulerPriority.ManualAction &&
                        active?.Priority == SchedulerPriority.ManualAction &&
                        active.Id < incomingId &&
                        (workKind != SchedulerWorkKind.CatalogScan ||
                         active.Kind == SchedulerWorkKind.CatalogScan);
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
                // Inspect the durable P0 journal outside the scheduler lock:
                // SQLite may briefly wait on a catalogue write. Background
                // catalogue execution is the only work gated by this rule;
                // scheduled source probes and manual/interactive jobs remain
                // independent and responsive.
                var persistedManualPending =
                    (lane is LaneBackground or LaneInitialImport or LaneUserAction) &&
                    _durableJournal?.HasPendingManualActions() == true;
                WorkItem? item;
                CancellationTokenSource? yieldRequest = null;
                lock (_gate)
                {
                    // A free background worker must not pick the next job while
                    // a higher-priority catalog request is pending or running.
                    // Otherwise parallel workers can reverse the logical order.
                    var blockedByHigherPriority =
                        (lane == LaneBackground || lane == LaneInitialImport) &&
                        (persistedManualPending ||
                         _catalogForegroundRunning > 0 ||
                         HasManualSourceProbeUnsafe() ||
                         _pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                             x.Priority <= SchedulerPriority.UserAction)) ||
                        lane == LaneUserAction &&
                        (persistedManualPending ||
                         _catalogManualRunning > 0 || _catalogInteractiveRunning > 0 ||
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
                            .ThenBy(x => lane == LaneManual &&
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
                    item = blockedByHigherPriority || newerManualProbe ||
                        (_restoringDurablePackages &&
                         lane is LaneManual or LaneManualSourceProbe)
                        ? null : candidate;
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
                        if (lane <= LaneManual || lane == LaneInitialImport)
                        {
                            yieldRequest = new CancellationTokenSource();
                            _runningPriorityJobs[lane] = item;
                            _runningYield[lane] = yieldRequest;
                        }
                        SetStateUnsafe(item, SchedulerJobState.Running);
                    }
                }
                if (item is null) continue;
                // The presenter must learn that the worker actually started,
                // even if the first SQLite/SMB/ExifTool call is still blocked
                // and has not yet reached its first ReportProgress boundary.
                PublishState(item);
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
                        // A cooperative yield can make this same package queued
                        // again without issuing another progress callback.
                        PublishState(item);
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
                            if ((lane <= LaneManual || lane == LaneInitialImport) &&
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
                                if (_catalogForegroundRunning == 0 &&
                                    !HasManualSourceProbeUnsafe() &&
                                    !_pending.Any(x => (x.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan) &&
                                        x.Priority <= SchedulerPriority.UserAction) &&
                                    _pending.Any(x => LaneFor(x.Priority, x.Kind) == LaneInitialImport))
                                    _signals[LaneInitialImport].Release();
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
        PublishState(item);
    }

    // A scheduler lifecycle transition is progress too. Invoke observers
    // OUTSIDE _gate, just as ReportProgress does; no UI queue or shadow state.
    private void PublishState(WorkItem item)
    {
        SchedulerJobSnapshot? snapshot;
        lock (_gate) snapshot = _history.LastOrDefault(x => x.Id == item.Id);
        if (snapshot is null) return;
        if (snapshot.Priority == SchedulerPriority.ManualAction)
            PCasaDiagnostics.WriteMessage("Scheduler.P0State",
                $"Key={snapshot.Key}; Kind={item.Kind}; State={snapshot.State}; " +
                $"Progress={snapshot.Processed}/{snapshot.Total}; " +
                $"Path={snapshot.CurrentPath ?? "(none)"}");
        try { ProgressChanged?.Invoke(snapshot); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
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
                SchedulerJobState.Failed or SchedulerJobState.Canceled ? now : null,
            // A yielded package must not claim that it is STILL writing the
            // last file when it re-enters the queue or starts a new attempt.
            CurrentPath = state is SchedulerJobState.Queued or SchedulerJobState.Running
                ? null : _history[index].CurrentPath,
            Phase = state is SchedulerJobState.Queued or SchedulerJobState.Running
                ? null : _history[index].Phase
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
        if (_deferredRetryWorker is not null)
            await _deferredRetryWorker;
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
