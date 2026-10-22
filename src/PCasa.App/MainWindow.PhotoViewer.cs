using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using PCasa.App.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _viewerOpen;
    private int _viewerCurrentIndex = -1;
    private double _viewerOpeningScrollOffset;
    private CancellationTokenSource? _viewerDecodeCancellation;
    private int _viewerImageRequest;
    private bool _viewerHasOriginal;
    private Window? _viewerFullscreenWindow;
    private Image? _viewerFullscreenImage;
    private ScrollViewer? _viewerFullscreenScroller;
    private Grid? _viewerFullscreenCanvas;
    private PhotoZoomNavigator? _viewerFullscreenNavigator;
    private UIElement? _viewerPanSurface;
    private Point _viewerPanStart;
    private bool _viewerDragMoved;
    private DispatcherTimer? _viewerSingleClickTimer;
    private double _viewerPanOffsetX;
    private double _viewerPanOffsetY;
    private (double X, double Y)? _viewerPendingImageCenter;
    private long _viewerNavigatorImageId = -1;
    private bool _viewerNavigatorWired;
    private CancellationTokenSource? _viewerPrefetchCancellation;
    private readonly Dictionary<long, (BitmapSource Bitmap, int RequiredSize)> _viewerOriginalCache = new();
    private readonly LinkedList<long> _viewerOriginalLru = new();
    private const long ViewerOriginalCacheLimit = 128L * 1024 * 1024;
    private PhotoViewerImageInfo? _viewerImageInfo;
    private bool _viewerFitToScreen = true;
    private double _viewerZoomPercent = 100;
    private bool _viewerUpdatingZoomControls;
    private long _viewerSuppressMouseUntil;
    private int _viewerZoomLayoutVersion;
    private CancellationTokenSource? _viewerZoomCancellation;

    private void BrowseSurface_ImageOpened(
        object? sender,
        BrowseImageClickedEventArgs e)
    {
        if (_viewerOpen || _browseCacheBuilding || _browseIndex.Count == 0)
        {
            return;
        }

        var index = FindBrowseImageIndex(e.ImageId);
        if (index < 0)
        {
            return;
        }

        // Never change the gallery's viewport while the viewer is open.
        _viewerOpeningScrollOffset = BrowseScrollViewer.VerticalOffset;
        _viewerCurrentIndex = index;
        _viewerOpen = true;
        if (!_viewerNavigatorWired)
        {
            PhotoViewerNavigator.CenterRequested += ViewerNavigator_CenterRequested;
            _viewerNavigatorWired = true;
        }

        _viewerNavigatorImageId = -1;
        _viewerFitToScreen = true;
        _viewerZoomPercent = 100;
        FolderTreePanel.Visibility = Visibility.Collapsed;
        ViewerEditingPanel.Visibility = Visibility.Visible;
        ThumbnailSizeToolbar.Visibility = Visibility.Collapsed;
        BrowseToolbar.Visibility = Visibility.Collapsed;
        ViewerToolbar.Visibility = Visibility.Visible;
        PhotoZoomToolbar.Visibility = Visibility.Visible;
        PhotoViewerPanel.Visibility = Visibility.Visible;
        Keyboard.Focus(PhotoViewerPanel);
        SelectViewerImage(e.ImageId);
        UpdateComparisonButtons();
        ShowViewerImage();
    }

    private int FindBrowseImageIndex(long imageId)
    {
        for (var i = 0; i < _browseIndex.Count; i++)
        {
            if (_browseIndex[i].ImageId == imageId)
            {
                return i;
            }
        }

        return -1;
    }

    private void SelectViewerImage(long imageId)
    {
        _selectedImageIds.Clear();
        _selectedImageIds.Add(imageId);
        BrowseSurface.SetSelection(_selectedImageIds);
        SelectionText.Text = "Auswahl: 1";
        UpdateComparisonButtons();
        _ = UpdateSidebarContentAsync();
    }

    private async void ShowViewerImage(bool keepCurrentAsPlaceholder = false)
    {
        if (!_viewerOpen || _viewerCurrentIndex < 0 ||
            _viewerCurrentIndex >= _browseIndex.Count)
        {
            return;
        }

        _viewerDecodeCancellation?.Cancel();
        _viewerDecodeCancellation?.Dispose();
        _viewerDecodeCancellation = new CancellationTokenSource();
        var token = _viewerDecodeCancellation.Token;
        var version = ++_viewerImageRequest;
        _viewerPrefetchCancellation?.Cancel();
        _viewerPrefetchCancellation?.Dispose();
        _viewerPrefetchCancellation = null;
        _viewerHasOriginal = false;
        var entry = _browseIndex[_viewerCurrentIndex];
        ViewerToolbarImageTitle.Text = $"{_viewerCurrentIndex + 1:N0} / {_browseIndex.Count:N0}";
        if (_viewerNavigatorImageId != entry.ImageId)
        {
            _viewerNavigatorImageId = entry.ImageId;
            PhotoViewerNavigator.ShowForNewImage();
            _viewerFullscreenNavigator?.ShowForNewImage();
        }

        // A blocked SQLite reader must not freeze navigation/zoom on WPF.
        // The request version fences asynchronous metadata from an older photo.
        PhotoViewerImageInfo? resolvedInfo = null;
        try
        {
            await _scheduler.EnqueueAsync(
                $"catalog:viewer-info:{entry.ImageId}:{version}",
                SchedulerPriority.Interactive,
                readToken =>
                {
                    readToken.ThrowIfCancellationRequested();
                    resolvedInfo = new PhotoViewerImageRepository(_database).Get(entry.ImageId);
                    return Task.CompletedTask;
                }, cancellationToken: token,
                workKind: SchedulerWorkKind.CatalogRead);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) { /* The prepared preview remains usable. */ }
        if (!IsCurrentViewerRequest(version, token)) return;
        _viewerImageInfo = resolvedInfo;

        if (_viewerImageInfo is { } infoForTitle)
        {
            ViewerToolbarImageTitle.Text =
                $"{_viewerCurrentIndex + 1:N0} / {_browseIndex.Count:N0} · {infoForTitle.FileName}";
        }

        ApplyViewerImageLayout();
        var size = GetViewerRequiredPixelSize();

        if (TryGetViewerOriginal(entry.ImageId, size, out var decoded))
        {
            _viewerHasOriginal = true;
            SetViewerBitmap(decoded);
            SetViewerQualityMessage(null);
            StartViewerNeighborPrefetch(size);
            return;
        }

        if (!keepCurrentAsPlaceholder)
        {
            // Display the existing Picasa browse-cache picture immediately.
            if (_browsePageCache?.TryGet(_viewerCurrentIndex, out var cached) == true &&
                cached is not null)
            {
                SetViewerBitmap(cached);
            }
            else
            {
                SetViewerBitmap(null);
                _ = LoadViewerPackedPreviewAsync(entry, version, token);
            }
        }

        if (_viewerImageInfo is { } selected && IsPathKnownOffline(selected.LocalPath))
        {
            // The local pack thumbnail is still useful even when its source NAS
            // is offline. Never attempt repeated synchronous OS network probes.
            SetViewerQualityMessage("Original momentan nicht erreichbar");
            return;
        }

        SetViewerQualityMessage("Verfeinerung …");
        _ = LoadViewerOriginalAsync(entry.ImageId, size, version, token);
    }

    private void SetViewerQualityMessage(string? message)
    {
        PhotoViewerQualityText.Text = message ?? string.Empty;
        PhotoViewerQualityBanner.Visibility =
            string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool TryGetViewerOriginal(long id, int requiredSize, out BitmapSource? bitmap)
    {
        if (_viewerOriginalCache.TryGetValue(id, out var cached) &&
            cached.RequiredSize >= requiredSize)
        {
            _viewerOriginalLru.Remove(id);
            _viewerOriginalLru.AddLast(id);
            bitmap = cached.Bitmap;
            return true;
        }

        bitmap = null;
        return false;
    }

    private void CacheViewerOriginal(long id, int requiredSize, BitmapSource bitmap)
    {
        if (_viewerOriginalCache.TryGetValue(id, out var existing) &&
            existing.RequiredSize > requiredSize)
        {
            return;
        }

        _viewerOriginalCache[id] = (bitmap, requiredSize);
        _viewerOriginalLru.Remove(id);
        _viewerOriginalLru.AddLast(id);

        // RAM remains bounded even when huge portrait/original images are viewed.
        long bytes = _viewerOriginalCache.Values.Sum(
            item => (long)item.Bitmap.PixelWidth * item.Bitmap.PixelHeight * 4);
        while (_viewerOriginalCache.Count > 1 &&
               (_viewerOriginalCache.Count > 3 || bytes > ViewerOriginalCacheLimit))
        {
            var oldest = _viewerOriginalLru.First!.Value;
            _viewerOriginalLru.RemoveFirst();
            var removed = _viewerOriginalCache[oldest];
            _viewerOriginalCache.Remove(oldest);
            bytes -= (long)removed.Bitmap.PixelWidth * removed.Bitmap.PixelHeight * 4;
        }
    }

    private void StartViewerNeighborPrefetch(int requiredSize)
    {
        _viewerPrefetchCancellation?.Cancel();
        _viewerPrefetchCancellation?.Dispose();
        _viewerPrefetchCancellation = null;

        // Neighbor preloading at native 100% could allocate multiple huge
        // 40+ megapixel images and flood an NAS. Only prefetch normal fit-sized
        // views; at high zoom, wait for explicit navigation.
        if (requiredSize > 4096)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _viewerPrefetchCancellation = cancellation;
        var origin = _viewerCurrentIndex;
        _ = PrefetchViewerNeighborsAsync(origin, requiredSize, cancellation.Token);
    }

    private async Task PrefetchViewerNeighborsAsync(
        int origin, int requiredSize, CancellationToken token)
    {
        // Only one prefetch chain at a time, with strictly sequential reads.
        // Visible-image decoding always begins first, so prefetch cannot block it.
        foreach (var index in new[] { origin + 1, origin - 1 })
        {
            if (token.IsCancellationRequested || !_viewerOpen ||
                _viewerCurrentIndex != origin || index < 0 || index >= _browseIndex.Count)
            {
                return;
            }

            var id = _browseIndex[index].ImageId;
            if (TryGetViewerOriginal(id, requiredSize, out _))
            {
                continue;
            }

            try
            {
                BitmapSource? bitmap = null;
                await _scheduler.EnqueueAsync(
                    $"viewer-prefetch:{origin}:{id}:{requiredSize}",
                    SchedulerPriority.Background,
                    schedulerToken =>
                    {
                        schedulerToken.ThrowIfCancellationRequested();
                        var info = new PhotoViewerImageRepository(_database).Get(id);
                        bitmap = info is null || IsPathKnownOffline(info.LocalPath)
                            ? null : PhotoViewerImageLoader.Load(info, requiredSize, schedulerToken);
                        return Task.CompletedTask;
                    }, cancellationToken: token, workKind: SchedulerWorkKind.ViewerRead);

                if (bitmap is not null && !token.IsCancellationRequested &&
                    _viewerOpen && _viewerCurrentIndex == origin)
                {
                    CacheViewerOriginal(id, requiredSize, bitmap);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Missing neighbors must never interrupt the current image.
            }
        }
    }

    private void SetViewerBitmap(BitmapSource? bitmap)
    {
        // The catalog may contain the physical, unrotated dimensions of a
        // JPEG with EXIF Orientation 5-8. An oriented decoder bitmap then has
        // the opposite aspect ratio. Correct the VIEW geometry before assigning
        // it: Stretch.Fill must never squeeze a portrait into a landscape box.
        // Keep the original pixel count for genuine 100% zoom; use only the
        // decoded bitmap's aspect to identify the axis swap.
        if (bitmap is { PixelWidth: > 0, PixelHeight: > 0 } &&
            _viewerImageInfo is { Width: > 0, Height: > 0 } info &&
            ViewerImageGeometry.NeedsAxisSwap(info.Width, info.Height,
                bitmap.PixelWidth, bitmap.PixelHeight))
        {
            _viewerImageInfo = info with { Width = info.Height, Height = info.Width };
            ApplyViewerImageLayout();
        }
        PhotoViewerImage.Source = bitmap;
        if (_viewerFullscreenImage is not null)
        {
            _viewerFullscreenImage.Source = bitmap;
        }

        UpdateViewerNavigator();
    }

    private bool IsCurrentViewerRequest(int version, CancellationToken token)
        => _viewerOpen && !token.IsCancellationRequested &&
           version == _viewerImageRequest;

    private async Task LoadViewerPackedPreviewAsync(
        BrowseThumbnailIndexEntry entry,
        int version,
        CancellationToken token)
    {
        try
        {
            BitmapSource? preview = null;
            await _scheduler.EnqueueAsync(
                $"viewer-preview:{entry.ImageId}:{version}",
                SchedulerPriority.UserAction,
                schedulerToken =>
                {
                    using var reader = new PackedThumbnailReader();
                    preview = reader.Load(entry, schedulerToken);
                    return Task.CompletedTask;
                }, cancellationToken: token, workKind: SchedulerWorkKind.ViewerRead);

            if (IsCurrentViewerRequest(version, token) && !_viewerHasOriginal &&
                preview is not null)
            {
                SetViewerBitmap(preview);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Originals can be temporarily offline. The prepared preview is optional.
        }
    }

    private async Task LoadViewerOriginalAsync(
        long imageId,
        int requiredSize,
        int version,
        CancellationToken token)
    {
        try
        {
            BitmapSource? bitmap = null;
            await _scheduler.EnqueueAsync(
                $"viewer-original:{imageId}:{version}:{requiredSize}",
                SchedulerPriority.Interactive,
                schedulerToken =>
                {
                    schedulerToken.ThrowIfCancellationRequested();
                    var info = new PhotoViewerImageRepository(_database).Get(imageId);
                    if (info is null)
                        throw new System.IO.FileNotFoundException(
                            "Bild ist nicht mehr im Katalog.");
                    bitmap = PhotoViewerImageLoader.Load(info, requiredSize, schedulerToken);
                    return Task.CompletedTask;
                }, cancellationToken: token, workKind: SchedulerWorkKind.ViewerRead);
            if (bitmap is null) throw new System.IO.IOException("Bild konnte nicht geladen werden.");

            if (IsCurrentViewerRequest(version, token))
            {
                _viewerHasOriginal = true;
                CacheViewerOriginal(imageId, requiredSize, bitmap);
                SetViewerBitmap(bitmap);
                SetViewerQualityMessage(null);
                StartViewerNeighborPrefetch(requiredSize);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentViewerRequest(version, token))
            {
                // Never silently leave a soft 192/384px cache image on screen.
                SetViewerQualityMessage("Original nicht verfügbar: " + ex.Message);
            }
        }
    }

    private ScrollViewer ActiveViewerScroller
        => _viewerFullscreenScroller ?? PhotoViewerScroller;

    private Grid ActiveViewerCanvas
        => _viewerFullscreenCanvas ?? PhotoViewerCanvas;

    private Image ActiveViewerImage
        => _viewerFullscreenImage ?? PhotoViewerImage;

    private FrameworkElement ActiveViewerRoot
        => (FrameworkElement?)_viewerFullscreenWindow ?? PhotoViewerPanel;

    private int GetViewerRequiredPixelSize()
    {
        var info = _viewerImageInfo;
        var (viewportWidth, viewportHeight) = ViewerViewportSize();
        var dpi = VisualTreeHelper.GetDpi(ActiveViewerRoot);
        if (info is not { Width: > 0, Height: > 0 })
        {
            return (int)Math.Clamp(Math.Ceiling(Math.Max(
                viewportWidth * dpi.DpiScaleX,
                viewportHeight * dpi.DpiScaleY)), 1, 32768);
        }

        var longest = Math.Max(info.Width, info.Height);
        var factor = _viewerFitToScreen
            ? Math.Min(
                viewportWidth * dpi.DpiScaleX / info.Width,
                viewportHeight * dpi.DpiScaleY / info.Height)
            : _viewerZoomPercent / 100d;
        // 100% always requests ORIGINAL pixels. Higher zoom only enlarges them.
        return (int)Math.Clamp(Math.Min(longest, Math.Ceiling(longest * factor)),
            1, 32768);
    }

    private (double Width, double Height) ViewerViewportSize()
    {
        var viewer = ActiveViewerScroller;
        var width = viewer.ViewportWidth;
        var height = viewer.ViewportHeight;
        if (width < 30 || height < 30)
        {
            if (_viewerFullscreenWindow is not null)
            {
                width = Math.Max(width, Math.Max(
                    _viewerFullscreenWindow.ActualWidth,
                    SystemParameters.PrimaryScreenWidth));
                height = Math.Max(height, Math.Max(
                    _viewerFullscreenWindow.ActualHeight,
                    SystemParameters.PrimaryScreenHeight));
            }
            else
            {
                width = Math.Max(width, BrowseScrollViewer.ActualWidth);
                height = Math.Max(height, BrowseScrollViewer.ActualHeight);
            }
        }

        return (Math.Max(1, width), Math.Max(1, height));
    }

    private (double X, double Y) CaptureViewerCenter()
    {
        var scroller = ActiveViewerScroller;
        var image = ActiveViewerImage;
        var canvas = ActiveViewerCanvas;
        var (viewportWidth, viewportHeight) = ViewerViewportSize();
        var canvasWidth = Math.Max(1, double.IsNaN(canvas.Width) ? viewportWidth : canvas.Width);
        var canvasHeight = Math.Max(1, double.IsNaN(canvas.Height) ? viewportHeight : canvas.Height);
        var imageWidth = Math.Max(1, double.IsNaN(image.Width) ? viewportWidth : image.Width);
        var imageHeight = Math.Max(1, double.IsNaN(image.Height) ? viewportHeight : image.Height);
        var left = Math.Max(0, (canvasWidth - imageWidth) / 2);
        var top = Math.Max(0, (canvasHeight - imageHeight) / 2);
        return (
            Math.Clamp((scroller.HorizontalOffset + viewportWidth / 2 - left) / imageWidth, 0, 1),
            Math.Clamp((scroller.VerticalOffset + viewportHeight / 2 - top) / imageHeight, 0, 1));
    }

    private double CurrentViewerZoomPercent()
    {
        if (!_viewerFitToScreen)
        {
            return _viewerZoomPercent;
        }

        if (_viewerImageInfo is not { Width: > 0, Height: > 0 } info)
        {
            return 100;
        }

        var (width, height) = ViewerViewportSize();
        var dpi = VisualTreeHelper.GetDpi(ActiveViewerRoot);
        return 100d * Math.Min(
            width * dpi.DpiScaleX / info.Width,
            height * dpi.DpiScaleY / info.Height);
    }

    private void ApplyViewerImageLayout()
    {
        if (!_viewerOpen)
        {
            return;
        }

        var info = _viewerImageInfo;
        if (info is not { Width: > 0, Height: > 0 })
        {
            UpdateViewerZoomControls(_viewerZoomPercent);
            return;
        }

        var scroller = ActiveViewerScroller;
        var canvas = ActiveViewerCanvas;
        var image = ActiveViewerImage;
        var (viewportWidth, viewportHeight) = ViewerViewportSize();
        var dpi = VisualTreeHelper.GetDpi(ActiveViewerRoot);
        var scale = _viewerFitToScreen
            ? Math.Min(viewportWidth / (double)info.Width,
                viewportHeight / (double)info.Height)
            : _viewerZoomPercent / (100d * dpi.DpiScaleX);

        var imageWidth = _viewerFitToScreen
            ? Math.Max(1, info.Width * scale)
            : Math.Max(1, info.Width * _viewerZoomPercent / (100d * dpi.DpiScaleX));
        var imageHeight = _viewerFitToScreen
            ? Math.Max(1, info.Height * scale)
            : Math.Max(1, info.Height * _viewerZoomPercent / (100d * dpi.DpiScaleY));

        var center = _viewerPendingImageCenter ?? CaptureViewerCenter();
        _viewerPendingImageCenter = null;
        var canvasWidth = Math.Max(viewportWidth, imageWidth);
        var canvasHeight = Math.Max(viewportHeight, imageHeight);
        image.Width = imageWidth;
        image.Height = imageHeight;
        canvas.Width = canvasWidth;
        canvas.Height = canvasHeight;

        UpdateViewerZoomControls(CurrentViewerZoomPercent());
        var version = ++_viewerZoomLayoutVersion;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_viewerOpen || version != _viewerZoomLayoutVersion ||
                !ReferenceEquals(scroller, ActiveViewerScroller))
            {
                return;
            }

            var (width, height) = ViewerViewportSize();
            var left = Math.Max(0, (canvasWidth - imageWidth) / 2);
            var top = Math.Max(0, (canvasHeight - imageHeight) / 2);
            scroller.ScrollToHorizontalOffset(Math.Max(0,
                left + center.X * imageWidth - width / 2));
            scroller.ScrollToVerticalOffset(Math.Max(0,
                top + center.Y * imageHeight - height / 2));
            UpdateViewerNavigator();
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        UpdateViewerNavigator();
    }

    private void UpdateViewerNavigator()
    {
        if (!_viewerOpen)
        {
            PhotoViewerNavigator.Visibility = Visibility.Collapsed;
            if (_viewerFullscreenNavigator is not null)
            {
                _viewerFullscreenNavigator.Visibility = Visibility.Collapsed;
            }
            return;
        }

        var info = _viewerImageInfo;
        var scroller = ActiveViewerScroller;
        var image = ActiveViewerImage;
        var canvas = ActiveViewerCanvas;
        var navigator = _viewerFullscreenNavigator ?? PhotoViewerNavigator;
        var other = _viewerFullscreenNavigator is not null ? PhotoViewerNavigator : null;
        if (other is not null)
        {
            other.Visibility = Visibility.Collapsed;
        }

        var bitmap = image.Source as BitmapSource;
        if (info is not { Width: > 0, Height: > 0 } ||
            bitmap is null || double.IsNaN(image.Width) || double.IsNaN(image.Height))
        {
            navigator.Visibility = Visibility.Collapsed;
            return;
        }

        var (viewportWidth, viewportHeight) = ViewerViewportSize();
        navigator.Update(
            bitmap, CurrentViewerZoomPercent(), info.Width, info.Height,
            image.Width, image.Height,
            viewportWidth, viewportHeight,
            Math.Max(viewportWidth, canvas.Width),
            Math.Max(viewportHeight, canvas.Height),
            scroller.HorizontalOffset, scroller.VerticalOffset);
    }

    private void ViewerNavigator_CenterRequested(
        object? sender, PhotoNavigatorCenterEventArgs e)
    {
        if (!_viewerOpen)
        {
            return;
        }

        var scroller = ActiveViewerScroller;
        var image = ActiveViewerImage;
        var canvas = ActiveViewerCanvas;
        var (width, height) = ViewerViewportSize();
        var imageLeft = Math.Max(0, (canvas.Width - image.Width) / 2);
        var imageTop = Math.Max(0, (canvas.Height - image.Height) / 2);
        scroller.ScrollToHorizontalOffset(Math.Max(0,
            imageLeft + image.Width * e.X - width / 2));
        scroller.ScrollToVerticalOffset(Math.Max(0,
            imageTop + image.Height * e.Y - height / 2));
        UpdateViewerNavigator();
    }

    private void PhotoViewerScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateViewerNavigator();

    private void ViewerFullscreenScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateViewerNavigator();

    // Logarithmic 1–400 % mapping: low zoom values have usable travel and
    // dragging toward the right always zooms in, not back to a hard 10% floor.
    private const double ViewerZoomMin = 1;
    private const double ViewerZoomMax = 400;

    private static double ZoomToTrack(double percent)
        => 100d * Math.Log(Math.Clamp(percent, ViewerZoomMin, ViewerZoomMax) /
            ViewerZoomMin) / Math.Log(ViewerZoomMax / ViewerZoomMin);

    private static double TrackToZoom(double track)
        => ViewerZoomMin * Math.Pow(ViewerZoomMax / ViewerZoomMin,
            Math.Clamp(track, 0d, 100d) / 100d);

    private void UpdateViewerZoomControls(double displayedZoom)
    {
        _viewerUpdatingZoomControls = true;
        try
        {
            PhotoZoomSlider.Value = ZoomToTrack(displayedZoom);
            PhotoZoomLabel.Text = _viewerFitToScreen
                ? $"Fit {displayedZoom:0.#} %"
                : $"{_viewerZoomPercent:0.#} %";
        }
        finally
        {
            _viewerUpdatingZoomControls = false;
        }
    }

    private void PhotoViewerBack_Click(object sender, RoutedEventArgs e)
        => ClosePhotoViewer();

    private void PhotoViewerPrev_Click(object sender, RoutedEventArgs e)
        => ViewerNavigate(-1);

    private void PhotoViewerNext_Click(object sender, RoutedEventArgs e)
        => ViewerNavigate(1);

    private void PhotoViewer_ManipulationStarting(
        object sender, ManipulationStartingEventArgs e)
    {
        if (!_viewerOpen) return;
        e.ManipulationContainer = ActiveViewerScroller;
        e.Mode = ManipulationModes.Scale;
        e.Handled = true;
    }

    private void PhotoViewer_ManipulationDelta(
        object sender, ManipulationDeltaEventArgs e)
    {
        if (!_viewerOpen || e.Manipulators.Count() < 2)
        {
            return;
        }

        var horizontal = e.DeltaManipulation.Scale.X;
        var vertical = e.DeltaManipulation.Scale.Y;
        if (!double.IsFinite(horizontal) || !double.IsFinite(vertical))
        {
            return;
        }

        var ratio = Math.Sqrt(Math.Abs(horizontal * vertical));
        if (Math.Abs(ratio - 1) < 0.002)
        {
            e.Handled = true;
            return;
        }

        _viewerSuppressMouseUntil = Environment.TickCount64 + 750;
        CancelViewerPendingClose();
        ReleaseViewerPan();

        // The gesture manipulates the same zoom state as Fit/100%/slider.
        var current = CurrentViewerZoomPercent();
        _viewerFitToScreen = false;
        _viewerZoomPercent = Math.Clamp(current * Math.Clamp(ratio, 0.75, 1.3),
            ViewerZoomMin, ViewerZoomMax);
        ApplyViewerImageLayout();
        ScheduleViewerZoomRefinement();
        e.Handled = true;
    }

    private void PhotoViewer_ManipulationCompleted(
        object sender, ManipulationCompletedEventArgs e)
    {
        _viewerSuppressMouseUntil = Environment.TickCount64 + 750;
        e.Handled = true;
    }

    private void PhotoViewerFit_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewerOpen) return;
        _viewerFitToScreen = true;
        ApplyViewerImageLayout();
        ScheduleViewerZoomRefinement();
        Keyboard.Focus(PhotoViewerPanel);
    }

    private void PhotoViewerActualSize_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewerOpen) return;
        _viewerFitToScreen = false;
        _viewerZoomPercent = 100;
        ApplyViewerImageLayout();
        ScheduleViewerZoomRefinement();
        Keyboard.Focus(PhotoViewerPanel);
    }

    private void PhotoViewerZoom_ValueChanged(
        object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_viewerOpen || _viewerUpdatingZoomControls) return;
        _viewerFitToScreen = false;
        _viewerZoomPercent = TrackToZoom(e.NewValue);
        ApplyViewerImageLayout();
        ScheduleViewerZoomRefinement();
    }

    private void PhotoViewerViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_viewerOpen) return;
        ApplyViewerImageLayout();
        if (_viewerFitToScreen)
        {
            ScheduleViewerZoomRefinement();
        }
    }

    private void ScheduleViewerZoomRefinement()
    {
        _viewerZoomCancellation?.Cancel();
        _viewerZoomCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _viewerZoomCancellation = cancellation;
        _ = RefineViewerZoomAfterDelayAsync(cancellation.Token);
    }

    private async Task RefineViewerZoomAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            if (token.IsCancellationRequested || !_viewerOpen ||
                _viewerFullscreenWindow is not null)
            {
                return;
            }

            var required = GetViewerRequiredPixelSize();
            if (_viewerCurrentIndex < 0 || _viewerCurrentIndex >= _browseIndex.Count)
            {
                return;
            }

            var id = _browseIndex[_viewerCurrentIndex].ImageId;
            if (TryGetViewerOriginal(id, required, out var bitmap) && bitmap is not null)
            {
                SetViewerBitmap(bitmap);
                SetViewerQualityMessage(null);
                return;
            }

            // Leave the currently displayed image on screen until the requested
            // higher-quality decode is ready.
            ShowViewerImage(keepCurrentAsPlaceholder: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ViewerNavigate(int direction)
    {
        CancelViewerPendingClose();
        ReleaseViewerPan();
        if (!_viewerOpen || direction == 0)
        {
            return;
        }

        var next = _viewerCurrentIndex + direction;
        if (next < 0 || next >= _browseIndex.Count)
        {
            return;
        }

        _viewerCurrentIndex = next;
        SelectViewerImage(_browseIndex[next].ImageId);
        ShowViewerImage();
    }

    private void ViewerToggleFullscreen()
    {
        CancelViewerPendingClose();
        if (!_viewerOpen)
        {
            return;
        }

        if (_viewerFullscreenWindow is not null)
        {
            LeaveViewerFullscreen();
            return;
        }

        // Freeze the CURRENT pixels-to-monitor-pixels magnification when
        // switching contexts. The fullscreen viewport must not silently
        // change the zoom to its own Fit value.
        _viewerPendingImageCenter = CaptureViewerCenter();
        _viewerZoomPercent = CurrentViewerZoomPercent();
        // Fullscreen should use the screen area rather than preserving a
        // smaller fit-size inherited from the in-window viewer.
        _viewerFitToScreen = true;
        var fullscreenImage = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Source = PhotoViewerImage.Source
        };
        RenderOptions.SetBitmapScalingMode(fullscreenImage, BitmapScalingMode.HighQuality);
        var canvas = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = BrowseScrollViewer.Background
        };
        canvas.Children.Add(fullscreenImage);
        var scroller = new ScrollViewer
        {
            Background = BrowseScrollViewer.Background,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            CanContentScroll = false,
            Content = canvas
        };
        scroller.ScrollChanged += ViewerFullscreenScroller_ScrollChanged;

        var navigator = new PhotoZoomNavigator();
        navigator.CenterRequested += ViewerNavigator_CenterRequested;
        navigator.ShowForNewImage();

        var surface = new Grid { Background = BrowseScrollViewer.Background, IsManipulationEnabled = true };
        surface.ManipulationStarting += PhotoViewer_ManipulationStarting;
        surface.ManipulationDelta += PhotoViewer_ManipulationDelta;
        surface.ManipulationCompleted += PhotoViewer_ManipulationCompleted;
        surface.Children.Add(scroller);
        surface.Children.Add(navigator);
        surface.PreviewMouseLeftButtonDown += PhotoViewerPanel_MouseLeftButtonDown;
        surface.PreviewMouseLeftButtonUp += PhotoViewerPan_MouseLeftButtonUp;
        surface.PreviewMouseMove += PhotoViewerPan_MouseMove;
        surface.MouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            LeaveViewerFullscreen();
        };

        var window = new Window
        {
            Owner = this,
            Title = "PCasa – Vollbild",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowState = WindowState.Maximized,
            ShowInTaskbar = false,
            Topmost = true,
            Background = BrowseScrollViewer.Background,
            Content = surface
        };
        window.PreviewKeyDown += ViewerFullscreen_PreviewKeyDown;
        window.Closed += ViewerFullscreen_Closed;
        scroller.SizeChanged += (_, _) =>
        {
            if (_viewerOpen && ReferenceEquals(window, _viewerFullscreenWindow))
            {
                ApplyViewerImageLayout();
            }
        };

        _viewerFullscreenWindow = window;
        _viewerFullscreenImage = fullscreenImage;
        _viewerFullscreenCanvas = canvas;
        _viewerFullscreenScroller = scroller;
        _viewerFullscreenNavigator = navigator;
        window.Show();
        window.Activate();
        Keyboard.Focus(window);
        ApplyViewerImageLayout();
        ShowViewerImage(keepCurrentAsPlaceholder: true);
    }

    private void ReleaseViewerPan()
    {
        var surface = _viewerPanSurface;
        _viewerPanSurface = null;
        _viewerDragMoved = false;
        if (surface?.IsMouseCaptured == true)
        {
            surface.ReleaseMouseCapture();
        }
    }

    private void LeaveViewerFullscreen()
    {
        CancelViewerPendingClose();
        var window = _viewerFullscreenWindow;
        if (window is null)
        {
            return;
        }

        _viewerPendingImageCenter = CaptureViewerCenter();
        if (_viewerFitToScreen)
        {
            _viewerZoomPercent = CurrentViewerZoomPercent();
            _viewerFitToScreen = false;
        }

        ReleaseViewerPan();
        _viewerFullscreenWindow = null;
        _viewerFullscreenImage = null;
        _viewerFullscreenCanvas = null;
        _viewerFullscreenScroller = null;
        _viewerFullscreenNavigator = null;
        window.Close();

        if (_viewerOpen && !Dispatcher.HasShutdownStarted)
        {
            Activate();
            Keyboard.Focus(PhotoViewerPanel);
            ApplyViewerImageLayout();
            ShowViewerImage(keepCurrentAsPlaceholder: true);
        }
    }

    private void ViewerFullscreen_Closed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _viewerFullscreenWindow))
        {
            return;
        }

        // Also handle Alt+F4, not just the application's right-click command.
        _viewerPendingImageCenter = CaptureViewerCenter();
        ReleaseViewerPan();
        _viewerFullscreenWindow = null;
        _viewerFullscreenImage = null;
        _viewerFullscreenCanvas = null;
        _viewerFullscreenScroller = null;
        _viewerFullscreenNavigator = null;
        if (_viewerOpen && !Dispatcher.HasShutdownStarted)
        {
            Activate();
            Keyboard.Focus(PhotoViewerPanel);
            ApplyViewerImageLayout();
            ShowViewerImage(keepCurrentAsPlaceholder: true);
        }
    }

    // A click returns to thumbnails; a drag pans; a double-click zooms to Fit.
    // WPF's preview events are used because ScrollViewer can handle bubbling
    // mouse events before they reach the outer image surface.
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private bool ViewerMouseIsChrome(DependencyObject? original, object sender)
    {
        for (var ancestor = original; ancestor is not null;)
        {
            if (ancestor is ScrollBar or Thumb or ButtonBase or PhotoZoomNavigator)
            {
                return true;
            }

            if (ReferenceEquals(ancestor, sender))
            {
                return false;
            }

            ancestor = ancestor is Visual or Visual3D
                ? VisualTreeHelper.GetParent(ancestor)
                : LogicalTreeHelper.GetParent(ancestor);
        }

        return false;
    }

    private void CancelViewerPendingClose()
    {
        if (_viewerSingleClickTimer is not null)
        {
            _viewerSingleClickTimer.Stop();
            _viewerSingleClickTimer.Tick -= ViewerSingleClickTimer_Tick;
            _viewerSingleClickTimer = null;
        }
    }

    private void ViewerSingleClickTimer_Tick(object? sender, EventArgs e)
    {
        CancelViewerPendingClose();
        if (_viewerOpen && _viewerPanSurface is null)
        {
            ClosePhotoViewer();
        }
    }

    private void ClosePhotoViewerAfterDoubleClickWindow()
    {
        CancelViewerPendingClose();
        // Previously we waited for the entire Windows double-click interval
        // (often 500 ms), which made returning to thumbnails feel sluggish.
        // Keep a short double-click grace period only in zoomed mode.
        var milliseconds = Math.Clamp((int)GetDoubleClickTime() / 2, 200, 270);
        _viewerSingleClickTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Normal,
            ViewerSingleClickTimer_Tick,
            Dispatcher);
        _viewerSingleClickTimer.Start();
    }

    private void PhotoViewerPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_comparisonActive) { e.Handled = true; return; }
        if (!_viewerOpen || Environment.TickCount64 < _viewerSuppressMouseUntil ||
            ViewerMouseIsChrome(e.OriginalSource as DependencyObject, sender))
        {
            return;
        }

        CancelViewerPendingClose();
        if (e.ClickCount >= 2)
        {
            // A double-click must win over the first click's deferred close.
            ReleaseViewerPan();
            if (!_viewerFitToScreen)
            {
                _viewerFitToScreen = true;
                ApplyViewerImageLayout();
                ScheduleViewerZoomRefinement();
            }

            e.Handled = true;
            return;
        }

        if (sender is not UIElement surface)
        {
            return;
        }

        ReleaseViewerPan();
        _viewerPanSurface = surface;
        _viewerPanStart = e.GetPosition(surface);
        _viewerDragMoved = false;
        _viewerPanOffsetX = ActiveViewerScroller.HorizontalOffset;
        _viewerPanOffsetY = ActiveViewerScroller.VerticalOffset;
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void PhotoViewerPan_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewerPanSurface is not UIElement surface ||
            !ReferenceEquals(sender, surface))
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ReleaseViewerPan();
            return;
        }

        var pos = e.GetPosition(surface);
        var dx = pos.X - _viewerPanStart.X;
        var dy = pos.Y - _viewerPanStart.Y;
        if (!_viewerDragMoved &&
            Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _viewerDragMoved = true;
        var scroller = ActiveViewerScroller;
        scroller.ScrollToHorizontalOffset(_viewerPanOffsetX - dx);
        scroller.ScrollToVerticalOffset(_viewerPanOffsetY - dy);
        e.Handled = true;
    }

    private void PhotoViewerPan_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(sender, _viewerPanSurface))
        {
            return;
        }

        var wasDrag = _viewerDragMoved;
        ReleaseViewerPan();
        e.Handled = true;
        if (wasDrag || !_viewerOpen)
        {
            return;
        }

        // Normal Fit retains instant one-click return. In zoomed mode the
        // second click must have time to trigger Fit without closing the viewer.
        if (_viewerFitToScreen)
        {
            ClosePhotoViewer();
        }
        else
        {
            ClosePhotoViewerAfterDoubleClickWindow();
        }
    }

    private void PhotoViewerPanel_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_comparisonActive) { e.Handled = true; return; }
        CancelViewerPendingClose();
        ReleaseViewerPan();
        e.Handled = true;
        ViewerToggleFullscreen();
    }

    private void PhotoViewerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
        => HandleViewerKey(e, fullscreen: false);

    private void PhotoViewerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Arrow navigation must also work after clicking a zoom button or
        // moving its slider, when focus is outside PhotoViewerPanel.
        if (_viewerOpen && _viewerFullscreenWindow is null)
        {
            HandleViewerKey(e, fullscreen: false);
        }
        else if (!_viewerOpen)
        {
            GalleryWindow_PreviewKeyDown(sender, e);
        }
    }

    private void ViewerFullscreen_PreviewKeyDown(object sender, KeyEventArgs e)
        => HandleViewerKey(e, fullscreen: true);

    private void HandleViewerKey(KeyEventArgs e, bool fullscreen)
    {
        if (!_viewerOpen)
        {
            return;
        }
        if (_comparisonActive && (e.Key == Key.Left || e.Key == Key.Right)) { e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Left:
                ViewerNavigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
                ViewerNavigate(+1);
                e.Handled = true;
                break;
            case Key.Escape:
                if (fullscreen)
                {
                    LeaveViewerFullscreen();
                }
                else
                {
                    ClosePhotoViewer();
                }
                e.Handled = true;
                break;
            case Key.F11:
                ViewerToggleFullscreen();
                e.Handled = true;
                break;
        }
    }

    private void ClosePhotoViewer()
    {
        var comparisonSelection = _comparisonActive ? _comparisonImageIds.Distinct().ToArray() : null;
        HideComparison();
        CancelViewerPendingClose();
        if (!_viewerOpen)
        {
            return;
        }

        var lastImageIndex = _viewerCurrentIndex;
        _viewerOpen = false;
        _viewerCurrentIndex = -1;
        _viewerDecodeCancellation?.Cancel();
        _viewerDecodeCancellation?.Dispose();
        _viewerDecodeCancellation = null;
        _viewerZoomCancellation?.Cancel();
        _viewerZoomCancellation?.Dispose();
        _viewerZoomCancellation = null;
        _viewerImageInfo = null;
        _viewerPrefetchCancellation?.Cancel();
        _viewerPrefetchCancellation?.Dispose();
        _viewerPrefetchCancellation = null;
        _viewerOriginalCache.Clear();
        _viewerOriginalLru.Clear();
        ++_viewerImageRequest;

        ReleaseViewerPan();
        var window = _viewerFullscreenWindow;
        _viewerFullscreenWindow = null;
        _viewerFullscreenImage = null;
        _viewerFullscreenCanvas = null;
        _viewerFullscreenScroller = null;
        _viewerFullscreenNavigator = null;
        window?.Close();

        PhotoViewerPanel.Visibility = Visibility.Collapsed;
        ViewerToolbar.Visibility = Visibility.Collapsed;
        BrowseToolbar.Visibility = Visibility.Visible;
        ViewerEditingPanel.Visibility = Visibility.Collapsed;
        FolderTreePanel.Visibility = Visibility.Visible;
        PhotoZoomToolbar.Visibility = Visibility.Collapsed;
        ThumbnailSizeToolbar.Visibility = Visibility.Visible;
        UpdateComparisonButtons();
        SetViewerBitmap(null);
        SetViewerQualityMessage(null);
        var settings = PhotoViewerSettings.Load();
        var target = settings.ReturnPosition == ViewerReturnPosition.CurrentImage
            ? BrowseSurface.GetImageOffset(lastImageIndex) ?? _viewerOpeningScrollOffset
            : _viewerOpeningScrollOffset;

        if (settings.ReturnPosition == ViewerReturnPosition.CurrentImage &&
            lastImageIndex >= 0 && lastImageIndex < _browseIndex.Count)
        {
            SelectViewerImage(_browseIndex[lastImageIndex].ImageId);
        }

        if (comparisonSelection is { Length: > 0 })
        {
            _selectedImageIds.Clear();
            foreach (var imageId in comparisonSelection) _selectedImageIds.Add(imageId);
            BrowseSurface.SetSelection(_selectedImageIds);
            SelectionText.Text = $"Auswahl: {_selectedImageIds.Count:N0}";
            UpdateComparisonButtons();
        }

        // Restore after the viewer overlay has been removed, without replacing
        // the gallery or rebuilding its index.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                BrowseScrollViewer.ScrollToVerticalOffset(target);
                Keyboard.Focus(BrowseSurface);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
