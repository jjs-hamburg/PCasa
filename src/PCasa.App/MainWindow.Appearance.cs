using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed class AppearanceState
    {
        public string Theme { get; set; } = "Dark";
        public string Background { get; set; } = "Auto";
        public string FolderStatusMode { get; set; } = "Problems";
        public bool HideCollapsedFolders { get; set; }
        public double ThumbnailSpacing { get; set; } = 10;
        public bool? ShowThumbnailShadows { get; set; }
        public bool RestoreGalleryPosition { get; set; }
        public double LastScrollOffset { get; set; }
        public string? LastFolderCanonicalPath { get; set; }
        public double LastFolderRelativeOffset { get; set; }
    }

    private readonly string _appearanceFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "appearance.json");

    private AppearanceState _appearance = new();
    private string? _sidebarMode;
    private readonly SidebarInfoRequestGate _sidebarInfoRequests = new();
    private bool _galleryPositionRestored;

    private void InitializeAppearance()
    {
        try
        {
            if (File.Exists(_appearanceFile))
                _appearance = JsonSerializer.Deserialize<AppearanceState>(
                    File.ReadAllText(_appearanceFile)) ?? new AppearanceState();
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Appearance.Load", ex); }
        if (_appearance.ShowThumbnailShadows is null)
        {
            // One-time, non-destructive migration from the old separate
            // ui-settings.json; appearance.json is now the sole UI settings owner.
            try
            {
                var legacy = Path.Combine(
                    Path.GetDirectoryName(_appearanceFile)!, "ui-settings.json");
                if (File.Exists(legacy))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
                    if (doc.RootElement.TryGetProperty("ShowThumbnailShadows", out var v) &&
                        v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        _appearance.ShowThumbnailShadows = v.GetBoolean();
                }
            }
            catch (Exception ex) { PCasaDiagnostics.Write("Appearance.LegacyMigration", ex); }
            _appearance.ShowThumbnailShadows ??= false;
            SaveAppearance();
        }
        ApplyAppearance();
        SystemEvents.UserPreferenceChanged += AppearanceUserPreferenceChanged;
        Closed += AppearanceWindowClosed;
    }

    private void AppearanceWindowClosed(object? sender, EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= AppearanceUserPreferenceChanged;
    }

    private void AppearanceUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_appearance.Theme != "System" || Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.BeginInvoke(ApplyAppearance);
    }

    private void SaveAppearance()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_appearanceFile)!);
            File.WriteAllText(_appearanceFile, JsonSerializer.Serialize(_appearance));
        }
        catch (Exception ex) { PCasaDiagnostics.Write("Appearance.Save", ex); }
    }

    private void AppearanceTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        _appearance.Theme = mode;
        ApplyAppearance();
        SaveAppearance();
    }

    private void AppearanceBackground_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        _appearance.Background = mode;
        ApplyAppearance();
        SaveAppearance();
    }

    private void FolderStatusMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        _appearance.FolderStatusMode = mode;
        ApplyFolderStatusMode();
        HideCollapsedFoldersMenu.IsChecked = _appearance.HideCollapsedFolders;
        SaveAppearance();
    }

    private void AppearanceRestorePosition_Click(object sender, RoutedEventArgs e)
    {
        _appearance.RestoreGalleryPosition = RestoreGalleryPositionMenu.IsChecked;
        SaveAppearance();
    }

    private void ApplyAppearance()
    {
        var mode = _appearance.Theme;
        ThemeLightMenu.IsChecked = mode == "Light";
        ThemeDarkMenu.IsChecked = mode == "Dark";
        ThemeSystemMenu.IsChecked = mode == "System";
        var useLight = mode == "Light" || (mode == "System" && IsWindowsLightTheme());
        SetColor("PcasaWindowBrush", useLight ? "#F5F5F2" : "#202122");
        SetColor("PcasaTopBrush", useLight ? "#E9E9E7" : "#1A1B1D");
        SetColor("PcasaPanelBrush", useLight ? "#F0F0EE" : "#252628");
        SetColor("PcasaPanelAltBrush", useLight ? "#E7E7E5" : "#2B2C2F");
        SetColor("PcasaTextBrush", useLight ? "#212121" : "#E7E7E7");
        SetColor("PcasaMutedTextBrush", useLight ? "#606064" : "#A9AAAC");
        SetColor("PcasaDividerBrush", useLight ? "#CFCFCD" : "#383A3D");
        SetColor("PcasaSelectionBrush", useLight ? "#BBD8EB" : "#42474B");
        SetColor("PcasaHoverBrush", useLight ? "#E0EBF4" : "#303235");
        var actualBackground = _appearance.Background == "Auto"
            ? (useLight ? "Light" : "Dark") : _appearance.Background;
        BrowseSurface.SetLightTheme(actualBackground is "Light" or "Canvas");

        GalleryBackgroundAutoMenu.IsChecked = _appearance.Background == "Auto";
        GalleryBackgroundDarkMenu.IsChecked = _appearance.Background == "Dark";
        GalleryBackgroundLightMenu.IsChecked = _appearance.Background == "Light";
        GalleryBackgroundCanvasMenu.IsChecked = _appearance.Background == "Canvas";
        GalleryBackgroundCanvasDarkMenu.IsChecked = _appearance.Background == "CanvasDark";
        RestoreGalleryPositionMenu.IsChecked = _appearance.RestoreGalleryPosition;
        ApplyFolderStatusMode();
        BrowseSurface.SetThumbnailSpacing(_appearance.ThumbnailSpacing);
        var background = actualBackground switch
        {
            "Light" => new SolidColorBrush(Color.FromRgb(239, 238, 234)),
            "Canvas" => MakeCanvasBrush(light: true),
            "CanvasDark" => MakeCanvasBrush(light: false),
            _ => new SolidColorBrush(Color.FromRgb(32, 33, 34))
        };
        BrowseScrollViewer.Background = background;
        BrowseSurface.SetGalleryBackground(background);
    }

    private static bool IsWindowsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0)) != 0;
        }
        catch { return false; }
    }

    private static void SetColor(string resource, string hex)
    {
        // Never mutate an existing WPF resource brush: StaticResource lookups
        // can auto-freeze Freezables even if the original dictionary said Freeze=False.
        // DynamicResource consumers pick up the replacement automatically.
        Application.Current.Resources[resource] =
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private static Brush MakeCanvasBrush(bool light)
    {
        var background = light ? Color.FromRgb(234, 229, 217) : Color.FromRgb(48, 47, 44);
        var thread = light ? Color.FromArgb(4, 120, 111, 91) : Color.FromArgb(5, 220, 213, 195);
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(background), null,
            new RectangleGeometry(new Rect(0, 0, 12, 12))));
        var pen = new Pen(new SolidColorBrush(thread), 0.6);
        foreach (var n in new[] { 1d, 3d, 5d, 7d, 9d, 11d })
        {
            group.Children.Add(new GeometryDrawing(null, pen,
                new LineGeometry(new Point(0, n), new Point(12, n))));
            group.Children.Add(new GeometryDrawing(null, pen,
                new LineGeometry(new Point(n, 0), new Point(n, 12))));
        }
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 12, 12),
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private void SaveGalleryPosition()
    {
        if (!_appearance.RestoreGalleryPosition) return;
        var scrollOffset = BrowseScrollViewer.VerticalOffset;
        _appearance.LastScrollOffset = scrollOffset;
        if (BrowseSurface.GetFolderIdAtOffset(scrollOffset + 4) is long folderId &&
            BrowseSurface.GetFolderOffset(folderId) is double folderTop)
        {
            var folder = _photoFolders.FirstOrDefault(item => item.Id == folderId);
            if (folder is not null)
            {
                _appearance.LastFolderCanonicalPath = folder.CanonicalPath;
                _appearance.LastFolderRelativeOffset = Math.Max(0, scrollOffset - folderTop);
            }
        }
        SaveAppearance();
    }

    private void RestoreGalleryPositionOnce()
    {
        if (_galleryPositionRestored || !_appearance.RestoreGalleryPosition || _browseIndex.Count == 0)
            return;
        _galleryPositionRestored = true;
        var target = _appearance.LastScrollOffset;
        if (_appearance.LastFolderCanonicalPath is { Length: > 0 } path &&
            _photoFolders.FirstOrDefault(folder =>
                string.Equals(folder.CanonicalPath, path, StringComparison.OrdinalIgnoreCase)) is { } folder &&
            BrowseSurface.GetFolderOffset(folder.Id) is double folderTop)
            target = folderTop + _appearance.LastFolderRelativeOffset;
        BrowseScrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
    }

    private void CloseSidebar_Click(object sender, RoutedEventArgs e)
    {
        QueueGalleryAnchorRestore();
        _sidebarInfoRequests.Invalidate();
        _sidebarMode = null;
        InfoSidebarDivider.Visibility = Visibility.Collapsed;
        InfoSidebarPanel.Visibility = Visibility.Collapsed;
        InfoSidebarDividerColumn.Width = new GridLength(0);
        InfoSidebarColumn.Width = new GridLength(0);
        RestoreAnchorAfterSidebarLayout();
    }

    private async void SidebarMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        QueueGalleryAnchorRestore();
        if (_sidebarMode == mode)
        {
            CloseSidebar_Click(sender, e);
            return;
        }

        _sidebarMode = mode;
        InfoSidebarDividerColumn.Width = new GridLength(1);
        InfoSidebarColumn.Width = new GridLength(245);
        InfoSidebarDivider.Visibility = Visibility.Visible;
        InfoSidebarPanel.Visibility = Visibility.Visible;
        RestoreAnchorAfterSidebarLayout();
        TagSidebar.Visibility = mode == "Tags" ? Visibility.Visible : Visibility.Collapsed;
        if (mode == "Tags") RefreshQuickTags();
        GeneralSidebarScroll.Visibility = mode == "Tags" ? Visibility.Collapsed : Visibility.Visible;
        SidePanelHeading.Text = mode switch
        {
            "Tags" => "Tags",
            "GPS" => "GPS",
            _ => "Bildinformationen"
        };
        await UpdateSidebarContentAsync();
    }

    private async Task UpdateSidebarContentAsync()
    {
        if (_sidebarMode is null) return;
        var mode = _sidebarMode;
        var request = _sidebarInfoRequests.Begin(mode, _selectedImageIds);
        if (mode == "Tags")
        {
            await RefreshTagSidebarAsync();
            return;
        }
        if (request.ImageId is not long imageId)
        {
            SidePanelText.Text = "Ein Bild auswählen.";
            return;
        }
        SidePanelText.Text = "Bildinformationen laden …";
        try
        {
            var content = await Task.Run(() => ReadImageInfo(imageId, mode));
            if (_sidebarInfoRequests.IsCurrent(request, _sidebarMode, _selectedImageIds))
                SidePanelText.Text = content;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Sidebar.ReadInfo", ex);
            if (_sidebarInfoRequests.IsCurrent(request, _sidebarMode, _selectedImageIds))
                SidePanelText.Text = "Informationen konnten nicht gelesen werden.";
        }
    }

    private string ReadImageInfo(long id, string mode)
    {
        if (mode == "Tags")
        {
            // Tags are served exclusively from SQLite; never reopen a JPEG
            // just because the user selects it in the sidebar.
            var keywords = new PCasa.App.Data.ImageTagRepository(_database).GetKeywords(id);
            return keywords is null ? "Tags werden noch indiziert." :
                keywords.Length == 0 ? "Keine Tags in den Bildmetadaten." :
                keywords;
        }
        var path = GetImagePath(id);
        if (path is null || !File.Exists(path)) return "Datei ist nicht erreichbar.";
        var file = new FileInfo(path);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var metadata = frame.Metadata as BitmapMetadata;
            if (mode == "GPS")
                return FormatGps(metadata);
            return $"Name: {file.Name}\nOrdner: {file.DirectoryName}\nGröße: {file.Length / 1024d / 1024d:N2} MB\nAbmessungen: {frame.PixelWidth} × {frame.PixelHeight}\nAufnahme: {metadata?.DateTaken ?? "–"}\nKamera: {metadata?.CameraManufacturer} {metadata?.CameraModel}\nLetzte Änderung: {file.LastWriteTime:g}";
        }
        catch
        {
            return $"Name: {file.Name}\nOrdner: {file.DirectoryName}\nGröße: {file.Length / 1024d / 1024d:N2} MB\nLetzte Änderung: {file.LastWriteTime:g}";
        }
    }

    private static string FormatGps(BitmapMetadata? metadata)
    {
        if (metadata is null) return "Keine GPS-Koordinaten in den Bildmetadaten.";
        try
        {
            var lat = ReadGpsCoordinate(metadata.GetQuery("/app1/ifd/gps/{ushort=2}"));
            var lon = ReadGpsCoordinate(metadata.GetQuery("/app1/ifd/gps/{ushort=4}"));
            if (lat is null || lon is null) return "Keine GPS-Koordinaten in den Bildmetadaten.";
            var latitudeRef = metadata.GetQuery("/app1/ifd/gps/{ushort=1}")?.ToString();
            var longitudeRef = metadata.GetQuery("/app1/ifd/gps/{ushort=3}")?.ToString();
            var latitude = lat.Value * (string.Equals(latitudeRef, "S", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
            var longitude = lon.Value * (string.Equals(longitudeRef, "W", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
            return $"Breitengrad: {latitude:F6}°\nLängengrad: {longitude:F6}°";
        }
        catch
        {
            return "Keine lesbaren GPS-Koordinaten gefunden.";
        }
    }

    private static double? ReadGpsCoordinate(object? value)
    {
        if (value is ulong[] u && u.Length >= 3)
        {
            static double Rational(ulong v)
            {
                var numerator = (uint)v;
                var denominator = (uint)(v >> 32);
                return denominator == 0 ? double.NaN : (double)numerator / denominator;
            }
            var result = Rational(u[0]) + Rational(u[1]) / 60 + Rational(u[2]) / 3600;
            return double.IsFinite(result) ? result : null;
        }
        if (value is uint[] n && n.Length >= 6)
        {
            var result = n[1] == 0 || n[3] == 0 || n[5] == 0
                ? double.NaN
                : (double)n[0] / n[1] + (double)n[2] / n[3] / 60 + (double)n[4] / n[5] / 3600;
            return double.IsFinite(result) ? result : null;
        }
        return null;
    }

    private string? GetImagePath(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_path FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }
}
