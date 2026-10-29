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
        SaveAppearance();
        if (_sidebarMode == "GPS" && _appearance.ShowGpsFilmstrip)
            UpdateGpsFilmstrip();
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
            GpsFilmstripList.ItemsSource = null;
            return;
        }

        var selected = _selectedImageIds.ToHashSet();
        var mapSelected = _gpsPoints.Where(p => p.Selected).ToDictionary(p => p.Id);
        var entries = new List<GpsFilmstripEntry>();
        var packed = new List<(GpsFilmstripEntry Item, BrowseThumbnailIndexEntry Index)>();
        // Follow the same sort/gallery projection the user sees, not database ID
        // ordering. No source file or NAS roundtrip for the strip.
        for (var imageIndex = 0; imageIndex < _browseIndex.Count; imageIndex++)
        {
            if (entries.Count >= MaximumGpsFilmstripPhotos) break;
            var index = _browseIndex[imageIndex];
            if (!selected.Contains(index.ImageId) || !mapSelected.TryGetValue(index.ImageId, out var point))
                continue;
            var item = new GpsFilmstripEntry { Id = index.ImageId, Name = point.Name };
            entries.Add(item);
            if (_browsePageCache is not null)
            {
                if (_browsePageCache.TryGet(imageIndex, out var cached))
                    item.Bitmap = cached;
            }
            if (item.Bitmap is null) packed.Add((item, index));
        }
        _gpsFilmstripEntries = entries;
        _gpsFilmstripUpdating = true;
        GpsFilmstripList.ItemsSource = entries;
        GpsFilmstripList.SelectedItem = entries.FirstOrDefault(p =>
            p.Id == _gallerySelection.FocusedId);
        _gpsFilmstripUpdating = false;
        GpsFilmstripToggle.Content = UiLanguage.T("Filmstreifen") +
            (selected.Count > MaximumGpsFilmstripPhotos ? " (max. 160)" : "");

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
                "gps:filmstrip:" + revision,
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
            if (GpsPhotoPopup.IsVisible && GpsPhotoPopup.Tag is long imageId)
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
            GpsFilmstripList.SelectedItem is not GpsFilmstripEntry item ||
            !_displayedBrowseIndex.Any(x => x.ImageId == item.Id)) return;
        BrowseSurface_ImageClicked(this,
            new PCasa.App.Controls.BrowseImageClickedEventArgs(item.Id, ModifierKeys.None));
        RestoreGalleryAnchor((item.Id, 0));
        ShowGpsPhotoPopup(item.Id);
    }

    private void GpsFilmstrip_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GpsFilmstripList.SelectedItem is not GpsFilmstripEntry item ||
            !_displayedBrowseIndex.Any(x => x.ImageId == item.Id)) return;
        BrowseSurface_ImageOpened(this,
            new PCasa.App.Controls.BrowseImageClickedEventArgs(item.Id, ModifierKeys.None));
    }

    private void GpsFilmstrip_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The virtualizing WPF strip owns its scroll position; scrolling must
        // never change the gallery selection or map viewport.
    }
}
