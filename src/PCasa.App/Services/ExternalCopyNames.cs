using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// One collision rule for gallery duplication and external Save Copy:
/// original stem and actual output extension; only a real name collision
/// adds -Kopie, -Kopie-2, ... . Never overwrite an existing file.
/// </summary>
public static class ExternalCopyNames
{
    public static string Candidate(string desired, int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0) return desired;
        var folder = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var extension = Path.GetExtension(desired);
        var suffix = index == 1 ? "-Kopie" : "-Kopie-" + index;
        return Path.Combine(folder, stem + suffix + extension);
    }

    public static string NextAvailable(string desired,
        IReadOnlySet<string>? reserved = null, bool requireCopySuffix = false)
    {
        for (var i = requireCopySuffix ? 1 : 0; i < 100000; i++)
        {
            var path = Candidate(desired, i);
            if (!File.Exists(path) && (reserved is null || !reserved.Contains(path)))
                return path;
        }
        throw new IOException("Kein freier Dateiname für die Kopie.");
    }
}
