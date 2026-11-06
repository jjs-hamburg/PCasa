using PCasa.App.Data;
using System.Text.Json;
using System.IO;

namespace PCasa.App.Services;

public enum SchedulerPriority { ManualAction = 0, Interactive = 1, UserAction = 2, Background = 3, Idle = 4 }
public enum SchedulerJobState { Queued, Running, YieldRequested, Completed, Canceled, Failed }
public enum SchedulerWorkKind { Catalog, CatalogScan, CatalogRead, ViewerRead, ThumbnailRead, SourceProbe, InitialImport, DatabaseBackup, BrowseRead }

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
    // A priority alone is not proof of a write: interactive catalog reads
    // must never force shutdown to wait for a blocked network source.
    public SchedulerWorkKind WorkKind { get; init; } = SchedulerWorkKind.Catalog;
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
    // Manual folder inventories may block inside SMB I/O. They must not
    // occupy the P0 original-file mutation executor (GPS, tags, transfers).
    // This is capacity within the SAME scheduler, not a second task queue.
    private const int LaneManualScan = 11;
    // A manually requested online database backup must not wait for either
    // blocked SMB scans or original-file writers. One scheduler, one rank.
    private const int LaneManualBackup = 12;
    private readonly object _gate = new();
    private DurablePackageJournal? _durableJournal;
    private SchedulerTaskOrderStore? _taskOrderStore;
    private readonly Dictionary<string,long> _userDispatchOrder =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string,SchedulerPriority> _userPriorityOverrides =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _taskAliases =
        new(StringComparer.Ordinal);
    // The owner of external source intents listens and reorders its
    // EXISTING scan cursor. No parallel queue or cloned scan work.
    public event Action? TaskOrderChanged;
    // A committed durable intent must be visible before factory creation,
    // P0 eligibility checks, or the first filesystem operation.
    public event Action? TaskAccepted;

    // One notification channel for the read-only task projection. Owners of
    // queued automatic scan intents also notify this scheduler; only the
    // scheduler publishes the change to task manager consumers.
    public event Action? TaskViewChanged;

    public void NotifyTaskViewChanged()
    {
        try { TaskViewChanged?.Invoke(); }
        catch (Exception ex) { PCasaDiagnostics.Write("Scheduler.TaskViewChanged", ex); }
    }

    private void NotifyTaskAccepted()
    {
        try { TaskAccepted?.Invoke(); }
        catch (Exception ex) { PCasaDiagnostics.Write("Scheduler.TaskAccepted", ex); }
        NotifyTaskViewChanged();
    }
    private readonly HashSet<Task> _durableCompletions = new();
    private bool _restoringDurablePackages;
    // The controller owns deferred activation. A passive probe only reports
    // source readiness; it never starts, ranks, or resumes a package.
    private Task<(HashSet<string> Ready, HashSet<string> Reconnected)>? _deferredProbe;
    private DateTimeOffset _nextDeferredProbe = DateTimeOffset.MinValue;
    private bool _deferredRecoveryReady;
    private Action<string, Task>? _durableRestoreObserver;
    private readonly Dictionary<string, Func<string, string, SchedulerTaskPackage>> _packageFactories = new(StringComparer.Ordinal);
    private readonly List<WorkItem> _pending = new();
    // All physical executor lanes participate in idle admission, including
    // read/thumbnail lanes that have no catalog-mutation counter.
    private readonly HashSet<WorkItem> _runningWork = new();
    private bool _outstandingNonIdleAtLastReview;
    // Nested operations inherit their owning package across await/Dispatcher
    // continuations. A package must never await a child that the queue puts
    // behind another waiting package (dependency deadlock).
    private readonly AsyncLocal<WorkItem?> _currentPackage = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    // Worker semaphores are execution handoffs ONLY. No worker chooses work.
    private readonly SemaphoreSlim[] _signals =
        Enumerable.Range(0, LaneManualBackup + 1).Select(_ => new SemaphoreSlim(0)).ToArray();
    private readonly Queue<(WorkItem Item, CancellationTokenSource? Yield)>[] _assigned =
        Enumerable.Range(0, LaneManualBackup + 1)
            .Select(_ => new Queue<(WorkItem, CancellationTokenSource?)>()).ToArray();
    private readonly int[] _laneCapacity = [1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 1];
    private readonly int[] _laneOccupied = new int[LaneManualBackup + 1];
    private readonly SemaphoreSlim _controllerWake = new(0, 1);
    private readonly Task _controller;
    // Track the dedicated first-import executor as well. Otherwise an
    // already-running initial scan never receives the scheduler's yield
    // cancellation when a new manual P0 package arrives.
    private readonly WorkItem?[] _runningPriorityJobs =
        new WorkItem?[LaneManualBackup + 1];
    private int _catalogForegroundRunning;
    private int _catalogManualRunning;
    private int _catalogInteractiveRunning;
    private int _manualSourceProbeRunning;
    private WorkItem? _runningManualSourceProbe;
    private readonly CancellationTokenSource?[] _runningYield =
        new CancellationTokenSource?[LaneManualBackup + 1];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _nextId;
    private bool _disposed;
    // An OLE FileDrop is an immediate foreground gesture. Keep lower-priority
    // source/scanning work suspended only for its synchronous drag lifetime.
    // This lease belongs to the ONE scheduler; no second job/queue is built.
    private int _nativeDragInteractionCount;

    private sealed class NativeDragLease(PCasaScheduler owner) : IDisposable
    {
        private PCasaScheduler? _owner = owner;
        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndNativeDragInteraction();
    }

    public IDisposable BeginNativeDragInteraction()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _nativeDragInteractionCount++;
            SignalController();
        }
        return new NativeDragLease(this);
    }

    private void EndNativeDragInteraction()
    {
        lock (_gate)
        {
            if (_nativeDragInteractionCount == 0) return;
            if (--_nativeDragInteractionCount != 0) return;
            // Workers may have consumed their signals while the drag held
            // background lanes. Wake those that still have pending work.
            foreach (var lane in new[]
                { LaneBackground, LaneInitialImport, LaneUserAction, LaneSourceProbe })
            {
                if (_pending.Any(item => LaneFor(item.Priority, item.Kind) == lane))
                    SignalController();
            }
        }
    }

    public PCasaScheduler(SourceAvailabilityGate? sourceAvailability = null)
    {
        SourceAvailability = sourceAvailability ?? new SourceAvailabilityGate();
        // File reading is bounded. UI painting and hit testing never enter a lane.
        _controller = Task.Run(ControlLoopAsync);
        _workers = new[]
        {
            Task.Run(() => RunAsync(LaneBackground)),
            // Same coordinator, same durable/source fences, separate bounded
            // capacity. An unfinished first import owns only its own root.
            Task.Run(() => RunAsync(LaneInitialImport)),
            Task.Run(() => RunAsync(LaneUserAction)),
            // A manually requested scan must not wait for blocked initial SMB I/O.
            Task.Run(() => RunAsync(LaneManual)),
            Task.Run(() => RunAsync(LaneManualScan)),
            Task.Run(() => RunAsync(LaneManualBackup)),
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

    // Shared by all source-related operations and the existing background
    // monitor. This is a cached readiness decision, never a parallel job lane.
    public SourceAvailabilityGate SourceAvailability { get; }

    public void EnableDurablePackages(LocalDatabase database)
    {
        if (_durableJournal is not null)
            throw new InvalidOperationException("Paketjournal bereits aktiviert.");
        _durableJournal = new DurablePackageJournal(database);
        _taskOrderStore = new SchedulerTaskOrderStore(database);
        foreach (var (key, placement) in _taskOrderStore.ReadAll())
        {
            _userDispatchOrder[key] = placement.Order;
            if (placement.PriorityOverride is SchedulerPriority chosen)
                _userPriorityOverrides[key] = chosen;
        }
    }

    // The Taskmanager observes the scheduler's ONE user-defined order.
    // Default priority/queue order is used until the user first rearranges
    // an active item. A newly created manual P0 job therefore still starts
    // ahead of untouched automatic P4 requests.
    public (SchedulerPriority Priority, long QueueOrder) EffectiveTaskPlacement(
        string key, SchedulerPriority defaultPriority, long defaultOrder)
    {
        lock (_gate)
        {
            return _userDispatchOrder.TryGetValue(key, out var order)
                ? (_userPriorityOverrides.GetValueOrDefault(key, defaultPriority),
                    order)
                : (defaultPriority, defaultOrder);
        }
    }

    // A presenter supplies ONLY descriptive metadata supplied by task
    // owners. The scheduler is the sole authority for effective priority,
    // persisted user order and the global dispatch sequence. This avoids
    // an independent Taskmanager sort algorithm drifting away from the
    // worker queue. Hidden status-filter rows still contribute to moves.
    public IReadOnlyList<T> OrderTaskView<T>(
        IEnumerable<T> source,
        Func<T,string> key,
        Func<T,SchedulerPriority> defaultPriority,
        Func<T,long> defaultOrder,
        Func<T,bool> completed,
        Func<T,SchedulerPriority,long,T> withPlacement)
    {
        lock (_gate)
        {
            return source.Select(item =>
            {
                var identity = key(item);
                var priority = defaultPriority(item);
                var order = defaultOrder(item);
                if (_userDispatchOrder.TryGetValue(identity, out var customOrder))
                {
                    order = customOrder;
                    if (_userPriorityOverrides.TryGetValue(identity,
                        out var customPriority))
                        priority = customPriority;
                }
                return (Value: withPlacement(item, priority, order),
                    Completed: completed(item), Priority: priority,
                    Order: order, Key: identity);
            }).OrderBy(item => item.Completed ? 1 : 0)
                .ThenBy(item => item.Priority)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Value)
                .ToArray();
        }
    }

    public void RegisterTaskAlias(string executorKey, string visibleTaskKey)
    {
        lock (_gate)
            _taskAliases[executorKey] = visibleTaskKey;
    }

    public void UnregisterTaskAlias(string executorKey)
    {
        lock (_gate) _taskAliases.Remove(executorKey);
    }

    private string TaskIdentityUnsafe(string executorKey) =>
        _taskAliases.TryGetValue(executorKey, out var identity)
            ? identity : executorKey;

    // No job-type, original-priority or lane restriction. The list must
    // contain all ACTIVE accepted tasks, including ones hidden by filters.
    // The store is the only durable place for user-selected dispatch order.
    public bool MoveTaskGlobally(string key, string targetKey,
        IReadOnlyList<SchedulerTaskOrderItem> acceptedTaskKeys)
    {
        var store = _taskOrderStore ??
            throw new InvalidOperationException("Taskmanager-Reihenfolge nicht aktiviert.");
        List<WorkItem> changed = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var order = store.Reorder(acceptedTaskKeys, key, targetKey);
            if (order is null) return false;
            foreach (var (id, placement) in order)
            {
                _userDispatchOrder[id] = placement.Order;
                if (placement.PriorityOverride is SchedulerPriority chosen)
                    _userPriorityOverrides[id] = chosen;
                else
                    _userPriorityOverrides.Remove(id);
            }
            foreach (var work in _pending)
            {
                if (!_userDispatchOrder.TryGetValue(
                    TaskIdentityUnsafe(work.Key), out var position))
                    continue;
                if (_userPriorityOverrides.TryGetValue(
                        TaskIdentityUnsafe(work.Key), out var chosen))
                    work.Priority = chosen;
                work.QueueOrder = position;
                SetStateUnsafe(work, SchedulerJobState.Queued);
                changed.Add(work);
            }
            // Keep the exact rank on live workers as well. A yielded file
            // must reenter the same global position after its safe checkpoint.
            foreach (var active in _runningPriorityJobs)
            {
                if (active is null ||
                    !_userDispatchOrder.TryGetValue(TaskIdentityUnsafe(active.Key),
                        out var position)) continue;
                if (_userPriorityOverrides.TryGetValue(
                        TaskIdentityUnsafe(active.Key), out var chosen))
                    active.Priority = chosen;
                active.QueueOrder = position;
                SetStateUnsafe(active, active.YieldRequested
                    ? SchedulerJobState.YieldRequested : SchedulerJobState.Running);
                changed.Add(active);
            }
            WakePendingWorkersUnsafe();
        }
        foreach (var work in changed) PublishState(work);
        try { TaskOrderChanged?.Invoke(); }
        catch (Exception ex) { PCasaDiagnostics.Write("Scheduler.TaskOrderChanged", ex); }
        NotifyTaskViewChanged();
        return true;
    }

    // Control-plane changes must not wait in the WORK queue that they are
    // changing. The scheduler serializes the mutation under its existing
    // gate; SQLite persists the actual owner intent in the same operation.
    // Call from Task.Run when invoked by WPF so disk contention never
    // stalls the click handler. No second execution queue is created.
    public T ChangeTaskIntent<T>(Func<T> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return mutation();
        }
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
                SignalController();
            }
        }
        // Enable the already-running controller to reconcile the journal.
        // There is no independently scheduled retry/activation coordinator.
        lock (_gate)
        {
            _deferredRecoveryReady = true;
            _nextDeferredProbe = DateTimeOffset.MinValue;
            SignalController();
        }
        return unresolved;
    }

    // The SINGLE continuously active decision maker. Worker threads do not
    // pick, rank, preempt or interpret the durable task journal.
    private async Task ControlLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                // Immediate wake on any mutation, PLUS a regular independent
                // reconciliation: neither progress callbacks nor UI events
                // are required to notice a different durable queue state.
                await _controllerWake.WaitAsync(TimeSpan.FromMilliseconds(150),
                    _shutdown.Token);
                if (_deferredRecoveryReady)
                {
                    // Completed source probes are DATA, not execution decisions.
                    // Only this controller activates a journal record.
                    if (_deferredProbe is { IsCompleted: true } finishedProbe)
                    {
                        _deferredProbe = null;
                        try { ActivateDeferredPackages(finishedProbe.GetAwaiter().GetResult()); }
                        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            PCasaDiagnostics.Write("Scheduler.DeferredReconcile", ex);
                        }
                    }
                    if (_deferredProbe is null &&
                        DateTimeOffset.UtcNow >= _nextDeferredProbe)
                    {
                        _nextDeferredProbe = DateTimeOffset.UtcNow.AddSeconds(2);
                        // Never block priority arbitration on offline SMB I/O.
                        // Probe merely reports due/reconnected identities.
                        _deferredProbe = Task.Run(ProbeDeferredPackages);
                    }
                }
                var durable = _durableJournal;
                bool manualWaiting = false, nonIdleWaiting = false;
                try
                {
                    if (durable is not null)
                    {
                        manualWaiting = durable.HasPendingEffectiveManualActions();
                        nonIdleWaiting = durable.HasOutstandingNonIdleTasks();
                    }
                }
                catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    PCasaDiagnostics.Write("Scheduler.ControlRead", ex);
                    // Never admit P4 on an unverified journal snapshot.
                    nonIdleWaiting = true;
                }
                var cancel = new List<CancellationTokenSource>();
                var viewChanged = false;
                lock (_gate)
                {
                    if (_disposed) break;
                    // Global rank, not per-lane callbacks, decides preemption.
                    var highest = _pending.OrderBy(p => RankOwner(p).Priority)
                        .ThenBy(p => RankOwner(p).QueueOrder)
                        .ThenBy(p => RankOwner(p).Id).FirstOrDefault();
                    for (var lane = 0; lane < _runningPriorityJobs.Length; lane++)
                    {
                        var running = _runningPriorityJobs[lane];
                        if (running is null || running.YieldRequested) continue;
                        if (!((RankOwner(running).Priority == SchedulerPriority.Idle &&
                               (nonIdleWaiting || _pending.Any(p =>
                                   RankOwner(p).Priority < SchedulerPriority.Idle))) ||
                              (_nativeDragInteractionCount > 0 &&
                               lane is LaneBackground or LaneInitialImport or LaneUserAction) ||
                              (highest is not null && Precedes(highest, running))))
                            continue;
                        running.YieldRequested = true;
                        SetStateUnsafe(running, SchedulerJobState.YieldRequested);
                        viewChanged = true;
                        if (_runningYield[lane] is { } yield) cancel.Add(yield);
                    }
                    // Keep filling available bounded capacity in global rank
                    // order. SelectNextJobUnsafe is the sole eligibility check.
                    var available = true;
                    while (available)
                    {
                        available = false;
                        for (var lane = 0; lane <= LaneManualBackup; lane++)
                        {
                            if (_laneOccupied[lane] >= _laneCapacity[lane]) continue;
                            var item = SelectNextJobUnsafe(lane,
                                manualWaiting, nonIdleWaiting);
                            if (item is null) continue;
                            _pending.Remove(item);
                            item.YieldRequested = false;
                            _laneOccupied[lane]++;
                            if (item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan)
                            {
                                if (lane == LaneUserAction) _catalogForegroundRunning++;
                                if (lane is LaneManual or LaneManualScan)
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
                            CancellationTokenSource? yieldRequest = null;
                            if (lane <= LaneManual ||
                                lane is LaneInitialImport or LaneManualScan or LaneManualBackup)
                            {
                                yieldRequest = new CancellationTokenSource();
                                _runningPriorityJobs[lane] = item;
                                _runningYield[lane] = yieldRequest;
                            }
                            _runningWork.Add(item);
                            SetStateUnsafe(item, SchedulerJobState.Running);
                            viewChanged = true;
                            _assigned[lane].Enqueue((item, yieldRequest));
                            _signals[lane].Release();
                            available = true;
                        }
                    }
                }
                // Publish only real dispatch/yield changes, outside the lock.
                if (viewChanged) NotifyTaskViewChanged();
                // Never execute cancellation callbacks under scheduler lock.
                foreach (var yield in cancel)
                {
                    try { _ = yield.CancelAsync(); }
                    catch (ObjectDisposedException) { }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    // Passive I/O probe, with no mutation of queue or journal state.
    // An offline SMB path may block; controller arbitration continues.
    private (HashSet<string> Ready, HashSet<string> Reconnected) ProbeDeferredPackages()
    {
        var ready = new HashSet<string>(StringComparer.Ordinal);
        var reconnected = new HashSet<string>(StringComparer.Ordinal);
        var journal = _durableJournal;
        if (journal is null || _shutdown.IsCancellationRequested)
            return (ready, reconnected);
        ready.UnionWith(journal.DueDeferredKeys());
        foreach (var pending in journal.Pending())
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            if (pending.State != "deferred" ||
                pending.Type != "manual-folder-scan-v1" ||
                pending.Error?.StartsWith(
                    "Quelle vorübergehend offline", StringComparison.Ordinal) != true)
                continue;
            try
            {
                using var recipe = JsonDocument.Parse(pending.Payload);
                if (!recipe.RootElement.TryGetProperty("Directory", out var directory) ||
                    directory.ValueKind != JsonValueKind.String)
                    continue;
                var source = directory.GetString();
                if (string.IsNullOrWhiteSpace(source) ||
                    !SourceAvailability.CanAccess(source, _shutdown.Token))
                    continue;
                ready.Add(pending.Key);
                reconnected.Add(pending.Key);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException
                                        or IOException or UnauthorizedAccessException)
            {
                PCasaDiagnostics.Write("Scheduler.SourceReconnectProbe", ex);
            }
        }
        return (ready, reconnected);
    }

    // Called ONLY on the controller loop. A probe never decides which package
    // may resume; SQLite CAS preserves the existing identity and step cursor.
    private void ActivateDeferredPackages(
        (HashSet<string> Ready, HashSet<string> Reconnected) probe)
    {
        var journal = _durableJournal;
        if (journal is null) return;
        foreach (var key in probe.Ready)
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
            var activated = probe.Reconnected.Contains(key)
                ? journal.RestartDeferred(key)
                : journal.ActivateDueDeferred(key);
            if (activated is null) continue;
            var completion = TrackDurableCompletion(
                QueueDurableExisting(package, activated));
            try { _durableRestoreObserver?.Invoke(key, completion); }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Scheduler.RetryObserver", ex);
            }
            PCasaDiagnostics.WriteMessage(
                probe.Reconnected.Contains(key) ? "Scheduler.SourceReconnected" : "Scheduler.Retry",
                $"Key={key}; Type={record.Type}; Step={record.CompletedSteps}");
        }
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
        NotifyTaskAccepted();
        package.RestoreCursor(record.CompletedSteps);
        return TrackDurableCompletion(QueueDurableExisting(package, record, cancellationToken));
    }

    // Register immutable intent synchronously. Building thousands of GPS
    // file steps happens AFTER the journal commit, off the WPF click thread.
    // A crash in between is handled by the normal durable recovery factory.
    // This only prepares a recipe: the ONE scheduler still dispatches and
    // executes every file step through its existing lanes and checkpoints.
    public Task EnqueueDurableRecipeAsync(string key, string type,
        string payload, SchedulerPriority priority,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog,
        CancellationToken cancellationToken = default)
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Paketjournal nicht aktiviert.");
        if (!_packageFactories.TryGetValue(type, out var factory))
            throw new InvalidOperationException(
                "Pakettyp ohne Wiederherstellungsfabrik: " + type);
        var record = journal.Put(key, type, payload, priority, workKind);
        if (record.State is "completed" or "failed" or "canceled")
            throw new InvalidOperationException("Paketidentität bereits abgeschlossen.");
        NotifyTaskAccepted();
        var completion = Task.Run(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = factory(record.Key, record.Payload);
                package.RestoreCursor(record.CompletedSteps);
                await QueueDurableExisting(package, record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never leave an unconstructable accepted intent invisible.
                journal.Defer(record.Key, record.CompletedSteps, ex.Message);
                throw;
            }
        }, CancellationToken.None);
        return TrackDurableCompletion(completion);
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
                cancellationToken, workKind: record.WorkKind,
                queueOrder: record.QueueOrder);
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
            // The worker already announced its final state, but the durable
            // journal commits completion/defer only in this continuation.
            // Notify after that commit so the task view never stays stale.
            NotifyTaskViewChanged();
        }
    }

    private void WakeDeferredAutomaticWork()
    {
        lock (_gate)
        {
            if (!_disposed) WakePendingWorkersUnsafe();
        }
    }

    private static int LaneFor(SchedulerPriority priority, SchedulerWorkKind kind)
        => kind switch
        {
            SchedulerWorkKind.ViewerRead => LaneViewer,
            SchedulerWorkKind.CatalogRead or SchedulerWorkKind.BrowseRead => LaneCatalogRead,
            SchedulerWorkKind.DatabaseBackup => LaneManualBackup,
            SchedulerWorkKind.ThumbnailRead => LaneThumbnail,
            SchedulerWorkKind.InitialImport => LaneInitialImport,
            SchedulerWorkKind.CatalogScan when priority == SchedulerPriority.ManualAction =>
                LaneManualScan,
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
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog,
        long? queueOrder = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        return EnqueueAsync(package.Key, priority, package.ExecuteAsync,
            cancellationToken: cancellationToken, workKind: workKind,
            queueOrder: queueOrder);
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
                WakePendingWorkersUnsafe();
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

    // The currently executing parent is available only inside its async
    // execution scope. It does not create another scheduler task.
    public string? CurrentPackageKey => _currentPackage.Value?.Key;

    // A shared asynchronous operation can have been queued by an earlier
    // caller before a higher-priority package begins awaiting it. Inherit
    // the waiting package's rank for its existing technical steps, too.
    // This only updates parentage in the ONE scheduler list; no new job,
    // cancellation or parallel queue is created.
    public void AttachDependentWorkToPackage(string packageKey,
        params string[] technicalKeyPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);
        lock (_gate)
        {
            var parent = _runningWork.FirstOrDefault(item =>
                string.Equals(item.Key, packageKey, StringComparison.Ordinal));
            if (parent is null)
                throw new InvalidOperationException(
                    "Dependent work has no running owning package: " + packageKey);
            foreach (var item in _pending.Concat(_runningWork))
            {
                if (ReferenceEquals(item, parent) ||
                    !technicalKeyPrefixes.Any(prefix =>
                        item.Key.StartsWith(prefix, StringComparison.Ordinal)))
                    continue;
                item.Parent = parent;
            }
            SignalController();
        }
    }

    // Restores a durable package's execution context across UI Dispatcher
    // boundaries. The nested work remains a STEP of the running package,
    // never an independently ranked P1 task. The scope flows across await.
    public async Task ExecuteDependentStepAsync(string packageKey, Func<Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageKey);
        ArgumentNullException.ThrowIfNull(operation);
        WorkItem? parent;
        lock (_gate)
            parent = _runningWork.FirstOrDefault(item =>
                string.Equals(item.Key, packageKey, StringComparison.Ordinal));
        if (parent is null)
            throw new InvalidOperationException(
                "Dependent scheduler step has no running owning package: " + packageKey);
        var previous = _currentPackage.Value;
        _currentPackage.Value = parent;
        try { await operation().ConfigureAwait(true); }
        finally { _currentPackage.Value = previous; }
    }

    // Typed scheduler results use exactly the same queue, priority,
    // cancellation and lifetime bookkeeping as catalog mutations.
    public async Task<TResult> RunAsync<TResult>(
        string key, SchedulerPriority priority,
        Func<CancellationToken, TResult> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.CatalogRead,
        string? parentKey = null)
    {
        TResult? result = default;
        await EnqueueAsync(key, priority,
            token =>
            {
                token.ThrowIfCancellationRequested();
                result = operation(token);
                return Task.CompletedTask;
            }, isCurrent, cancellationToken, workKind, parentKey: parentKey);
        return result!;
    }

    // Taskmanager commands remain on the ONE scheduler owner. The journal
    // persists ranks first; a promoted waiting task then preempts a running
    // durable peer in its OWN worker lane at the next safe scan checkpoint.
    // No second scheduler or duplicate scan recipe is created.
    public bool MoveDurableTask(string key, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var journal = _durableJournal ?? throw new InvalidOperationException("Kein Paketjournal.");
        var all = journal.Pending();
        var source = all.FirstOrDefault(row => row.Key == key);
        if (source is null) return false;
        var lane = all.Where(row =>
                row.Priority == source.Priority &&
                LaneFor(row.Priority, row.WorkKind) ==
                    LaneFor(source.Priority, source.WorkKind)).ToList();
        var index = lane.FindIndex(row => row.Key == key);
        if (index < 0 || index + direction < 0 || index + direction >= lane.Count)
            return false;
        return MoveDurableTaskTo(key, lane[index + direction].Key);
    }

    // Drag/drop and arrow buttons call the same atomic journal mutation.
    // The drop target denotes the desired final slot in scheduler dispatch
    // order; it can be the currently running, interruptible durable scan.
    public bool MoveDurableTaskTo(string key, string targetKey)
    {
        var journal = _durableJournal ?? throw new InvalidOperationException("Kein Paketjournal.");
        lock (_gate)
        {
            var rows = journal.Pending();
            var source = rows.FirstOrDefault(row => row.Key == key);
            var target = rows.FirstOrDefault(row => row.Key == targetKey);
            if (source is null || target is null ||
                source.Priority != target.Priority ||
                LaneFor(source.Priority, source.WorkKind) !=
                    LaneFor(target.Priority, target.WorkKind) ||
                !_pending.Any(item => item.Key == key &&
                    item.Priority == source.Priority))
                return false; // never move the live executor or an absent row

            var changed = journal.Reorder(key, targetKey);
            if (changed is null || changed.Count == 0) return false;
            foreach (var work in _pending)
                if (changed.TryGetValue(work.Key, out var rank))
                    work.QueueOrder = rank;
            // The active work item is NOT in _pending. It still needs its
            // new rank, both to detect that a queued peer outranks it and
            // to resume in the correct position after cooperative yield.
            foreach (var active in _runningPriorityJobs)
                if (active is not null &&
                    changed.TryGetValue(active.Key, out var rank))
                    active.QueueOrder = rank;
            if (_runningManualSourceProbe is { } probe &&
                changed.TryGetValue(probe.Key, out var probeRank))
                probe.QueueOrder = probeRank;

            SignalController();
        }

        return true;
    }

    // No new retry queue: activate the same durable recipe through the
    // scheduler's existing factory and completion observer.
    public bool RetryDurableTask(string key)
    {
        var journal = _durableJournal ?? throw new InvalidOperationException("Kein Paketjournal.");
        var record = journal.Pending().FirstOrDefault(item => item.Key == key);
        if (record is null || record.State != "deferred") return false;
        if (!_packageFactories.TryGetValue(record.Type, out var factory))
            throw new InvalidOperationException("Wiederherstellungsrezept fehlt.");
        var package = factory(record.Key, record.Payload);
        package.RestoreCursor(record.CompletedSteps);
        var activated = journal.RestartDeferred(key);
        if (activated is null) return false;
        var task = TrackDurableCompletion(QueueDurableExisting(package, activated));
        try { _durableRestoreObserver?.Invoke(key, task); }
        catch (Exception ex) { PCasaDiagnostics.Write("Taskmanager.RetryObserver", ex); }
        return true;
    }

    public async Task<bool> DeleteDurableTaskAsync(string key)
    {
        var journal = _durableJournal ?? throw new InvalidOperationException("Kein Paketjournal.");
        var record = journal.Pending().FirstOrDefault(item => item.Key == key);
        if (record is null) return false;
        // Tag intents are already mirrored into the displayed catalog.
        // Silently canceling them after the first SQLite step would leave
        // pending_tag_edits and wrong keywords. The existing error/discard
        // path must first reconcile those originals, including offline NAS.
        if (record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
            (record.CompletedSteps > 0 || record.State == "running" ||
             record.Type.StartsWith("tag-recovery-", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Dieser Tag-Auftrag enthält bereits vorgemerkte Änderungen. " +
                "Er darf erst nach deren sicherem Abgleich oder Verwerfen gelöscht werden.");
        WorkItem? running;
        Task? completion = null;
        lock (_gate)
        {
            // The first tag step can be staging SQLite intents while its
            // durable cursor still reads zero. Do not cancel in that gap.
            if (record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
                _runningPriorityJobs.Any(item => item?.Key == key))
                throw new InvalidOperationException(
                    "Ein laufender Tag-Schreibauftrag kann erst nach sicherem " +
                    "Abgleich der vorgemerkten Änderungen entfernt werden.");
            var waiting = _pending.FirstOrDefault(item => item.Key == key);
            if (waiting is not null)
            {
                if (!journal.Cancel(key)) return false;
                _pending.Remove(waiting);
                SetStateUnsafe(waiting, SchedulerJobState.Canceled);
                waiting.Completion.TrySetCanceled();
                WakePendingWorkersUnsafe();
                return true;
            }
            running = _runningPriorityJobs.FirstOrDefault(item => item?.Key == key);
            if (running is null && _runningManualSourceProbe?.Key == key)
                running = _runningManualSourceProbe;
            if (running is null)
            {
                var canceled = journal.Cancel(key); // deferred; no live executor
                if (canceled) WakePendingWorkersUnsafe();
                return canceled;
            }
            completion = running.Completion.Task;
        }
        // Cooperative cancellation respects a file's atomic write/commit
        // boundary. Only once the worker exits may the journal record cancel.
        await running.UserCancellation.CancelAsync();
        try { await completion!; }
        catch (OperationCanceledException) { }
        return true;
    }

    // The scheduler owns the read-only unified task projection. It does
    // not introduce an executor, queue, or additional source of dispatch
    // truth; durable and scanner owners merely supply their existing intents.
    public SchedulerTaskView CreateTaskView(LocalDatabase database,
        bool canMoveAutomatic, bool canResumeAutomatic,
        bool canStopAutomatic, bool canRetryFailed, bool canDiscardFailed)
        => new(this, database, canMoveAutomatic, canResumeAutomatic,
            canStopAutomatic, canRetryFailed, canDiscardFailed);

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
            NotifyTaskViewChanged();
        }
    }

    public Task EnqueueAsync(string key, SchedulerPriority priority,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent = null,
        CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog,
        long? queueOrder = null, string? parentKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(operation);
        Task completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var identity = TaskIdentityUnsafe(key);
            var existing = _pending.FirstOrDefault(x =>
                x.Kind == workKind && x.Key == key &&
                !x.CancellationToken.IsCancellationRequested);
            if (existing is not null)
            {
                if (priority < existing.Priority)
                {
                    existing.Priority = priority;
                    SetStateUnsafe(existing, SchedulerJobState.Queued);
                    SignalController();
                }
                completion = existing.Completion.Task;
            }
            else
            {
                if (_userPriorityOverrides.TryGetValue(identity, out var selectedPriority))
                    priority = selectedPriority;
                var item = new WorkItem(++_nextId, key, priority, workKind,
                    operation, isCurrent, cancellationToken)
                {
                    // Ephemeral manual preflight (e.g. GPS eligibility)
                    // must never be buried behind the old durable ledger's
                    // large historical sequence values. Durable ranks remain
                    // authoritative relative to each other.
                    QueueOrder = _userDispatchOrder.TryGetValue(identity, out var acceptedRank)
                        ? acceptedRank
                        : queueOrder ?? (priority == SchedulerPriority.ManualAction
                            ? (long.MinValue / 2) - _nextId : _nextId)
                };
                // Technical catalog reads started by an executing package
                // are dependent STEPS, not independent competitors for rank.
                // Explicit owner identity survives WPF Dispatcher hops;
                // AsyncLocal alone cannot cover that asynchronous boundary.
                if (!string.IsNullOrWhiteSpace(parentKey))
                    item.Parent = _runningWork.FirstOrDefault(work =>
                        string.Equals(work.Key, parentKey, StringComparison.Ordinal));
                else if (_currentPackage.Value is { } parent &&
                         _runningWork.Contains(parent))
                    item.Parent = parent;
                _pending.Add(item);
                _history.Add(new SchedulerJobSnapshot(item.Id, key, priority,
                    SchedulerJobState.Queued, DateTimeOffset.UtcNow, null)
                { WorkKind = workKind });
                TrimHistoryUnsafe();
                SignalController();
                completion = item.Completion.Task;
            }

            SignalController();
        }
        NotifyTaskViewChanged();
        return completion;
    }

    // A technical catalog read, thumbnail read or publication started by a
    // package inherits its owner's GLOBAL rank. The lane only determines
    // which bounded executor performs the step; it never creates a new task
    // competing with the package awaiting that step.
    private static WorkItem RankOwner(WorkItem item)
    {
        while (item.Parent is { } parent)
            item = parent;
        return item;
    }

    // One global ordering rule for Taskmanager moves, default priorities,
    // manual LIFO and cross-lane admission. QueueOrder is assigned once at
    // registration and updated only by an explicit user reorder.
    private static bool Precedes(WorkItem first, WorkItem second)
    {
        var a = RankOwner(first);
        var b = RankOwner(second);
        return a.Priority < b.Priority ||
               (a.Priority == b.Priority &&
                (a.QueueOrder < b.QueueOrder ||
                 (a.QueueOrder == b.QueueOrder && a.Id < b.Id)));
    }

    // Called under _gate. This is the ONE business decision about which
    // accepted job may start. The physical lane is only an execution-capacity
    // constraint. A new P0 outranks an older P0 on another blocked SMB lane.
    // It may start on a free worker while that older worker reaches a safe
    // interruption boundary. No task's priority is derived from its lane.
    private WorkItem? SelectNextJobUnsafe(int lane,
        bool durableManualPending, bool durableNonIdlePending)
    {
        var candidate = _pending
            .Where(x => LaneFor(x.Priority, x.Kind) == lane)
            .OrderBy(x => RankOwner(x).Priority)
            .ThenBy(x => RankOwner(x).QueueOrder)
            .ThenBy(x => RankOwner(x).Id)
            .FirstOrDefault();
        if (candidate is null) return null;

        // Restore the saved journal/order before allowing any restored P0
        // worker to start. Existing packages are NOT recreated.
        if (_restoringDurablePackages &&
            lane is LaneManual or LaneManualScan or LaneManualBackup or LaneManualSourceProbe)
            return null;
        if (_nativeDragInteractionCount > 0 &&
            lane is LaneBackground or LaneInitialImport or LaneUserAction or LaneSourceProbe)
            return null;

        // P4 is pure idle work. All waiting/running P0-P3 requests exclude
        // it, INCLUDING durable 'deferred' packages visible as Wartet in TM.
        if (RankOwner(candidate).Priority == SchedulerPriority.Idle &&
            (durableNonIdlePending ||
             _pending.Any(other => !ReferenceEquals(other, candidate) &&
                 RankOwner(other).Priority < SchedulerPriority.Idle) ||
             _runningWork.Any(other => RankOwner(other).Priority < SchedulerPriority.Idle)))
            return null;

        // The read-only browse projection is a technical view operation,
        // not a competing user package. It has bounded read capacity and
        // cannot write originals or seize a P0 execution lane. In particular,
        // an offline/slow durable P0 NAS scan must never leave the initial
        // folder tree and gallery blank on startup.
        var independentBrowseRead = candidate.Kind == SchedulerWorkKind.BrowseRead;
        // One package owns its dependent operations. They must never wait
        // behind an unrelated queued P0 while the owner awaits them.
        var childOfRunningPackage = candidate.Parent is { } parent &&
            _runningWork.Contains(parent);
        if (!childOfRunningPackage && !independentBrowseRead &&
            _pending.Any(other => !ReferenceEquals(other, candidate) &&
                Precedes(other, candidate)))
            return null;
        // Manual P0 packages form an actual LIFO interruption stack.
        // P1-P3 may run on independent read/executor capacity while an
        // older higher-priority operation is already in flight. P4 is
        // explicitly stricter and was checked above against ALL running jobs.
        if (RankOwner(candidate).Priority == SchedulerPriority.ManualAction &&
            _runningWork.Any(active =>
                RankOwner(active).Priority == SchedulerPriority.ManualAction &&
                Precedes(active, candidate)))
            return null;

        // Journal recovery may have an accepted P0 package not yet copied
        // into the in-memory run queue. It still wins over P2-P4 execution.
        if (!childOfRunningPackage && !independentBrowseRead &&
            durableManualPending &&
            RankOwner(candidate).Priority > SchedulerPriority.ManualAction)
            return null;

        // Technical safety only: never overlap a foreground catalog mutation
        // with a lower-priority bulk catalog pass. This does NOT assign order.
        if (lane is LaneBackground or LaneInitialImport &&
            (_catalogForegroundRunning > 0 || HasManualSourceProbeUnsafe()))
            return null;
        if (lane == LaneUserAction &&
            (_catalogManualRunning > 0 || _catalogInteractiveRunning > 0 ||
             HasManualSourceProbeUnsafe()))
            return null;
        return candidate;
    }

    // A scheduler state change never depends on which worker happened to
    // finish. Reconsider ALL pending jobs against the same global order.
    // Calling under _gate avoids lost wakeups between eligibility and sleep.
    private void SignalController()
    {
        if (_shutdown.IsCancellationRequested) return;
        try { _controllerWake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void WakePendingWorkersUnsafe() => SignalController();

    private async Task RunAsync(int lane)
    {
        try
        {
            while (true)
            {
                await _signals[lane].WaitAsync(_shutdown.Token);
                WorkItem item;
                CancellationTokenSource? yieldRequest;
                lock (_gate)
                {
                    if (_assigned[lane].Count == 0) continue; // stale signal
                    (item, yieldRequest) = _assigned[lane].Dequeue();
                }
                // The presenter must learn that the worker actually started,
                // even if the first SQLite/SMB/ExifTool call is still blocked
                // and has not yet reached its first ReportProgress boundary.
                PublishState(item);
                using (yieldRequest)
                using (var linked = yieldRequest is null
                    ? CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token, item.CancellationToken,
                        item.UserCancellation.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(
                        _shutdown.Token, item.CancellationToken,
                        item.UserCancellation.Token, yieldRequest.Token))
                {
                    try
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (item.IsCurrent is not null && !item.IsCurrent())
                            throw new OperationCanceledException();
                        var previousPackage = _currentPackage.Value;
                        _currentPackage.Value = item;
                        try { await item.Operation(linked.Token); }
                        finally { _currentPackage.Value = previousPackage; }
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
                                SignalController();
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
                            _runningWork.Remove(item);
                            _laneOccupied[lane]--;
                            // Every completed, canceled or yielded operation
                            // changes the ONE global eligibility decision.
                            // Wake each pending lane once; the scheduler will
                            // choose the next ranked job, never the worker.
                            WakePendingWorkersUnsafe();
                            if ((lane <= LaneManual || lane is LaneInitialImport or LaneManualScan or LaneManualBackup) &&
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
                                if (lane is LaneManual or LaneManualScan)
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
        NotifyTaskViewChanged();
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
        await _controller;
        if (_deferredProbe is not null)
        {
            try { await _deferredProbe; }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
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
        _controllerWake.Dispose();
    }

    private sealed class WorkItem(
        long id, string key, SchedulerPriority priority, SchedulerWorkKind kind,
        Func<CancellationToken, Task> operation,
        Func<bool>? isCurrent, CancellationToken cancellationToken)
    {
        public long Id { get; } = id;
        public string Key { get; } = key;
        public long QueueOrder { get; set; }
        public WorkItem? Parent { get; set; }
        public CancellationTokenSource UserCancellation { get; } = new();
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
