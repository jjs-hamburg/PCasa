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
    private const uint MoveFileReplaceExisting = 0x1;
    // MOVEFILE_COPY_ALLOWED (0x2) is deliberately never specified.

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);

    public static MoveMethod Move(string source, string destination,
        bool replace = false)
    {
        var flags = replace ? MoveFileReplaceExisting : 0;
        if (MoveFileEx(source, destination, flags))
            return MoveMethod.Rename;

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotSameDevice)
            throw new IOException(
                $"Datei konnte nicht serverseitig verschoben werden ({error}): " +
                new Win32Exception(error).Message,
                Marshal.GetHRForLastWin32Error());

        // Different volumes require physical transfer. Stage the entire file
        // on the destination volume, verify size, then rename there. Never
        // touch the original until the staged destination is complete.
        var stage = destination + ".pcasa-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, stage, overwrite: false);
            if (new FileInfo(source).Length != new FileInfo(stage).Length)
                throw new IOException("Kopiervorgang unvollständig; Quelldatei bleibt erhalten.");
            File.Move(stage, destination, overwrite: replace);
            File.Delete(source);
            return MoveMethod.CrossVolumeCopy;
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }
}
