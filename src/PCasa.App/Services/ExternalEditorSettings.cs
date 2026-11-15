using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

/// <summary>
/// Configured independent applications, not PCasa plug-ins. No executable is
/// installed or launched implicitly. Paths belong in the user's profile.
/// A format selector accepts photo, video, *, or extensions (.jpg;.arw).
/// </summary>
public sealed record ExternalEditorProgram(
    string Name, string ExecutablePath, string Formats = "photo")
{
    public bool Matches(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(ExecutablePath)) return false;
        var extension = Path.GetExtension(sourcePath);
        var filters = Formats.Split([',', ';', ' ', '\t', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var filter in filters)
        {
            if (filter == "*" || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
                return true;
            if (filter.Equals("video", StringComparison.OrdinalIgnoreCase) &&
                VideoFormats.IsVideo(sourcePath)) return true;
            if (filter.Equals("photo", StringComparison.OrdinalIgnoreCase) &&
                !VideoFormats.IsVideo(sourcePath)) return true;
            var ext = filter.StartsWith("*.") ? filter[1..] :
                filter.StartsWith('.') ? filter : "." + filter;
            if (ext.Equals(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public sealed class ExternalEditorSettings
{
    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "external-editors.json");

    public List<ExternalEditorProgram> Programs { get; set; } = [];

    public static ExternalEditorSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new ExternalEditorSettings();
            var settings = JsonSerializer.Deserialize<ExternalEditorSettings>(
                File.ReadAllText(ConfigPath)) ?? new ExternalEditorSettings();
            settings.Programs ??= [];
            return settings;
        }
        catch (Exception)
        {
            // An invalid profile must never start unexpected programs.
            return new ExternalEditorSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        if (Programs.Any(p => string.IsNullOrWhiteSpace(p.Name) ||
            p.Name.Length > 100 || p.ExecutablePath.Length > 1024 ||
            p.Formats.Length > 512))
            throw new InvalidDataException("Ungültiger externer Bildeditor.");
        var temporary = ConfigPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<ExternalEditorProgram> ForFile(string path) =>
        Programs.Where(p => p.Matches(path)).ToArray();
}

/// <summary>
/// A unique local working directory prevents any external application from
/// changing PCasa's original (especially RAW/NAS files). Exports are imported
/// only through the existing durable copy/package after explicit confirmation.
/// </summary>
public static class ExternalEditorWorkspace
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "ExternalEdits");

    public static async Task<(string Folder, string WorkingCopy)> StageAsync(
        string original,
        IProgress<(long Done, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullOriginal = Path.GetFullPath(original);
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        var workCopy = Path.Combine(folder, Path.GetFileName(fullOriginal));
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            try
            {
                using var input = new FileStream(fullOriginal, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(workCopy, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                var total = input.Length;
                long copied = 0;
                var buffer = new byte[1024 * 1024];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var n = input.Read(buffer, 0, buffer.Length);
                    if (n == 0) break;
                    output.Write(buffer, 0, n);
                    copied += n;
                    if ((copied & 0x3fffff) < n || copied == total)
                        progress?.Report((copied, total));
                }
                output.Flush(true);
            }
            catch
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { }
                throw;
            }
        }, cancellationToken);
        return (folder, workCopy);
    }
}
