using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Single owner of the most recent scan state for each directory. Aggregate
/// states are a derived view, never a separate history of failures.
/// </summary>
public sealed class FolderScanStatusTracker
{
    public readonly record struct State(
        string Code, DateTime UpdatedUtc, bool Recursive, long Sequence,
        bool RescanPending = false, bool PendingRecursive = false);

    private readonly Dictionary<string, State> _current =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, State> _aggregate =
        new(StringComparer.OrdinalIgnoreCase);
    // Verification is separate from current activity, but owned by this
    // SAME status tracker. A new scan must not erase evidence that a prior
    // complete recursive inventory succeeded.
    private readonly Dictionary<string, DateTime> _verifiedRecursiveRoots =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _aggregateDirty = true;
    private long _revision;

    public void Set(string path, string code, bool recursive = false)
    {
        var normalized = Normalize(path);
        var now = DateTime.UtcNow;
        var sequence = ++_revision;
        var hasPrevious = _current.TryGetValue(normalized, out var previous);

        if (code == "Pending" && hasPrevious && previous.Code == "Working")
        {
            // A watcher may enqueue another change while this exact folder
            // is being scanned. Do not let the running scan erase that request.
            _current[normalized] = previous with
            {
                RescanPending = true,
                PendingRecursive = previous.PendingRecursive || recursive
            };
            _aggregateDirty = true;
            return;
        }

        var startedSequence = hasPrevious && previous.Code == "Working" && previous.Recursive
            ? previous.Sequence : sequence;

        if (code == "Ready" && recursive)
        {
            // A successful recursive scan supersedes results that were already
            // old when the scan began; newer results and queued work survive.
            foreach (var descendant in _current
                .Where(entry => !entry.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                    && IsAtOrBelow(entry.Key, normalized)
                    && (entry.Value.Code is "Ready" or "Failed")
                    && entry.Value.Sequence <= startedSequence)
                .Select(entry => entry.Key).ToArray())
            {
                _current.Remove(descendant);
            }
        }

        if (code == "Ready" && recursive)
            _verifiedRecursiveRoots[normalized] = now;

        var effectiveCode = code == "Ready" && hasPrevious &&
                            previous.Code == "Working" && previous.RescanPending
            ? "Pending" : code;
        var effectiveRecursive = effectiveCode == "Pending" && code == "Ready"
            ? previous.PendingRecursive : recursive;
        _current[normalized] = new State(effectiveCode, now, effectiveRecursive, sequence);
        _aggregateDirty = true;
    }

    public void Forget(string path)
    {
        var normalized = Normalize(path);
        foreach (var key in _current.Keys
            .Where(key => IsAtOrBelow(key, normalized)).ToArray())
            _current.Remove(key);
        foreach (var key in _verifiedRecursiveRoots.Keys
            .Where(key => IsAtOrBelow(key, normalized)).ToArray())
            _verifiedRecursiveRoots.Remove(key);
        _aggregateDirty = true;
    }

    public State? Get(string path)
    {
        var normalized = Normalize(path);
        if (_aggregateDirty) RebuildAggregate();
        State? result = _aggregate.TryGetValue(normalized, out var current)
            ? current : null;

        // A parent working through 20,000 files is NOT executing all its
        // descendant folders at the same time. The running icon belongs to
        // the exact active directory, or to ancestors of that active job
        // (aggregated above); never propagate it DOWN the entire tree.
        //
        // Conversely, a completed recursive import proves that every
        // included descendant was checked. That confirmation survives later
        // scans of other directories and is a baseline when no specific
        // newer change is pending for this child.
        var ancestor = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrWhiteSpace(ancestor))
        {
            ancestor = Normalize(ancestor);
            if (_verifiedRecursiveRoots.TryGetValue(ancestor, out var verifiedAt) &&
                result is null)
                result = new State("Ready", verifiedAt, true, 0);
            if (_current.TryGetValue(ancestor, out var inherited) &&
                inherited.Recursive)
            {
                if (inherited.Code == "Ready" && result is null)
                    result = inherited;
                else if (inherited.Code is "Pending" or "Working" &&
                    result is null)
                {
                    // Descendant is waiting for the parent's ongoing pass,
                    // not itself being scanned by the worker.
                    result = inherited with { Code = "Pending" };
                }
            }
            var parent = Path.GetDirectoryName(ancestor);
            if (string.IsNullOrWhiteSpace(parent) ||
                parent.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
                break;
            ancestor = parent;
        }

        return result;
    }

    private void RebuildAggregate()
    {
        _aggregate.Clear();
        _aggregateDirty = false;
        foreach (var (path, state) in _current)
        {
            var current = path;
            while (!string.IsNullOrWhiteSpace(current))
            {
                // "Ready" never rises from a single child to its parent:
                // other siblings may be entirely unscanned. Only failures,
                // pending work or live work aggregate upward.
                if ((current.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                     state.Code is not ("Ready" or "Unknown")) &&
                    (!_aggregate.TryGetValue(current, out var previous) ||
                     Rank(state.Code) > Rank(previous.Code) ||
                     (Rank(state.Code) == Rank(previous.Code) &&
                      state.Sequence > previous.Sequence)))
                    _aggregate[current] = state;

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                    break;
                current = Normalize(parent);
            }
        }
    }

    private static int Rank(string code) => code switch
    {
        "Failed" => 5,
        "Offline" => 4,
        "Working" => 3,
        "Pending" => 2,
        "Ready" => 1,
        _ => 0
    };

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Replace('/', '\\');

    private static bool IsAtOrBelow(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + "\\",
            StringComparison.OrdinalIgnoreCase);
}
