using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

public sealed class BackgroundScanSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa",
        "scan-settings.json");

    public static event EventHandler? Changed;

    public bool Enabled { get; set; } = true;

    public static BackgroundScanSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new BackgroundScanSettings();
            }

            return JsonSerializer.Deserialize<BackgroundScanSettings>(
                       File.ReadAllText(SettingsPath))
                   ?? new BackgroundScanSettings();
        }
        catch
        {
            return new BackgroundScanSettings();
        }
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        catch
        {
            // A preference write failure must never prevent PCasa from running.
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
