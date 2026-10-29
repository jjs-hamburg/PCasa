using System.Diagnostics;
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
    private bool _gpsComboUpdating;
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
        GpsMapHost.SizeChanged += (_, _) =>
        {
            if (_gpsBrowserReady && _gpsBrowser is { IsDisposed: false } browser)
                browser.ExecuteScriptAsync("window.pcasaResize?.()");
        };
        Closed += (_, _) =>
        {
            _gpsQueryRevision++;
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
        _gpsComboUpdating = true;
        try
        {
            GpsProviderComboBox.Items.Clear();
            foreach (var p in AllGpsProviders())
                GpsProviderComboBox.Items.Add(new ComboBoxItem
                {
                    Content = p.Name,
                    Tag = p.Id,
                    IsSelected = p.Id == CurrentGpsProvider().Id
                });
            GpsProviderComboBox.Items.Add(new ComboBoxItem
            {
                Content = UiLanguage.T("Eigener Kartenanbieter") + " …",
                Tag = "__new"
            });
        }
        finally { _gpsComboUpdating = false; }
    }

    private void ActivateGpsMap()
    {
        // A hidden panel never creates an expensive browser. Its only
        // persistent state lives in appearance.json, not in a second store.
        if (_sidebarMode != "GPS" || _gpsBrowser is not null) return;
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
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                "PCasa.App.Resources.gps-map.html")
                ?? throw new InvalidOperationException("Embedded map is missing.");
            using var reader = new StreamReader(stream);
            if (!browser.LoadHtml(reader.ReadToEnd(), "https://pcasa-map.invalid/gps"))
                throw new InvalidOperationException("Local map HTML could not be loaded.");
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

    private void GpsJavascriptMessageReceived(object? sender,
        JavascriptMessageReceivedEventArgs e)
    {
        if (e.Message is not string message || message.Length > 1024) return;
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
                if (_gpsBrowser is null || _sidebarMode != "GPS") return;
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
                    ShowGpsPhotoPopup(id);
                    if (_displayedBrowseIndex.Any(x => x.ImageId == id))
                    {
                        BrowseSurface_ImageClicked(this,
                            new BrowseImageClickedEventArgs(id, ModifierKeys.None));
                        RestoreGalleryAnchor((id, 0));
                    }
                    else
                    {
                        // A marker outside the filtered gallery is not silently
                        // assigned a different photo. Report the exact image.
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
        if (!_gpsBrowserReady || _gpsBrowser is null) return;
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
        if (!_gpsBrowserReady || _gpsBrowser is null) return;
        var json = JsonSerializer.Serialize(_gpsPoints.Select(p => new
        {
            id = p.Id, name = p.Name, lat = p.Lat, lon = p.Lon, selected = p.Selected
        }));
        _gpsBrowser.ExecuteScriptAsync("window.pcasaSetPoints(" + json + ")");
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
            // Multi-image selection is a meaningful map set, rather than only
            // the first selected image used by the previous GPS text sidebar.
            _gpsCurrentCoordinates = points.Count == 1
                ? (points[0].Lat, points[0].Lon) : null;
            GpsCopyButton.IsEnabled = _gpsCurrentCoordinates is not null;
            GpsGoogleButton.IsEnabled = _gpsCurrentCoordinates is not null;
            GpsCoordinatesText.Text = points.Count == 0
                ? UiLanguage.T("Keine GPS-Koordinaten für die Auswahl")
                : points.Count == 1
                    ? string.Format(CultureInfo.CurrentCulture,
                        "{0:F6}°, {1:F6}°", points[0].Lat, points[0].Lon)
                    : UiLanguage.Format("Bilder mit GPS: {0:N0}", points.Count);
            SendGpsPointsToMap();
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
        const int maxPoints = 5000;
        var result = new List<GpsPoint>();
        using var connection = _database.OpenConnection();

        void Read(string where, long[]? subset, long? folder)
        {
            if (result.Count >= maxPoints) return;
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT i.id, i.file_name, x.gps_latitude, x.gps_longitude
                FROM images i JOIN image_exif x ON x.image_id = i.id
                  AND x.source_modified_utc_ticks = i.modified_utc_ticks
                  AND x.source_file_size = i.file_size
                WHERE x.gps_latitude BETWEEN -90 AND 90
                  AND x.gps_longitude BETWEEN -180 AND 180
                """ + where + " ORDER BY i.id LIMIT " + (maxPoints - result.Count);
            if (folder is long f) command.Parameters.AddWithValue("$folder", f);
            if (subset is not null)
                for (var i = 0; i < subset.Length; i++)
                    command.Parameters.AddWithValue("$p" + i, subset[i]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var id = reader.GetInt64(0);
                result.Add(new GpsPoint(id, reader.GetString(1),
                    reader.GetDouble(2), reader.GetDouble(3),
                    ids.Contains(id)));
            }
        }

        if (ids.Length > 0)
        {
            foreach (var chunk in ids.Distinct().Chunk(350))
            {
                cancellation.ThrowIfCancellationRequested();
                Read(" AND i.id IN (" + string.Join(",", chunk.Select(
                    (_, i) => "$p" + i)) + ")", chunk, null);
                if (result.Count >= maxPoints) break;
            }
        }
        else
            Read(folderId is not null ? " AND i.photo_folder_id = $folder" : "",
                null, folderId);
        return result;
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
        if (GpsPhotoPopup.Tag is not long id ||
            !_displayedBrowseIndex.Any(x => x.ImageId == id)) return;
        BrowseSurface_ImageOpened(this, new BrowseImageClickedEventArgs(id, ModifierKeys.None));
    }

    private void GpsProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_gpsComboUpdating || GpsProviderComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string id) return;
        if (id == "__new")
        {
            OpenGpsProviderEditor(null);
            UpdateGpsProviderMenu();
            return;
        }
        _appearance.ActiveGpsProviderId = id;
        SaveAppearance();
        ApplyGpsMapProvider();
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

    private void GpsGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsCurrentCoordinates is not { } p) return;
        var coordinates = string.Create(CultureInfo.InvariantCulture,
            $"{p.Lat:F6},{p.Lon:F6}");
        Process.Start(new ProcessStartInfo(
            "https://www.google.com/maps/search/?api=1&query=" +
            Uri.EscapeDataString(coordinates)) { UseShellExecute = true });
    }
}
