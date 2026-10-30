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
        public string Language { get; set; } = "de";
        public string Background { get; set; } = "Auto";
        // Only the JPG filename, never an absolute path. The folder is beside
        // PCasa.App.exe so the user's textures remain portable.
        public string? TextureFileName { get; set; }
        // User-approved default: Name ascending, per folder. No per-folder
        // configuration and no separate sorting persistence mechanism.
        public string GallerySortBy { get; set; } = "Name";
        public bool GallerySortDescending { get; set; }
        // New profiles show confirmed checks by default. An existing saved
        // explicit Problems/None selection is always respected.
        public string FolderStatusMode { get; set; } = "Always";
        public bool HideCollapsedFolders { get; set; }
        public bool SearchGalleryViewOnly { get; set; }
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
        public int ThumbnailFilenameFontSize { get; set; } = 10;
        public bool ShowThumbnailIcons { get; set; } = true;
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
        // Relative proportions of the resizable three-column workspace.
        // The folder's user preference stays intact while expanded GPS hides it.
        public bool FolderSidebarVisible { get; set; } = true;
        public double FolderSidebarFraction { get; set; } = 0.22;
        public double SidebarFraction { get; set; } = 0.25;
        public double ExpandedGpsFraction { get; set; } = 0.55;
        public bool GpsExpanded { get; set; }
        public bool ShowGpsFilmstrip { get; set; }
        public double? GpsMapCenterLat { get; set; }
        public double? GpsMapCenterLon { get; set; }
        public double? GpsMapZoom { get; set; }
        public string? ActiveSidebarMode { get; set; }
        public string ActiveGpsProviderId { get; set; } = "openfreemap-liberty";
        public List<GpsMapProvider> GpsProviders { get; set; } = new();
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
        // Language selection lives in this existing appearance state. It is
        // safe for older profiles that have no Language key (defaults to de).
        UiLanguage.Current.Select(_appearance.Language);
        SyncLanguageMenu();
        // Preserve older Leinwand selections in the single configurable
        // texture path. Start with the finer supplied JPGs.
        if (_appearance.Background is "Canvas" or "CanvasDark")
        {
            _appearance.TextureFileName = _appearance.Background == "Canvas"
                ? "Leinwand hell - fein.jpg" : "Leinwand dunkel - fein.jpg";
            _appearance.Background = "Texture";
            SaveAppearance();
        }
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
        UiLanguage.Current.PropertyChanged += AppearanceUiLanguageChanged;
        RestoreWindowPlacement();
        InitializeWorkspaceLayout();
        InitializeGpsProviderMenu();
        SystemEvents.UserPreferenceChanged += AppearanceUserPreferenceChanged;
        Closed += AppearanceWindowClosed;
    }

    // One shutdown owner. The original MainWindow remains open until all
    // scheduler workers AND durable journal continuations have finished.
    // A canceled first Close() is never allowed to race a second disposer.
    private bool _shutdownStarted;
    private bool _shutdownFinished;
    private ShutdownProgressWindow? _shutdownProgressWindow;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownFinished)
        {
            PersistWindowBounds();
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted) return; // Double click/Alt+F4 cannot start a second shutdown.
        // Unconfirmed Tag changes live only in the editing session. Do not
        // silently discard them when the user closes the main window.
        if (_tagDraft.HasChanges)
        {
            var choice = MessageBox.Show(this,
                UiLanguage.Format("{0:N0} Bilder mit {1:N0} vorgemerkten Tag-Änderungen verwerfen?",
                    _tagDraft.ImageCount, _tagDraft.ChangeCount),
                UiLanguage.T("Nicht bestätigte Tag-Änderungen"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes) return;
            _tagDraft.Clear();
        }
        _shutdownStarted = true;

        // In-flight catalog work or owned ExifTool sessions can need an
        // uninterruptible file transaction or a slow NAS cancellation.
        // Make this visible before any slow cleanup, not after it.
        var busy = ExifToolProcessRegistry.LiveCount > 0 ||
            ExifToolKeywordWriter.HasLiveSessions ||
            _scheduler.Snapshot().Any(job => job.State is
                SchedulerJobState.Queued or SchedulerJobState.Running or
                SchedulerJobState.YieldRequested);
        // Disable pointer input, NOT the WPF window: IsEnabled=false
        // applies the disabled visual state to every child and makes
        // the folder tree unreadable while the shutdown dialog is shown.
        MainLayout.IsHitTestVisible = false;
        if (busy)
        {
            _shutdownProgressWindow = new ShutdownProgressWindow { Owner = this };
            _shutdownProgressWindow.Show();
        }

        // All slow work takes place off the WPF dispatcher. A normal idle
        // shutdown has no modal UI and reaches Close() as soon as the workers
        // acknowledge cancellation.
        _ = FinishShutdownAsync();
    }

    private async Task FinishShutdownAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(async () =>
            {
                // The scheduler's shutdown token cancels all work types,
                // including source scans, without a competing stop mechanism.
                // Wait for the existing durable package journal to checkpoint.
                try { await _scheduler.DisposeAsync(); }
                finally
                {
                    // Native handles may only be reaped AFTER scheduler-owned
                    // writes have reached their safe transaction boundary.
                    // The existing scheduler has finished all in-flight
                    // writes. Kill every PCasa-owned reader/writer child now.
                    ExifToolProcessRegistry.StopAll();
                    ExifToolKeywordWriter.StopAll();
                }
            });
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Shutdown.Workers", ex);
            // Even an exceptional cleanup must be visible in diagnostics;
            // do not create a second disposal path on the UI thread.
        }
        finally
        {
            watch.Stop();
            if (watch.ElapsedMilliseconds >= 1000)
                PCasaDiagnostics.WriteMessage("Shutdown.Duration",
                    $"Abschlussdauer: {watch.ElapsedMilliseconds} ms; " +
                    "Scheduler und native Metadatenprozesse abgeschlossen.");
            _shutdownProgressWindow?.Close();
            _shutdownProgressWindow = null;
            _shutdownFinished = true;
            if (!Dispatcher.HasShutdownStarted)
                Close();
        }
    }

    private void PersistWindowBounds()
    {
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
        UiLanguage.Current.PropertyChanged -= AppearanceUiLanguageChanged;
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

    private void ApplyTooltipSetting()
    {
        // One shared application-wide gate handles dynamically created folder
        // labels, context menus and independently opened WPF windows too.
        App.SetTooltipsEnabled(_appearance.TooltipsEnabled);
        System.Windows.Controls.ToolTipService.SetIsEnabled(this, _appearance.TooltipsEnabled);
    }

    private void AppearanceLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string code } ||
            !UiLanguage.IsSupported(code)) return;
        _appearance.Language = code;
        UiLanguage.Current.Select(code);
        SyncLanguageMenu();
        SaveAppearance();
    }

    private void SyncLanguageMenu()
    {
        if (LanguageGermanMenu is null || LanguageEnglishMenu is null) return;
        LanguageGermanMenu.IsChecked = UiLanguage.Current.Code == "de";
        LanguageEnglishMenu.IsChecked = UiLanguage.Current.Code == "en";
    }

    // The sort selector shows the currently chosen field. Its text is generated
    // at runtime, so changing language must refresh it just like XAML bindings.
    private void AppearanceUiLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiLanguage.Revision) ||
            Dispatcher.HasShutdownStarted) return;
        if (Dispatcher.CheckAccess())
        {
            SyncGallerySortMenu();
            RefreshTagDraftControls();
        }
        else
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncGallerySortMenu();
                RefreshTagDraftControls();
            }));
    }

    private void AppearanceSettings_Click(object sender, RoutedEventArgs e)
    {
        // The only appearance editor stages all values until OK.
        // This is a snapshot; dialog controls never mutate persisted state.
        var current = new AppearanceDialogValues(
            _appearance.Theme, _appearance.Background, _appearance.TextureFileName,
            _appearance.FolderStatusMode, _appearance.RestoreGalleryPosition,
            _appearance.HideCollapsedFolders, _appearance.ShowThumbnailFileNames,
            _appearance.BoldThumbnailFileNames, _appearance.ShowThumbnailIcons,
            _appearance.ShowThumbnailShadows == true, _appearance.TooltipsEnabled,
            _viewerSettings.SequenceScope.ToString(),
            _appearance.ThumbnailFilenameFontSize is 10 or 12 or 14
                ? _appearance.ThumbnailFilenameFontSize : 10);
        var dialog = new AppearanceSettingsWindow(current, ShowFolderStatusLegend)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        var next = dialog.Values;
        var visibilityChanged = next.HideCollapsed != _appearance.HideCollapsedFolders;
        var galleryLayoutChanged =
            next.FileNames != _appearance.ShowThumbnailFileNames ||
            next.BoldFileNames != _appearance.BoldThumbnailFileNames ||
            next.Icons != _appearance.ShowThumbnailIcons ||
            next.CaptionSize != _appearance.ThumbnailFilenameFontSize;
        if (galleryLayoutChanged || visibilityChanged) QueueGalleryAnchorRestore();
        _appearance.Theme = next.Theme;
        _appearance.Background = next.Background;
        _appearance.TextureFileName = next.Texture;
        _appearance.FolderStatusMode = next.FolderStatus;
        _appearance.RestoreGalleryPosition = next.RestorePosition;
        _appearance.HideCollapsedFolders = next.HideCollapsed;
        _appearance.ShowThumbnailFileNames = next.FileNames;
        _appearance.BoldThumbnailFileNames = next.BoldFileNames;
        _appearance.ShowThumbnailIcons = next.Icons;
        _appearance.ShowThumbnailShadows = next.Shadows;
        _appearance.TooltipsEnabled = next.Tooltips;
        _appearance.ThumbnailFilenameFontSize = next.CaptionSize;
        ApplyAppearance();
        ApplyTooltipSetting();
        if (visibilityChanged)
        {
            UpdateGalleryFolderVisibility();
            RefreshGallerySearchAfterTreeVisibilityChange();
        }
        if (galleryLayoutChanged || visibilityChanged)
        {
            BrowseScrollViewer.UpdateLayout();
            RestorePendingGalleryAnchor();
            BrowseSurface.SetViewport(BrowseScrollViewer.VerticalOffset,
                Math.Max(1, BrowseScrollViewer.ViewportHeight));
        }
        if (Enum.TryParse<PhotoSequenceScope>(next.SequenceScope, out var sequence) &&
            Enum.IsDefined(sequence))
            ApplyPhotoSequenceScope(sequence);
        SaveAppearance();
    }

    private void ApplyAppearance()
    {
        var mode = _appearance.Theme;
        var useLight = mode == "Light" || (mode == "System" && IsWindowsLightTheme());
        // WPF's built-in controls and window chrome must use the SAME mode as
        // our PCasa brushes. Changing brushes alone left Fluent.Dark controls
        // (radio buttons, buttons and title bars) white on light panels.
        // Application-level theme ownership also covers future dialogs.
#pragma warning disable WPF0001 // .NET 10 WPF Fluent ThemeMode is experimental
        Application.Current.ThemeMode = mode == "System"
            ? ThemeMode.System : useLight ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        SetColor("PcasaWindowBrush", useLight ? "#F5F5F2" : "#202122");
        SetColor("PcasaTopBrush", useLight ? "#E9E9E7" : "#1A1B1D");
        SetColor("PcasaPanelBrush", useLight ? "#F0F0EE" : "#252628");
        SetColor("PcasaPanelAltBrush", useLight ? "#E7E7E5" : "#2B2C2F");
        SetColor("PcasaTextBrush", useLight ? "#212121" : "#E7E7E7");
        SetColor("PcasaMutedTextBrush", useLight ? "#606064" : "#A9AAAC");
        SetColor("PcasaDeleteIconBrush", useLight ? "#BE2736" : "#DF4A54");
        SetColor("PcasaDividerBrush", useLight ? "#CFCFCD" : "#383A3D");
        SetColor("PcasaToolbarFrameBrush", useLight ? "#9DA2A6" : "#73777B");
        SetColor("PcasaSelectionBrush", useLight ? "#BBD8EB" : "#42474B");
        SetColor("PcasaHoverBrush", useLight ? "#E0EBF4" : "#303235");
        SetColor("PcasaSearchActiveBrush", useLight ? "#D8D8D6" : "#424447");
        SetColor("PcasaSearchActiveTextBrush", useLight ? "#212121" : "#F5F5F5");
        var actualBackground = _appearance.Background == "Auto"
            ? (useLight ? "Light" : "Dark") : _appearance.Background;


        GalleryViewFilterToggle.IsChecked = _appearance.SearchGalleryViewOnly;
        UpdateAllFoldersButtonState();
        BrowseSurface.SetThumbnailAnnotations(_appearance.ShowThumbnailFileNames, _appearance.ShowThumbnailIcons);
        BrowseSurface.SetFilenameBold(_appearance.BoldThumbnailFileNames);
        BrowseSurface.SetFilenameFontSize(_appearance.ThumbnailFilenameFontSize);
        BrowseSurface.SetShadowEnabled(_appearance.ShowThumbnailShadows == true);
        ApplyFolderStatusMode();
        if (ThumbnailSizeSlider is not null)
            ThumbnailSizeSlider.Value = Math.Clamp(_appearance.ThumbnailWidth, 72, 600);
        _appearance.ThumbnailSpacing = Math.Clamp(_appearance.ThumbnailSpacing, 0, 160);
        if (ThumbnailSpacingSlider is not null)
            ThumbnailSpacingSlider.Value = _appearance.ThumbnailSpacing;
        BrowseSurface.SetThumbnailSpacing(_appearance.ThumbnailSpacing);
        var background = ResolveGalleryBackground(actualBackground,
            VisualTreeHelper.GetDpi(BrowseSurface), useLight, out var isLight);
        BrowseSurface.SetLightTheme(isLight);
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

    private Brush ResolveGalleryBackground(string mode, DpiScale dpi,
        bool designIsLight, out bool isLight)
    {
        isLight = mode == "Light" || (mode == "Auto" && designIsLight);
        if (mode != "Texture")
            return mode == "Light"
                ? new SolidColorBrush(Color.FromRgb(239, 238, 234))
                : new SolidColorBrush(Color.FromRgb(32, 33, 34));
        var selected = GalleryTextureLibrary.FindFile(_appearance.TextureFileName);
        if (selected is not null)
        {
            try
            {
                var bitmap = GalleryTextureLibrary.LoadBitmap(selected);
                isLight = GalleryTextureLibrary.IsLight(bitmap);
                return GalleryTextureLibrary.CreateBrush(bitmap, dpi);
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Appearance.Texture", ex);
            }
        }
        // Missing/invalid user file: retain the selection in settings so it
        // can be restored, but always keep the gallery usable.
        isLight = designIsLight;
        return designIsLight
            ? new SolidColorBrush(Color.FromRgb(239, 238, 234))
            : new SolidColorBrush(Color.FromRgb(32, 33, 34));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (BrowseSurface is null || BrowseScrollViewer is null ||
            _appearance.Background != "Texture") return;
        var useLight = _appearance.Theme == "Light" ||
            (_appearance.Theme == "System" && IsWindowsLightTheme());
        var background = ResolveGalleryBackground("Texture", newDpi,
            useLight, out var isLight);
        BrowseSurface.SetLightTheme(isLight);
        BrowseScrollViewer.Background = background;
        BrowseSurface.SetGalleryBackground(background);
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
        _sidebarInfoRequests.Invalidate();
        _sidebarMode = null;
        _gpsFullscreen = false;
        _appearance.ActiveSidebarMode = null;
        UpdateWorkspaceLayoutPreservingGallery();
        SaveAppearance();
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

        _sidebarInfoRequests.Invalidate();
        _sidebarMode = mode;
        _gpsFullscreen = false;
        _appearance.ActiveSidebarMode = mode;
        UpdateWorkspaceLayoutPreservingGallery();
        SaveAppearance();
        if (mode == "Tags") RefreshQuickTags();
        if (mode == "GPS") ActivateGpsMap();
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
        if (mode == "GPS")
        {
            await RefreshGpsMapAsync();
            return;
        }
        if (request.ImageId is not long imageId)
        {
            SidePanelText.Text = UiLanguage.T("Ein Bild auswählen.");
            return;
        }
        SidePanelText.Text = UiLanguage.T("Bildinformationen laden …");
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
                SidePanelText.Text = UiLanguage.T("Informationen konnten nicht gelesen werden.");
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
                   x.gps_altitude, t.keywords
            FROM images i
            LEFT JOIN image_exif x ON x.image_id = i.id
                AND x.source_modified_utc_ticks = i.modified_utc_ticks
                AND x.source_file_size = i.file_size
            LEFT JOIN image_tags t ON t.image_id = i.id
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
        var infoKeywords = StringAt(12);
        var tagLines = string.IsNullOrWhiteSpace(infoKeywords) ? "–"
            : string.Join("\n  • ", infoKeywords.Split(
                ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));
        return $"Name: {row.GetString(0)}\nOrdner: {Path.GetDirectoryName(path)}" +
               $"\nGröße: {row.GetInt64(2) / 1024d / 1024d:N2} MB" +
               $"\nAbmessungen: {row.GetInt32(3)} × {row.GetInt32(4)}" +
               $"\nAufnahme: {StringAt(8) ?? "–"}" +
               $"\nKamera: {(string.IsNullOrEmpty(camera) ? "–" : camera)}" +
               $"\nGPS: {(lat.HasValue && lon.HasValue ? "vorhanden" : "–")}" +
               $"\nTags: {(tagLines == "–" ? "–" : "\n  • " + tagLines)}" +
               $"\nLetzte Änderung: {modified:g}";
    }

}
