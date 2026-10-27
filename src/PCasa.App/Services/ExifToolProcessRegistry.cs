using System.Diagnostics;
using System.IO;

namespace PCasa.App.Services;

// Tracks only ExifTool processes started by PCasa. A single exit barrier
// prevents late starts and kills all owned children after scheduled writes
// have finished. Never enumerate or kill another application's ExifTool.
internal static class ExifToolProcessRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<Process> Owned = new();
    private static bool _stopping;

    static ExifToolProcessRegistry()
        => AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();

    // A fresh MainWindow may be created after a completed window shutdown
    // (e.g. an in-process WPF restart test). Reopen only when no child from
    // the previous session remains. Shutdown never revives itself.
    internal static void BeginSession()
    {
        lock (Gate)
        {
            if (Owned.Any(IsAlive))
                throw new InvalidOperationException(
                    "Die vorige ExifTool-Sitzung ist noch nicht beendet.");
            _stopping = false;
        }
    }

    internal static int LiveCount
    {
        get { lock (Gate) return Owned.Count(IsAlive); }
    }

    internal static Process Start(ProcessStartInfo info)
    {
        lock (Gate)
        {
            if (_stopping)
                throw new OperationCanceledException("PCasa wird beendet.");
            var child = Process.Start(info)
                ?? throw new IOException("ExifTool konnte nicht gestartet werden.");
            Owned.Add(child);
            return child;
        }
    }

    internal static void Retire(Process child)
    {
        lock (Gate) KillAndRelease(child);
    }

    internal static void StopAll()
    {
        lock (Gate)
        {
            _stopping = true;
            foreach (var child in Owned.ToArray())
                KillAndRelease(child);
        }
    }

    private static bool IsAlive(Process child)
    {
        try { return !child.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    // Under Gate from both callers. No handle is forgotten while the native
    // process still lives; a later ProcessExit pass may retry termination.
    private static void KillAndRelease(Process child)
    {
        try
        {
            if (IsAlive(child))
            {
                try { child.Kill(entireProcessTree: true); }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (IsAlive(child)) child.Kill();
                }
                if (!child.WaitForExit(3000))
                    PCasaDiagnostics.WriteMessage("ExifTool.Process",
                        $"ExifTool PID {child.Id} did not exit.");
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex)
        {
            PCasaDiagnostics.Write("ExifTool.Process", ex);
        }
        catch (AggregateException ex)
        {
            PCasaDiagnostics.Write("ExifTool.ProcessTree", ex);
        }
        finally
        {
            if (!IsAlive(child) && Owned.Remove(child))
                child.Dispose();
        }
    }
}
