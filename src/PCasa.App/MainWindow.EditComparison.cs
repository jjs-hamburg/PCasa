using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private enum EditCompareMode { Single, SideBySide, Split }

    private EditCompareMode _editCompareMode = EditCompareMode.Single;
    private BitmapSource? _editCompareOriginal;
    private BitmapSource? _editCompareEdited;
    private BitmapSource? _editCompareHeldEdited;
    private long _editCompareImageId = -1;
    private int _editCompareRequest;
    private double _editCompareSplitFraction = 0.5;
    private bool _editCompareHoldingOriginal;
    private DispatcherTimer? _editCompareHoldTimer;
    private bool _editCompareHoldArmed;
    private long _editCompareHoldArmedImageId = -1;
    private double _editCompareZoomFactor = 1;
    private bool _editComparePanActive;
    private bool _editComparePanMoved;
    private Point _editComparePanStart;
    private double _editComparePanStartX;
    private double _editComparePanStartY;
    private double _editComparePanX;
    private double _editComparePanY;
    // A Save-Copy result retains its source identity only for this process,
    // without extra files, sidecars, catalog relationships or sorting rules.
    // A newly generated panorama does NOT get such a link.
    private readonly Dictionary<long, long> _editCompareCopySources = new();

    // The last external comparison belongs to ONE immutable editor session,
    // not the newest Undo entry or whatever bitmap happens to be in the
    // viewer. Retain decoded, bounded (2400px) pixels after the temporary
    // exchange workspace is cleaned; never try to reopen its deleted file.
    private sealed record ExternalComparisonSnapshot(
        Guid SessionId, long ImageId, string ResultPath,
        long ResultSize, DateTime ResultWriteUtc,
        BitmapSource Before, BitmapSource After);
    private ExternalComparisonSnapshot? _latestExternalComparison;
    private long? _externalComparisonUnavailableImageId;

    private async Task<ExternalComparisonSnapshot?> CaptureExternalComparisonAsync(
        ExternalEditReview review)
    {
        if (review.IsVideo || review.ResultPath is not { Length: > 0 } output)
            return null;
        var originalSnapshot = review.Snapshot.BeforePath;
        if (!File.Exists(originalSnapshot))
        {
            // Pre-Alpha.41 sessions do not have an immutable input. Use the
            // gallery source ONLY if its bytes are still the sent version.
            // If that original is offline, do not stall Save/Cancel or display
            // a misleading comparison.
            try
            {
                if (ExternalEditTransaction.OriginalChanged(review.Snapshot))
                    return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PCasaDiagnostics.Write("EditComparison.LegacyOriginal", ex);
                return null;
            }
            originalSnapshot = review.Snapshot.OriginalPath;
        }
        var sessionId = review.Snapshot.Id;
        var resultSize = review.ResultSize;
        var resultWriteUtc = review.ResultWriteUtc;
        try
        {
            var pair = await Task.Run(() =>
            {
                BitmapSource LoadLocal(string path)
                {
                    var dimensions = EditableImageFormats.Dimensions(path);
                    return PhotoViewerImageLoader.Load(
                        new PhotoViewerImageInfo(review.ImageId, path,
                            Path.GetFileName(path), dimensions.Width,
                            dimensions.Height), 2400, CancellationToken.None);
                }
                return (Before: LoadLocal(originalSnapshot), After: LoadLocal(output));
            });
            // If an editor exported again while we decoded, never label a
            // stale preview as the result of that new export.
            var resultNow = new FileInfo(output);
            if (!resultNow.Exists || resultNow.Length != resultSize ||
                resultNow.LastWriteTimeUtc != resultWriteUtc)
                return null;
            return new ExternalComparisonSnapshot(sessionId, review.ImageId,
                output, resultSize, resultWriteUtc, pair.Before, pair.After);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidDataException or NotSupportedException)
        {
            PCasaDiagnostics.Write("EditComparison.ExternalSnapshot", ex);
            return null; // Save/Cancel must remain available without a preview.
        }
    }

    private void UpdateEditComparisonAvailability()
    {
        if (EditCompareSingleButton is null) return;
        var imageId = _viewerImageInfo?.Id;
        var eligible = _viewerOpen && !_comparisonActive &&
            _externalBatchReview is null &&
            imageId is long id &&
            EditableImageFormats.Supports(_viewerImageInfo!.LocalPath) &&
            ((_photoEditSession is { HasChanges: true } live && live.ImageId == id) ||
             (_externalEditReview is { ResultPath: not null } external &&
              external.ImageId == id) ||
             (_latestExternalComparison?.ImageId == id) ||
             (_externalComparisonUnavailableImageId != id &&
              (_restoreBadgeImageIds.Contains(id) ||
               _editCompareCopySources.ContainsKey(id))));
        EditCompareSingleButton.IsEnabled = _viewerOpen && !_comparisonActive;
        EditCompareSideButton.IsEnabled = eligible;
        EditCompareSplitButton.IsEnabled = eligible;
        // The icons are vector geometry, not text: FontWeight does NOTHING
        // to their visual selection. Use a thick outlined active button and
        // full opacity; do not rely on hue (color-blind accessibility).
        void Mark(Button button, bool active)
        {
            button.BorderThickness = active ? new Thickness(3) : new Thickness(1);
            button.Opacity = active ? 1 : 0.76;
        }
        Mark(EditCompareSingleButton, _editCompareMode == EditCompareMode.Single);
        Mark(EditCompareSideButton, _editCompareMode == EditCompareMode.SideBySide);
        Mark(EditCompareSplitButton, _editCompareMode == EditCompareMode.Split);
    }

    private void EditCompareSingle_Click(object sender, RoutedEventArgs e)
        => ResetEditComparison();

    private async void EditCompareSide_Click(object sender, RoutedEventArgs e)
        => await ShowEditComparisonAsync(EditCompareMode.SideBySide);

    private async void EditCompareSplit_Click(object sender, RoutedEventArgs e)
        => await ShowEditComparisonAsync(EditCompareMode.Split);

    private async Task<(BitmapSource Original, BitmapSource Edited)?>
        LoadEditComparisonPairAsync(long imageId)
    {
        var live = _photoEditSession;
        if (live is { HasChanges: true } && live.ImageId == imageId)
        {
            var edited = PhotoViewerImage.Source as BitmapSource ?? live.Render();
            return (live.Original, edited);
        }
        var external = _externalEditReview;
        if (external is { ResultPath: not null } && external.ImageId == imageId)
        {
            var captured = _latestExternalComparison;
            if (captured is null || captured.SessionId != external.Snapshot.Id ||
                !string.Equals(captured.ResultPath, external.ResultPath,
                    StringComparison.OrdinalIgnoreCase) ||
                captured.ResultSize != external.ResultSize ||
                captured.ResultWriteUtc != external.ResultWriteUtc)
            {
                captured = await CaptureExternalComparisonAsync(external);
                if (captured is not null &&
                    ReferenceEquals(_externalEditReview, external))
                    _latestExternalComparison = captured;
            }
            return captured is not null
                ? (captured.Before, captured.After) : null;
        }
        // After Save/Save Copy the isolated workspace no longer exists.
        // Its exact session-bound pixels take priority over Undo history.
        if (_latestExternalComparison is { } last && last.ImageId == imageId)
            return (last.Before, last.After);
        // A failed snapshot is not permission to compare an arbitrary
        // previous Undo version and falsely label it the external input.
        if (_externalComparisonUnavailableImageId == imageId)
            return null;
        var info = _viewerImageInfo;
        if (info is null || info.Id != imageId) return null;
        if (_editCompareCopySources.TryGetValue(imageId, out var sourceId))
        {
            var images = await Task.WhenAll(new[] { sourceId, imageId }
                .Select(id => _scheduler.RunAsync(
                    "viewer:edit-copy-before:" + Guid.NewGuid().ToString("N"),
                    SchedulerPriority.Interactive,
                    token => LoadComparisonBitmap(id, token),
                    workKind: SchedulerWorkKind.ViewerRead)));
            return images[0] is { } original && images[1] is { } edited
                ? (original, edited) : null;
        }
        var version = await Task.Run(() =>
            new UndoHistoryService(UndoOptions.Load())
                .GetVersions(info.LocalPath).FirstOrDefault());
        if (version is null) return null;

        var bitmaps = await _scheduler.RunAsync(
            "viewer:edit-before:" + Guid.NewGuid().ToString("N"),
            SchedulerPriority.Interactive,
            token =>
            {
                token.ThrowIfCancellationRequested();
                var dims = EditableImageFormats.Dimensions(version.SnapshotPath);
                var original = PhotoViewerImageLoader.Load(
                    new PhotoViewerImageInfo(imageId, version.SnapshotPath,
                        Path.GetFileName(version.SnapshotPath),
                        dims.Width, dims.Height),
                    2400, token);
                var edited = LoadComparisonBitmap(imageId, token);
                return (Original: original, Edited: edited);
            }, workKind: SchedulerWorkKind.ViewerRead);
        return bitmaps.Original is not null && bitmaps.Edited is not null
            ? (bitmaps.Original, bitmaps.Edited) : null;
    }

    private async Task ShowEditComparisonAsync(EditCompareMode requested)
    {
        if (!_viewerOpen || _comparisonActive || _externalBatchReview is not null ||
            _viewerImageInfo is not { } info) return;
        var id = info.Id;
        var generation = ++_editCompareRequest;
        try
        {
            var pair = await LoadEditComparisonPairAsync(id);
            if (generation != _editCompareRequest || !_viewerOpen ||
                _viewerImageInfo?.Id != id) return;
            if (pair is null)
            {
                StatusText.Text = UiLanguage.T("Keine bearbeitete und ursprüngliche Version verfügbar.");
                return;
            }
            _editCompareOriginal = pair.Value.Original;
            _editCompareEdited = pair.Value.Edited;
            _editCompareImageId = id;
            // Never silently switch the requested split into side-by-side.
            // JPEG/RAW decoders often round preview pixel sizes differently
            // even when their source aspect ratios are visually identical.
            // Two uniformly fitted images share the same full-size viewport.
            _editCompareMode = requested;
            EditCompareSideOriginal.Source = _editCompareOriginal;
            EditCompareSideEdited.Source = _editCompareEdited;
            EditCompareSwipeOriginal.Source = _editCompareOriginal;
            EditCompareSwipeEdited.Source = _editCompareEdited;
            EditCompareSidePanel.Visibility = requested == EditCompareMode.SideBySide
                ? Visibility.Visible : Visibility.Collapsed;
            EditCompareSwipePanel.Visibility = requested == EditCompareMode.Split
                ? Visibility.Visible : Visibility.Collapsed;
            PhotoViewerScroller.Visibility = Visibility.Collapsed;
            PhotoViewerNavigator.Visibility = Visibility.Collapsed;
            EditComparePanel.Visibility = Visibility.Visible;
            UpdateEditComparisonAvailability();
            UpdateEditComparisonZoom();
            UpdateEditCompareClip();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("EditComparison.Load", ex);
            if (generation == _editCompareRequest)
                StatusText.Text = UiLanguage.T("Bildvergleich fehlgeschlagen: ") + ex.Message;
        }
    }

    private void UpdateEditComparisonFromSession()
    {
        if (_editCompareMode == EditCompareMode.Single ||
            _editCompareHoldingOriginal ||
            _editCompareImageId != _viewerImageInfo?.Id ||
            _photoEditSession is not { HasChanges: true } session ||
            session.ImageId != _editCompareImageId)
            return;
        _editCompareOriginal = session.Original;
        _editCompareEdited = PhotoViewerImage.Source as BitmapSource ?? session.Render();
        EditCompareSideOriginal.Source = _editCompareOriginal;
        EditCompareSideEdited.Source = _editCompareEdited;
        EditCompareSwipeOriginal.Source = _editCompareOriginal;
        EditCompareSwipeEdited.Source = _editCompareEdited;
        UpdateEditCompareClip();
    }

    private void UpdateEditComparisonZoom()
    {
        if (_editCompareMode == EditCompareMode.Single ||
            _editCompareEdited is null) return;
        var panel = _editCompareMode == EditCompareMode.SideBySide
            ? EditCompareSidePanel : EditCompareSwipePanel;
        var width = Math.Max(1, panel.ActualWidth /
            (_editCompareMode == EditCompareMode.SideBySide ? 2 : 1));
        var height = Math.Max(1, panel.ActualHeight);
        var fit = Math.Min(width / _editCompareEdited.PixelWidth,
            height / _editCompareEdited.PixelHeight);
        if (_editCompareMode == EditCompareMode.Split)
        {
            // WPF Image.Stretch=Uniform independently fits the two decoded
            // aspect ratios, so even adjacent JPG pixel-rounding differences
            // make the comparison drift. Size ONE centered frame using the
            // edited image's aspect; both images then fill that identical
            // frame without distorting the edited result.
            EditCompareImageFrame.Width = _editCompareEdited.PixelWidth * fit;
            EditCompareImageFrame.Height = _editCompareEdited.PixelHeight * fit;
        }
        _editCompareZoomFactor = _viewerFitToScreen ? 1 :
            Math.Clamp(_viewerZoomPercent / Math.Max(1, 100 * fit), 0.25, 12);
        ApplyEditComparisonTransforms();
    }

    private void ApplyEditComparisonTransforms()
    {
        foreach (var image in new[]
        {
            EditCompareSideOriginal, EditCompareSideEdited,
            EditCompareSwipeOriginal, EditCompareSwipeEdited
        })
        {
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(
                _editCompareZoomFactor, _editCompareZoomFactor));
            transform.Children.Add(new TranslateTransform(
                _editComparePanX, _editComparePanY));
            image.RenderTransform = transform;
        }
        UpdateEditCompareClip();
    }

    private bool TryBeginEditComparisonPan(MouseButtonEventArgs e)
    {
        // The draggable split handle must retain the normal Thumb input route.
        for (DependencyObject? source = e.OriginalSource as DependencyObject;
             source is not null;)
        {
            if (ReferenceEquals(source, EditCompareSplitHandle)) return false;
            try { source = VisualTreeHelper.GetParent(source); }
            catch (InvalidOperationException) { break; }
        }
        e.Handled = true;
        _editComparePanActive = true;
        _editComparePanMoved = false;
        _editComparePanStart = e.GetPosition(PhotoViewerPanel);
        _editComparePanStartX = _editComparePanX;
        _editComparePanStartY = _editComparePanY;
        PhotoViewerPanel.CaptureMouse();
        return true;
    }

    private bool MoveEditComparisonPan(MouseEventArgs e)
    {
        if (!_editComparePanActive) return false;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _editComparePanActive = false;
            PhotoViewerPanel.ReleaseMouseCapture();
            return true;
        }
        var point = e.GetPosition(PhotoViewerPanel);
        if (Math.Abs(point.X - _editComparePanStart.X) >=
                SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(point.Y - _editComparePanStart.Y) >=
                SystemParameters.MinimumVerticalDragDistance)
            _editComparePanMoved = true;
        if (!_editComparePanMoved || _editCompareZoomFactor <= 1.001)
        {
            e.Handled = true;
            return true;
        }
        var maxX = Math.Max(0,
            (_editCompareZoomFactor - 1) * EditComparePanel.ActualWidth /
            (_editCompareMode == EditCompareMode.SideBySide ? 4 : 2));
        var maxY = Math.Max(0,
            (_editCompareZoomFactor - 1) * EditComparePanel.ActualHeight / 2);
        _editComparePanX = Math.Clamp(
            _editComparePanStartX + point.X - _editComparePanStart.X,
            -maxX, maxX);
        _editComparePanY = Math.Clamp(
            _editComparePanStartY + point.Y - _editComparePanStart.Y,
            -maxY, maxY);
        ApplyEditComparisonTransforms();
        e.Handled = true;
        return true;
    }

    private bool EndEditComparisonPan(MouseButtonEventArgs e)
    {
        if (!_editComparePanActive) return false;
        _editComparePanActive = false;
        PhotoViewerPanel.ReleaseMouseCapture();
        e.Handled = true;
        // A regular click must still leave the viewer, including after
        // a successful external Save. Only a real drag pans the comparison.
        if (!_editComparePanMoved) ClosePhotoViewer();
        _editComparePanMoved = false;
        return true;
    }

    private void EditCompareSwipe_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateEditComparisonZoom();
        UpdateEditCompareClip();
    }

    private void EditCompareSplit_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var width = EditCompareSwipePanel.ActualWidth;
        if (width < 1) return;
        _editCompareSplitFraction = Math.Clamp(
            _editCompareSplitFraction + e.HorizontalChange / width, 0.02, 0.98);
        UpdateEditCompareClip();
    }

    private void UpdateEditCompareClip()
    {
        if (_editCompareMode != EditCompareMode.Split) return;
        var width = EditCompareSwipePanel.ActualWidth;
        var height = EditCompareSwipePanel.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var x = width * _editCompareSplitFraction;
        var frameWidth = EditCompareImageFrame.Width;
        var frameHeight = EditCompareImageFrame.Height;
        if (!(frameWidth > 0) || !(frameHeight > 0)) return;
        // The visible image frame is centered inside the full swipe panel.
        // The thumb stays at the panel coordinate, whereas the clip must
        // use the image's OWN unscaled local coordinates. Pan/zoom modify
        // the mapping without ever modifying either image's frame geometry.
        var localEdge = frameWidth / 2 +
            (x - width / 2 - _editComparePanX) /
            Math.Max(0.01, _editCompareZoomFactor);
        EditCompareSwipeOriginal.Clip = new RectangleGeometry(
            new Rect(0, 0, Math.Clamp(localEdge, 0, frameWidth), frameHeight));
        Canvas.SetLeft(EditCompareSplitHandle, x - EditCompareSplitHandle.Width / 2);
    }

    private void CancelPendingOriginalHold()
    {
        _editCompareHoldTimer?.Stop();
        _editCompareHoldTimer = null;
        _editCompareHoldArmed = false;
        _editCompareHoldArmedImageId = -1;
    }

    private bool TryBeginEditOriginalHold(MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || _editCompareMode != EditCompareMode.Single ||
            _externalBatchReview is not null || _comparisonActive ||
            _cropActive || _straightenActive || _viewerImageInfo is not { } info ||
            _externalEditReview is { ResultPath: null })
            return false;
        var session = _photoEditSession;
        var live = session is { HasChanges: true } && session.ImageId == info.Id;
        if (!live && !_restoreBadgeImageIds.Contains(info.Id) &&
            _latestExternalComparison?.ImageId != info.Id &&
            !_editCompareCopySources.ContainsKey(info.Id) &&
            _externalEditReview?.ResultPath is null)
            return false;

        // A short click ALWAYS retains the normal Gallery return. Once the
        // button is deliberately held for 280 ms, show Before until release.
        // This preserves the original agreed hold gesture without stealing
        // quick clicks or requiring a new keyboard modifier.
        CancelPendingOriginalHold();
        _editCompareHoldArmed = true;
        _editCompareHoldArmedImageId = info.Id;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(280),
            DispatcherPriority.Input, (_, _) =>
            {
                CancelPendingOriginalHold();
                if (!_viewerOpen || _viewerImageInfo?.Id != info.Id ||
                    Mouse.LeftButton != MouseButtonState.Pressed ||
                    _viewerDragMoved || _editCompareMode != EditCompareMode.Single)
                    return;
                _editCompareHoldingOriginal = true;
                _editCompareHeldEdited = PhotoViewerImage.Source as BitmapSource;
                if (live && _photoEditSession?.ImageId == info.Id)
                {
                    SetViewerBitmap(_photoEditSession.Original);
                    ApplyViewerImageLayout();
                }
                else if (_editCompareOriginal is not null &&
                    _editCompareImageId == info.Id)
                {
                    SetViewerBitmap(_editCompareOriginal);
                    ApplyViewerImageLayout();
                }
                else
                    _ = ShowSavedOriginalWhileHeldAsync(info.Id);
            }, Dispatcher);
        _editCompareHoldTimer = timer;
        timer.Start();
        return false; // Let the ordinary click/pan path run until hold matures.
    }

    private async Task ShowSavedOriginalWhileHeldAsync(long imageId)
    {
        try
        {
            var pair = await LoadEditComparisonPairAsync(imageId);
            if (!_editCompareHoldingOriginal || _viewerImageInfo?.Id != imageId ||
                pair is null) return;
            _editCompareImageId = imageId;
            _editCompareOriginal = pair.Value.Original;
            SetViewerBitmap(pair.Value.Original);
            ApplyViewerImageLayout();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("EditComparison.Hold", ex);
        }
    }

    private bool EndEditOriginalHold(MouseButtonEventArgs e)
    {
        // On a short click this returns false and the ordinary viewer code
        // closes to the gallery. On a long hold it restores After instead.
        CancelPendingOriginalHold();
        if (!_editCompareHoldingOriginal) return false;
        _editCompareHoldingOriginal = false;
        ReleaseViewerPan();
        if (_editCompareHeldEdited is not null)
        {
            SetViewerBitmap(_editCompareHeldEdited);
            ApplyViewerImageLayout();
        }
        _editCompareHeldEdited = null;
        e.Handled = true;
        return true;
    }

    private void ResetEditComparison()
    {
        CancelPendingOriginalHold();
        ++_editCompareRequest;
        if (_editComparePanActive)
        {
            _editComparePanActive = false;
            PhotoViewerPanel.ReleaseMouseCapture();
        }
        _editComparePanX = _editComparePanY = 0;
        _editCompareMode = EditCompareMode.Single;
        _editCompareHoldingOriginal = false;
        _editCompareOriginal = _editCompareEdited = _editCompareHeldEdited = null;
        _editCompareImageId = -1;
        EditCompareSideOriginal.Source = EditCompareSideEdited.Source = null;
        EditCompareSwipeOriginal.Source = EditCompareSwipeEdited.Source = null;
        EditCompareSwipeOriginal.Clip = null;
        EditComparePanel.Visibility = Visibility.Collapsed;
        EditCompareSidePanel.Visibility = Visibility.Collapsed;
        EditCompareSwipePanel.Visibility = Visibility.Collapsed;
        if (_viewerOpen && !_comparisonActive)
        {
            PhotoViewerScroller.Visibility = Visibility.Visible;
            PhotoViewerNavigator.Visibility = Visibility.Visible;
        }
        UpdateEditComparisonAvailability();
    }
}
