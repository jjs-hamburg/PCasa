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
    private int _gpsSelectionCount;
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
        var html = reader.ReadToEnd();
        var assembly = Assembly.GetExecutingAssembly();
        using var css = assembly.GetManifestResourceStream(
            "PCasa.App.Resources.maplibre-gl.css");
        using var js = assembly.GetManifestResourceStream(
            "PCasa.App.Resources.maplibre-gl.js");
        if (css is not null && js is not null)
        {
            // Bundled JS/CSS (BSD-3-Clause) work without a CDN. The
            // network is needed only for tiles and optional place search.
            using var cssReader = new StreamReader(css);
            using var jsReader = new StreamReader(js);
            const string cssLink = "<link href=\"https://cdn.jsdelivr.net/npm/maplibre-gl@5.9.0/dist/maplibre-gl.css\" rel=\"stylesheet\">";
            html = html.Replace(cssLink, "<style>" + cssReader.ReadToEnd() +
                "</style>", StringComparison.Ordinal);
            const string scriptSource = "<script src=\"https://cdn.jsdelivr.net/npm/maplibre-gl@5.9.0/dist/maplibre-gl.js\"";
            var first = html.IndexOf(scriptSource, StringComparison.Ordinal);
            var last = first < 0 ? -1 : html.IndexOf("</script>", first, StringComparison.Ordinal);
            if (first < 0 || last < 0)
                throw new InvalidDataException("MapLibre script placeholder is missing.");
            var embeddedScript = jsReader.ReadToEnd().Replace("</script",
                @"<\/script", StringComparison.OrdinalIgnoreCase);
            html = html[..first] + "<script>" + embeddedScript + "</script>" +
                html[(last + "</script>".Length)..];
        }
        if (!browser.LoadHtml(html, "https://pcasa-map.invalid/gps"))
            throw new InvalidOperationException("Local map HTML could not be loaded.");
    }

    // Chromium's callback runs off the WPF thread. No JsonDocument OR
    // JsonElement may cross Dispatcher.BeginInvoke: capture immutable values
    // while the document is alive, and dispatch only this typed snapshot.
    private sealed record GpsMapMessage(
        string Kind, long Id = 0, string? Error = null,
        double? Latitude = null, double? Longitude = null, double? Zoom = null,
        double? South = null, double? West = null, double? North = null,
        double? East = null);

    private static GpsMapMessage? ParseGpsJavascriptMessage(string message)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
            return null;
        var kind = type.GetString();
        double? Number(string name)
        {
            if (!root.TryGetProperty(name, out var value) ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var number) || !double.IsFinite(number))
                return null;
            return number;
        }
        switch (kind)
        {
            case "ready":
                return new GpsMapMessage("ready");
            case "error":
                return new GpsMapMessage("error",
                    Error: root.TryGetProperty("message", out var detail) &&
                        detail.ValueKind == JsonValueKind.String
                        ? detail.GetString() : null);
            case "select":
                return new GpsMapMessage("select", Id:
                    root.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.Number &&
                    id.TryGetInt64(out var parsed) ? parsed : 0);
            case "draft":
            {
                var lat = Number("lat");
                var lon = Number("lon");
                return lat is double && lon is double
                    ? new GpsMapMessage("draft", Latitude: lat, Longitude: lon)
                    : null;
            }
            case "bounds":
            {
                var south = Number("south");
                var west = Number("west");
                var north = Number("north");
                var east = Number("east");
                return south is double && west is double &&
                       north is double && east is double
                    ? new GpsMapMessage("bounds", South: south, West: west,
                        North: north, East: east) : null;
            }
            case "viewport":
            {
                var lat = Number("lat");
                var lon = Number("lon");
                var zoom = Number("zoom");
                return lat is double && lon is double && zoom is double
                    ? new GpsMapMessage("viewport", Latitude: lat,
                        Longitude: lon, Zoom: zoom) : null;
            }
            default:
                return null;
        }
    }

    private void GpsJavascriptMessageReceived(object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        // Only our embedded local map, never the external Google website.
        if (_gpsGoogleWebsiteActive ||
            e.Message is not string message || message.Length > 1024) return;
        GpsMapMessage? data;
        try { data = ParseGpsJavascriptMessage(message); }
        catch (JsonException) { return; }
        if (data is null || Dispatcher.HasShutdownStarted) return;

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_gpsBrowser is null || _sidebarMode != "GPS" ||
                _gpsGoogleWebsiteActive || Dispatcher.HasShutdownStarted)
                return;
            if (data.Kind == "ready")
            {
                _gpsBrowserReady = true;
                GpsMapStatus.Visibility = Visibility.Collapsed;
                ApplyGpsMapProvider();
                SendGpsPointsToMap();
                if (_appearance.GpsMapCenterLat is double savedCenterLat &&
                    _appearance.GpsMapCenterLon is double savedCenterLon &&
                    _appearance.GpsMapZoom is double savedZoom)
                    _gpsBrowser.ExecuteScriptAsync("window.pcasaRestoreViewport?.(" +
                        savedCenterLat.ToString("R", CultureInfo.InvariantCulture) + "," +
                        savedCenterLon.ToString("R", CultureInfo.InvariantCulture) + "," +
                        savedZoom.ToString("R", CultureInfo.InvariantCulture) + ")");
                if (_gpsDraft is { } draft)
                    SetGpsDraft(draft.Lat, draft.Lon, recenter: false);
                GpsPickPosition_Changed(this, new RoutedEventArgs());
            }
            else if (data.Kind == "draft" &&
                     GpsEditorToggle.IsChecked == true &&
                     data.Latitude is double dLat &&
                     data.Longitude is double dLon)
            {
                SetGpsDraft(dLat, dLon, recenter: false);
            }
            else if (data.Kind == "bounds" &&
                     data.South is double south &&
                     data.West is double west &&
                     data.North is double north &&
                     data.East is double east)
            {
                _ = ApplyGpsBoundsAsync(south, west, north, east);
            }
            else if (data.Kind == "viewport" &&
                     data.Latitude is double lat &&
                     data.Longitude is double lon &&
                     data.Zoom is double zoom &&
                     lat is >= -90 and <= 90 &&
                     lon is >= -180 and <= 180 &&
                     zoom is >= 0 and <= 23)
            {
                // The map is the owner of camera movements; this only
                // persists them, without changing the displayed camera.
                _appearance.GpsMapCenterLat = lat;
                _appearance.GpsMapCenterLon = lon;
                _appearance.GpsMapZoom = zoom;
                SaveAppearance();
            }
            else if (data.Kind == "select" && data.Id > 0 &&
                     _gpsPoints.Any(x => x.Id == data.Id))
            {
                var selectedId = data.Id;
                FocusGpsPhotoForDisplay(selectedId);
                if (_gpsFullscreen)
                {
                    _gpsHighlightedImageId = selectedId;
                    GpsPhotoPopup.Visibility = Visibility.Collapsed;
                    ShowGpsFullscreenImage(selectedId);
                }
                else if (_appearance.ShowGpsFilmstrip ||
                         _gpsSelectionCount > 1)
                {
                    _gpsHighlightedImageId = selectedId;
                    if (_appearance.ShowGpsFilmstrip)
                    {
                        GpsPhotoPopup.Visibility = Visibility.Collapsed;
                        _gpsFilmstripUpdating = true;
                        GpsFilmstripList.SelectedItem =
                            _gpsFilmstripEntries.FirstOrDefault(x => x.Id == selectedId);
                        _gpsFilmstripUpdating = false;
                    }
                    else
                        ShowGpsPhotoPopup(selectedId);
                    HighlightGpsPointOnMap(selectedId);
                }
                else
                {
                    ShowGpsPhotoPopup(selectedId);
                    if (_displayedBrowseIndex.Any(x => x.ImageId == selectedId))
                    {
                        BrowseSurface_ImageClicked(this,
                            new BrowseImageClickedEventArgs(selectedId, ModifierKeys.None));
                        RestoreGalleryAnchor((selectedId, 0));
                    }
                    else
                        GpsCoordinatesText.Text =
                            _gpsPoints.First(x => x.Id == selectedId).Name;
                }
            }
            else if (data.Kind == "error" && data.Error is not null)
            {
                PCasaDiagnostics.WriteMessage("GPS.Map", data.Error);
                GpsMapStatus.Visibility = Visibility.Visible;
                GpsMapStatus.Text =
                    UiLanguage.T("Kartenanbieter konnte nicht geladen werden");
            }
        }));
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

    // A filmstrip/map focus change is not a gallery selection change: do
    // not change selected IDs, browse anchor, or the map camera. It DOES
    // change whose GPS coordinates the sidebar shows, and an old draft
    // must not continue to look applicable to the newly focused photo.
    private void FocusGpsPhotoForDisplay(long imageId)
    {
        if (_gpsHighlightedImageId != imageId)
        {
            ClearGpsEditorDraft();
            GpsEditorToggle.IsChecked = false;
        }
        _gpsHighlightedImageId = imageId;
        var photo = _gpsPoints.FirstOrDefault(p => p.Id == imageId);
        if (photo is null) return;
        _gpsCurrentCoordinates = (photo.Lat, photo.Lon);
        GpsCoordinatesText.Text = UiLanguage.Format(
            "GPS (Foto): {0:F6}°, {1:F6}°", photo.Lat, photo.Lon);
        GpsCopyButton.IsEnabled = true;
        UpdateGpsGoogleButton();
    }

    private async Task RefreshGpsMapAsync()
    {
        if (_sidebarMode != "GPS") return;
        var revision = ++_gpsQueryRevision;
        var selectedIds = _selectedImageIds.ToArray();
        // Before any async read, discard the place/marker left by the
        // previous photo. Mere refresh of the same selection keeps editing.
        ResetGpsEditorOnSelectionChange(selectedIds);
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
            _gpsSelectionCount = selectedIds.Length;
            // Public Google URLs cannot encode arbitrary PCasa photo markers.
            // Never silently represent multiple selected images as one pin.
            var googleMultiSelection = _gpsGoogleWebsiteActive &&
                _gpsSelectionCount > 1;
            if (googleMultiSelection)
                ExitGoogleMapsWebsite();
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
            GpsRemoveButton.IsEnabled = selectedIds.Length > 0;
            GpsApplyMissingButton.IsEnabled = selectedIds.Length > 0 &&
                _gpsDraft.HasValue;
            GpsReplaceButton.IsEnabled = selectedIds.Length > 0 &&
                _gpsDraft.HasValue;
            UpdateGpsGoogleButton();
            GpsCoordinatesText.Text = googleMultiSelection
                ? UiLanguage.T("Mehrfachauswahl in PCasa-Fotokarte")
                : selectedIds.Length > 0 && selectedGps.Length == 0
                    ? UiLanguage.T("Keine GPS-Koordinaten für die Auswahl")
                : points.Count == 0
                    ? UiLanguage.T("Keine GPS-Koordinaten für die Auswahl")
                    : _gpsCurrentCoordinates is { } coordinate
                        ? UiLanguage.Format("GPS (Foto): {0:F6}°, {1:F6}°",
                            coordinate.Lat, coordinate.Lon)
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
            if (GpsFullscreenImageOverlay.Visibility == Visibility.Visible &&
                GpsFullscreenImageOverlay.Tag is long shownId &&
                !points.Any(point => point.Id == shownId && point.Selected))
                CloseGpsFullscreenImage();
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
        // The same GPS gallery display is used for filmstrip and marker
        // previews. Do not enter the normal viewer here, which would replace
        // the user's multi-selection and obscure the map context.
        GpsPhotoPopup.Visibility = Visibility.Collapsed;
        _gpsHighlightedImageId = id;
        HighlightGpsPointOnMap(id);
        ShowGpsFullscreenImage(id);
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
        // On an embedded, narrow Chromium view, Google's pure map action
        // can display a stack of Home/Work, weather and place suggestions
        // over the map. The tested coordinate search is the less intrusive
        // fallback and keeps the selected location identifiable with a pin.
        // Google does not expose a supported URL switch to hide its full UI.
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
        if (_gpsCurrentCoordinates is null || _gpsSelectionCount > 1) return;
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
        GpsGoogleButton.IsEnabled = _gpsGoogleWebsiteActive ||
            (_gpsCurrentCoordinates is not null && _gpsSelectionCount <= 1);
        GpsGoogleButton.ToolTip = UiLanguage.T(_gpsSelectionCount > 1
            ? "Mehrfachauswahl in PCasa-Fotokarte"
            : "Google Maps ohne Fotomarkierung");
    }
}
