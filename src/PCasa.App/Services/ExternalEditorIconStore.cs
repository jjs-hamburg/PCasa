using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Import a configured editor's executable icon ONCE into PCasa's local
/// application data. Opening the image editor reads only the local PNG,
/// never the executable on disk/NAS or through antivirus scanning.
/// </summary>
internal static class ExternalEditorIconStore
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "external-editor-icons");

    private static string CachePath(string executable)
    {
        var normalized = Path.GetFullPath(executable).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Path.Combine(CacheDirectory, Convert.ToHexString(hash) + ".png");
    }

    public static bool IsCached(string executable)
    {
        try { return File.Exists(CachePath(executable)); }
        catch (Exception ex) when (ex is IOException or ArgumentException
            or NotSupportedException) { return false; }
    }

    public static BitmapSource? Load(string executable)
    {
        try
        {
            var path = CachePath(executable);
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            PCasaDiagnostics.Write("ExternalEditor.CachedIcon", ex);
            return null;
        }
    }

    public static void EnsureCaptured(string executable)
    {
        if (IsCached(executable)) return;
        Capture(executable);
    }

    public static void Capture(string executable)
    {
        if (!File.Exists(executable))
            throw new FileNotFoundException("Editor executable missing", executable);
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
        if (icon is null) return;
        using var bitmap = icon.ToBitmap();
        Directory.CreateDirectory(CacheDirectory);
        var target = CachePath(executable);
        var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            bitmap.Save(tmp, System.Drawing.Imaging.ImageFormat.Png);
            File.Move(tmp, target, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public static void RemoveUnused(IReadOnlyCollection<ExternalEditorProgram> programs)
    {
        if (!Directory.Exists(CacheDirectory)) return;
        var keep = programs.Select(program => CachePath(program.ExecutablePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.png"))
        {
            if (!keep.Contains(file))
                File.Delete(file);
        }
    }
}
