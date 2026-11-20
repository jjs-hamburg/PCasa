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
    // Normalized X in the picture (not in the screen viewport).
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
        // A before/after decision exists ONLY while an unsaved changed edit
        // is active. Recycle/Undo versions are NOT editing sessions and
        // finishing Save/Cancel revokes this mode immediately.
        var eligible = _viewerOpen && !_comparisonActive &&
            _externalBatchReview is null &&
            imageId is long id &&
            EditableImageFormats.Supports(_viewerImageInfo!.LocalPath) &&
            ((_photoEditSession is { } live && live.ImageId == id &&
                 (live.HasChanges || GetPendingLiveEditStep() is not null)) ||
             (_externalEditReview is { Ready: true, Busy: false,
                 ResultPath: not null } external && external.ImageId == id));
        if (!eligible && _editCompareMode != EditCompareMode.Single)
            ResetEditComparison();
        // When there is no unsaved before/after decision, remove the
        // entire group. A greyed-out icon cannot convey availability to
        // every user (and steals space from the zoom controls).
        var visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
        EditCompareSingleButton.Visibility = visibility;
        EditCompareSideButton.Visibility = visibility;
        EditCompareSplitButton.Visibility = visibility;
        EditCompareSingleButton.IsEnabled = eligible;
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
        if (live is not null && live.ImageId == imageId)
        {
            var pending = GetPendingLiveEditStep();
            if (pending is not null)
            {
                // Crop/straighten are visual overlays in the main viewer.
                // Render their actual output from the very same pending
                // recipe that Save will apply, without accessing the NAS.
                var accepted = live.Render();
                var edited = await Task.Run(() =>
                    PhotoEditSession.Render(accepted, [pending]));
                return (live.Original, edited);
            }
            if (live.HasChanges)
            {
                var edited = PhotoViewerImage.Source as BitmapSource ?? live.Render();
                return (live.Original, edited);
            }
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
        // Save/Cancel ends the decision. Neither the latest Undo snapshot
        // nor a previous external session may masquerade as a fresh edit.
        // Restore has its OWN comparison dialog and explicit version pairing.
        return null;
    }

    private async Task ShowEditComparisonAsync(EditCompareMode requested)
    {
        if (!_viewerOpen || _comparisonActive || _externalBatchReview is not null ||
            _viewerImageInfo is not { } info ||
            !EditCompareSideButton.IsEnabled) return;
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
            // A previously scheduled resolution refinement belongs to
            // the single-view scroller. It must never call ShowViewerImage
            // and reset a comparison after the user has selected it.
            _viewerZoomCancellation?.Cancel();
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
            _photoEditSession is not { } session ||
            session.ImageId != _editCompareImageId ||
            (!session.HasChanges && GetPendingLiveEditStep() is null))
            return;
        _editCompareOriginal = session.Original;
        _editCompareEdited = PhotoViewerImage.Source as BitmapSource ?? session.Render();
        EditCompareSideOriginal.Source = _editCompareOriginal;
        EditCompareSideEdited.Source = _editCompareEdited;
        EditCompareSwipeOriginal.Source = _editCompareOriginal;
        EditCompareSwipeEdited.Source = _editCompareEdited;
        // Recalculate transforms from the user's unchanged Fit/zoom setting.
        // Undo replaces only pixels, never mode, split position or scale.
        UpdateEditComparisonZoom();
    }

    private void UpdateEditComparisonZoom()
    {
        if (_editCompareMode == EditCompareMode.Single ||
            _editCompareEdited is null) return;
        var side = _editCompareMode == EditCompareMode.SideBySide;
        var panel = side ? EditCompareSidePanel : EditCompareSwipePanel;
        var width = panel.ActualWidth;
        var height = panel.ActualHeight;
        if (!(width > 1) || !(height > 1)) return;

        var availableWidth = Math.Max(1, width / (side ? 2 : 1) -
            (side ? 8 : 0));
        var availableHeight = Math.Max(1, height - (side ? 6 : 0));
        var fit = Math.Min(availableWidth / _editCompareEdited.PixelWidth,
            availableHeight / _editCompareEdited.PixelHeight);
        if (side)
        {
            var before = _editCompareOriginal ?? _editCompareEdited;
            var beforeFit = Math.Min(availableWidth / before.PixelWidth,
                availableHeight / before.PixelHeight);
            EditCompareSideOriginal.Width = before.PixelWidth * beforeFit;
            EditCompareSideOriginal.Height = before.PixelHeight * beforeFit;
            EditCompareSideEdited.Width = _editCompareEdited.PixelWidth * fit;
            EditCompareSideEdited.Height = _editCompareEdited.PixelHeight * fit;
        }
        else
        {
            EditCompareImageFrame.Width = _editCompareEdited.PixelWidth * fit;
            EditCompareImageFrame.Height = _editCompareEdited.PixelHeight * fit;
        }

        // 100% is the original's real pixel resolution, independent of
        // reduced preview dimensions and which comparison mode is active.
        var logicalWidth = _viewerImageInfo is { Width: > 0 } info
            ? GetViewerLogicalImageSize(info).Width
            : _editCompareEdited.PixelWidth;
        var dpi = VisualTreeHelper.GetDpi(PhotoViewerPanel).DpiScaleX;
        var actualWidth = Math.Max(1, logicalWidth * _viewerZoomPercent /
            (100 * dpi));
        var fitWidth = Math.Max(1, _editCompareEdited.PixelWidth * fit);
        _editCompareZoomFactor = _viewerFitToScreen ? 1 :
            Math.Clamp(actualWidth / fitWidth, .05, 64);
        if (_viewerFitToScreen)
            _editComparePanX = _editComparePanY = 0;
        if (!side)
            KeepEditCompareDividerVisible(width);
        ApplyEditComparisonTransforms();
        UpdateViewerZoomControls(_viewerFitToScreen
            ? 100 * fitWidth * dpi / Math.Max(1, logicalWidth)
            : _viewerZoomPercent);
    }

    private void KeepEditCompareDividerVisible(double viewportWidth)
    {
        var imageWidth = EditCompareImageFrame.Width * _editCompareZoomFactor;
        if (!(imageWidth > 0) || !(viewportWidth > 0)) return;
        var maxPan = Math.Max(0, (imageWidth - viewportWidth) / 2);
        var x = viewportWidth / 2 + _editComparePanX +
            (_editCompareSplitFraction - .5) * imageWidth;
        var edge = Math.Min(18, viewportWidth / 4);
        if (x < edge) _editComparePanX += edge - x;
        else if (x > viewportWidth - edge)
            _editComparePanX -= x - (viewportWidth - edge);
        _editComparePanX = Math.Clamp(_editComparePanX, -maxPan, maxPan);
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

    // The clip and the grip share one image-coordinate fraction. WPF
    // mouse capture is owned by the Thumb throughout a drag.
    private double _editCompareThumbGrabOffset;
    private static double SplitFractionForPointer(
        double pointerX, double grabOffset, double viewportWidth,
        double frameWidth, double zoom, double panX)
    {
        var actualWidth = frameWidth * zoom;
        if (!(actualWidth > 0) || !double.IsFinite(pointerX)) return .5;
        return Math.Clamp(.5 + (pointerX - grabOffset -
            viewportWidth / 2 - panX) / actualWidth, .02, .98);
    }

    private double EditCompareDividerScreenX()
        => EditCompareSwipePanel.ActualWidth / 2 + _editComparePanX +
            (_editCompareSplitFraction - .5) *
            EditCompareImageFrame.Width * _editCompareZoomFactor;

    private void EditCompareSplit_DragStarted(object sender, DragStartedEventArgs e)
    {
        _editCompareThumbGrabOffset =
            Mouse.GetPosition(EditCompareSwipePanel).X -
            EditCompareDividerScreenX();
    }

    private void EditCompareSplit_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!(EditCompareSwipePanel.ActualWidth > 0) ||
            !(EditCompareImageFrame.Width > 0)) return;
        _editCompareSplitFraction = SplitFractionForPointer(
            Mouse.GetPosition(EditCompareSwipePanel).X,
            _editCompareThumbGrabOffset, EditCompareSwipePanel.ActualWidth,
            EditCompareImageFrame.Width, _editCompareZoomFactor, _editComparePanX);
        UpdateEditCompareClip();
    }

    private void EditCompareSplit_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _editCompareThumbGrabOffset = 0;
        UpdateEditCompareClip();
    }

    private void UpdateEditCompareClip()
    {
        if (_editCompareMode != EditCompareMode.Split) return;
        var width = EditCompareSwipePanel.ActualWidth;
        var height = EditCompareSwipePanel.ActualHeight;
        var frameWidth = EditCompareImageFrame.Width;
        var frameHeight = EditCompareImageFrame.Height;
        if (!(width > 0) || !(height > 0) ||
            !(frameWidth > 0) || !(frameHeight > 0)) return;

        // Clip before the image transform. Never recompute its image X
        // from an unrelated screen X when Fit, zoom or pan changes.
        EditCompareSwipeOriginal.Clip = new RectangleGeometry(
            new Rect(0, 0, _editCompareSplitFraction * frameWidth, frameHeight));
        var x = EditCompareDividerScreenX();
        Canvas.SetLeft(EditCompareSplitHandle,
            x - EditCompareSplitHandle.Width / 2);
        var visualHeight = Math.Max(1, frameHeight * _editCompareZoomFactor);
        EditCompareSplitHandle.Height = visualHeight;
        Canvas.SetTop(EditCompareSplitHandle,
            height / 2 + _editComparePanY - visualHeight / 2);
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
        var live = session is not null && session.ImageId == info.Id &&
            (session.HasChanges || GetPendingLiveEditStep() is not null);
        if (!live && (_externalEditReview is not { ResultPath: not null } review ||
                     review.ImageId != info.Id))
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
                    DisplayHoldPixelsWithoutRelayout(_photoEditSession.Original);
                }
                else if (_editCompareOriginal is not null &&
                    _editCompareImageId == info.Id)
                {
                    DisplayHoldPixelsWithoutRelayout(_editCompareOriginal);
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
            DisplayHoldPixelsWithoutRelayout(pair.Value.Original);
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("EditComparison.Hold", ex);
        }
    }

    // Press-and-hold must NEVER recalculate image geometry or zoom.
    private void DisplayHoldPixelsWithoutRelayout(BitmapSource pixels)
    {
        PhotoViewerImage.Source = pixels;
        if (_viewerFullscreenImage is not null)
            _viewerFullscreenImage.Source = pixels;
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
            DisplayHoldPixelsWithoutRelayout(_editCompareHeldEdited);
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
            ApplyViewerImageLayout();
        }
        UpdateEditComparisonAvailability();
    }
}
