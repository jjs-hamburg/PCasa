using System.IO;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// Bounded, best-effort diagnostics. Operational network unavailability is
/// not a fault and must not be written here. The persistent original-write
/// failure ledger is independent of this rotating diagnostic history.
/// </summary>
internal static class PCasaDiagnostics
{
    internal const int MaxFileBytes = 1024 * 1024;
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "logs", "pcasa-errors.log");

    private static readonly object Sync = new();
    private static bool _initialized;
    private static string? _previousEntry;
    private static int _repeated;

    public static void WriteMessage(string source, string message)
        => Append($"[{source}] {message.TrimEnd()}");

    public static void Write(string source, Exception exception)
    {
        // Only unexpected failures include a stack trace. Files skipped by
        // the scanner use WriteMessage(path, reason) instead.
        Append($"[{source}]\n{exception}");
    }

    private static void Append(string entry)
    {
        try
        {
            lock (Sync)
            {
                EnsureInitialized();
                // Collapse IDENTICAL consecutive errors without discarding
                // the first occurrence. Later unrelated messages flush the
                // omitted count. No timer, queue or asynchronous logger.
                if (string.Equals(_previousEntry, entry, StringComparison.Ordinal))
                {
                    _repeated++;
                    return;
                }
                FlushRepeated();
                WriteEntry(entry);
                _previousEntry = entry;
            }
        }
        catch
        {
            // Failure to log must never block shutdown or file processing.
        }
    }

    private static void FlushRepeated()
    {
        if (_repeated <= 0) return;
        WriteEntry($"[Diagnostics] Previous identical entry repeated {_repeated} more time(s).");
        _repeated = 0;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        // Handle oversized logs left by earlier releases. Retain the MOST
        // RECENT full lines, never preserve an unlimited legacy archive.
        foreach (var file in new[] { LogPath, Archive(1), Archive(2) })
            TrimOversizedFile(file);
        _initialized = true;
    }

    private static void TrimOversizedFile(string path)
    {
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read);
        if (stream.Length <= MaxFileBytes) return;
        var bytes = new byte[MaxFileBytes];
        stream.Seek(-MaxFileBytes, SeekOrigin.End);
        stream.ReadExactly(bytes);
        // The cut can start in the middle of a UTF-8 character or line.
        // Drop the partial first line to preserve readable diagnostics.
        var start = Array.IndexOf(bytes, (byte)'\n') + 1;
        if (start == 0) start = bytes.Length; // One overlong entry: omit.
        stream.SetLength(0);
        stream.Position = 0;
        stream.Write(bytes.AsSpan(start));
    }

    private static string Archive(int index) => Archive(LogPath, index);

    private static string Archive(string path, int index)
        => Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.{index}.log");

    private static void Rotate(string path)
    {
        if (File.Exists(Archive(path, 2))) File.Delete(Archive(path, 2));
        if (File.Exists(Archive(path, 1)))
            File.Move(Archive(path, 1), Archive(path, 2));
        if (File.Exists(path))
            File.Move(path, Archive(path, 1));
    }

    private static void WriteEntry(string entry)
        => WriteEntryToPath(LogPath, entry);

    // Same actual writer path is used by production and the isolated
    // regression test with a temporary sandbox file.
    private static void WriteEntryToPath(string path, string entry)
    {
        var line = $"{DateTimeOffset.Now:O} {entry}\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length > MaxFileBytes)
        {
            // A pathological exception must never break the size cap.
            line = line[..Math.Min(line.Length, MaxFileBytes / 2)] +
                "\n[Diagnostics] Entry truncated.\n";
            bytes = Encoding.UTF8.GetBytes(line);
        }
        if (File.Exists(path) &&
            new FileInfo(path).Length + bytes.Length > MaxFileBytes)
            Rotate(path);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.Read);
        stream.Write(bytes);
    }
}
