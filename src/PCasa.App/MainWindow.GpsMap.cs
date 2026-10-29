using System.Diagnostics;
using System.Windows.Threading;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.Wpf;
using PCasa.App.Controls;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record GpsPoint(long Id, string Name, double Lat, double Lon, bool Selected);
    private static readonly GpsMapProvider[] BuiltInGpsProviders =
    [
        new() { Id = "openfreemap-liberty", Name = "OpenFreeMap · Liberty",
            Format = "style", Url = "https://tiles.openfreemap.org/styles/liberty",
            Attribution = "OpenFreeMap © OpenMapTiles · OpenStreetMap contributors" },
        new() { Id = "openfreemap-positron", Name = "OpenFreeMap · Positron",
            Format = "style", Url = "https://tiles.openfreemap.org/styles/positron",
            Attribution = "OpenFreeMap © OpenMapTiles · OpenStreetMap contributors" },
        new() { Id = "openfreemap-bright", Name = "OpenFreeMap · Bright",
            Format = "style", Url = "https://tiles.openfreemap.org/styles/bright",
            Attribution = "OpenFreeMap © OpenMapTiles · OpenStreetMap contributors" }
    ];

    private ChromiumWebBrowser? _gpsBrowser;
    private bool _gpsBrowserReady;
    private bool _gpsBrowserInitializationPending;
    private bool _gpsGoogleWebsiteActive;
    private string? _gpsGoogleLoadedUrl;
    private long? _gpsHighlightedImageId;
    private string? _gpsAppliedProviderId;
    private long _gpsQueryRevision;
    private IReadOnlyList<GpsPoint> _gpsPoints = [];
    private (double Lat, double Lon)? _gpsCurrentCoordinates;

    private IEnumerable<GpsMapProvider> AllGpsProviders()
        => BuiltInGpsProviders.Concat(_appearance.GpsProviders ?? []);

    private GpsMapProvider CurrentGpsProvider()
        => AllGpsProviders().FirstOrDefault(p => p.Id == _appearance.ActiveGpsProviderId)
            ?? BuiltInGpsProviders[0];

    private void InitializeGpsProviderMenu()
    {
        _appearance.GpsProviders ??= [];
        _appearance.GpsProviders = _appearance.GpsProviders
            .Where(GpsMapProvider.IsValid).ToList();
        UpdateGpsProviderMenu();
        InitializeGpsFilmstrip();
        GpsMapHost.SizeChanged += (_, _) =>
        {
            if (!_gpsGoogleWebsiteActive && _gpsBrowserReady &&
                _gpsBrowser is { IsDisposed: false } browser)
                browser.ExecuteScriptAsync("window.pcasaResize?.()");
        };
        Closed += (_, _) =>
        {
            _gpsQueryRevision++;
            _gpsFilmstripRevision++;
            CloseGpsFullscreenImage();
            if (_gpsBrowser is not null)
            {
                _gpsBrowser.JavascriptMessageReceived -= GpsJavascriptMessageReceived;
                _gpsBrowser.Dispose();
                _gpsBrowser = null;
            }
        };
    }

    private void UpdateGpsProviderMenu()
    {
        // One compact launcher: source selection and user-owned provider
        // management share the same menu, never a full-width search bar.
        var menu = GpsProviderButton.ContextMenu ?? new ContextMenu();
        menu.Items.Clear();
        var active = CurrentGpsProvider();
        foreach (var provider in AllGpsProviders())
        {
            var item = new MenuItem
            {
                Header = provider.Name,
                Tag = provider.Id,
                IsCheckable = true,
                IsChecked = provider.Id == active.Id
            };
            item.Click += GpsProviderItem_Click;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = UiLanguage.T("Eigener Kartenanbieter") + " …" };
        add.Click += (_, _) => OpenGpsProviderEditor(null);
        menu.Items.Add(add);
        if (_appearance.GpsProviders.Any(p => p.Id == active.Id))
        {
            var edit = new MenuItem { Header = UiLanguage.T("Kartenanbieter konfigurieren") + " …" };
            edit.Click += GpsProviderSettings_Click;
            menu.Items.Add(edit);
        }
        GpsProviderButton.ContextMenu = menu;
        GpsProviderButton.ToolTip = UiLanguage.T("Kartenanbieter auswählen") + ": " + active.Name;
    }

    private void GpsProviderButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateGpsProviderMenu();
        if (GpsProviderButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = GpsProviderButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void GpsProviderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        if (!AllGpsProviders().Any(p => p.Id == id)) return;
        _appearance.ActiveGpsProviderId = id;
        UpdateGpsProviderMenu();
        SaveAppearance();
        if (_gpsGoogleWebsiteActive)
            ExitGoogleMapsWebsite();
        else
            ApplyGpsMapProvider();
    }

    private void ActivateGpsMap()
    {
        if (_sidebarMode != "GPS" || _gpsBrowser is not null ||
            _gpsBrowserInitializationPending || _shutdownStarted) return;
        // CEF must initialize on the WPF UI thread. Deferring until the first
        // input/layout work is dispatched prevents the entire main window from
        // being held hostage at startup when GPS was previously selected.
        _gpsBrowserInitializationPending = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _gpsBrowserInitializationPending = false;
            if (_sidebarMode == "GPS" && !_shutdownStarted)
                InitializeGpsMapBrowser();
        }), DispatcherPriority.ApplicationIdle);
    }

    private void InitializeGpsMapBrowser()
    {
        if (_gpsBrowser is not null || _shutdownStarted) return;
        try
        {
            if (Cef.IsInitialized is not true)
            {
                var cache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PCasa", "map-browser-cache");
                Directory.CreateDirectory(cache);
                if (Cef.Initialize(new CefSettings { CachePath = cache }) is not true)
                    throw new InvalidOperationException("CEF initialisation failed.");
            }

            var browser = new ChromiumWebBrowser();
            browser.JavascriptMessageReceived += GpsJavascriptMessageReceived;
            _gpsBrowser = browser;
            GpsMapHost.Children.Insert(0, browser);
            if (_gpsGoogleWebsiteActive)
                NavigateGoogleMapsToSelection();
            else
                LoadPcasaMapDocument(browser);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Map.Initialize", ex);
            GpsMapStatus.Text = UiLanguage.T("Karte ist nicht verfügbar");
            if (_gpsBrowser is not null)
            {
                _gpsBrowser.JavascriptMessageReceived -= GpsJavascriptMessageReceived;
                GpsMapHost.Children.Remove(_gpsBrowser);
                _gpsBrowser.Dispose();
                _gpsBrowser = null;
            }
        }
    }

    private static void LoadPcasaMapDocument(ChromiumWebBrowser browser)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "PCasa.App.Resources.gps-map.html")
            ?? throw new InvalidOperationException("Embedded map is missing.");
        using var reader = new StreamReader(stream);
        if (!browser.LoadHtml(reader.ReadToEnd(), "https://pcasa-map.invalid/gps"))
            throw new InvalidOperationException("Local map HTML could not be loaded.");
    }

    private void GpsJavascriptMessageReceived(object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        // Only our local MapLibre page can send selection messages, never
        // the experimental third-party Google Maps website.
        if (_gpsGoogleWebsiteActive ||
            e.Message is not string message || message.Length > 1024) return;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            var kind = type.GetString();
            var id = kind == "select" &&
                root.TryGetProperty("id", out var p) && p.TryGetInt64(out var parsed)
                    ? parsed : 0;
            var error = kind == "error" &&
                root.TryGetProperty("message", out var detail)
                    ? detail.GetString() : null;
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_gpsBrowser is null || _sidebarMode != "GPS" ||
                    _gpsGoogleWebsiteActive) return;
                if (kind == "ready")
                {
                    _gpsBrowserReady = true;
                    GpsMapStatus.Visibility = Visibility.Collapsed;
                    ApplyGpsMapProvider();
                    SendGpsPointsToMap();
                }
                else if (kind == "select" && id > 0 &&
                         _gpsPoints.Any(x => x.Id == id))
                {
                    if (_gpsFullscreen)
                    {
                        // Map full-screen opens the photo over the map. A click
                        // on that overlay restores the existing GPS view.
                        _gpsHighlightedImageId = id;
                        GpsPhotoPopup.Visibility = Visibility.Collapsed;
                        ShowGpsFullscreenImage(id);
                    }
                    else if (_appearance.ShowGpsFilmstrip)
                    {
                        _gpsHighlightedImageId = id;
                        GpsPhotoPopup.Visibility = Visibility.Collapsed;
                        _gpsFilmstripUpdating = true;
                        GpsFilmstripList.SelectedItem =
                            _gpsFilmstripEntries.FirstOrDefault(x => x.Id == id);
                        _gpsFilmstripUpdating = false;
                        HighlightGpsPointOnMap(id);
                    }
                    else
                    {
                        ShowGpsPhotoPopup(id);
                        if (_displayedBrowseIndex.Any(x => x.ImageId == id))
                        {
                            BrowseSurface_ImageClicked(this,
                                new BrowseImageClickedEventArgs(id, ModifierKeys.None));
                            RestoreGalleryAnchor((id, 0));
                        }
                        else
                            GpsCoordinatesText.Text = _gpsPoints.First(x => x.Id == id).Name;
                    }
                }
                else if (kind == "error" && error is not null)
                {
                    PCasaDiagnostics.WriteMessage("GPS.Map", error);
                    GpsMapStatus.Visibility = Visibility.Visible;
                    GpsMapStatus.Text = UiLanguage.T("Kartenanbieter konnte nicht geladen werden");
                }
            });
        }
        catch (JsonException) { }
    }

    private void ApplyGpsMapProvider()
    {
        if (_gpsGoogleWebsiteActive || !_gpsBrowserReady || _gpsBrowser is null) return;
        var p = CurrentGpsProvider();
        try
        {
            var url = p.ResolveUrl();
            var payload = JsonSerializer.Serialize(new
            {
                format = p.Format, url, attribution = p.Attribution
            });
            _gpsBrowser.ExecuteScriptAsync("window.pcasaApplyProvider(" + payload + ")");
            _gpsAppliedProviderId = p.Id;
            GpsMapAttribution.Text = p.Attribution;
            GpsMapStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Map.Provider", ex);
            GpsMapStatus.Visibility = Visibility.Visible;
            GpsMapStatus.Text = UiLanguage.T("Kartenanbieter konnte nicht geladen werden");
        }
    }

    private void SendGpsPointsToMap()
    {
        if (_gpsGoogleWebsiteActive || !_gpsBrowserReady || _gpsBrowser is null) return;
        var json = JsonSerializer.Serialize(_gpsPoints.Select(p => new
        {
            id = p.Id, name = p.Name, lat = p.Lat, lon = p.Lon, selected = p.Selected
        }));
        _gpsBrowser.ExecuteScriptAsync("window.pcasaSetPoints(" + json + ")");
        if (_gpsHighlightedImageId is long selected)
            HighlightGpsPointOnMap(selected);
    }

    private void HighlightGpsPointOnMap(long imageId)
    {
        if (_gpsGoogleWebsiteActive || !_gpsBrowserReady ||
            _gpsBrowser is not { IsDisposed: false } browser) return;
        browser.ExecuteScriptAsync("window.pcasaHighlightPoint?.(" +
            imageId.ToString(CultureInfo.InvariantCulture) + ")");
    }

    private async Task RefreshGpsMapAsync()
    {
        if (_sidebarMode != "GPS") return;
        var revision = ++_gpsQueryRevision;
        var selectedIds = _selectedImageIds.ToArray();
        var folderId = _activePhotoFolderId;
        try
        {
            var points = await _scheduler.RunAsync(
                "gps:catalog:" + revision,
                SchedulerPriority.Interactive,
                cancellation =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return ReadGpsPoints(selectedIds, folderId, cancellation);
                },
                () => revision == _gpsQueryRevision && _sidebarMode == "GPS",
                workKind: SchedulerWorkKind.CatalogRead);
            if (revision != _gpsQueryRevision || _sidebarMode != "GPS") return;
            _gpsPoints = points;
            // Foreground selection and folder context are intentionally
            // independent. One selection may coexist with thousands of
            // unselected background markers in the same map source.
            var selectedGps = points.Where(p => p.Selected).ToArray();
            if (_gpsHighlightedImageId is long oldId &&
                !selectedGps.Any(p => p.Id == oldId))
                _gpsHighlightedImageId = null;
            // Multi-selection retains a focused coordinate for Google Maps
            // and Copy GPS, while MapLibre still renders all selected points.
            var focused = selectedGps.FirstOrDefault(p =>
                p.Id == _gpsHighlightedImageId)
                ?? selectedGps.FirstOrDefault(p => p.Id == _gallerySelection.FocusedId);
            _gpsCurrentCoordinates = focused is not null
                ? (focused.Lat, focused.Lon)
                : selectedGps.Length == 1
                    ? (selectedGps[0].Lat, selectedGps[0].Lon)
                    : selectedIds.Length == 0 && points.Count == 1
                        ? (points[0].Lat, points[0].Lon) : null;
            GpsCopyButton.IsEnabled = _gpsCurrentCoordinates is not null;
            UpdateGpsGoogleButton();
            GpsCoordinatesText.Text = selectedIds.Length > 0 && selectedGps.Length == 0
                ? UiLanguage.T("Keine GPS-Koordinaten für die Auswahl")
                : points.Count == 0
                    ? UiLanguage.T("Keine GPS-Koordinaten für die Auswahl")
                    : _gpsCurrentCoordinates is { } coordinate
                        ? string.Format(CultureInfo.CurrentCulture,
                            "{0:F6}°, {1:F6}°", coordinate.Lat, coordinate.Lon)
                        : UiLanguage.Format("Bilder mit GPS: {0:N0}",
                            selectedGps.Length > 0 ? selectedGps.Length : points.Count);
            if (_gpsGoogleWebsiteActive)
                NavigateGoogleMapsToSelection();
            else
                SendGpsPointsToMap();
            if (_appearance.ShowGpsFilmstrip)
                GpsPhotoPopup.Visibility = Visibility.Collapsed;
            else
                SyncGpsPhotoPopupToGallerySelection();
            UpdateGpsFilmstrip();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Catalog", ex);
            if (revision == _gpsQueryRevision)
                GpsCoordinatesText.Text =
                    UiLanguage.T("Informationen konnten nicht gelesen werden.");
        }
    }

    private List<GpsPoint> ReadGpsPoints(long[] ids, long? folderId,
        CancellationToken cancellation)
    {
        const int maximum = 5000;
        var result = new List<GpsPoint>();
        var selected = ids.ToHashSet();
        var seen = new HashSet<long>();
        using var connection = _database.OpenConnection();

        void Read(string where, long[]? chunk, long? folder)
        {
            if (result.Count >= maximum) return;
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT i.id, i.file_name, x.gps_latitude, x.gps_longitude
                FROM images i JOIN image_exif x ON x.image_id = i.id
                  AND x.source_modified_utc_ticks = i.modified_utc_ticks
                  AND x.source_file_size = i.file_size
                WHERE x.gps_latitude BETWEEN -90 AND 90
                  AND x.gps_longitude BETWEEN -180 AND 180
                """ + where + " ORDER BY i.id LIMIT " + maximum;
            if (folder is long f) command.Parameters.AddWithValue("$folder", f);
            if (chunk is not null)
                for (var i = 0; i < chunk.Length; i++)
                    command.Parameters.AddWithValue("$p" + i, chunk[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read() && result.Count < maximum)
            {
                cancellation.ThrowIfCancellationRequested();
                var id = reader.GetInt64(0);
                if (!seen.Add(id)) continue;
                result.Add(new GpsPoint(id, reader.GetString(1),
                    reader.GetDouble(2), reader.GetDouble(3), selected.Contains(id)));
            }
        }

        // Selected pictures are always fetched first: if they lie outside the
        // current folder or at the end of a large catalog, the 5000-marker
        // limit can never hide them behind background markers.
        foreach (var chunk in ids.Distinct().Chunk(350))
        {
            cancellation.ThrowIfCancellationRequested();
            Read(" AND i.id IN (" + string.Join(",", chunk.Select(
                (_, i) => "$p" + i)) + ")", chunk, null);
            if (result.Count >= maximum) return result;
        }
        // The current folder remains visible as surrounding context even
        // when the selection changes. This is one consistent SQLite read,
        // with the same scheduler-owned CatalogRead lifecycle.
        if (folderId is long currentFolder)
            Read(" AND i.photo_folder_id = $folder", null, currentFolder);
        else if (ids.Length == 0)
            Read("", null, null);
        return result;
    }

    private void SyncGpsPhotoPopupToGallerySelection()
    {
        if (GpsPhotoPopup.Visibility != Visibility.Visible) return;
        var focused = _gallerySelection.FocusedId;
        var selected = _gpsPoints.FirstOrDefault(p => p.Id == focused && p.Selected)
            ?? _gpsPoints.FirstOrDefault(p => p.Selected);
        if (selected is null)
        {
            GpsPhotoPopup.Visibility = Visibility.Collapsed;
            return;
        }
        if (GpsPhotoPopup.Tag is not long previous || previous != selected.Id)
            ShowGpsPhotoPopup(selected.Id);
    }

    private void ShowGpsPhotoPopup(long id)
    {
        var point = _gpsPoints.FirstOrDefault(x => x.Id == id);
        if (point is null) return;
        GpsPhotoPopup.Tag = id;
        GpsPhotoName.Text = point.Name;
        GpsPhotoPreview.Source = null;
        GpsPhotoPreview.Visibility = Visibility.Collapsed;
        // Use already decoded local thumbnail pages. Never open a potentially
        // disconnected NAS original solely to show a map popup.
        if (_browsePageCache is not null)
        {
            for (var i = 0; i < _browseIndex.Count; i++)
                if (_browseIndex[i].ImageId == id)
                {
                    if (_browsePageCache.TryGet(i, out var bitmap) && bitmap is not null)
                    {
                        GpsPhotoPreview.Source = bitmap;
                        GpsPhotoPreview.Visibility = Visibility.Visible;
                    }
                    break;
                }
        }
        GpsPhotoPopup.Visibility = Visibility.Visible;
    }

    private void GpsPhotoClose_Click(object sender, RoutedEventArgs e)
        => GpsPhotoPopup.Visibility = Visibility.Collapsed;

    private void GpsPhotoOpen_Click(object sender, RoutedEventArgs e)
    {
        if (GpsPhotoPopup.Tag is not long id) return;
        if (_gpsFullscreen)
        {
            GpsPhotoPopup.Visibility = Visibility.Collapsed;
            ShowGpsFullscreenImage(id);
            return;
        }
        if (!_displayedBrowseIndex.Any(x => x.ImageId == id)) return;
        BrowseSurface_ImageOpened(this, new BrowseImageClickedEventArgs(id, ModifierKeys.None));
    }

    private void GpsProviderSettings_Click(object sender, RoutedEventArgs e)
    {
        var provider = _appearance.GpsProviders
            .FirstOrDefault(x => x.Id == _appearance.ActiveGpsProviderId);
        OpenGpsProviderEditor(provider);
    }

    private void OpenGpsProviderEditor(GpsMapProvider? provider)
    {
        GpsProviderNameBox.Text = provider?.Name ?? "";
        GpsProviderUrlBox.Text = provider?.Url ?? "";
        GpsProviderAttributionBox.Text = provider?.Attribution ?? "";
        GpsProviderFormatBox.SelectedIndex = provider?.Format == "xyz" ? 1 : 0;
        GpsProviderKeyBox.Clear(); // Never reveal the existing DPAPI secret.
        GpsProviderError.Text = "";
        GpsProviderEditor.Tag = provider?.Id;
        GpsProviderEditor.Visibility = Visibility.Visible;
    }

    private void GpsProviderSave_Click(object sender, RoutedEventArgs e)
    {
        var previousId = GpsProviderEditor.Tag as string;
        var old = _appearance.GpsProviders.FirstOrDefault(p => p.Id == previousId);
        var provider = new GpsMapProvider
        {
            Id = old?.Id ?? Guid.NewGuid().ToString("N"),
            Name = GpsProviderNameBox.Text.Trim(),
            Format = GpsProviderFormatBox.SelectedIndex == 1 ? "xyz" : "style",
            Url = GpsProviderUrlBox.Text.Trim(),
            Attribution = GpsProviderAttributionBox.Text.Trim(),
            ProtectedKey = GpsProviderKeyBox.Password.Length > 0
                ? GpsMapProvider.EncryptKey(GpsProviderKeyBox.Password)
                : old?.ProtectedKey ?? ""
        };
        if (!GpsMapProvider.IsValid(provider))
        {
            GpsProviderError.Text = "Ungültiger HTTPS-Kartenanbieter / XYZ-Platzhalter.";
            return;
        }
        if (old is not null) _appearance.GpsProviders.Remove(old);
        _appearance.GpsProviders.Add(provider);
        _appearance.ActiveGpsProviderId = provider.Id;
        GpsProviderKeyBox.Clear();
        GpsProviderEditor.Visibility = Visibility.Collapsed;
        UpdateGpsProviderMenu();
        SaveAppearance();
        ApplyGpsMapProvider();
    }

    private void GpsProviderDelete_Click(object sender, RoutedEventArgs e)
    {
        var provider = _appearance.GpsProviders.FirstOrDefault(
            x => x.Id == GpsProviderEditor.Tag as string);
        if (provider is not null) _appearance.GpsProviders.Remove(provider);
        _appearance.ActiveGpsProviderId = BuiltInGpsProviders[0].Id;
        GpsProviderKeyBox.Clear();
        GpsProviderEditor.Visibility = Visibility.Collapsed;
        UpdateGpsProviderMenu();
        SaveAppearance();
        ApplyGpsMapProvider();
    }

    private void GpsProviderCancel_Click(object sender, RoutedEventArgs e)
    {
        GpsProviderKeyBox.Clear();
        GpsProviderEditor.Visibility = Visibility.Collapsed;
    }

    private void GpsCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsCurrentCoordinates is not { } p) return;
        Clipboard.SetText(string.Create(CultureInfo.InvariantCulture,
            $"{p.Lat:F6}, {p.Lon:F6}"));
    }

    private static string BuildGoogleMapsWebsiteUrl(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        var value = string.Create(CultureInfo.InvariantCulture,
            $"{latitude:F6},{longitude:F6}");
        return "https://www.google.com/maps/search/?api=1&query=" +
            Uri.EscapeDataString(value);
    }

    private void GpsGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsGoogleWebsiteActive)
        {
            ExitGoogleMapsWebsite();
            return;
        }
        if (_gpsCurrentCoordinates is null) return;
        _gpsGoogleWebsiteActive = true;
        _gpsBrowserReady = false;
        _gpsGoogleLoadedUrl = null;
        GpsPhotoPopup.Visibility = Visibility.Collapsed;
        CloseGpsFullscreenImage();
        UpdateGpsGoogleButton();
        ActivateGpsMap();
        NavigateGoogleMapsToSelection();
    }

    private void NavigateGoogleMapsToSelection()
    {
        if (!_gpsGoogleWebsiteActive ||
            _gpsBrowser is not { IsDisposed: false } browser) return;
        var url = _gpsCurrentCoordinates is { } point
            ? BuildGoogleMapsWebsiteUrl(point.Lat, point.Lon)
            : "about:blank";
        if (string.Equals(url, _gpsGoogleLoadedUrl, StringComparison.Ordinal))
            return;
        _gpsGoogleLoadedUrl = url;
        GpsMapStatus.Visibility = _gpsCurrentCoordinates is null
            ? Visibility.Visible : Visibility.Collapsed;
        GpsMapStatus.Text = UiLanguage.T("Keine GPS-Koordinaten für die Auswahl");
        GpsMapAttribution.Text = UiLanguage.T("Google Maps – experimentelle Webansicht");
        try { browser.Load(url); }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.GoogleMapsWebsite", ex);
            GpsMapStatus.Text = UiLanguage.T("Google Maps konnte nicht geladen werden");
            GpsMapStatus.Visibility = Visibility.Visible;
        }
    }

    private void ExitGoogleMapsWebsite()
    {
        if (!_gpsGoogleWebsiteActive) return;
        _gpsGoogleWebsiteActive = false;
        _gpsGoogleLoadedUrl = null;
        _gpsBrowserReady = false;
        UpdateGpsGoogleButton();
        GpsMapAttribution.Text = CurrentGpsProvider().Attribution;
        GpsMapStatus.Visibility = Visibility.Visible;
        GpsMapStatus.Text = UiLanguage.T("Karte wird geladen");
        if (_gpsBrowser is { IsDisposed: false } browser)
        {
            try { LoadPcasaMapDocument(browser); }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("GPS.GoogleMapsReturn", ex);
                GpsMapStatus.Text = UiLanguage.T("Karte ist nicht verfügbar");
            }
        }
    }

    private void UpdateGpsGoogleButton()
    {
        GpsGoogleButton.Content = UiLanguage.T(_gpsGoogleWebsiteActive
            ? "Zur Fotokarte" : "Google Maps");
        GpsGoogleButton.IsEnabled =
            _gpsGoogleWebsiteActive || _gpsCurrentCoordinates is not null;
    }
}
