using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

public enum ViewerReturnPosition
{
    OpeningPosition,
    CurrentImage
}

// Applies to the image viewer and the future slideshow alike.
public enum PhotoSequenceScope
{
    CurrentFolder,
    Gallery
}

public sealed class PhotoViewerSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa",
        "viewer-settings.json");

    public ViewerReturnPosition ReturnPosition { get; set; } = ViewerReturnPosition.OpeningPosition;
    public PhotoSequenceScope SequenceScope { get; set; } = PhotoSequenceScope.Gallery;

    public static PhotoViewerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<PhotoViewerSettings>(
                    File.ReadAllText(SettingsPath)) ?? new PhotoViewerSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new PhotoViewerSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
