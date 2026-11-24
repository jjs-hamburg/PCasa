using PCasa.App.Data;
using System.Text.Json;
using System.IO;

namespace PCasa.App.Services;

// Persisted logical job priorities: P0=ManualAction, P1=Background,
// P2=Idle. Intermediate legacy values are retained for technical reads
// until independent read admission is extracted from this service.
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
/// The one operation coordinator. The running work set is authoritative;
/// bounded executors provide technical capacity, not independent job order.
/// Only one logical catalog owner may mutate at a time. Gallery reads keep
/// their independent capacity.
/// </summary>
public sealed class PCasaScheduler : IAsyncDisposable
{
    public const string ContinuousReviewKey = "automatic:continuous-review";
    private const int MaximumHistory = 512;
    // Catalog admission is by logical owner, never by priority or work type.
    // Technical reads/probes retain bounded, independent capacity.
    private const int ExecutorCatalog = 0;
    private const int ExecutorViewer = 1;
    private const int ExecutorThumbnail = 2;
    private const int ExecutorCatalogRead = 3;
    private const int ExecutorSourceProbe = 4;
    private const int ExecutorManualSourceProbe = 5;
    private const int ExecutorInteractiveSourceProbe = 6;
    private const int ExecutorGalleryRead = 7;
    private readonly object _gate = new();
    private DurablePackageJournal? _durableJournal;
    private SchedulerTaskOrderStore? _taskOrderStore;
    private readonly Dictionary<string,long> _userDispatchOrder =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string,SchedulerPriority> _userPriorityOverrides =
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
    // In-flight durable owners, keyed by the SAME existing package identity.
    // A pause awaits the journal continuation, not just a worker's callback.
    private readonly Dictionary<string, Task> _durableActiveTasks =
        new(StringComparer.Ordinal);
    private bool _restoringDurablePackages;
    // The controller owns deferred activation. A passive probe only reports
    // source readiness; it never starts, ranks, or resumes a package.
    private Task<(HashSet<string> Ready, HashSet<string> Reconnected)>? _deferredProbe;
    private DateTimeOffset _nextDeferredProbe = DateTimeOffset.MinValue;
    private bool _deferredRecoveryReady;
    private Action<string, Task>? _durableRestoreObserver;
    private readonly Dictionary<string, Func<string, string, SchedulerTaskPackage>> _packageFactories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pausedExternalTasks =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _releasedStartupExternalTasks =
        new(StringComparer.Ordinal);
    private readonly List<WorkItem> _pending = new();
    // All admitted executions participate in idle admission, including reads.
    private readonly HashSet<WorkItem> _runningWork = new();
    // Nested operations inherit their owning package across await/Dispatcher
    // continuations. A package must never await a child that the queue puts
    // behind another waiting package (dependency deadlock).
    private readonly AsyncLocal<WorkItem?> _currentPackage = new();
    private readonly List<SchedulerJobSnapshot> _history = new();
    // The controller admits work directly to the thread pool. These tasks
    // are tracked only to await shutdown, not to decide order or eligibility.
    private readonly List<Task> _executions = new();
    private static readonly int[] ReadCapacity = [0, 2, 2, 2, 2, 1, 1, 2];
    private readonly SemaphoreSlim _controllerWake = new(0, 1);
    private readonly Task _controller;
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _gracefulShutdownRequested;
    public bool GracefulShutdownRequested => _gracefulShutdownRequested;

    // Freeze new dispatch before waiting for already-running file mutations.
    // Never cancel an original-file writer while its atomic write is in flight.
    public void BeginGracefulShutdown()
    {
        lock (_gate)
        {
            _gracefulShutdownRequested = true;
            SignalController();
        }
    }

    public async Task PrepareGracefulShutdownAsync()
    {
        BeginGracefulShutdown();
        // Read the durable journal outside _gate; it owns its own SQLite locks.
        var fileKeys = _durableJournal?.Pending()
            .Where(p => !DurablePackageJournal.SurvivesSession(p.Type))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        Task[] writing;
        lock (_gate)
            writing = _runningWork
                .Where(item => fileKeys.Contains(item.Key))
                .Select(item => item.Completion.Task).ToArray();
        try { await Task.WhenAll(writing).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* written checkpoint is durable */ }
        catch (Exception ex) { PCasaDiagnostics.Write("Scheduler.GracefulDrain", ex); }
    }
    private long _nextId;
    private bool _disposed;
    // An OLE FileDrop is an immediate foreground gesture. Keep lower-priority
    // source/scanning work suspended only for its synchronous drag lifetime.
    // This lease belongs to the ONE scheduler; no second job/queue is built.
    private int _nativeDragInteractionCount;
    private int _externalEditInteractionCount;
    private int _maintenancePauseCount;

    private sealed class MaintenanceLease(PCasaScheduler owner) : IDisposable
    {
        private PCasaScheduler? _owner = owner;
        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndMaintenancePause();
    }

    // Maintenance is not a job: freeze new catalog mutation dispatch and
    // wait asynchronously for any existing owner to reach a safe boundary.
    // Local gallery/thumbnail reads remain independent.
    public async Task<IDisposable> AcquireMaintenancePauseAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_maintenancePauseCount != 0)
                throw new InvalidOperationException("Maintenance already active.");
            _maintenancePauseCount++;
            SignalController();
        }
        var lease = new MaintenanceLease(this);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_runningWork.Any(IsCatalogMutation))
                        return lease;
                }
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void EndMaintenancePause()
    {
        lock (_gate)
        {
            if (_maintenancePauseCount > 0) _maintenancePauseCount--;
            if (!_disposed) SignalController();
        }
    }

    private static bool IsCatalogMutation(WorkItem item) =>
        item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan or
            SchedulerWorkKind.InitialImport or SchedulerWorkKind.DatabaseBackup;

    private sealed class NativeDragLease(PCasaScheduler owner) : IDisposable
    {
        private PCasaScheduler? _owner = owner;
        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndNativeDragInteraction();
    }

    // A PCasa-owned external edit is one deliberate foreground interaction.
    // Reuse the same scheduler admission/yield gate as native drag: suspend
    // P2-P4 source/import lanes while leaving viewer/gallery read lanes alive.
    // The lease lives until PCasa commits Cancel, Save Copy or Save.
    private sealed class ExternalEditLease(PCasaScheduler owner) : IDisposable
    {
        private PCasaScheduler? _owner = owner;
        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.EndExternalEditInteraction();
    }

    public IDisposable BeginExternalEditInteraction()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _externalEditInteractionCount++;
            SignalController();
        }
        return new ExternalEditLease(this);
    }

    private void EndExternalEditInteraction()
    {
        lock (_gate)
        {
            if (_externalEditInteractionCount == 0) return;
            if (--_externalEditInteractionCount != 0) return;
            SignalController();
        }
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
            SignalController();
        }
    }

    public PCasaScheduler(SourceAvailabilityGate? sourceAvailability = null)
    {
        SourceAvailability = sourceAvailability ?? new SourceAvailabilityGate();
        _controller = Task.Run(ControlLoopAsync);
    }

    // Shared by all source-related operations and the existing background
    // monitor. This is a cached readiness decision, never a parallel job lane.
    public SourceAvailabilityGate SourceAvailability { get; }

    private bool _retireFileJobsOnSessionEnd;

    public void EnableDurablePackages(LocalDatabase database,
        bool retireFileJobsOnSessionEnd = false)
    {
        if (_durableJournal is not null)
            throw new InvalidOperationException("Paketjournal bereits aktiviert.");
        _durableJournal = new DurablePackageJournal(database);
        _durableJournal.NormalizeLegacyJobPriorities();
        _retireFileJobsOnSessionEnd = retireFileJobsOnSessionEnd;
        if (_retireFileJobsOnSessionEnd)
            _durableJournal.RetireUnfinishedFileJobs();
        _durableJournal.ReleaseStartupPauses();
        _taskOrderStore = new SchedulerTaskOrderStore(database);
        _taskOrderStore.NormalizeLegacyJobPriorities();
        foreach (var key in _taskOrderStore.ReleaseStartupPauses())
            _releasedStartupExternalTasks.Add(key);
        foreach (var key in _taskOrderStore.ReadPaused())
            _pausedExternalTasks.Add(key);
        foreach (var (key, placement) in _taskOrderStore.ReadAll())
        {
            if (placement.Order != long.MaxValue)
                _userDispatchOrder[key] = placement.Order;
            if (placement.PriorityOverride is SchedulerPriority chosen)
                _userPriorityOverrides[key] = chosen;
        }
    }

    public bool PromoteTaskNow(string key)
    {
        if (key == ContinuousReviewKey) return false;
        var store = _taskOrderStore ??
            throw new InvalidOperationException("Taskmanager-Reihenfolge nicht aktiviert.");
        List<WorkItem> changed = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pausedExternalTasks.Contains(key)) return false;
            // This is a control-plane mutation. The existing worker sees
            // its new effective rank; no second package is queued.
            store.PromoteNow(key);
            foreach (var (identity, placement) in store.ReadAll())
            {
                if (placement.Order != long.MaxValue)
                    _userDispatchOrder[identity] = placement.Order;
                if (placement.PriorityOverride is SchedulerPriority rank)
                    _userPriorityOverrides[identity] = rank;
                else
                    _userPriorityOverrides.Remove(identity);
            }
            foreach (var item in _pending.Concat(_runningWork))
            {
                var identity = item.Key;
                if (!_userDispatchOrder.TryGetValue(identity, out var order))
                    continue;
                var effective = _userPriorityOverrides.GetValueOrDefault(
                    identity, item.Priority);
                if (item.Priority == effective && item.QueueOrder == order)
                    continue;
                item.Priority = effective;
                item.QueueOrder = order;
                SetStateUnsafe(item, item.YieldRequested
                    ? SchedulerJobState.YieldRequested :
                    _runningWork.Contains(item) ? SchedulerJobState.Running :
                    SchedulerJobState.Queued);
                changed.Add(item);
            }
            WakePendingWorkersUnsafe();
        }
        foreach (var item in changed) PublishState(item);
        NotifyTaskViewChanged();
        return true;
    }

    // Revert a one-shot Start Now after successful completion. Repeated
    // future P4 scans must fall back to idle priority, never remain P0.
    public void CompleteTaskPromotion(string key)
    {
        var store = _taskOrderStore;
        if (store is null) return;
        lock (_gate)
        {
            if (!store.ClearPromotion(key)) return;
            var saved = store.ReadAll();
            if (saved.TryGetValue(key, out var placement) &&
                placement.Order != long.MaxValue)
                _userDispatchOrder[key] = placement.Order;
            else
                _userDispatchOrder.Remove(key);
            if (saved.TryGetValue(key, out placement) &&
                placement.PriorityOverride is SchedulerPriority priority)
                _userPriorityOverrides[key] = priority;
            else
                _userPriorityOverrides.Remove(key);
            WakePendingWorkersUnsafe();
        }
        NotifyTaskViewChanged();
    }

    public bool IsExternalTaskPaused(string key)
    {
        lock (_gate) return _pausedExternalTasks.Contains(key);
    }

    public IReadOnlyList<string> PausedExternalTaskKeys()
    {
        lock (_gate) return _pausedExternalTasks.ToArray();
    }

    public IReadOnlyList<string> RecoverableExternalTaskKeys()
    {
        lock (_gate)
            return _pausedExternalTasks.Concat(_releasedStartupExternalTasks)
                .Distinct(StringComparer.Ordinal).ToArray();
    }

    public void AcknowledgeRecoveredExternalTask(string key)
    {
        lock (_gate) _releasedStartupExternalTasks.Remove(key);
    }

    public bool SetExternalTaskPaused(string key, bool paused)
    {
        var store = _taskOrderStore ??
            throw new InvalidOperationException("Taskmanager-Reihenfolge nicht aktiviert.");
        bool changed;
        lock (_gate)
        {
            changed = store.SetPaused(key, paused);
            if (paused) _pausedExternalTasks.Add(key);
            else _pausedExternalTasks.Remove(key);
            if (changed) SignalController();
        }
        if (changed) NotifyTaskViewChanged();
        return changed;
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
        Func<T,SchedulerPriority,long,T> withPlacement,
        Func<T,bool>? running = null)
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
                    Completed: completed(item),
                    Running: running?.Invoke(item) == true,
                    Priority: priority, Order: order, Key: identity);
            // Execution is shown above the waiting queue; within each
            // group the one scheduler-defined priority/order remains intact.
            // This is a VIEW only. Dispatch and persisted ranks are unchanged.
            }).OrderBy(item => item.Completed ? 2 : item.Running ? 0 : 1)
                .ThenBy(item => item.Priority)
                .ThenBy(item => item.Order)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Value)
                .ToArray();
        }
    }

    // No job-type, original-priority or lane restriction. The list must
    // contain all ACTIVE accepted tasks, including ones hidden by filters.
    // The store is the only durable place for user-selected dispatch order.
    public bool MoveTaskGlobally(string key, string targetKey,
        IReadOnlyList<SchedulerTaskOrderItem> acceptedTaskKeys)
    {
        if (key == ContinuousReviewKey || targetKey == ContinuousReviewKey)
            return false;
        acceptedTaskKeys = acceptedTaskKeys.Where(item =>
            item.Key != ContinuousReviewKey).ToArray();
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
                    work.Key, out var position))
                    continue;
                if (_userPriorityOverrides.TryGetValue(
                        work.Key, out var chosen))
                    work.Priority = chosen;
                work.QueueOrder = position;
                SetStateUnsafe(work, SchedulerJobState.Queued);
                changed.Add(work);
            }
            // Keep the exact rank on live workers as well. A yielded file
            // must reenter the same global position after its safe checkpoint.
            foreach (var active in _runningWork)
            {
                if (!_userDispatchOrder.TryGetValue(active.Key,
                        out var position)) continue;
                if (_userPriorityOverrides.TryGetValue(
                        active.Key, out var chosen))
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
                if (record.State is "deferred" or "paused") continue;
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
                        QueueDurableExisting(package, record), record.Key);
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
                if (_gracefulShutdownRequested)
                    continue; // Freeze arbitration and all preemptions mid-write.
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
                        nonIdleWaiting = durable.HasRunnableNonIdleTasks();
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
                    var highest = _pending.Where(p => !IsIndependentReadItem(p))
                        .OrderBy(p => RankOwner(p).Priority)
                        .ThenBy(p => RankOwner(p).QueueOrder)
                        .ThenBy(p => RankOwner(p).Id).FirstOrDefault();
                    foreach (var running in _runningWork)
                    {
                        if (running.YieldSource is not { } yield || running.YieldRequested) continue;
                        // The owning job requests yield. Its dependent steps
                        // must be allowed to finish the current safe boundary.
                        if (HasRunningParent(running)) continue;
                        if (!((_maintenancePauseCount > 0 && IsCatalogMutation(running)) ||
                              (RankOwner(running).Priority == SchedulerPriority.Idle &&
                               (nonIdleWaiting || _pending.Any(p =>
                                   !IsIndependentReadItem(p) &&
                                   RankOwner(p).Priority < SchedulerPriority.Idle))) ||
                              IsBlockedByInteraction(running) ||
                              (highest is not null && Precedes(highest, running))))
                            continue;
                        running.YieldRequested = true;
                        SetStateUnsafe(running, SchedulerJobState.YieldRequested);
                        viewChanged = true;
                        cancel.Add(yield);
                    }
                    // Keep filling available bounded capacity in global rank
                    // order. SelectNextJobUnsafe is the sole eligibility check.
                    var available = true;
                    while (available && !_gracefulShutdownRequested)
                    {
                        available = false;
                        for (var executor = 0; executor <= ExecutorGalleryRead; executor++)
                        {
                            // Presentation I/O is admitted independently of
                            // the journal probe and logical job arbitration.
                            if (executor is ExecutorGalleryRead or ExecutorThumbnail or
                                ExecutorViewer or ExecutorCatalogRead)
                                continue;
                            if (executor != ExecutorCatalog &&
                                _runningWork.Count(work => work.ExecutionClass == executor) >=
                                    ReadCapacity[executor]) continue;
                            var item = SelectNextJobUnsafe(executor,
                                manualWaiting, nonIdleWaiting);
                            if (item is null) continue;
                            StartExecutionUnsafe(item, executor);
                            viewChanged = true;
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

    // Called only under _gate. Read executors share lifetime/cancellation
    // bookkeeping with the coordinator, but admission is driven by read
    // capacity, not by logical job rank or the SQLite journal probe.
    // SourceProbe is deliberately excluded: some existing callers perform
    // actual filesystem mutations using that kind.
    private void DispatchIndependentReadsUnsafe()
    {
        if (_disposed || _gracefulShutdownRequested || _shutdown.IsCancellationRequested)
            return;
        foreach (var executor in new[] { ExecutorGalleryRead, ExecutorThumbnail,
                     ExecutorViewer, ExecutorCatalogRead })
        {
            while (_runningWork.Count(work => work.ExecutionClass == executor) <
                   ReadCapacity[executor])
            {
                var next = _pending.Where(item =>
                        IsIndependentReadItem(item) &&
                        ExecutorFor(item.Priority, item.Kind) == executor)
                    .OrderByDescending(HasRunningParent)
                    .ThenBy(item => item.Priority)
                    .ThenBy(item => item.QueueOrder)
                    .ThenBy(item => item.Id)
                    .FirstOrDefault();
                if (next is null) break;
                StartExecutionUnsafe(next, executor);
            }
        }
    }

    private void StartExecutionUnsafe(WorkItem item, int executor)
    {
        _pending.Remove(item);
        item.YieldRequested = false;
        item.ExecutionClass = executor;
        var yieldRequest = IsCatalogMutation(item) ? new CancellationTokenSource() : null;
        item.YieldSource = yieldRequest;
        _runningWork.Add(item);
        SetStateUnsafe(item, SchedulerJobState.Running);
        _executions.RemoveAll(task => task.IsCompletedSuccessfully);
        _executions.Add(Task.Run(() => ExecuteAsync(item, yieldRequest)));
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
                QueueDurableExisting(package, activated), key);
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
        if (record.State is "completed" or "failed" or "canceled" or "paused")
            throw new InvalidOperationException("Paketidentität bereits abgeschlossen.");
        // Explicit replay of the SAME immutable recipe after an offline
        // source comes back must first reactivate its durable journal row.
        // Otherwise the executor's isCurrent guard rejects "deferred" and
        // silently cancels this legitimate resume before its first step.
        if (record.State == "deferred")
            record = journal.RestartDeferred(package.Key) ??
                throw new InvalidOperationException("Zurückgestelltes Paket konnte nicht aktiviert werden.");
        NotifyTaskAccepted();
        package.RestoreCursor(record.CompletedSteps);
        return TrackDurableCompletion(QueueDurableExisting(package, record, cancellationToken), record.Key);
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
        return TrackDurableCompletion(completion, key);
    }

    // Durable ledger commits are part of scheduler shutdown, not detached
    // fire-and-forget continuations that outlive the database owner.
    private Task TrackDurableCompletion(Task completion, string key)
    {
        lock (_gate)
        {
            _durableCompletions.Add(completion);
            _durableActiveTasks[key] = completion;
        }
        _ = completion.ContinueWith(task =>
        {
            // Observe exceptions, but retain the original Task for callers.
            _ = task.Exception;
            lock (_gate)
            {
                _durableCompletions.Remove(task);
                if (_durableActiveTasks.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, task))
                    _durableActiveTasks.Remove(key);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion;
    }

    private async Task QueueDurableExisting(SchedulerTaskPackage package,
        DurablePackageRecord record, CancellationToken cancellationToken = default)
    {
        var journal = _durableJournal!;
        package.OnStepCompleted = step => journal.SetState(record.Key, "running", step);
        package.ShouldYieldAfterStep = () => _gracefulShutdownRequested;
        try
        {
            // A user may pause immediately after the durable INSERT, before
            // the asynchronous recipe factory has finished constructing.
            // Never admit that paused identity as fresh runnable work.
            if (journal.Pending().Any(p => p.Key == record.Key && p.State == "paused"))
                return;
            // Durable identity/priority come only from the journal; never
            // substitute a default background priority on restart.
            await EnqueuePackageAsync(package, record.Priority,
                cancellationToken, workKind: record.WorkKind,
                queueOrder: record.QueueOrder,
                isCurrent: () => journal.Pending().Any(item =>
                    item.Key == record.Key &&
                    item.State is "queued" or "running"),
                durableJob: true).ConfigureAwait(false);
            // Recovery may originate from the WPF dispatcher. Journal state
            // must be committed by the scheduler continuation even if the
            // UI thread is blocked or the process is beginning to close.
            journal.SetState(record.Key, "completed", package.CompletedSteps);
            CompleteTaskPromotion(record.Key);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is a yield, user cancellation is final. Never
            // resurrect a deliberately canceled destructive operation.
            var endingSession = _shutdown.IsCancellationRequested || _gracefulShutdownRequested;
            journal.SetState(record.Key,
                endingSession
                    ? _retireFileJobsOnSessionEnd &&
                      !DurablePackageJournal.SurvivesSession(record.Type)
                        ? "canceled" : "queued"
                    : journal.Pending().Any(p => p.Key == record.Key && p.State == "paused")
                        ? "paused" : "canceled",
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
            // An image edit is a session-bound original write, NOT a background
            // replay recipe. On a sharing violation (or any failed edit), a
            // fresh user Save must be possible after the error. Keep the
            // failure and cursor for diagnostics, but never leave a deferred
            // image-edit task that blocks the same image indefinitely.
            if (record.Type == "image-edit-v1")
            {
                PCasaDiagnostics.Write("Scheduler.ImageEditFailed:" + record.Key, ex);
                journal.SetState(record.Key, "failed", package.CompletedSteps, ex.Message);
            }
            else
            {
                PCasaDiagnostics.Write("Scheduler.PackageDeferred:" + record.Key, ex);
                journal.Defer(record.Key, package.CompletedSteps, ex.Message);
            }
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

    private static int ExecutorFor(SchedulerPriority priority, SchedulerWorkKind kind)
        => kind switch
        {
            SchedulerWorkKind.ViewerRead => ExecutorViewer,
            SchedulerWorkKind.BrowseRead => ExecutorGalleryRead,
            SchedulerWorkKind.CatalogRead => ExecutorCatalogRead,
            SchedulerWorkKind.ThumbnailRead => ExecutorThumbnail,
            SchedulerWorkKind.SourceProbe => priority switch
            {
                SchedulerPriority.ManualAction => ExecutorManualSourceProbe,
                SchedulerPriority.Interactive => ExecutorInteractiveSourceProbe,
                _ => ExecutorSourceProbe
            },
            _ => ExecutorCatalog
        };

    // Preserve existing interaction semantics without letting an execution
    // class determine priority or whether a catalog owner may run.
    private static bool IsBulkWork(WorkItem item) =>
        item.Kind == SchedulerWorkKind.InitialImport ||
        (item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan &&
         item.Priority >= SchedulerPriority.UserAction);

    private bool IsBlockedByInteraction(WorkItem item)
    {
        var bulk = IsBulkWork(item) ||
            (item.Kind == SchedulerWorkKind.SourceProbe &&
             item.Priority >= SchedulerPriority.UserAction);
        return (_nativeDragInteractionCount > 0 && bulk) ||
               (_externalEditInteractionCount > 0 &&
                (bulk || (item.Kind == SchedulerWorkKind.CatalogScan &&
                          item.Priority == SchedulerPriority.ManualAction)));
    }

    private static bool WaitsForRestoredOrder(WorkItem item) =>
        item.Kind == SchedulerWorkKind.DatabaseBackup ||
        (item.Priority == SchedulerPriority.ManualAction &&
         item.Kind is SchedulerWorkKind.Catalog or SchedulerWorkKind.CatalogScan
             or SchedulerWorkKind.SourceProbe);

    private bool HasManualSourceProbeUnsafe()
        => _runningWork.Any(item => item.ExecutionClass == ExecutorManualSourceProbe) ||
            _pending.Any(item => item.Kind == SchedulerWorkKind.SourceProbe &&
                item.Priority == SchedulerPriority.ManualAction);

    // A package is an open-ended sequence of reusable steps. It keeps its
    // cursor when a newer manual package interrupts it, and preserves the
    // SAME scheduler identity and priority through indexing and publication.
    public Task EnqueuePackageAsync(SchedulerTaskPackage package,
        SchedulerPriority priority, CancellationToken cancellationToken = default,
        SchedulerWorkKind workKind = SchedulerWorkKind.Catalog,
        long? queueOrder = null, Func<bool>? isCurrent = null,
        bool durableJob = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        return EnqueueAsync(package.Key, priority, package.ExecuteAsync,
            isCurrent: isCurrent, cancellationToken: cancellationToken,
            workKind: workKind, queueOrder: queueOrder, durableJob: durableJob);
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

    // One-click cancellation for a session-only file operation. An already
    // executing original-file mutation is allowed to finish its atomic
    // boundary before the caller removes it from the exit review.
    public async Task<bool> CancelActiveTaskAsync(string key)
    {
        WorkItem? executing;
        Task? completion;
        lock (_gate)
        {
            var waiting = _pending.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.Ordinal));
            if (waiting is not null)
            {
                _pending.Remove(waiting);
                SetStateUnsafe(waiting, SchedulerJobState.Canceled);
                waiting.Completion.TrySetCanceled();
                WakePendingWorkersUnsafe();
                return true;
            }
            executing = _runningWork.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.Ordinal));
            completion = executing?.Completion.Task;
        }
        if (executing is null) return false;
        await executing.UserCancellation.CancelAsync();
        try { await completion!; }
        catch (OperationCanceledException) { }
        return true;
    }

    // A deferred durable record can remain queued in SQLite after its
    // last in-memory executor has failed. Only the scheduler may determine
    // whether a live execution for that identity still exists.
    public bool HasLiveExecution(string key)
    {
        lock (_gate)
            return _pending.Any(item => item.Key == key) ||
                _runningWork.Any(item => item.Key == key);
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
        var task = TrackDurableCompletion(QueueDurableExisting(package, activated), key);
        try { _durableRestoreObserver?.Invoke(key, task); }
        catch (Exception ex) { PCasaDiagnostics.Write("Taskmanager.RetryObserver", ex); }
        return true;
    }

    // The controller commits PAUSED before requesting cooperative worker
    // cancellation. The journal preserves the original cursor and priority.
    public async Task<bool> PauseDurableTaskAsync(string key)
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Kein Paketjournal.");
        WorkItem? executing;
        Task? lifetime;
        lock (_gate)
        {
            if (!journal.Pause(key)) return false;
            var waiting = _pending.FirstOrDefault(item => item.Key == key);
            if (waiting is not null)
            {
                _pending.Remove(waiting);
                SetStateUnsafe(waiting, SchedulerJobState.Canceled);
                waiting.Completion.TrySetCanceled();
                WakePendingWorkersUnsafe();
            }
            executing = _runningWork.FirstOrDefault(item => item.Key == key);
            _durableActiveTasks.TryGetValue(key, out lifetime);
        }
        if (executing is not null)
        {
            // Atomic original-file writes finish at their existing safe
            // cancellation boundary, never midway through an EXIF update.
            try { await executing.UserCancellation.CancelAsync(); }
            catch (ObjectDisposedException) { }
        }
        if (lifetime is not null)
        {
            try { await lifetime; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // A concurrent IO failure may have been reported, but the
                // explicit PAUSED journal marker still owns this task.
                PCasaDiagnostics.Write("Taskmanager.PauseSettled", ex);
            }
        }
        NotifyTaskViewChanged();
        return true;
    }

    // Resume reconstructs the SAME recipe using the persisted step cursor.
    // It cannot run alongside a still-active worker; Pause awaits its exit.
    public bool ResumeDurableTask(string key)
    {
        var journal = _durableJournal ??
            throw new InvalidOperationException("Kein Paketjournal.");
        lock (_gate)
        {
            if (_durableActiveTasks.TryGetValue(key, out var previous))
            {
                if (!previous.IsCompleted)
                    return false; // previous worker still settling
                _durableActiveTasks.Remove(key);
            }
        }
        var record = journal.Pending().FirstOrDefault(row =>
            row.Key == key && row.State == "paused");
        if (record is null) return false;
        if (!_packageFactories.TryGetValue(record.Type, out var factory))
            throw new InvalidOperationException("Wiederherstellungsrezept fehlt.");
        var package = factory(record.Key, record.Payload);
        package.RestoreCursor(record.CompletedSteps);
        var resumed = journal.ResumePaused(key);
        if (resumed is null) return false;
        var task = TrackDurableCompletion(QueueDurableExisting(package, resumed), key);
        try { _durableRestoreObserver?.Invoke(key, task); }
        catch (Exception ex) { PCasaDiagnostics.Write("Taskmanager.ResumeObserver", ex); }
        NotifyTaskViewChanged();
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
                _runningWork.Any(item => item.Key == key && IsCatalogMutation(item)))
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
            running = _runningWork.FirstOrDefault(item => item.Key == key);
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

            var work = _runningWork.FirstOrDefault(item => item.Key == key);
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
        long? queueOrder = null, string? parentKey = null,
        bool durableJob = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(operation);
        Task completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var identity = key;
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
                    DurableJob = durableJob,
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

            if (IsIndependentRead(workKind))
                DispatchIndependentReadsUnsafe();
            else
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

    private static bool IsIndependentRead(SchedulerWorkKind kind) =>
        kind is SchedulerWorkKind.BrowseRead or SchedulerWorkKind.ThumbnailRead or
            SchedulerWorkKind.ViewerRead or SchedulerWorkKind.CatalogRead;

    private static bool IsIndependentReadItem(WorkItem item) =>
        IsIndependentRead(item.Kind);

    // Durable mutation packages and catalog inventories are logical jobs.
    // A worker lane is not permission to overlap a second logical job.
    // Owned dependent steps are part of the same job, never a second owner.
    private bool HasRunningParent(WorkItem item) =>
        item.Parent is { } parent && _runningWork.Contains(parent);

    // Called under _gate. A new P0 requests a yield from the older owner;
    // admission follows only after that owner reaches a safe boundary.
    // Source probes and view presentation are technical operations.
    private WorkItem? SelectNextJobUnsafe(int executor,
        bool durableManualPending, bool durableNonIdlePending)
    {
        var candidates = _pending
            .Where(x => ExecutorFor(x.Priority, x.Kind) == executor);
        if (executor == ExecutorCatalog)
        {
            // An active owner can finish its own dependent catalog steps,
            // but no different owner can start before the safe boundary.
            candidates = candidates.Where(candidate =>
                !_runningWork.Any(active => IsCatalogMutation(active) &&
                    !ReferenceEquals(RankOwner(active), RankOwner(candidate))));
        }
        var candidate = candidates
            .OrderByDescending(HasRunningParent)
            .ThenBy(x => RankOwner(x).Priority)
            .ThenBy(x => RankOwner(x).QueueOrder)
            .ThenBy(x => RankOwner(x).Id)
            .FirstOrDefault();
        if (candidate is null) return null;

        var childOfRunningPackage = HasRunningParent(candidate);
        var ownedCatalogStep = childOfRunningPackage &&
            _runningWork.Any(active => IsCatalogMutation(active) &&
                ReferenceEquals(RankOwner(active), RankOwner(candidate)));
        if (_maintenancePauseCount > 0 && IsCatalogMutation(candidate) &&
            !ownedCatalogStep)
            return null;

        // Dependent work must reach its owner's safe boundary, even when
        // a higher-ranked task or a maintenance/interaction hold is waiting.
        if (ownedCatalogStep || (childOfRunningPackage &&
                                 !IsCatalogMutation(candidate)))
            return candidate;

        if (_restoringDurablePackages && WaitsForRestoredOrder(candidate))
            return null;
        if (IsBlockedByInteraction(candidate))
            return null;

        // Idle verification yields to runnable requests. Deferred or paused
        // journal entries remain visible but do not reserve the worker.
        if (RankOwner(candidate).Priority == SchedulerPriority.Idle &&
            (durableNonIdlePending ||
             _pending.Any(other => !ReferenceEquals(other, candidate) &&
                 !IsIndependentReadItem(other) &&
                 RankOwner(other).Priority < SchedulerPriority.Idle) ||
             _runningWork.Any(other => !IsIndependentReadItem(other) &&
                 RankOwner(other).Priority < SchedulerPriority.Idle)))
            return null;

        // One package owns its dependent operations. They must never wait
        // behind an unrelated queued P0 while the owner awaits them.
        if (!childOfRunningPackage &&
            _pending.Any(other => !ReferenceEquals(other, candidate) &&
                !IsIndependentReadItem(other) && Precedes(other, candidate)))
            return null;
        // Manual P0 packages form an actual LIFO interruption stack.
        // P1-P3 may run on independent read/executor capacity while an
        // older higher-priority operation is already in flight. P4 is
        // explicitly stricter and was checked above against ALL running jobs.
        if (!childOfRunningPackage &&
            RankOwner(candidate).Priority == SchedulerPriority.ManualAction &&
            _runningWork.Any(active =>
                !IsIndependentReadItem(active) &&
                RankOwner(active).Priority == SchedulerPriority.ManualAction &&
                Precedes(active, candidate)))
            return null;

        // Journal recovery may have an accepted P0 package not yet copied
        // into the in-memory run queue. It still wins over P2-P4 execution.
        if (!childOfRunningPackage &&
            durableManualPending &&
            RankOwner(candidate).Priority > SchedulerPriority.ManualAction)
            return null;

        // Source preflight retains its existing protection while it is
        // running or queued. Catalog-owner exclusion above replaces the
        // redundant foreground/background mutation counters.
        if (!childOfRunningPackage &&
            IsBulkWork(candidate) && HasManualSourceProbeUnsafe())
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

    private async Task ExecuteAsync(WorkItem item, CancellationTokenSource? yieldRequest)
    {
        // The presenter must learn that the worker actually started,
        // even if the first SQLite/SMB/ExifTool call is still blocked
        // and has not yet reached its first ReportProgress boundary.
        PublishState(item);
        bool interrupted = false, resume = false;
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
                if (_gracefulShutdownRequested)
                    throw new OperationCanceledException(
                        "Application exit: preserve pending durable work.");
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
                interrupted = true;
                if (item.YieldRequested && !_gracefulShutdownRequested &&
                    !_shutdown.IsCancellationRequested &&
                    !item.CancellationToken.IsCancellationRequested &&
                    !item.UserCancellation.IsCancellationRequested)
                {
                    try { resume = item.IsCurrent is null || item.IsCurrent(); }
                    catch { /* Startup/monitor reconciliation can retry. */ }
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
                    _runningWork.Remove(item);
                    item.YieldSource = null;
                    item.ExecutionClass = -1;
                    // Remove the old assignment and requeue under the
                    // same lock. A priority change can select a different
                    // executor; it must never see the same item both
                    // pending and running or lose its new assignment.
                    if (interrupted)
                    {
                        if (resume && !_disposed && !_gracefulShutdownRequested &&
                            !_shutdown.IsCancellationRequested &&
                            !item.CancellationToken.IsCancellationRequested &&
                            !item.UserCancellation.IsCancellationRequested)
                        {
                            item.YieldRequested = false;
                            _pending.Add(item);
                            SetStateUnsafe(item, SchedulerJobState.Queued);
                        }
                        else
                        {
                            SetStateUnsafe(item, SchedulerJobState.Canceled);
                            item.Completion.TrySetCanceled();
                        }
                    }
                    if (IsIndependentReadItem(item))
                        DispatchIndependentReadsUnsafe();
                    else
                        WakePendingWorkersUnsafe();
                }
                if (interrupted) PublishState(item);
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
        Task[] executing;
        lock (_gate)
        {
            foreach (var item in _pending)
            {
                SetStateUnsafe(item, SchedulerJobState.Canceled);
                item.Completion.TrySetCanceled();
            }
            _pending.Clear();
            executing = _executions.ToArray();
        }
        await Task.WhenAll(executing);
        // Worker completion can unblock the journal continuation AFTER the
        // worker exits. Do not close the app while a checkpoint is in flight.
        Task[] durable;
        lock (_gate) durable = _durableCompletions.ToArray();
        try { await Task.WhenAll(durable); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        if (_retireFileJobsOnSessionEnd)
            _durableJournal?.RetireUnfinishedFileJobs();
        _shutdown.Dispose();
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
        public bool DurableJob { get; init; }
        public CancellationTokenSource UserCancellation { get; } = new();
        public SchedulerPriority Priority { get; set; } = priority;
        public SchedulerWorkKind Kind { get; } = kind;
        // Assignment metadata belongs to this running item, not a second
        // lane-indexed registry of logical jobs. Access under _gate.
        public int ExecutionClass { get; set; } = -1;
        public CancellationTokenSource? YieldSource { get; set; }
        public bool YieldRequested { get; set; }
        public long LastProgressNoticeTick { get; set; }
        public Func<CancellationToken, Task> Operation { get; } = operation;
        public Func<bool>? IsCurrent { get; } = isCurrent;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
