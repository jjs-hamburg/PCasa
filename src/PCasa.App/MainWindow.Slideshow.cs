using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private SlideshowWindow? _slideshowWindow;

    private void SlideshowOptions_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SlideshowSettingsWindow(SlideshowSettings.Load())
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    private void SlideshowStart_Click(object sender, RoutedEventArgs e)
    {
        if (_slideshowWindow is { IsVisible: true })
        {
            _slideshowWindow.Activate();
            return;
        }
        if (!_browseInitialized || _viewerOpen || _displayedBrowseIndex.Count == 0)
            return;

        // The focused photo wins; otherwise use the current on-screen image.
        // The same gallery snapshot already includes sorting, search and
        // hidden-folder state. No new DB read, tree or NAS enumeration.
        var hidden = BrowseSurface.HiddenFolderIds;
        bool Available(BrowseThumbnailIndexEntry entry) =>
            !hidden.Contains(entry.PhotoFolderId) &&
            !VideoFormats.IsVideo(entry.FileName);
        var seed = _displayedBrowseIndex.FirstOrDefault(entry =>
            entry.ImageId == _gallerySelection.FocusedId && Available(entry));
        if (seed is null)
        {
            foreach (var (first, last) in BrowseSurface.GetVisibleIndexRanges())
            {
                for (var i = first; i <= last && i < _displayedBrowseIndex.Count; i++)
                {
                    var entry = _displayedBrowseIndex[i];
                    if (!Available(entry)) continue;
                    seed = entry;
                    break;
                }
                if (seed is not null) break;
            }
        }
        seed ??= _displayedBrowseIndex.FirstOrDefault(Available);
        if (seed is null)
        {
            PcasaDialog.Show(this, UiLanguage.T("Keine Fotos für die Slideshow."),
                UiLanguage.T("Slideshow"), MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        var sequence = PhotoSequence.Create(_displayedBrowseIndex, hidden,
            seed.ImageId, _viewerSettings.SequenceScope);
        var startIndex = sequence.IndexOfImage(seed.ImageId);
        if (startIndex < 0 || sequence.Count == 0) return;

        // The gallery may be reindexed while the slideshow is open.
        // An ordinal from this immutable sequence must never be reused
        // against a different, later page-cache index.
        var gallerySnapshot = _displayedBrowseIndex;
        _slideshowWindow = new SlideshowWindow(this, sequence, startIndex,
            _database, BrowseScrollViewer.Background,
            SlideshowSettings.Load(),
            index => ReferenceEquals(_displayedBrowseIndex, gallerySnapshot) &&
                _browsePageCache is { } pages &&
                pages.TryGet(index, out var bitmap) ? bitmap : null);
        _slideshowWindow.Closed += (_, _) =>
        {
            _slideshowWindow = null;
            if (!Dispatcher.HasShutdownStarted && !_shutdownStarted)
                Keyboard.Focus(BrowseSurface);
        };
        _slideshowWindow.Show();
    }
}
