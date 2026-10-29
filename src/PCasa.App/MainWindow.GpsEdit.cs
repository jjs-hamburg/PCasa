using CefSharp;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed record GpsEditRecipe(long[] ImageIds, double? Latitude,
        double? Longitude, GpsMetadataRepository.EditMode Mode);
    private sealed record PlaceHit(string Name, double Lat, double Lon)
    {
        public override string ToString() => Name;
    }

    private static readonly HttpClient GpsPlaceClient = CreateGpsPlaceClient();
    // Nominatim public API policy: max one explicitly triggered request/sec
    // across all windows; never call it from autocomplete or map movement.
    private static readonly SemaphoreSlim GpsPlaceRequestGate = new(1, 1);
    private static DateTimeOffset _lastGpsPlaceRequest = DateTimeOffset.MinValue;
    private static HttpClient CreateGpsPlaceClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PCasa/0.1 (open-source GPS photo manager; https://github.com/jjs-hamburg/PCasa)");
        return client;
    }

    private (double Lat, double Lon)? _gpsDraft;
    private CancellationTokenSource? _gpsPlaceSearchCancellation;
    private long _gpsPlaceSearchRevision;
    private long _gpsAreaRevision;
    private HashSet<long>? _gpsAreaFilterIds;

    private void GpsPlaceSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SearchGpsPlacesAsync();
    }

    private void GpsPlaceSearch_Click(object sender, RoutedEventArgs e)
        => _ = SearchGpsPlacesAsync();

    private async Task SearchGpsPlacesAsync()
    {
        var query = GpsPlaceSearchBox.Text.Trim();
        if (query.Length < 2)
        {
            GpsEditStatus.Text = UiLanguage.T("Mindestens zwei Zeichen für die Ortssuche eingeben.");
            return;
        }
        var version = ++_gpsPlaceSearchRevision;
        _gpsPlaceSearchCancellation?.Cancel();
        _gpsPlaceSearchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _gpsPlaceSearchCancellation = cancellation;
        GpsEditStatus.Text = UiLanguage.T("Ort wird gesucht …");
        GpsPlaceSearchButton.IsEnabled = false;
        try
        {
            // Explicit searches only: no autocomplete requests to Nominatim.
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=6&accept-language=" +
                Uri.EscapeDataString(_appearance.Language) + "&q=" +
                Uri.EscapeDataString(query);
            string json;
            await GpsPlaceRequestGate.WaitAsync(cancellation.Token);
            try
            {
                var pause = TimeSpan.FromSeconds(1) -
                    (DateTimeOffset.UtcNow - _lastGpsPlaceRequest);
                if (pause > TimeSpan.Zero)
                    await Task.Delay(pause, cancellation.Token);
                _lastGpsPlaceRequest = DateTimeOffset.UtcNow;
                using var response = await GpsPlaceClient.GetAsync(url, cancellation.Token);
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellation.Token);
            }
            finally { GpsPlaceRequestGate.Release(); }
            using var doc = JsonDocument.Parse(json);
            var hits = new List<PlaceHit>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!double.TryParse(item.GetProperty("lat").GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(item.GetProperty("lon").GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    continue;
                hits.Add(new PlaceHit(item.GetProperty("display_name").GetString() ??
                    query, lat, lon));
            }
            if (version != _gpsPlaceSearchRevision || _sidebarMode != "GPS") return;
            GpsPlaceResults.ItemsSource = hits;
            GpsPlaceResults.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            GpsEditStatus.Text = hits.Count == 0
                ? UiLanguage.T("Kein Ort gefunden.") : UiLanguage.T("Ort auswählen oder auf Karte klicken.");
            if (hits.Count == 1) GpsPlaceResults.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.PlaceSearch", ex);
            if (version == _gpsPlaceSearchRevision)
                GpsEditStatus.Text = UiLanguage.T("Ortssuche nicht verfügbar (Internetverbindung prüfen).");
        }
        finally
        {
            if (version == _gpsPlaceSearchRevision)
                GpsPlaceSearchButton.IsEnabled = true;
        }
    }

    private void GpsPlaceResults_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (GpsPlaceResults.SelectedItem is not PlaceHit hit) return;
        GpsPickPositionToggle.IsChecked = true;
        SetGpsDraft(hit.Lat, hit.Lon, recenter: true);
    }

    private void GpsPickPosition_Changed(object sender, RoutedEventArgs e)
    {
        if (!_gpsBrowserReady || _gpsBrowser is null || _gpsGoogleWebsiteActive) return;
        _gpsBrowser.ExecuteScriptAsync("window.pcasaEnablePlacement?.(" +
            (GpsPickPositionToggle.IsChecked == true ? "true" : "false") + ")");
    }

    private void SetGpsDraft(double lat, double lon, bool recenter)
    {
        if (!double.IsFinite(lat) || !double.IsFinite(lon) ||
            lat < -90 || lat > 90 || lon < -180 || lon > 180) return;
        _gpsDraft = (lat, lon);
        GpsDraftCoordinates.Text = string.Format(CultureInfo.CurrentCulture,
            "{0:F6}°, {1:F6}°", lat, lon);
        GpsEditStatus.Text = UiLanguage.T("Position bereit – GPS übernehmen oder ändern.");
        GpsApplyMissingButton.IsEnabled = _selectedImageIds.Count > 0;
        GpsReplaceButton.IsEnabled = _selectedImageIds.Count > 0;
        if (!_gpsBrowserReady || _gpsBrowser is null || _gpsGoogleWebsiteActive) return;
        _gpsBrowser.ExecuteScriptAsync("window.pcasaSetDraft?.(" +
            lat.ToString("R", CultureInfo.InvariantCulture) + "," +
            lon.ToString("R", CultureInfo.InvariantCulture) + "," +
            (recenter ? "true" : "false") + ")");
    }

    private void GpsApplyMissing_Click(object sender, RoutedEventArgs e)
        => _ = CommitGpsEditAsync(GpsMetadataRepository.EditMode.MissingOnly);
    private void GpsReplace_Click(object sender, RoutedEventArgs e)
        => _ = CommitGpsEditAsync(GpsMetadataRepository.EditMode.Replace);
    private void GpsRemove_Click(object sender, RoutedEventArgs e)
        => _ = CommitGpsEditAsync(GpsMetadataRepository.EditMode.Remove);

    private async Task CommitGpsEditAsync(GpsMetadataRepository.EditMode mode)
    {
        var ids = _selectedImageIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        if (mode != GpsMetadataRepository.EditMode.Remove && _gpsDraft is null)
        {
            GpsEditStatus.Text = UiLanguage.T("Zuerst einen Ort suchen oder auf die Karte klicken.");
            return;
        }
        if (mode == GpsMetadataRepository.EditMode.Replace &&
            MessageBox.Show(this,
                UiLanguage.Format("Vorhandene GPS-Positionen für bis zu {0} ausgewählte Bilder überschreiben?", ids.Length),
                UiLanguage.T("GPS ändern"), MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            // Validate ALL extensions before enqueuing a permanent P0 package.
            // No sidecar fallback and no endlessly retried unsupported files.
            var unsupported = await _scheduler.RunAsync(
                "gps:validate:" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.Interactive,
                token =>
                {
                    var failures = new List<string>();
                    using var db = _database.OpenConnection();
                    foreach (var chunk in ids.Chunk(300))
                    {
                        token.ThrowIfCancellationRequested();
                        using var cmd = db.CreateCommand();
                        var parameters = chunk.Select((id,i) => "$i" + i).ToArray();
                        cmd.CommandText = "SELECT local_path FROM images WHERE id IN (" +
                            string.Join(",", parameters) + ")";
                        for (var i=0;i<chunk.Length;i++)
                            cmd.Parameters.AddWithValue(parameters[i], chunk[i]);
                        using var rows = cmd.ExecuteReader();
                        while (rows.Read())
                        {
                            var path = rows.GetString(0);
                            var ext = Path.GetExtension(path).ToLowerInvariant();
                            if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff"
                                or ".dng" or ".nef" or ".arw" or ".cr2"))
                                failures.Add(Path.GetFileName(path) + " (" + ext + ")");
                        }
                    }
                    return failures;
                }, workKind: SchedulerWorkKind.CatalogRead);
            if (unsupported.Count > 0)
            {
                MessageBox.Show(this,
                    UiLanguage.T("Keine Sidecars: GPS-Direktschreiben für diese Formate nicht freigegeben:") +
                    "\n" + string.Join("\n", unsupported.Take(12)) +
                    (unsupported.Count > 12 ? "\n…" : ""),
                    "GPS", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var request = new GpsEditRecipe(ids, _gpsDraft?.Lat,
                _gpsDraft?.Lon, mode);
            var key = "gps:edit:" + Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(request);
            BeginVisibleManualOperation(key, "GPS in Originaldateien schreiben", ids.Length);
            var completion = _scheduler.EnqueueDurablePackageAsync(
                BuildGpsEditPackage(key, payload), "gps-edit-v1", payload,
                SchedulerPriority.ManualAction);
            GpsEditStatus.Text = UiLanguage.Format(
                "GPS-Auftrag für {0} Bilder gespeichert.", ids.Length);
            _ = ObserveGpsEditAsync(completion, key);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Edit.Queue", ex);
            GpsEditStatus.Text = ex.Message;
        }
    }

    private SchedulerTaskPackage BuildGpsEditPackage(string key, string payload)
    {
        var request = JsonSerializer.Deserialize<GpsEditRecipe>(payload)
            ?? throw new InvalidDataException("GPS-Paket enthält ungültige Daten.");
        if (request.ImageIds.Length == 0 ||
            request.ImageIds.Distinct().Count() != request.ImageIds.Length ||
            request.ImageIds.Any(id => id <= 0) ||
            (request.Mode != GpsMetadataRepository.EditMode.Remove &&
              (request.Latitude is not double lat || request.Longitude is not double lon ||
               !double.IsFinite(lat) || !double.IsFinite(lon) ||
               lat is < -90 or > 90 || lon is < -180 or > 180)))
            throw new InvalidDataException("GPS-Auftrag enthält ungültige Koordinaten.");
        var package = new SchedulerTaskPackage(key);
        for (var i = 0; i < request.ImageIds.Length; i++)
        {
            var id = request.ImageIds[i];
            var finished = i + 1;
            package.AddStep(token =>
            {
                var repository = new GpsMetadataRepository(_database);
                repository.Apply(id, request.Latitude, request.Longitude,
                    request.Mode, token,
                    path => _scheduler.ReportProgress(key,
                        "GPS in Originaldateien schreiben",
                        finished - 1, request.ImageIds.Length, path),
                    packageKey: key);
                _scheduler.ReportProgress(key, "GPS in Originaldateien schreiben",
                    finished, request.ImageIds.Length);
                // Only committed files may appear at the next regular
                // map refresh. Do not queue one expensive query per image.
                return Task.CompletedTask;
            });
        }
        return package;
    }

    private async Task ObserveGpsEditAsync(Task completion, string key)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Edit.Package", ex);
            if (!Dispatcher.HasShutdownStarted)
                GpsEditStatus.Text = UiLanguage.T(
                    "GPS-Schreibvorgang unterbrochen; im Scheduler gespeichert.");
        }
        finally
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                EndVisibleManualOperation(key);
                // Only refresh the existing locally packed thumbnail index,
                // so the GPS badge changes without decoding/rebuilding images.
                if (_browseInitialized)
                    await RefreshBrowseGalleryAsync(forceIndexReload: true);
                if (_sidebarMode == "GPS") await RefreshGpsMapAsync();
            }
        }
    }

    private void GpsShowArea_Click(object sender, RoutedEventArgs e)
    {
        if (_gpsBrowser is null || !_gpsBrowserReady || _gpsGoogleWebsiteActive)
            return;
        _gpsBrowser.ExecuteScriptAsync("window.pcasaReadBounds?.()");
    }

    private async Task ApplyGpsBoundsAsync(double south, double west,
        double north, double east)
    {
        if (_sidebarMode != "GPS" || _browsePageCache is null ||
            south > north || west > east) return;
        var version = ++_gpsAreaRevision;
        try
        {
            var ids = await _scheduler.RunAsync("gps:area:" + version,
                SchedulerPriority.Interactive, token =>
                {
                    var result = new HashSet<long>();
                    using var db = _database.OpenConnection();
                    using var cmd = db.CreateCommand();
                    cmd.CommandText = """
                        SELECT i.id FROM images i
                        JOIN image_exif x ON x.image_id=i.id
                        WHERE x.source_file_size=i.file_size
                          AND x.source_modified_utc_ticks=i.modified_utc_ticks
                          AND x.gps_latitude BETWEEN $s AND $n
                          AND x.gps_longitude BETWEEN $w AND $e;
                        """;
                    cmd.Parameters.AddWithValue("$s", south);
                    cmd.Parameters.AddWithValue("$n", north);
                    cmd.Parameters.AddWithValue("$w", west);
                    cmd.Parameters.AddWithValue("$e", east);
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        result.Add(rows.GetInt64(0));
                    }
                    return result;
                }, () => version == _gpsAreaRevision,
                workKind: SchedulerWorkKind.CatalogRead);
            if (version != _gpsAreaRevision) return;
            _gpsAreaFilterIds = ids;
            GpsClearAreaButton.IsEnabled = true;
            await ApplyTagFilterAsync(scrollToTop: true);
            GpsEditStatus.Text = UiLanguage.Format(
                "{0} Fotos im Kartenausschnitt.", ids.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.AreaFilter", ex);
            GpsEditStatus.Text = ex.Message;
        }
    }

    private async void GpsClearArea_Click(object sender, RoutedEventArgs e)
    {
        ++_gpsAreaRevision;
        _gpsAreaFilterIds = null;
        GpsClearAreaButton.IsEnabled = false;
        await ApplyTagFilterAsync();
    }
}
