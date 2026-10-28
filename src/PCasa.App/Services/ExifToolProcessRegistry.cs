using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCasa.App.Services;

// Tracks only ExifTool processes started by PCasa. A single exit barrier
// prevents late starts and kills all owned children after scheduled writes
// have finished. Never enumerate or kill another application's ExifTool.
internal static class ExifToolProcessRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<Process> Owned = new();
    private static bool _stopping;
    // A Windows job object owns EVERY native ExifTool descendant. Closing the
    // handle kills them even when PCasa exits abnormally before WPF cleanup.
    private static SafeFileHandle? _processJob;

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
            _processJob?.Dispose();
            _processJob = CreateKillOnCloseJob();
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
            if (_processJob is { IsInvalid: false, IsClosed: false } job &&
                !AssignProcessToJobObject(job, child.Handle))
                PCasaDiagnostics.WriteMessage("ExifTool.Job",
                    $"PID {child.Id} could not join Windows process job: {Marshal.GetLastWin32Error()}");
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
            // Job close is the final OS-level fallback. Try the normal
            // per-child kill first so errors can be logged with their PIDs.
            foreach (var child in Owned.ToArray())
                KillAndRelease(child);
            _processJob?.Dispose();
            _processJob = null;
            // Job close can have terminated a child whose first Kill failed.
            foreach (var child in Owned.ToArray())
                if (!IsAlive(child) && Owned.Remove(child))
                    child.Dispose();
        }
    }

    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(
        IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, uint informationClass,
        ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job, IntPtr process);

    private static SafeFileHandle? CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                job.Dispose();
                PCasaDiagnostics.WriteMessage("ExifTool.Job",
                    "Windows CreateJobObject failed: " + Marshal.GetLastWin32Error());
                return null;
            }
            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                    ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                PCasaDiagnostics.WriteMessage("ExifTool.Job",
                    "Windows SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
                job.Dispose();
                return null;
            }
            return job;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("ExifTool.Job", ex);
            return null; // Existing owned-process registry still provides cleanup.
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
