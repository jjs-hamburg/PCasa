using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

/// <summary>
/// Configured independent applications, not PCasa plug-ins. No executable is
/// installed or launched implicitly. Paths belong in the user's profile.
/// A format selector accepts photo, video, *, or extensions (.jpg;.png).
/// RAW is excluded even if a wildcard or explicit RAW extension is configured.
/// </summary>
public sealed record ExternalEditorProgram(
    string Name, string ExecutablePath, string Formats = "photo",
    bool Panorama = false, bool Psd = false)
{
    public bool Matches(string sourcePath)
    {
        // RAW is viewable/catalogued but cannot be edited in this release.
        // Reject BEFORE matching photo, wildcard or explicit extensions.
        if (Panorama || RawImageSource.IsRawPath(sourcePath)) return false;
        if (Path.GetExtension(sourcePath).Equals(".psd", StringComparison.OrdinalIgnoreCase))
            return Psd && !string.IsNullOrWhiteSpace(Name) &&
                   !string.IsNullOrWhiteSpace(ExecutablePath);
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

    public IReadOnlyList<ExternalEditorProgram> ForPanorama() =>
        Programs.Where(p => p.Panorama &&
            !string.IsNullOrWhiteSpace(p.Name) &&
            !string.IsNullOrWhiteSpace(p.ExecutablePath)).ToArray();
}
