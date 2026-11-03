using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    public sealed class GpsFilmstripEntry : INotifyPropertyChanged
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        // These flags come from the SAME gallery thumbnail index; no second
        // EXIF/tag/GPS reader or independent state interpretation.
        private bool _hasGps;
        private bool _hasTags;
        private bool _isRaw;
        private bool _showIcons = true;
        public bool HasGps => _hasGps;
        public bool HasTags => _hasTags;
        public bool IsRaw => _isRaw;
        public bool ShowIcons
        {
            get => _showIcons;
            set
            {
                if (_showIcons == value) return;
                _showIcons = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowIcons)));
            }
        }
        public void UpdateStatus(BrowseThumbnailIndexEntry index)
        {
            if (_hasGps != index.HasGps)
            {
                _hasGps = index.HasGps;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasGps)));
            }
            if (_hasTags != index.HasTags)
            {
                _hasTags = index.HasTags;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTags)));
            }
            if (_isRaw != index.IsRaw)
            {
                _isRaw = index.IsRaw;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRaw)));
            }
        }
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
    // A pinned strip owns a snapshot of image identities/thumbnails, not a
    // second gallery selection or an additional catalog owner.
    private bool _gpsFilmstripFrozen;
    private bool _gpsFilmstripFocusActive;
    private long? _gpsLastMapSourceFocusId;
    private IReadOnlyList<BrowseThumbnailIndexEntry>? _gpsFilmstripStatusIndex;
    private HwndSource? _gpsFilmstripHwnd;
    private int _gpsFilmstripVerticalWheelRemainder;
    private int _gpsFilmstripHorizontalWheelRemainder;
    private const int WmMouseHWheel = 0x020E;

    private void InitializeGpsFilmstrip()
    {
        _gpsFilmstripUpdating = true;
        GpsFilmstripToggle.IsChecked = _appearance.ShowGpsFilmstrip;
        _gpsFilmstripUpdating = false;
        ApplyGpsFilmstripVisibility();
        UpdateGpsFilmstripControls();
        SourceInitialized += GpsFilmstrip_SourceInitialized;
        Closed += GpsFilmstrip_WindowClosed;
    }

    private void GpsFilmstrip_SourceInitialized(object? sender, EventArgs e)
    {
        // Precision touchpads deliver horizontal gestures as WM_MOUSEHWHEEL.
        // Scope the hook to THIS WPF window and only the hovered filmstrip.
        _gpsFilmstripHwnd = PresentationSource.FromVisual(this) as HwndSource;
        _gpsFilmstripHwnd?.AddHook(GpsFilmstrip_NativeWheel);
    }

    private void GpsFilmstrip_WindowClosed(object? sender, EventArgs e)
    {
        _gpsFilmstripHwnd?.RemoveHook(GpsFilmstrip_NativeWheel);
        _gpsFilmstripHwnd = null;
    }

    private IntPtr GpsFilmstrip_NativeWheel(IntPtr hwnd, int message,
        IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseHWheel || !_appearance.ShowGpsFilmstrip ||
            !GpsFilmstripList.IsVisible || !GpsFilmstripList.IsMouseOver)
            return IntPtr.Zero;
        var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        handled = ScrollGpsFilmstripByWheel(delta,
            ref _gpsFilmstripHorizontalWheelRemainder);
        return IntPtr.Zero;
    }

    private void GpsFilmstrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // A normal vertical mouse wheel and a two-finger vertical touchpad
        // gesture navigate the HORIZONTAL strip. Do not scroll the gallery.
        if (ScrollGpsFilmstripByWheel(-e.Delta,
            ref _gpsFilmstripVerticalWheelRemainder))
            e.Handled = true;
    }

    private bool ScrollGpsFilmstripByWheel(int delta, ref int remainder)
    {
        if (!_appearance.ShowGpsFilmstrip || !GpsFilmstripList.IsVisible)
            return false;
        var scroller = GpsFilmstripList.Template.FindName(
            "GpsFilmScrollViewer", GpsFilmstripList) as ScrollViewer;
        if (scroller is null || scroller.ScrollableWidth <= 0)
            return false;

        // The virtualized horizontal stack uses ITEM units. Accumulate
        // high-resolution touchpad deltas; one wheel notch moves three
        // frames. Keep scroll within the native ScrollViewer's extent.
        if (delta != 0 && Math.Sign(remainder) != Math.Sign(delta))
            remainder = 0;
        remainder += delta;
        var steps = remainder / 40;
        remainder -= steps * 40;
        if (steps != 0)
            scroller.ScrollToHorizontalOffset(Math.Clamp(
                scroller.HorizontalOffset + steps, 0, scroller.ScrollableWidth));
        return true;
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
            if (!_gpsFilmstripFrozen) _gpsHighlightedImageId = null;
            if (!_gpsFullscreen && GpsFullscreenImageOverlay.Visibility == Visibility.Visible)
                CloseGpsFullscreenImage();
        }
        SaveAppearance();
        if (_sidebarMode == "GPS" && _appearance.ShowGpsFilmstrip)
            UpdateGpsFilmstrip();
    }

    private void UpdateGpsFilmstripControls()
    {
        if (GpsFilmstripFreezeToggle is null) return;
        GpsFilmstripFreezeCaption.Text = _gpsFilmstripFrozen
            ? UiLanguage.T("Fixiert") : UiLanguage.T("Fixieren");
        GpsFilmstripFreezeToggle.ToolTip = _gpsFilmstripFrozen
            ? UiLanguage.T("Fixierung lösen: Filmstrip folgt wieder der Galerieauswahl.")
            : UiLanguage.T("Filmstrip-Auswahl festhalten. Neue Galerieauswahlen verändern dann nur die Karte.");
        GpsFilmstripCountText.Text = _gpsFilmstripEntries.Count.ToString("N0");
        var source = GpsFilmstripList.SelectedItem as GpsFilmstripEntry;
        var selectedSources = GpsFilmstripList.SelectedItems.Count;
        GpsFilmstripRemoveGpsButton.IsEnabled = selectedSources > 0;
        GpsFilmstripToGalleryButton.IsEnabled = source is not null &&
            selectedSources == 1 &&
            _selectedImageIds.Count > 0 &&
            _gpsPoints.Any(p => p.Id == source.Id);
    }

    private void GpsFilmstripFreeze_Changed(object sender, RoutedEventArgs e)
    {
        if (_gpsFilmstripUpdating) return;
        _gpsFilmstripFrozen = GpsFilmstripFreezeToggle.IsChecked == true;
        _gpsFilmstripPointIds = null;
        if (!_gpsFilmstripFrozen)
        {
            _gpsFilmstripFocusActive = false;
            UpdateGpsFilmstrip();
            _ = RefreshGpsMapAsync();
        }
        UpdateGpsFilmstripControls();
    }

    private void GpsFilmstripToGallery_Click(object sender, RoutedEventArgs e)
    {
        if (GpsFilmstripList.SelectedItems.Count != 1 ||
            GpsFilmstripList.SelectedItem is not GpsFilmstripEntry source ||
            _selectedImageIds.Count == 0)
            return;
        var point = _gpsPoints.FirstOrDefault(p => p.Id == source.Id);
        if (point is null) return;
        _ = CommitGpsEditAsync(GpsMetadataRepository.EditMode.Replace,
            (point.Lat, point.Lon));
    }

    private void GpsFilmstripRemoveGps_Click(object sender, RoutedEventArgs e)
    {
        var sources = GpsFilmstripList.SelectedItems
            .OfType<GpsFilmstripEntry>().Select(entry => entry.Id)
            .Distinct().ToArray();
        if (sources.Length == 0) return;
        // Immutable filmstrip IDs are handed to the existing durable P0
        // GPS writer. Never substitute/clear the gallery target selection.
        _ = CommitGpsEditAsync(GpsMetadataRepository.EditMode.Remove,
            explicitTargetIds: sources);
    }

    private void GpsFilmstrip_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Make standard Explorer Ctrl+A explicit even when the virtualized
        // film has only a small subset of its ListBoxItems realized.
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            GpsFilmstripList.SelectAll();
            e.Handled = true;
        }
    }

    // A clicked cluster is a temporary VIEW of its photo identities, not a
    // new gallery selection or an additional query/scheduler. Reuse the one
    // existing filmstrip and its packed-thumbnail reader.
    private void ShowGpsPointGroup(HashSet<long> ids, long anchorId)
    {
        if (ids.Count == 0 || !ids.Contains(anchorId)) return;
        if (_gpsFilmstripFrozen)
        {
            // Clicking map clusters never replaces a pinned source set.
            HighlightGpsPointOnMap(anchorId);
            if (ids.Count == 1 && !_gpsFullscreen)
                ShowGpsPhotoPopup(anchorId);
            return;
        }
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
        if (_gpsFilmstripFrozen)
        {
            // Keep the source snapshot, but refresh lightweight status flags
            // if the catalog index has changed after tag/GPS writes.
            if (!ReferenceEquals(_gpsFilmstripStatusIndex, _browseIndex))
            {
                var visible = _gpsFilmstripEntries.ToDictionary(item => item.Id);
                if (visible.Count > 0)
                    foreach (var index in _browseIndex)
                        if (visible.TryGetValue(index.ImageId, out var item))
                            item.UpdateStatus(index);
                _gpsFilmstripStatusIndex = _browseIndex;
            }
            foreach (var item in _gpsFilmstripEntries)
                item.ShowIcons = _appearance.ShowThumbnailIcons;
            // A gallery search, folder change or scan does not replace this
            // snapshot. Keep the existing selected item and packed thumbnails.
            if (_sidebarMode == "GPS" && _appearance.ShowGpsFilmstrip &&
                !ReferenceEquals(GpsFilmstripList.ItemsSource, _gpsFilmstripEntries))
                GpsFilmstripList.ItemsSource = _gpsFilmstripEntries;
            UpdateGpsFilmstripControls();
            return;
        }
        var revision = ++_gpsFilmstripRevision;
        if (_sidebarMode != "GPS" || !_appearance.ShowGpsFilmstrip)
        {
            _gpsFilmstripEntries = [];
            _gpsFilmstripIndexes.Clear();
            _gpsFilmstripRequestedIds.Clear();
            _gpsFilmstripStatusIndex = null;
            GpsFilmstripList.ItemsSource = null;
            UpdateGpsFilmstripControls();
            return;
        }

        var selected = _gpsFilmstripPointIds ?? _selectedImageIds.ToHashSet();
        _gpsFilmstripIndexes.Clear();
        _gpsFilmstripRequestedIds.Clear();
        // The filmstrip is ONLY a GPS source set. The main gallery remains
        // the unrestricted target selection, including images without GPS.
        // Use the same catalog index status as the gallery badges.
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
            if (!selected.Contains(index.ImageId) || !index.HasGps)
                continue;
            var item = new GpsFilmstripEntry
            {
                Id = index.ImageId,
                Name = mapNames.TryGetValue(index.ImageId, out var gpsName)
                    ? gpsName : index.FileName,
                ShowIcons = _appearance.ShowThumbnailIcons
            };
            item.UpdateStatus(index);
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
        _gpsFilmstripStatusIndex = _browseIndex;
        _gpsFilmstripUpdating = true;
        GpsFilmstripList.ItemsSource = entries;
        GpsFilmstripList.SelectedItem = entries.FirstOrDefault(p =>
            p.Id == _gpsHighlightedImageId)
            ?? entries.FirstOrDefault(p => p.Id == _gallerySelection.FocusedId);
        _gpsFilmstripUpdating = false;
        UpdateGpsFilmstripControls();
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
            // The map-click photo popup and the filmstrip share the same
            // asynchronous packed-thumbnail reader. A click can arrive
            // before the bitmap is decoded; update its preview when ready,
            // even though the filmstrip has been opened for that point.
            if (GpsPhotoPopup.IsVisible &&
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
        if (_gpsFilmstripUpdating) return;
        UpdateGpsFilmstripControls();

        // Explorer multi-selection: added items identify the last explicitly
        // focused source. The selection can contain many independently of
        // the gallery; the MAP still previews only this one source.
        var item = e.AddedItems.OfType<GpsFilmstripEntry>().LastOrDefault()
            ?? GpsFilmstripList.SelectedItem as GpsFilmstripEntry;
        if (item is null)
        {
            _gpsFilmstripFocusActive = false;
            _gpsHighlightedImageId = null;
            _ = RefreshGpsMapAsync();
            return;
        }
        FocusGpsPhotoForDisplay(item.Id);
        GpsPhotoPopup.Visibility = Visibility.Collapsed;
        _gpsFilmstripFocusActive = true;
        _ = RefreshGpsMapAsync();
        // Only an ALREADY OPEN image follows a source click. The normal
        // gallery selection remains intact even with Ctrl/Shift/Ctrl+A.
        if (GpsFullscreenImageOverlay.Visibility == Visibility.Visible &&
            GpsFullscreenImageOverlay.Tag is long shownId && shownId != item.Id)
            ShowGpsFullscreenImage(item.Id);
    }

    private void GpsFilmstrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Clicking the already selected source must still refocus its map
        // position after changing the gallery selection.
        if (_gpsFilmstripFocusActive ||
            GpsFilmstripList.SelectedItem is not GpsFilmstripEntry item)
            return;
        FocusGpsPhotoForDisplay(item.Id);
        _gpsFilmstripFocusActive = true;
        _ = RefreshGpsMapAsync();
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
        if ((!_gpsFilmstripFrozen && _gpsFilmstripPointIds is null) ||
            !_appearance.ShowGpsFilmstrip ||
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
