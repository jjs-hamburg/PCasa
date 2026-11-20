using System.IO;
using System.Text.Json;

namespace PCasa.App.Services;

// Presentation-only slideshow choices. The image sequence remains owned by
// PhotoViewerSettings/PhotoSequence and is NOT duplicated here.
public sealed class SlideshowSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "slideshow-settings.json");

    public int IntervalSeconds { get; set; } = 5;
    public bool PreloadNext { get; set; } = true;
    public string Transition { get; set; } = "projector";
    public int TransitionMilliseconds { get; set; } = 700;
    public string DisplayMode { get; set; } = "gallery";
    public int GalleryPercent { get; set; } = 85;
    // Gallery means the actual PCasa gallery brush, including any texture.
    // No second texture file/name is stored.
    public string BackgroundMode { get; set; } = "gallery";
    public string? CustomBackgroundPath { get; set; }
    public bool WhiteBorder { get; set; } = true;
    public int BorderWidth { get; set; } = 10;
    public bool Shadow { get; set; } = true;
    public int ShadowWidth { get; set; } = 26;
    public int ShadowOffset { get; set; } = 12;
    public int ShadowOpacity { get; set; } = 60;

    public SlideshowSettings Normalize()
    {
        IntervalSeconds = Math.Clamp(IntervalSeconds, 1, 3600);
        TransitionMilliseconds = Math.Clamp(TransitionMilliseconds, 0, 3000);
        GalleryPercent = Math.Clamp(GalleryPercent, 50, 100);
        BorderWidth = Math.Clamp(BorderWidth, 0, 100);
        ShadowWidth = Math.Clamp(ShadowWidth, 0, 150);
        ShadowOffset = Math.Clamp(ShadowOffset, 0, 100);
        ShadowOpacity = Math.Clamp(ShadowOpacity, 0, 100);
        Transition = Transition is "none" or "fade" or "zoom" or "slide" or "projector"
            ? Transition : "projector";
        DisplayMode = DisplayMode is "gallery" or "fullscreen"
            ? DisplayMode : "gallery";
        BackgroundMode = BackgroundMode is "gallery" or "black" or "custom"
            ? BackgroundMode : "gallery";
        return this;
    }

    public SlideshowSettings Copy() => (SlideshowSettings)MemberwiseClone();

    public static SlideshowSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<SlideshowSettings>(
                    File.ReadAllText(FilePath)) ?? new SlideshowSettings()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException) { PCasaDiagnostics.Write("Slideshow.Settings", ex); }
        return new SlideshowSettings();
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
