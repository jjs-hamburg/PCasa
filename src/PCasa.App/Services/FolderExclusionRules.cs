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

    public static bool IsExcluded(
        string path, IReadOnlyCollection<string> exclusions)
        => exclusions.Any(excluded => IsAtOrBelow(path, excluded));
}
