using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Bounded per-source locks for in-place metadata writes, source decoding,
/// duplication and deletion. File operations on unrelated photos must never
/// hold CatalogRemovalService.CacheLock across slow SMB / ExifTool calls.
/// A fixed stripe set avoids retaining 500,000 path objects indefinitely.
/// </summary>
public static class SourceFileLocks
{
    private static readonly object[] Gates =
        Enumerable.Range(0, 1021).Select(_ => new object()).ToArray();

    public static object ForPath(string path)
    {
        var canonical = Path.GetFullPath(path).Replace('/', '\\');
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(canonical) & int.MaxValue;
        return Gates[hash % Gates.Length];
    }
}
