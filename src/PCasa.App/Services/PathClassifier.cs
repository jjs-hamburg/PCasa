using System.IO;
using System.Runtime.InteropServices;

namespace PCasa.App.Services;

public sealed record PathClassification(string LocalPath, string CanonicalPath, bool IsNetwork);

public static class PathClassifier
{
    private const int UniversalNameInfoLevel = 1;
    private const int ErrorMoreData = 234;

    public static PathClassification Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Der Ordnerpfad darf nicht leer sein.", nameof(path));
        }

        var normalizedInput = NormalizeLocalPath(path);

        if (normalizedInput.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new PathClassification(normalizedInput, NormalizeUncPath(normalizedInput), true);
        }

        var root = Path.GetPathRoot(normalizedInput);
        if (string.IsNullOrWhiteSpace(root))
        {
            return new PathClassification(normalizedInput, normalizedInput, false);
        }

        var drive = new DriveInfo(root);
        if (drive.DriveType != DriveType.Network)
        {
            return new PathClassification(normalizedInput, normalizedInput, false);
        }

        var unc = TryGetUniversalName(normalizedInput);
        return new PathClassification(
            normalizedInput,
            unc is null ? normalizedInput : NormalizeUncPath(unc),
            true);
    }

    private static string NormalizeLocalPath(string path)
    {
        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);

        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeUncPath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    private static string? TryGetUniversalName(string localPath)
    {
        var bufferSize = 0;
        var result = WNetGetUniversalName(localPath, UniversalNameInfoLevel, IntPtr.Zero, ref bufferSize);
        if (result != ErrorMoreData || bufferSize <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = WNetGetUniversalName(localPath, UniversalNameInfoLevel, buffer, ref bufferSize);
            if (result != 0)
            {
                return null;
            }

            var info = Marshal.PtrToStructure<UniversalNameInfo>(buffer);
            return Marshal.PtrToStringUni(info.UniversalName);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniversalNameInfo
    {
        public IntPtr UniversalName;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetUniversalName(
        string localPath,
        int infoLevel,
        IntPtr buffer,
        ref int bufferSize);
}
