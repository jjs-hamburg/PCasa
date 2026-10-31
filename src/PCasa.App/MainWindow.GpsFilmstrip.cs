using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    public sealed class GpsFilmstripEntry : INotifyPropertyChanged
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        private BitmapSource? _bitmap;
        public BitmapSource? Bitmap
        {
            get => _bitmap;
            set
            {
                if (ReferenceEquals(_bitmap, value)) return;
                _bitmap = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitmap)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private const int MaximumGpsFilmstripPhotos = 160;
    private long _gpsFilmstripRevision;
    private bool _gpsFilmstripUpdating;
    private List<GpsFilmstripEntry> _gpsFilmstripEntries = [];
    private HashSet<long>? _gpsFilmstripPointIds;
    private readonly Dictionary<long, BrowseThumbnailIndexEntry> _gpsFilmstripIndexes = [];
    private readonly HashSet<long> _gpsFilmstripRequestedIds = [];

    private void InitializeGpsFilmstrip()
    {
        _gpsFilmstripUpdating = true;
        GpsFilmstripToggle.IsChecked = _appearance.ShowGpsFilmstrip;
        _gpsFilmstripUpdating = false;
        ApplyGpsFilmstripVisibility();
    }

    private void ApplyGpsFilmstripVisibility()
        => GpsFilmstripPanel.Visibility = _appearance.ShowGpsFilmstrip
            ? Visibility.Visible : Visibility.Collapsed;

    private void GpsFilmstripToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_gpsFilmstripUpdating) return;
        _appearance.ShowGpsFilmstrip = GpsFilmstripToggle.IsChecked == true;
        ApplyGpsFilmstripVisibility();
        if (_appearance.ShowGpsFilmstrip)
            GpsPhotoPopup.Visibility = Visibility.Collapsed;
        else
        {
            _gpsFilmstripPointIds = null;
            _gpsHighlightedImageId = null;
            if (!_gpsFullscreen && GpsFullscreenImageOverlay.Visibility == Visibility.Visible)
                CloseGpsFullscreenImage();
        }
        SaveAppearance();
        if (_sidebarMode == "GPS" && _appearance.ShowGpsFilmstrip)
            UpdateGpsFilmstrip();
    }

    // A clicked cluster is a temporary VIEW of its photo identities, not a
    // new gallery selection or an additional query/scheduler. Reuse the one
    // existing filmstrip and its packed-thumbnail reader.
    private void ShowGpsPointGroup(HashSet<long> ids, long anchorId)
    {
        if (ids.Count == 0 || !ids.Contains(anchorId)) return;
        _gpsFilmstripPointIds = ids;
        if (!_appearance.ShowGpsFilmstrip)
        {
            _appearance.ShowGpsFilmstrip = true;
            _gpsFilmstripUpdating = true;
            GpsFilmstripToggle.IsChecked = true;
            _gpsFilmstripUpdating = false;
            ApplyGpsFilmstripVisibility();
            SaveAppearance();
        }
        FocusGpsPhotoForDisplay(anchorId);
        GpsPhotoPopup.Visibility = Visibility.Collapsed;
        UpdateGpsFilmstrip();
        HighlightGpsPointOnMap(anchorId);
    }

    // Called from the same catalog selection publication as the map markers.
    // This strip is a projection of the selected IDs, never another selection
    // owner, independent catalog or thumbnail-processing pipeline.
    private void UpdateGpsFilmstrip()
    {
        var revision = ++_gpsFilmstripRevision;
        if (_sidebarMode != "GPS" || !_appearance.ShowGpsFilmstrip)
        {
            _gpsFilmstripEntries = [];
            _gpsFilmstripIndexes.Clear();
            _gpsFilmstripRequestedIds.Clear();
            GpsFilmstripList.ItemsSource = null;
            return;
        }

        var selected = _gpsFilmstripPointIds ?? _selectedImageIds.ToHashSet();
        _gpsFilmstripIndexes.Clear();
        _gpsFilmstripRequestedIds.Clear();
        // The strip is a projection of the GALLERY SELECTION, not of EXIF GPS
        // coverage. A selected photo must appear even when coordinates are
        // absent, invalid or awaiting reindexing.
        var mapNames = _gpsPoints.ToDictionary(p => p.Id, p => p.Name);
        var entries = new List<GpsFilmstripEntry>();
        var packed = new List<(GpsFilmstripEntry Item, BrowseThumbnailIndexEntry Index)>();
        // Follow the same sort/gallery projection the user sees, not database ID
        // ordering. No source file or NAS roundtrip for the strip.
        for (var imageIndex = 0; imageIndex < _browseIndex.Count; imageIndex++)
        {
            // Normal gallery selections retain the existing 160-photo bound.
            // A clicked map cell must expose ALL its matching photo IDs; the
            // WPF filmstrip is virtualized, and previews load by scroll page.
            if (_gpsFilmstripPointIds is null &&
                entries.Count >= MaximumGpsFilmstripPhotos) break;
            var index = _browseIndex[imageIndex];
            if (!selected.Contains(index.ImageId))
                continue;
            var item = new GpsFilmstripEntry
            {
                Id = index.ImageId,
                Name = mapNames.TryGetValue(index.ImageId, out var gpsName)
                    ? gpsName : index.FileName
            };
            entries.Add(item);
            _gpsFilmstripIndexes[item.Id] = index;
            if (_browsePageCache is not null)
            {
                if (_browsePageCache.TryGet(imageIndex, out var cached))
                    item.Bitmap = cached;
            }
            if (item.Bitmap is null &&
                (_gpsFilmstripPointIds is null ||
                 entries.Count <= MaximumGpsFilmstripPhotos))
            {
                packed.Add((item, index));
                _gpsFilmstripRequestedIds.Add(item.Id);
            }
        }
        _gpsFilmstripEntries = entries;
        _gpsFilmstripUpdating = true;
        GpsFilmstripList.ItemsSource = entries;
        GpsFilmstripList.SelectedItem = entries.FirstOrDefault(p =>
            p.Id == _gpsHighlightedImageId)
            ?? entries.FirstOrDefault(p => p.Id == _gallerySelection.FocusedId);
        _gpsFilmstripUpdating = false;
        GpsFilmstripToggle.ToolTip = UiLanguage.T("Filmstreifen") +
            (_gpsFilmstripPointIds is null ? "" :
                $" · {selected.Count:N0} Fotos am Kartenpunkt") +
            (_gpsFilmstripPointIds is null &&
             selected.Count > MaximumGpsFilmstripPhotos ? " (max. 160)" : "");

        if (packed.Count == 0) return;
        _ = LoadGpsFilmstripBitmapsAsync(packed, revision);
    }

    private async Task LoadGpsFilmstripBitmapsAsync(
        List<(GpsFilmstripEntry Item, BrowseThumbnailIndexEntry Index)> pending,
        long revision)
    {
        try
        {
            // One scheduled, cancellable read package. One packed-file reader
            // owns its file handles; it never touches the originals.
            var images = await _scheduler.RunAsync(
                // Scroll pages are independent reads of the SAME packed
                // thumbnail store; never coalesce two different page requests.
                "gps:filmstrip:" + revision + ":" + Guid.NewGuid().ToString("N"),
                SchedulerPriority.Interactive,
                token =>
                {
                    using var reader = new PackedThumbnailReader();
                    var result = new List<(GpsFilmstripEntry Item, BitmapSource Bitmap)>();
                    foreach (var (item, index) in pending)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (reader.Load(index, token) is { } bitmap)
                                result.Add((item, bitmap));
                        }
                        catch (System.IO.IOException) { /* missing local packed cache */ }
                    }
                    return result;
                },
                () => revision == _gpsFilmstripRevision &&
                    _sidebarMode == "GPS" && _appearance.ShowGpsFilmstrip,
                workKind: SchedulerWorkKind.ThumbnailRead);
            if (revision != _gpsFilmstripRevision || _sidebarMode != "GPS" ||
                !_appearance.ShowGpsFilmstrip) return;
            foreach (var (item, bitmap) in images)
                item.Bitmap = bitmap;
            if (!_appearance.ShowGpsFilmstrip && GpsPhotoPopup.IsVisible &&
                GpsPhotoPopup.Tag is long imageId)
            {
                var popupItem = _gpsFilmstripEntries.FirstOrDefault(i => i.Id == imageId);
                if (popupItem?.Bitmap is { } bmp)
                {
                    GpsPhotoPreview.Source = bmp;
                    GpsPhotoPreview.Visibility = Visibility.Visible;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.Filmstrip", ex);
        }
    }

    private void GpsFilmstrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_gpsFilmstripUpdating ||
            GpsFilmstripList.SelectedItem is not GpsFilmstripEntry item) return;
        // A single click changes only the map highlight, NOT gallery
        // selection, folder focus or the map photo popup.
        FocusGpsPhotoForDisplay(item.Id);
        GpsPhotoPopup.Visibility = Visibility.Collapsed;
        HighlightGpsPointOnMap(item.Id);
        // Only an ALREADY OPEN image follows single clicks. Ordinary strip
        // navigation remains map-highlight only and never changes gallery
        // selection, map scale or the current map viewport.
        if (GpsFullscreenImageOverlay.Visibility == Visibility.Visible &&
            GpsFullscreenImageOverlay.Tag is long shownId && shownId != item.Id)
            ShowGpsFullscreenImage(item.Id);
    }

    private void GpsFilmstrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GpsFilmstripList.SelectedItem is not GpsFilmstripEntry item) return;
        // Never route through BrowseSurface_ImageOpened here: its
        // SelectViewerImage deliberately replaces the multiple selection.
        // The one in-place GPS photo display stays open and subsequent
        // single clicks on filmstrip photos replace its image.
        if (GpsFullscreenImageOverlay.Visibility != Visibility.Visible ||
            GpsFullscreenImageOverlay.Tag is not long displayedId ||
            displayedId != item.Id)
            ShowGpsFullscreenImage(item.Id);
    }

    private void GpsFilmstrip_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The virtualizing WPF strip owns its scroll position; scrolling must
        // never change the gallery selection or map viewport. Load only the
        // newly visible page for point groups, never thousands of bitmaps.
        if (_gpsFilmstripPointIds is null || !_appearance.ShowGpsFilmstrip ||
            _gpsFilmstripEntries.Count == 0 || e.HorizontalChange == 0)
            return;
        var first = Math.Clamp((int)Math.Floor(e.HorizontalOffset) - 8,
            0, _gpsFilmstripEntries.Count - 1);
        var count = Math.Clamp((int)Math.Ceiling(e.ViewportWidth) + 16, 40, 96);
        var pending = new List<(GpsFilmstripEntry Item, BrowseThumbnailIndexEntry Index)>();
        for (var i = first; i < Math.Min(_gpsFilmstripEntries.Count, first + count); i++)
        {
            var item = _gpsFilmstripEntries[i];
            if (item.Bitmap is not null ||
                !_gpsFilmstripIndexes.TryGetValue(item.Id, out var index) ||
                !_gpsFilmstripRequestedIds.Add(item.Id))
                continue;
            pending.Add((item, index));
        }
        if (pending.Count > 0)
            _ = LoadGpsFilmstripBitmapsAsync(pending, _gpsFilmstripRevision);
    }
}
