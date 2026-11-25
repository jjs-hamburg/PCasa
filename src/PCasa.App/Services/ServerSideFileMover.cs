using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace PCasa.App.Services;

/// <summary>
/// The only physical move implementation. Windows must attempt a rename
/// without MOVEFILE_COPY_ALLOWED: an SMB rename inside one share is executed
/// by the file server, without streaming photo bytes through a VPN client.
/// A true cross-volume error is the ONLY permission to copy as fallback.
/// </summary>
public static class ServerSideFileMover
{
    public enum MoveMethod { Rename, CrossVolumeCopy }
    private const int ErrorNotSameDevice = 17;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int MaxTransientRetries = 12;
    private const uint MoveFileReplaceExisting = 0x1;
    // MOVEFILE_COPY_ALLOWED (0x2) is deliberately never specified.

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    // Unix rename APIs must NOT silently fall back to a copy (as File.Move
    // may do). No-replace is atomic and fails closed on unsupported filesystems.
    private const int UnixCrossDevice = 18;
    private const int LinuxAtFdcwd = -100;
    private const uint LinuxRenameNoReplace = 1;
    private const uint MacRenameExclusive = 0x00000004;

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int LinuxRename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int LinuxRenameAt2(int oldDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source, int newDirectory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "renamex_np", SetLastError = true)]
    private static extern int MacRename(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    private static int TryUnixRename(string source, string destination, bool replace)
    {
        int result;
        try
        {
            if (OperatingSystem.IsLinux())
                result = replace ? LinuxRename(source, destination) :
                    LinuxRenameAt2(LinuxAtFdcwd, source, LinuxAtFdcwd,
                        destination, LinuxRenameNoReplace);
            else if (OperatingSystem.IsMacOS())
                result = MacRename(source, destination, replace ? 0 : MacRenameExclusive);
            else
                throw new PlatformNotSupportedException("Atomic file rename is unavailable.");
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException("Atomic file rename is unavailable.", ex);
        }
        return result == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    private static IOException UnixRenameError(int error)
        => new IOException($"Datei konnte nicht serverseitig verschoben werden ({error}).");

    public static MoveMethod Move(string source, string destination,
        bool replace = false, string? stagingPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = TryUnixRename(source, destination, replace);
            if (error == 0) return MoveMethod.Rename;
            if (error != UnixCrossDevice) throw UnixRenameError(error);
            return TransferAcrossVolumes(source, destination, replace, stagingPath, cancellationToken);
        }
        var flags = replace ? MoveFileReplaceExisting : 0;
        // A newly created destination can be briefly opened by a scanner
        // or native image reader without FILE_SHARE_DELETE. MoveFileEx may
        // report ACCESS_DENIED (5), or sharing/lock violation (32/33).
        // Retry the SAME server-side rename only; never copy as a fallback
        // for a same-volume failure. Persistent permission errors still fail.
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MoveFileEx(source, destination, flags))
                return MoveMethod.Rename;
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotSameDevice) break;
            if ((error is ErrorAccessDenied or ErrorSharingViolation or
                    ErrorLockViolation) && retry < MaxTransientRetries)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(250, 80 + retry * 30));
                if (cancellationToken.WaitHandle.WaitOne(delay))
                    cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            throw new IOException(
                $"Datei konnte nicht serverseitig verschoben werden ({error}): " +
                new Win32Exception(error).Message,
                unchecked((int)(0x80070000u | (uint)error)));
        }

        return TransferAcrossVolumes(source, destination, replace, stagingPath, cancellationToken);
    }

    private static MoveMethod TransferAcrossVolumes(string source, string destination,
        bool replace, string? stagingPath, CancellationToken cancellationToken)
    {
        // Different volumes require physical transfer. Stage the entire file
        // on the destination volume, verify size, then rename there. Never
        // touch the original until the staged destination is complete.
        var stage = stagingPath ?? destination + ".pcasa-" +
            Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // A durable caller owns its deterministic stage and may safely
            // retry an interrupted partial copy without touching the target.
            if (stagingPath is not null && File.Exists(stage)) File.Delete(stage);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(source, stage, overwrite: false);
            if (new FileInfo(source).Length != new FileInfo(stage).Length)
                throw new IOException("Kopiervorgang unvollständig; Quelldatei bleibt erhalten.");
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
                File.Move(stage, destination, overwrite: replace);
            else
            {
                var error = TryUnixRename(stage, destination, replace);
                if (error != 0) throw UnixRenameError(error);
            }
            File.Delete(source);
            return MoveMethod.CrossVolumeCopy;
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }
}
