using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private CancellationTokenSource? _gpsFullscreenImageCancellation;
    private int _gpsFullscreenImageRequest;

    private void ShowGpsFullscreenImage(long imageId)
    {
        if (_sidebarMode != "GPS") return;
        var item = _gpsPoints.FirstOrDefault(p => p.Id == imageId);
        if (item is null) return;

        // A full-map overlay cannot show a photo in the gallery. Return to
        // the previously agreed two-pane photo/map workspace first. Opening
        // a photo must NEVER cover the geographic context.
        if (_gpsFullscreen)
        {
            _gpsFullscreen = false;
            _gpsExpanded = true;
            _appearance.GpsExpanded = true;
            UpdateWorkspaceLayoutPreservingGallery();
        }

        CancelGpsFullscreenImageRead();
        var version = _gpsFullscreenImageRequest;
        GpsFullscreenImageOverlay.Tag = imageId;
        GpsFullscreenImageCaption.Text = item.Name;
        GpsFullscreenImage.Source = null;
        GpsFullscreenImageStatus.Text = UiLanguage.T("Bild wird geladen");
        GpsFullscreenImageStatus.Visibility = Visibility.Visible;

        // Fast preview from the SAME local packed-thumbnail cache as the
        // gallery. No uncatalogued disk access occurs on the WPF UI thread.
        var strip = _gpsFilmstripEntries.FirstOrDefault(p => p.Id == imageId);
        BitmapSource? preview = strip?.Bitmap;
        if (preview is null && _browsePageCache is not null)
        {
            for (var i = 0; i < _browseIndex.Count; i++)
            {
                if (_browseIndex[i].ImageId != imageId) continue;
                _browsePageCache.TryGet(i, out preview);
                break;
            }
        }
        if (preview is not null)
        {
            GpsFullscreenImage.Source = preview;
            GpsFullscreenImageStatus.Visibility = Visibility.Collapsed;
        }

        GpsFullscreenImageOverlay.Visibility = Visibility.Visible;
        var cancellation = new CancellationTokenSource();
        _gpsFullscreenImageCancellation = cancellation;
        var size = Math.Clamp((int)Math.Ceiling(
            Math.Max(GpsFullscreenImageOverlay.ActualWidth,
                GpsFullscreenImageOverlay.ActualHeight) * 1.5),
            800, 4096);
        _ = LoadGpsFullscreenImageAsync(imageId, size, version, cancellation.Token);
    }

    private async Task LoadGpsFullscreenImageAsync(
        long imageId, int size, int version, CancellationToken token)
    {
        try
        {
            var bitmap = await _scheduler.RunAsync(
                "gps:fullscreen-image:" + imageId + ":" + version,
                SchedulerPriority.Interactive,
                schedulerToken =>
                {
                    schedulerToken.ThrowIfCancellationRequested();
                    var info = new PhotoViewerImageRepository(_database).Get(imageId)
                        ?? throw new FileNotFoundException("Bild nicht im Katalog.");
                    return PhotoViewerImageLoader.Load(info, size, schedulerToken);
                },
                () => !token.IsCancellationRequested &&
                    version == _gpsFullscreenImageRequest &&
                    _sidebarMode == "GPS" &&
                    GpsFullscreenImageOverlay.Visibility == Visibility.Visible,
                cancellationToken: token,
                workKind: SchedulerWorkKind.ViewerRead);
            if (token.IsCancellationRequested ||
                version != _gpsFullscreenImageRequest ||
                GpsFullscreenImageOverlay.Visibility != Visibility.Visible) return;

            GpsFullscreenImage.Source = bitmap;
            GpsFullscreenImageStatus.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("GPS.FullscreenImage", ex);
            if (token.IsCancellationRequested ||
                version != _gpsFullscreenImageRequest) return;
            // Keep a locally available preview instead of blanking the image
            // if the original lives on a disconnected NAS/VPN drive.
            GpsFullscreenImageStatus.Text =
                GpsFullscreenImage.Source is not null
                    ? UiLanguage.T("Original nicht verfügbar")
                    : UiLanguage.T("Bild konnte nicht geladen werden");
            GpsFullscreenImageStatus.Visibility = Visibility.Visible;
        }
    }

    private void GpsFullscreenImageOverlay_Click(object sender,
        MouseButtonEventArgs e)
    {
        CloseGpsFullscreenImage();
        e.Handled = true;
    }

    private void CancelGpsFullscreenImageRead()
    {
        ++_gpsFullscreenImageRequest;
        _gpsFullscreenImageCancellation?.Cancel();
        _gpsFullscreenImageCancellation?.Dispose();
        _gpsFullscreenImageCancellation = null;
    }

    private void CloseGpsFullscreenImage()
    {
        CancelGpsFullscreenImageRead();
        GpsFullscreenImageOverlay.Visibility = Visibility.Collapsed;
        GpsFullscreenImageOverlay.Tag = null;
        GpsFullscreenImage.Source = null;
        GpsFullscreenImageStatus.Visibility = Visibility.Collapsed;
    }
}
