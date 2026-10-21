using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

public sealed class FolderTreeDisplaySettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "folder-tree.json");

    public bool Compressed { get; set; }

    public static FolderTreeDisplaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<FolderTreeDisplaySettings>(
                    File.ReadAllText(SettingsPath)) ?? new FolderTreeDisplaySettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new FolderTreeDisplaySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
