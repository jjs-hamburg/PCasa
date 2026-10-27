using System.IO;
using System.ComponentModel;
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
        // User-approved default: Name ascending, per folder. No per-folder
        // configuration and no separate sorting persistence mechanism.
        public string GallerySortBy { get; set; } = "Name";
        public bool GallerySortDescending { get; set; }
        public string FolderStatusMode { get; set; } = "Problems";
        public bool HideCollapsedFolders { get; set; }
        public bool TooltipsEnabled { get; set; } = true;
        public double ThumbnailSpacing { get; set; } = 10;
        public double ThumbnailWidth { get; set; } = 190;
        public bool? ShowThumbnailShadows { get; set; }
        public bool RestoreGalleryPosition { get; set; }
        public double LastScrollOffset { get; set; }
        public string? LastFolderCanonicalPath { get; set; }
        public double LastFolderRelativeOffset { get; set; }
        public string? SelectedTreePath { get; set; }
        public bool FolderExpansionsSaved { get; set; }
        public string[] ExpandedTreePaths { get; set; } = [];
        public bool ShowThumbnailFileNames { get; set; } = true;
        public bool BoldThumbnailFileNames { get; set; }
        public bool ShowThumbnailIcons { get; set; } = true;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
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
        ApplyTooltipSetting();
        SyncGallerySortMenu();
        RestoreWindowPlacement();
        SystemEvents.UserPreferenceChanged += AppearanceUserPreferenceChanged;
        Closed += AppearanceWindowClosed;
    }

    private bool _closingTagScheduler;
    private bool _tagSchedulerStopped;

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // A native ExifTool write is NOT interruptible halfway through its
        // temp-file replacement. Keep WPF alive until the shared scheduler
        // has yielded all tag workers and committed their durable checkpoints.
        // This happens only if owned writers or P0 tag work still exist.
        if (!_tagSchedulerStopped &&
            (_closingTagScheduler ||
             ExifToolKeywordWriter.HasLiveSessions ||
             _scheduler.Snapshot().Any(job =>
                 job.Key.StartsWith("tags:", StringComparison.Ordinal) &&
                 job.State is SchedulerJobState.Queued or
                     SchedulerJobState.Running or SchedulerJobState.YieldRequested)))
        {
            e.Cancel = true;
            if (_closingTagScheduler) return;
            _closingTagScheduler = true;
            StatusText.Text = "Laufenden Tag-Schreibvorgang sicher abschließen …";
            try
            {
                _activeScanCancellation?.Cancel();
                _backgroundCurrentScanCancellation?.Cancel();
                ImageExifReader.StopAll();
                await _scheduler.DisposeAsync();
                // Dispose uses the same lock as the native write request, so
                // no ExifTool process is abandoned after the scheduler exits.
                ExifToolKeywordWriter.StopAll();
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Shutdown.TagWriters", ex);
            }
            finally
            {
                _tagSchedulerStopped = true;
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            return;
        }

        _activeScanCancellation?.Cancel();
        _backgroundCurrentScanCancellation?.Cancel();
        ImageExifReader.StopAll();
        // Idle ExifTool writer sessions must not survive closing, either.
        // No native writes are in flight after the guarded branch above.
        ExifToolKeywordWriter.StopAll();

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
            double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
            bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _appearance.WindowLeft = bounds.Left;
            _appearance.WindowTop = bounds.Top;
            _appearance.WindowWidth = bounds.Width;
            _appearance.WindowHeight = bounds.Height;
        }
        _appearance.WindowMaximized = WindowState == WindowState.Maximized;
        SaveAppearance();
    }

    private void RestoreWindowPlacement()
    {
        if (_appearance.WindowWidth is double width &&
            _appearance.WindowHeight is double height &&
            _appearance.WindowLeft is double left &&
            _appearance.WindowTop is double top &&
            double.IsFinite(width) && double.IsFinite(height) &&
            double.IsFinite(left) && double.IsFinite(top) &&
            width >= MinWidth && height >= MinHeight)
        {
            var desktop = new Rect(SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            var stored = new Rect(left, top, width, height);
            if (desktop.IntersectsWith(stored))
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }
        }
        if (_appearance.WindowMaximized)
            WindowState = WindowState.Maximized;
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

    private void TooltipsEnabled_Click(object sender, RoutedEventArgs e)
    {
        _appearance.TooltipsEnabled = TooltipsEnabledMenu.IsChecked;
        ApplyTooltipSetting();
        SaveAppearance();
    }

    private void ApplyTooltipSetting()
    {
        if (TooltipsEnabledMenu is null) return;
        TooltipsEnabledMenu.IsChecked = _appearance.TooltipsEnabled;
        // One shared application-wide gate handles dynamically created folder
        // labels, context menus and independently opened WPF windows too.
        App.SetTooltipsEnabled(_appearance.TooltipsEnabled);
        System.Windows.Controls.ToolTipService.SetIsEnabled(this, _appearance.TooltipsEnabled);
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
        UpdateAllFoldersButtonState();
        SaveAppearance();
    }

    private void AppearanceThumbnailOptions_Click(object sender, RoutedEventArgs e)
    {
        QueueGalleryAnchorRestore();
        _appearance.ShowThumbnailFileNames = ThumbnailFileNamesMenu.IsChecked;
        _appearance.BoldThumbnailFileNames = ThumbnailBoldFileNamesMenu.IsChecked;
        _appearance.ShowThumbnailIcons = ThumbnailIconsMenu.IsChecked;
        BrowseSurface.SetThumbnailAnnotations(
            _appearance.ShowThumbnailFileNames, _appearance.ShowThumbnailIcons);
        BrowseSurface.SetFilenameBold(_appearance.BoldThumbnailFileNames);
        SaveAppearance();
        BrowseScrollViewer.UpdateLayout();
        RestorePendingGalleryAnchor();
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
        HideCollapsedFoldersMenu.IsChecked = _appearance.HideCollapsedFolders;
        UpdateAllFoldersButtonState();
        ThumbnailFileNamesMenu.IsChecked = _appearance.ShowThumbnailFileNames;
        ThumbnailBoldFileNamesMenu.IsChecked = _appearance.BoldThumbnailFileNames;
        ThumbnailIconsMenu.IsChecked = _appearance.ShowThumbnailIcons;
        BrowseSurface.SetThumbnailAnnotations(_appearance.ShowThumbnailFileNames, _appearance.ShowThumbnailIcons);
        BrowseSurface.SetFilenameBold(_appearance.BoldThumbnailFileNames);
        ApplyFolderStatusMode();
        if (ThumbnailSizeSlider is not null)
            ThumbnailSizeSlider.Value = Math.Clamp(_appearance.ThumbnailWidth, 72, 600);
        _appearance.ThumbnailSpacing = Math.Clamp(_appearance.ThumbnailSpacing, 0, 160);
        if (ThumbnailSpacingSlider is not null)
            ThumbnailSpacingSlider.Value = _appearance.ThumbnailSpacing;
        BrowseSurface.SetThumbnailSpacing(_appearance.ThumbnailSpacing);
        var background = actualBackground switch
        {
            "Light" => new SolidColorBrush(Color.FromRgb(239, 238, 234)),
            "Canvas" => MakeCanvasBrush(light: true, VisualTreeHelper.GetDpi(BrowseSurface)),
            "CanvasDark" => MakeCanvasBrush(light: false, VisualTreeHelper.GetDpi(BrowseSurface)),
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

    // The original texture samples are 128 x 128 pixels. Cache the decoded
    // bitmaps, not the WPF brushes: a brush's viewport depends on monitor DPI.
    private static readonly Lazy<BitmapSource> LightCanvasBitmap =
        new(() => LoadCanvasBitmap("CanvasLight.jpg"));
    private static readonly Lazy<BitmapSource> DarkCanvasBitmap =
        new(() => LoadCanvasBitmap("CanvasDark.jpg"));

    private static BitmapSource LoadCanvasBitmap(string filename)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(
            "pack://application:,,,/PCasa.App;component/Assets/" + filename,
            UriKind.Absolute);
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush MakeCanvasBrush(bool light, DpiScale dpi)
    {
        var bitmap = light ? LightCanvasBitmap.Value : DarkCanvasBitmap.Value;
        // Viewport is specified in device-independent units, not physical
        // pixels. Compensating for DPI means exactly one image pixel is drawn
        // per screen pixel at 100%, 125%, 150%, 200%, etc.
        var brush = new ImageBrush(bitmap)
        {
            TileMode = TileMode.FlipXY,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0,
                bitmap.PixelWidth / dpi.DpiScaleX,
                bitmap.PixelHeight / dpi.DpiScaleY),
            Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // Moving between monitors must not rescale the fixed-pixel weave.
        // Update only the two backgrounds; never trigger a gallery relayout.
        if (BrowseSurface is null || BrowseScrollViewer is null ||
            _appearance.Background is not ("Canvas" or "CanvasDark")) return;
        var brush = MakeCanvasBrush(_appearance.Background == "Canvas", newDpi);
        BrowseScrollViewer.Background = brush;
        BrowseSurface.SetGalleryBackground(brush);
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
        if (string.IsNullOrEmpty(_appearance.SelectedTreePath) &&
            BrowseSurface.GetFolderIdAtOffset(target + 4) is long restoredFolderId)
            UpdateActiveFolderDisplay(restoredFolderId, updateTreeSelection: true);
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
            var content = await _scheduler.RunAsync(
                $"catalog:sidebar-info:{imageId}:{Guid.NewGuid():N}",
                SchedulerPriority.Interactive,
                token => { token.ThrowIfCancellationRequested(); return ReadImageInfo(imageId, mode); },
                () => _sidebarInfoRequests.IsCurrent(request, _sidebarMode, _selectedImageIds),
                workKind: SchedulerWorkKind.CatalogRead);
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
            var keywords = new PCasa.App.Data.ImageTagRepository(_database).GetKeywords(id);
            return keywords is null ? "Tags werden noch indiziert." :
                keywords.Length == 0 ? "Keine Tags in den Bildmetadaten." : keywords;
        }

        // Read the SAME catalog imported by FolderScanner, never reopen
        // the original photo merely to switch the Info/GPS sidebar.
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.file_name, i.local_path, i.file_size, i.width, i.height,
                   i.modified_utc_ticks, x.camera_make, x.camera_model,
                   x.date_time_original, x.gps_latitude, x.gps_longitude,
                   x.gps_altitude
            FROM images i
            LEFT JOIN image_exif x ON x.image_id = i.id
                AND x.source_modified_utc_ticks = i.modified_utc_ticks
                AND x.source_file_size = i.file_size
            WHERE i.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var row = command.ExecuteReader();
        if (!row.Read()) return "Bild ist nicht mehr im Katalog.";
        string? StringAt(int col) => row.IsDBNull(col) ? null : row.GetString(col);
        double? NumberAt(int col) => row.IsDBNull(col) ? null : row.GetDouble(col);

        var lat = NumberAt(9);
        var lon = NumberAt(10);
        if (mode == "GPS")
            return lat is double latitude && lon is double longitude
                ? $"Breitengrad: {latitude:F6}°\nLängengrad: {longitude:F6}°" +
                  (NumberAt(11) is double alt ? $"\nHöhe: {alt:F1} m" : "")
                : StringAt(6) is null && StringAt(7) is null && StringAt(8) is null
                    ? "Keine GPS-Daten im Katalog (ggf. Metadaten noch einlesen)."
                    : "Keine GPS-Koordinaten in den Bildmetadaten.";

        var path = row.GetString(1);
        var modified = new DateTime(row.GetInt64(5), DateTimeKind.Utc).ToLocalTime();
        var camera = string.Join(" ", new[] { StringAt(6), StringAt(7) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return $"Name: {row.GetString(0)}\nOrdner: {Path.GetDirectoryName(path)}" +
               $"\nGröße: {row.GetInt64(2) / 1024d / 1024d:N2} MB" +
               $"\nAbmessungen: {row.GetInt32(3)} × {row.GetInt32(4)}" +
               $"\nAufnahme: {StringAt(8) ?? "–"}" +
               $"\nKamera: {(string.IsNullOrEmpty(camera) ? "–" : camera)}" +
               $"\nGPS: {(lat.HasValue && lon.HasValue ? "vorhanden" : "–")}" +
               $"\nLetzte Änderung: {modified:g}";
    }

}
