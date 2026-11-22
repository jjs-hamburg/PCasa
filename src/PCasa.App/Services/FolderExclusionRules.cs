using System.IO;
using PCasa.App.Data;
using PCasa.App.Models;

namespace PCasa.App.Services;

/// <summary>
/// Exact directory boundaries matter: excluding C:\Photos\A must never
/// exclude C:\Photos\Albums. Rules apply equally to mapped drives and UNC.
/// The scanner loads the rules at the start of each run.
/// </summary>
public static class FolderExclusionRules
{
    public static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path).Replace('/', '\\'));

    public static bool IsAtOrBelow(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(r.EndsWith('\\') ? r : r + "\\",
                StringComparison.OrdinalIgnoreCase);
    }

    // A nested monitored root owns its own subtree. Never allow an outer
    // root to override an inner root's exclusion rule or generation guard.
    public static MonitoredFolder? FindOwner(
        string path, IEnumerable<MonitoredFolder> monitoredRoots)
        => monitoredRoots
            .Where(root => IsAtOrBelow(path, root.LocalPath))
            .OrderByDescending(root => Normalize(root.LocalPath).Length)
            .FirstOrDefault();

    // The most specific stored rule wins. This allows a child to be
    // deliberately included inside an excluded ancestor (and vice versa).
    public static bool IsExcluded(
        string path, IReadOnlyCollection<string> exclusions,
        IReadOnlyCollection<string>? inclusions = null)
    {
        var normalized = Normalize(path);
        var deepest = -1;
        var excluded = false;
        foreach (var rule in exclusions)
        {
            var root = Normalize(rule);
            if (root.Length > deepest && IsAtOrBelow(normalized, root))
            {
                deepest = root.Length;
                excluded = true;
            }
        }
        if (inclusions is not null)
        {
            foreach (var rule in inclusions)
            {
                var root = Normalize(rule);
                if (root.Length > deepest && IsAtOrBelow(normalized, root))
                {
                    deepest = root.Length;
                    excluded = false;
                }
            }
        }
        return excluded;
    }

    // An excluded ancestor still needs minimal DIRECTORY traversal when
    // a specifically re-included descendant is reachable below it.
    public static bool MayContainIncludedDescendant(
        string directory, IReadOnlyCollection<string> inclusions)
        => inclusions.Any(path => IsAtOrBelow(path, directory));
}
