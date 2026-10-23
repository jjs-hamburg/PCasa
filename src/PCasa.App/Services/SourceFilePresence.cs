using System.IO;

namespace PCasa.App.Services;

public enum FilePresence { Present, ConfirmedMissing, Unknown }

/// <summary>
/// Shared filesystem truth policy for reconciliation. File.Exists() is
/// unsafe as negative evidence on SMB: it also returns false for some
/// access/network failures. Only an explicit FileNotFoundException inside
/// a reachable parent proves absence; everything else is uncertain.
/// </summary>
public static class SourceFilePresence
{
    public static FilePresence Probe(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            return FilePresence.Unknown;

        try
        {
            _ = File.GetAttributes(path);
            return FilePresence.Present;
        }
        catch (FileNotFoundException) { return FilePresence.ConfirmedMissing; }
        catch (DirectoryNotFoundException) { return FilePresence.Unknown; }
        catch (UnauthorizedAccessException) { return FilePresence.Unknown; }
        catch (IOException) { return FilePresence.Unknown; }
    }
}
