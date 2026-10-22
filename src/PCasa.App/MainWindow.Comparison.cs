using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCasa.App.Controls;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _comparisonActive;
    private long[] _comparisonImageIds = Array.Empty<long>();
    private int _comparisonRequest;

    private void UpdateComparisonButtons()
    {
        CompareAbButton.IsEnabled = !_viewerOpen && _selectedImageIds.Count == 2;
        CompareAaButton.IsEnabled = !_viewerOpen && _selectedImageIds.Count == 1;
    }

    private async void CompareAb_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImageIds.Count != 2 || _viewerOpen) return;
        var ordered = _browseIndex.Select(x => x.ImageId)
            .Where(id => _selectedImageIds.Contains(id)).Take(2).ToArray();
        if (ordered.Length == 2) await OpenComparisonAsync(ordered);
    }

    private async void CompareAa_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedImageIds.Count != 1 || _viewerOpen) return;
        var id = _selectedImageIds.Single();
        await OpenComparisonAsync(new[] { id, id });
    }

    private async void ViewerAa_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewerOpen) return;
        var id = _browseIndex[_viewerCurrentIndex].ImageId;
        await DisplayComparisonAsync(new[] { id, id });
    }

    private void ViewerSingle_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewerOpen) return;
        HideComparison();
        ShowViewerImage();
    }

    private async Task OpenComparisonAsync(long[] ids)
    {
        if (_viewerOpen || _browseCacheBuilding) return;
        BrowseSurface_ImageOpened(this, new BrowseImageClickedEventArgs(ids[0], ModifierKeys.None));
        if (_viewerOpen) await DisplayComparisonAsync(ids);
    }

    private async Task DisplayComparisonAsync(long[] ids)
    {
        if (!_viewerOpen || ids.Length != 2) return;
        var request = ++_comparisonRequest;
        _comparisonActive = true;
        _comparisonImageIds = ids;
        ComparisonPanel.Visibility = Visibility.Visible;
        PhotoViewerScroller.Visibility = Visibility.Collapsed;
        PhotoViewerNavigator.Visibility = Visibility.Collapsed;
        ViewerNavigationToolbar.Visibility = Visibility.Collapsed;
        ViewerComparisonToolbar.Visibility = Visibility.Visible;
        PhotoZoomToolbar.Visibility = Visibility.Collapsed;
        ViewerToolbarImageTitle.Text = ids[0] == ids[1] ? "A/A" : "A/B";
        try
        {

            BitmapSource?[] bitmaps;
            if (ids[0] == ids[1])
            {
                var original = await _scheduler.RunAsync(
                    $"viewer:compare:{request}:{ids[0]}",
                    SchedulerPriority.Interactive,
                    token => LoadComparisonBitmap(ids[0], token),
                    () => request == _comparisonRequest && _viewerOpen,
                    workKind: SchedulerWorkKind.ViewerRead);
                bitmaps = new[] { original, original };
            }
            else
            {
                bitmaps = await Task.WhenAll(ids.Select(id =>
                    _scheduler.RunAsync(
                        $"viewer:compare:{request}:{id}",
                        SchedulerPriority.Interactive,
                        token => LoadComparisonBitmap(id, token),
                        () => request == _comparisonRequest && _viewerOpen,
                        workKind: SchedulerWorkKind.ViewerRead)));
            }
            if (request != _comparisonRequest || !_viewerOpen || !_comparisonActive) return;
            ComparisonImageA.Source = bitmaps[0];
            ComparisonImageB.Source = bitmaps[1];
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Comparison.Load", ex);
            if (request == _comparisonRequest)
                MessageBox.Show(this, "Mindestens eines der Bilder konnte nicht geöffnet werden.",
                    "PCasa", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BitmapSource? LoadComparisonBitmap(long imageId, CancellationToken token)
    {
        var info = new PhotoViewerImageRepository(_database).Get(imageId);
        if (info is null) return null;
        return PhotoViewerImageLoader.Load(info, 2000, token);
    }

    private void HideComparison()
    {
        ++_comparisonRequest;
        _comparisonActive = false;
        _comparisonImageIds = Array.Empty<long>();
        ComparisonImageA.Source = null;
        ComparisonImageB.Source = null;
        ComparisonPanel.Visibility = Visibility.Collapsed;
        PhotoViewerScroller.Visibility = Visibility.Visible;
        PhotoViewerNavigator.Visibility = Visibility.Visible;
        ViewerNavigationToolbar.Visibility = Visibility.Visible;
        ViewerComparisonToolbar.Visibility = Visibility.Collapsed;
        if (_viewerOpen) PhotoZoomToolbar.Visibility = Visibility.Visible;
    }
}
