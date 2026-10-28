using System.Collections.Concurrent;
using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Source reachability is a property of a physical drive/share, not of each
/// image. Scheduler-owned and shared by manual jobs, background monitor and
/// original-file writers. A lost VPN is probed once per cooldown for the WHOLE
/// source; other roots continue without waiting for the SMB timeout.
/// This is a readiness gate only: no worker, queue, retry timer or side effects.
/// The existing durable scheduler remains the sole retry coordinator.
/// </summary>
public sealed class SourceAvailabilityGate
{
    private readonly ConcurrentDictionary<string, SourceState> _sources =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, bool> _probeRoot;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _offlineInterval;
    private readonly TimeSpan _onlineInterval;
    private readonly TimeSpan _failedFileProbeInterval;

    public SourceAvailabilityGate(Func<string, bool>? probeRoot = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? offlineInterval = null,
        TimeSpan? onlineInterval = null)
    {
        _probeRoot = probeRoot ?? Directory.Exists;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _offlineInterval = offlineInterval ?? TimeSpan.FromSeconds(30);
        _onlineInterval = onlineInterval ?? TimeSpan.FromSeconds(30);
        _failedFileProbeInterval = TimeSpan.FromSeconds(30);
    }

    private sealed class SourceState
    {
        internal bool HasResult;
        internal bool Reachable;
        internal bool Probing;
        internal DateTimeOffset NextProbeUtc;
        internal DateTimeOffset NextErrorProbeUtc;
    }

    public static string RootOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetPathRoot(Path.GetFullPath(path))
            ?? throw new ArgumentException("Source has no filesystem root.", nameof(path));
    }

    public bool CanAccess(string path, CancellationToken token = default,
        bool force = false)
    {
        token.ThrowIfCancellationRequested();
        var root = RootOf(path);
        var state = _sources.GetOrAdd(root, _ => new SourceState());
        var now = _now();
        lock (state)
        {
            if (!force && state.HasResult && now < state.NextProbeUtc)
                return state.Reachable;
            // Never queue additional SMB probes behind a blocked one. The
            // caller can defer itself; the scheduler tries again later.
            if (state.Probing)
                return state.HasResult && state.Reachable;
            state.Probing = true;
        }

        bool reachable;
        try
        {
            token.ThrowIfCancellationRequested();
            reachable = _probeRoot(root);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (state) state.Probing = false;
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reachable = false;
        }
        catch
        {
            lock (state) state.Probing = false;
            throw;
        }

        bool changed;
        lock (state)
        {
            changed = state.HasResult && state.Reachable != reachable;
            state.HasResult = true;
            state.Reachable = reachable;
            state.NextProbeUtc = _now() +
                (reachable ? _onlineInterval : _offlineInterval);
            state.Probing = false;
        }
        if (changed || !reachable)
            PCasaDiagnostics.WriteMessage("Scheduler.SourceAvailability",
                $"Root={root}; Reachable={reachable}; next check after " +
                (reachable ? _onlineInterval : _offlineInterval));
        return reachable;
    }

    // An original-file error does NOT by itself prove that the whole drive
    // is disconnected. Confirm its root, at most once per cooldown, before
    // suppressing all other operations against that root.
    public bool CheckAfterSourceError(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = RootOf(path);
        var state = _sources.GetOrAdd(root, _ => new SourceState());
        lock (state)
        {
            if (state.HasResult && !state.Reachable &&
                _now() < state.NextProbeUtc)
                return false;
            if (_now() < state.NextErrorProbeUtc)
                return !state.HasResult || state.Reachable;
            state.NextErrorProbeUtc = _now() + _failedFileProbeInterval;
        }
        return CanAccess(path, token, force: true);
    }

    public void ConfirmAvailable(string path)
    {
        var root = RootOf(path);
        var state = _sources.GetOrAdd(root, _ => new SourceState());
        lock (state)
        {
            state.HasResult = true;
            state.Reachable = true;
            state.NextProbeUtc = _now() + _onlineInterval;
        }
    }
}
