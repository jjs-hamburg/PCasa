using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Diagnostic log for faults that would otherwise be lost from an async void
/// background operation or a file-system watcher callback. Never depends on
/// the monitored drives being accessible.
/// </summary>
internal static class PCasaDiagnostics
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "logs", "pcasa-errors.log");

    private static readonly object Sync = new();

    public static void Write(string source, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:O} [{source}]\n{exception}\n\n");
            }
        }
        catch
        {
            // Logging must never trigger another crash.
        }
    }
}
