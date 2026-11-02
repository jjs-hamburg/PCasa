using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// One shared, session-local timeout estimator for native Tag and GPS writes.
/// Measurements are ExifTool completion times, not VPN line-speed estimates.
/// The P0 scheduler retains sole responsibility for retries and packages.
/// </summary>
public sealed class AdaptiveMetadataWriteTimeout
{
    private const double MiB = 1024d * 1024d;
    private const int MaxSamplesPerRoot = 8;
    private const int MaxTrackedRoots = 64;
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<double>> _secondsPerMiB =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A Windows drive root or one UNC share. DriveType checks are OS-local
    /// and do not open, list or benchmark any remote file. Separate models
    /// are maintained for local and mapped SMB sources.
    /// </summary>
    public static string SourceKey(string originalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPath);
        var root = Path.GetPathRoot(Path.GetFullPath(originalPath))
            ?? throw new ArgumentException("Original has no filesystem root.", nameof(originalPath));
        var network = root.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType == DriveType.Network;
        return (network ? "SMB:" : "LOCAL:") + root;
    }

    /// <summary>Return the maximum time to await one native write response,
    /// never an artificial delay. The fallback is Build #992's safe size-
    /// based allowance; successful recent writes adapt it up or down.</summary>
    public TimeSpan GetTimeout(string sourceKey, long sourceBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
        var mib = Math.Max(1d, sourceBytes / MiB);
        var baseline = Math.Min(1800d, 120d + Math.Ceiling(sourceBytes / MiB) * 30d);
        lock (_gate)
        {
            if (!_secondsPerMiB.TryGetValue(sourceKey, out var samples) ||
                samples.Count == 0)
                return TimeSpan.FromSeconds(baseline);

            // Conservative slowest recent *successful* throughput. A recent
            // slower network immediately raises the budget, while a faster
            // network must demonstrate sustained improvement before lowering.
            var worst = samples.Max();
            var predicted = 90d + 3d * worst * mib;
            // Never shrink the initial safe SMB budget by over 50 percent.
            // A sudden VPN slowdown still has breathing room.
            var seconds = Math.Clamp(Math.Max(baseline * 0.5d, predicted),
                90d, 1800d);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public void RecordSuccess(string sourceKey, long sourceBytes, TimeSpan elapsed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
        if (elapsed <= TimeSpan.Zero || !double.IsFinite(elapsed.TotalSeconds))
            return;
        var mib = Math.Max(1d, sourceBytes / MiB);
        var observed = Math.Max(0.001d, elapsed.TotalSeconds / mib);
        lock (_gate)
        {
            if (!_secondsPerMiB.TryGetValue(sourceKey, out var samples))
            {
                // Source roots are few in practice. Avoid unbounded memory
                // from arbitrary or malformed paths without affecting writes.
                if (_secondsPerMiB.Count >= MaxTrackedRoots) return;
                samples = new Queue<double>(MaxSamplesPerRoot);
                _secondsPerMiB.Add(sourceKey, samples);
            }
            if (samples.Count == MaxSamplesPerRoot) samples.Dequeue();
            samples.Enqueue(observed);
        }
    }
}
