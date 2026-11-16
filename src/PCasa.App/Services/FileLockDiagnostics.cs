using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// Failure-only Windows file-lock diagnostics. Observes, but never closes
/// handles, kills processes, changes ACLs, or retries a write. A Restart
/// Manager owner is only a candidate; SMB servers and drivers may not report
/// any process even when a sharing violation is real.
/// </summary>
public static class FileLockDiagnostics
{
    private const int ErrorMoreData = 234;
    private const int MaxOwners = 32;
    private const int MaxPaths = 3;

    public static void ReportFailure(string operation, Exception error,
        params string[] paths)
    {
        try
        {
            PCasaDiagnostics.WriteMessage("FileLock",
                DescribeFailure(operation, error, paths));
        }
        catch (Exception diagnosticError)
        {
            // The original exception remains authoritative.
            PCasaDiagnostics.Write("FileLock.Diagnostics", diagnosticError);
        }
    }

    // Also used by the real Windows smoke test: report observable evidence,
    // not a guessed reason such as OneDrive or the PCasa viewer.
    public static string DescribeFailure(string operation, Exception error,
        params string[] paths)
    {
        var builder = new StringBuilder();
        var code = error.HResult & 0xffff;
        builder.Append("Operation=").Append(operation)
            .Append("; PID=").Append(Environment.ProcessId)
            .Append("; HRESULT=0x").Append(error.HResult.ToString("X8"))
            .Append("; Win32=").Append(code)
            .Append("; Error=").Append(error.GetType().Name)
            .Append(": ").Append(error.Message.Replace('\r', ' ').Replace('\n', ' '));
        var index = 0;
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPaths))
        {
            builder.AppendLine();
            builder.Append("File[").Append(++index).Append("]=").Append(path);
            builder.Append("; ").Append(Probe(path));
            builder.Append("; ").Append(FindProcessOwners(path));
        }
        return builder.ToString();
    }

    private static string Probe(string path)
    {
        try
        {
            if (!File.Exists(path)) return "present=no";
            var attrs = File.GetAttributes(path);
            var info = new FileInfo(path);
            var sb = new StringBuilder();
            sb.Append("present=yes; bytes=").Append(info.Length)
                .Append("; attributes=").Append(attrs)
                .Append("; exclusive-read=").Append(TryOpen(path, FileAccess.Read))
                .Append("; exclusive-write=").Append(TryOpen(path, FileAccess.ReadWrite));
            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "probe-unavailable=" + ex.GetType().Name +
                "(0x" + ex.HResult.ToString("X8") + ")";
        }
    }

    private static string TryOpen(string path, FileAccess access)
    {
        try
        {
            // Non-mutating test: OPEN an existing file, never truncate, create
            // or write bytes. Share.None checks that no incompatible handle is
            // currently open. Exclusive-write can also fail due to permissions.
            using var handle = new FileStream(path, FileMode.Open, access, FileShare.None);
            return "ok";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "failed(0x" + ex.HResult.ToString("X8") + ")";
        }
    }

    private static string FindProcessOwners(string path)
    {
        if (!OperatingSystem.IsWindows()) return "owners=not-windows";
        uint handle = 0;
        try
        {
            var sessionKey = new StringBuilder(33);
            var status = RmStartSession(out handle, 0, sessionKey);
            if (status != 0) return "owners=restart-manager-error-" + status;
            status = RmRegisterResources(handle, 1, [path], 0, IntPtr.Zero,
                0, IntPtr.Zero);
            if (status != 0) return "owners=register-error-" + status;
            uint required = 0, provided = 0, rebootReasons = 0;
            status = RmGetList(handle, out required, ref provided,
                [], ref rebootReasons);
            if (status == 0 && required == 0)
                return "owners=none-reported(not-proof-of-no-lock)";
            if (status != ErrorMoreData)
                return "owners=query-error-" + status;
            provided = Math.Min(required, MaxOwners);
            var owners = new RmProcessInfo[provided];
            status = RmGetList(handle, out required, ref provided, owners,
                ref rebootReasons);
            if (status != 0 && status != ErrorMoreData)
                return "owners=query-error-" + status;
            var labels = owners.Take((int)Math.Min(provided, (uint)owners.Length))
                .Where(info => info.Process.ProcessId > 0)
                .Select(info => info.ApplicationName + "(PID=" +
                    info.Process.ProcessId + ")")
                .Distinct()
                .ToArray();
            return "owners=" + (labels.Length == 0
                ? "none-reported(not-proof-of-no-lock)"
                : string.Join(", ", labels)) +
                (required > MaxOwners ? " [more omitted]" : "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or
            EntryPointNotFoundException or Win32Exception or
            UnauthorizedAccessException or IOException or
            System.Runtime.InteropServices.ExternalException)
        {
            return "owners=unavailable(" + ex.GetType().Name + ")";
        }
        finally
        {
            if (handle != 0)
            {
                try { RmEndSession(handle); }
                catch { /* diagnostics must not mask save failure */ }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceName;
        public int ApplicationType;
        public uint ApplicationStatus;
        public uint TerminalSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle,
        int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle,
        uint filesCount, [MarshalAs(UnmanagedType.LPArray,
            ArraySubType = UnmanagedType.LPWStr)] string[] fileNames,
        uint appCount, IntPtr apps, uint serviceCount, IntPtr services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle,
        out uint needed, ref uint count, [In, Out] RmProcessInfo[] owners,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);
}
