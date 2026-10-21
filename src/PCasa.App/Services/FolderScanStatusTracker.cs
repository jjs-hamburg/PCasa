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
        _aggregateDirty = true;
    }

    public State? Get(string path)
    {
        var normalized = Normalize(path);
        if (_aggregateDirty) RebuildAggregate();
        State? result = _aggregate.TryGetValue(normalized, out var current)
            ? current : null;

        // A newer recursive parent pass (including a newly queued one)
        // supersedes OLD completed child statuses. Keep fresh failures and
        // explicit pending/working child jobs until they themselves finish.
        var ancestor = Path.GetDirectoryName(normalized);
        while (!string.IsNullOrWhiteSpace(ancestor))
        {
            ancestor = Normalize(ancestor);
            if (_current.TryGetValue(ancestor, out var inherited) &&
                inherited.Recursive &&
                (inherited.Code is "Working" or "Pending" or "Ready") &&
                (result is null ||
                 ((result.Value.Code is "Ready" or "Failed") &&
                  inherited.Sequence > result.Value.Sequence)))
                result = inherited;
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
                if (!_aggregate.TryGetValue(current, out var previous) ||
                    Rank(state.Code) >= Rank(previous.Code))
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
