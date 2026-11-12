using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CefSharp;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private sealed class GpxEntry(string name, string fullPath) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string FullPath { get; } = fullPath;
        private string _tooltip = fullPath;
        public string Tooltip
        {
            get => _tooltip;
            set { _tooltip = value; PropertyChanged?.Invoke(this,
                new PropertyChangedEventArgs(nameof(Tooltip))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed record GpxAssignment(long Id, string Name, double? Lat,
        double? Lon, string Status, bool ExistingGps);
    private sealed record GpxWriteItem(long Id, double Lat, double Lon);
    private sealed record GpxWriteRecipe(GpxWriteItem[] Items, bool Replace);
    private GpxOptions _gpxOptions = GpxOptions.Load();
    private bool _gpxUiUpdating;
    private long _gpxLoadRevision;
    private long _gpxListRevision;
    private IReadOnlyList<GpxTrackPoint>[] _gpxSourceTracks = [];
    private List<GpxTrackPoint> _gpxCurrentPoints = [];

    private void InitializeGpxUi()
    {
        _gpxUiUpdating = true;
        GpsGpxToggle.IsChecked = _appearance.ShowGpxImport;
        _gpxUiUpdating = false;
        ApplyGpsGpxVisibility();
        if (_appearance.ShowGpxImport) RefreshGpxFiles();
    }

    private void ApplyGpsGpxVisibility()
    {
        if (GpsGpxPanel is not null)
            GpsGpxPanel.Visibility = _appearance.ShowGpxImport
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GpsGpxToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_gpxUiUpdating) return;
        _appearance.ShowGpxImport = GpsGpxToggle.IsChecked == true;
        if (_appearance.ShowGpxImport)
        {
            _gpsFilmstripUpdating = true;
            _appearance.ShowGpsFilmstrip = false;
            GpsFilmstripToggle.IsChecked = false;
            _gpsFilmstripUpdating = false;
        }
        ApplyGpsFilmstripVisibility();
        ApplyGpsGpxVisibility();
        SaveAppearance();
        if (_appearance.ShowGpxImport) RefreshGpxFiles();
        else ClearGpxOverlay();
    }

    private void GpxOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new GpxOptionsWindow(this, _gpxOptions);
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        _gpxOptions = dialog.Result;
        if (_appearance.ShowGpxImport) RefreshGpxFiles();
    }

    private void RefreshGpxFiles()
    {
        var old = GpsGpxList.SelectedItems.OfType<GpxEntry>()
            .Select(e => e.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(_gpxOptions.DirectoryPath);
            var files = Directory.EnumerateFiles(_gpxOptions.DirectoryPath, "*",
                SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetExtension(f).Equals(".gpx",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new GpxEntry(Path.GetFileName(f), f)).ToArray();
            GpsGpxList.ItemsSource = files;
            GpsGpxEmptyText.Visibility = files.Length == 0
                ? Visibility.Visible : Visibility.Collapsed;
            foreach (var file in files)
                if (old.Contains(file.FullPath)) GpsGpxList.SelectedItems.Add(file);
            GpsGpxHeader.Text = "GPX Import (" + files.Length + ")";
            ResizeGpxEntries();
            _ = LoadGpxTooltipsAsync(files, ++_gpxListRevision);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { GpsGpxHeader.Text = ex.Message;
          GpsGpxEmptyText.Visibility = Visibility.Collapsed; }
    }

    private async Task LoadGpxTooltipsAsync(GpxEntry[] files, long revision)
    {
        await Task.Run(() =>
        {
            foreach (var file in files)
            {
                if (revision != _gpxListRevision || _shutdownStarted) return;
                try
                {
                    var points = GpxTrackReader.Read(file.FullPath);
                    var text = points.Count == 0 ? file.FullPath + "\nKeine GPS-Punkte" :
                        file.FullPath + "\n" +
                        points[0].UtcTime.LocalDateTime.ToString("g") + " – " +
                        points[^1].UtcTime.LocalDateTime.ToString("g") + "\n" +
                        points.Count.ToString("N0") + " GPS-Punkte · " +
                        (points[^1].UtcTime - points[0].UtcTime).ToString(@"hh\:mm\:ss");
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (revision == _gpxListRevision) file.Tooltip = text;
                    }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.Xml.XmlException or InvalidDataException)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (revision == _gpxListRevision)
                            file.Tooltip = file.FullPath + "\n" + ex.Message;
                    }));
                }
            }
        });
    }

    private void GpsGpx_SizeChanged(object sender, SizeChangedEventArgs e)
        => ResizeGpxEntries();

    private void ResizeGpxEntries()
    {
        if (GpsGpxList is null) return;
        var width = Math.Max(130, GpsGpxList.ActualWidth - 28);
        var itemWidth = width / (width >= 360 ? 2 : 1) - 3;
        for (int i = 0; i < GpsGpxList.Items.Count; i++)
            if (GpsGpxList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item)
                item.Width = itemWidth;
    }

    private async void GpsGpx_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GpsGpxList.SelectedItems.OfType<GpxEntry>().ToArray();
        GpsGpxToGalleryButton.IsEnabled = selected.Length > 0;
        var revision = ++_gpxLoadRevision;
        if (selected.Length == 0) { ClearGpxOverlay(); return; }
        try
        {
            var tracks = await Task.Run(() =>
                selected.Select(x => GpxTrackReader.Read(x.FullPath)).ToArray());
            if (revision != _gpxLoadRevision || !_appearance.ShowGpxImport) return;
            _gpxSourceTracks = tracks;
            _gpxCurrentPoints = tracks.SelectMany(x => x).ToList();
            GpsGpxHeader.Text = $"GPX ({selected.Length}, {_gpxCurrentPoints.Count:N0} Punkte)";
            PublishGpxOverlay(true);
        }
        catch (Exception ex)
        {
            if (revision != _gpxLoadRevision) return;
            GpsGpxHeader.Text = ex.Message;
            ClearGpxOverlay();
        }
    }

    private void PublishGpxOverlay(bool fit)
    {
        if (_gpsBrowser is null || !_gpsBrowserReady || _gpsGoogleWebsiteActive) return;
        var segments = new List<List<double[]>>();
        foreach (var track in _gpxSourceTracks)
        {
            List<double[]>? current = null;
            GpxTrackPoint? prev = null;
            var count = 0;
            var stride = Math.Max(1, track.Count / 12000);
            foreach (var point in track)
            {
                if (prev is null || point.SegmentId != prev.SegmentId ||
                    (point.UtcTime - prev.UtcTime).TotalSeconds >
                    _gpxOptions.MaximumGapSeconds)
                {
                    current = [];
                    segments.Add(current);
                    count = 0;
                }
                if (++count % stride == 0 || count == 1)
                    current!.Add([point.Longitude, point.Latitude]);
                prev = point;
            }
        }
        _gpsBrowser.ExecuteScriptAsync("window.pcasaShowGpx?.(" +
            JsonSerializer.Serialize(segments.Where(s => s.Count >= 2).ToArray()) +
            "," + (fit ? "true" : "false") + ")");
    }

    private void ClearGpxOverlay()
    {
        ++_gpxLoadRevision;
        _gpxCurrentPoints = [];
        _gpxSourceTracks = [];
        if (_gpsBrowser is not null && _gpsBrowserReady && !_gpsGoogleWebsiteActive)
            _gpsBrowser.ExecuteScriptAsync("window.pcasaShowGpx?.([],false)");
    }

    private async void GpsGpxToGallery_Click(object sender, RoutedEventArgs e)
    {
        if (_gpxCurrentPoints.Count == 0) return;
        var ids = _selectedImageIds.Count > 0
            ? _selectedImageIds.ToArray()
            : _displayedBrowseIndex.Where(x => _activePhotoFolderId.HasValue &&
                    x.PhotoFolderId == _activePhotoFolderId.Value)
                .Select(x => x.ImageId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            PcasaDialog.Show(this, "Keine Zielfotos ausgewählt.", "GPX",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var assignments = new List<GpxAssignment>();
        var tracks = _gpxSourceTracks.ToArray();
        var options = _gpxOptions;
        GpsGpxToGalleryButton.IsEnabled = false;
        try
        {
            await _galleryCatalogReader.ReadAsync(token =>
            {
                using var connection = _database.OpenConnection();
                foreach (var chunk in ids.Chunk(250))
                {
                    token.ThrowIfCancellationRequested();
                    using var command = connection.CreateCommand();
                    var names = chunk.Select((_, i) => "$p" + i).ToArray();
                    command.CommandText = "SELECT i.id,i.file_name,x.date_time_original," +
                        "x.gps_latitude,x.gps_longitude FROM images i " +
                        "LEFT JOIN image_exif x ON x.image_id=i.id WHERE i.id IN (" +
                        string.Join(",", names) + ")";
                    for (int i = 0; i < chunk.Length; i++)
                        command.Parameters.AddWithValue(names[i], chunk[i]);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        token.ThrowIfCancellationRequested();
                        var id = reader.GetInt64(0);
                        var name = reader.GetString(1);
                        var hasGps = !reader.IsDBNull(3) && !reader.IsDBNull(4);
                        var dateText = reader.IsDBNull(2) ? null : reader.GetString(2);
                        if (!TryParseCameraTime(dateText, out var local))
                        {
                            assignments.Add(new(id,name,null,null,"Keine Aufnahmezeit",hasGps));
                            continue;
                        }
                        try
                        {
                            var utc = GpxTrackReader.CameraToUtc(local, options);
                            var matches = tracks.Select(t => GpxTrackReader.Match(t,
                                    utc, options.MaximumGapSeconds))
                                .Where(m => m.HasValue).Select(m => m!.Value).ToArray();
                            if (matches.Length > 1 &&
                                matches.Skip(1).Any(m =>
                                    GpxDistanceMeters(matches[0],m) > 35))
                                assignments.Add(new(id,name,null,null,
                                    "Mehrere widersprüchliche GPX-Tracks",hasGps));
                            else if (matches.Length > 0)
                                assignments.Add(new(id,name,matches[0].Latitude,
                                    matches[0].Longitude,
                                    hasGps ? "GPS vorhanden" : "Zuordnung möglich",hasGps));
                            else
                                assignments.Add(new(id,name,null,null,
                                    "Keine passende GPX-Position",hasGps));
                        }
                        catch (ArgumentException)
                        { assignments.Add(new(id,name,null,null,"Ungültige Aufnahmezeit",hasGps)); }
                        catch (InvalidOperationException)
                        { assignments.Add(new(id,name,null,null,"Mehrdeutige lokale Zeit",hasGps)); }
                    }
                }
                return Task.CompletedTask;
            });
            var preview = new GpxImportPreviewWindow(this,
                assignments.Select(a => new GpxImportPreviewRow(a.Id,a.Name,
                    a.Lat,a.Lon,a.Status,a.ExistingGps)).ToArray());
            if (preview.ShowDialog() != true) return;
            var chosen = assignments.Where(x => x.Lat.HasValue && x.Lon.HasValue &&
                (preview.ReplaceExisting || !x.ExistingGps))
                .Select(x => new GpxWriteItem(x.Id,x.Lat!.Value,x.Lon!.Value)).ToArray();
            if (chosen.Length == 0) return;
            var key = "gpx:write:" + Guid.NewGuid().ToString("N");
            var payload = JsonSerializer.Serialize(new GpxWriteRecipe(chosen,
                preview.ReplaceExisting));
            BeginVisibleManualOperation(key, "GPX → Galerie", chosen.Length);
            Task completion;
            try
            {
                completion = _scheduler.EnqueueDurableRecipeAsync(key,
                    "gpx-write-v1", payload,
                    SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
                _taskManagerWindow?.RequestRefresh();
            }
            catch { EndVisibleManualOperation(key); throw; }
            _ = ObserveGpsEditAsync(completion,key);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPX.Import",ex);
            PcasaDialog.Show(this,ex.Message,"GPX",MessageBoxButton.OK,MessageBoxImage.Error);
        }
        finally { GpsGpxToGalleryButton.IsEnabled = true; }
    }

    private static double GpxDistanceMeters(
        (double Latitude,double Longitude) a,
        (double Latitude,double Longitude) b)
    {
        const double rad = Math.PI/180;
        var dLat=(b.Latitude-a.Latitude)*rad;
        var dLon=(b.Longitude-a.Longitude)*rad;
        var x=Math.Pow(Math.Sin(dLat/2),2)+
            Math.Cos(a.Latitude*rad)*Math.Cos(b.Latitude*rad)*
            Math.Pow(Math.Sin(dLon/2),2);
        return 12742000*Math.Asin(Math.Min(1,Math.Sqrt(x)));
    }

    private static bool TryParseCameraTime(string? text, out DateTime value)
    {
        return DateTime.TryParseExact(text,
            ["yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
             "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private SchedulerTaskPackage BuildGpxWritePackage(string key,string payload)
    {
        var recipe = JsonSerializer.Deserialize<GpxWriteRecipe>(payload)
            ?? throw new InvalidDataException("Ungültiger GPX-Auftrag.");
        if (recipe.Items.Length == 0 ||
            recipe.Items.Any(x => x.Id <= 0 || !double.IsFinite(x.Lat) ||
                !double.IsFinite(x.Lon) || x.Lat is < -90 or > 90 ||
                x.Lon is < -180 or > 180))
            throw new InvalidDataException("Ungültige GPX-Koordinaten.");
        var package = new SchedulerTaskPackage(key);
        var completed = 0;
        foreach (var entry in recipe.Items)
        {
            var point = entry;
            package.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                new GpsMetadataRepository(_database).Apply(point.Id,
                    point.Lat,point.Lon,
                    recipe.Replace ? GpsMetadataRepository.EditMode.Replace :
                        GpsMetadataRepository.EditMode.MissingOnly, token,
                    path => _scheduler.ReportProgress(key,"GPX → Galerie",
                        completed,recipe.Items.Length,path),packageKey:key);
                completed++;
                _scheduler.ReportProgress(key,"GPX → Galerie",
                    completed,recipe.Items.Length);
                return Task.CompletedTask;
            });
        }
        return package;
    }
}
