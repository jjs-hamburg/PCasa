using PCasa.App.Models;

namespace PCasa.App.Services;

// Keep Windows paths and mapped-drive identities exactly as before.
// Unix hosts keep their native separators; volume identity mapping remains
// a platform responsibility rather than an image decoder concern.
public static class CatalogPathPolicy
{
    public static string NormalizeSeparators(string path)
        => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    public static string ResolveCanonicalFullPath(MonitoredFolder folder, string fullLocalPath)
    {
        var normalizedLocal = Path.GetFullPath(fullLocalPath);
        if (string.Equals(folder.LocalPath, folder.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            return NormalizeSeparators(normalizedLocal);
        var suffix = Path.GetRelativePath(folder.LocalPath, normalizedLocal);
        return NormalizeSeparators(suffix == "." ? folder.CanonicalPath :
            Path.Combine(folder.CanonicalPath, suffix));
    }
}
