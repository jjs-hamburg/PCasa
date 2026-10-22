using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App.Controls;

public sealed class BrowseImageClickedEventArgs : EventArgs
{
    public BrowseImageClickedEventArgs(long imageId, ModifierKeys modifiers)
    {
        ImageId = imageId;
        Modifiers = modifiers;
    }

    public long ImageId { get; }
    public ModifierKeys Modifiers { get; }
}

public sealed class BrowseGallerySurface : FrameworkElement
{
    private const double HorizontalMargin = 10;
    private const double HeaderHeight = 42;
    private const double HeaderTopMargin = 8;
    private double _cellGap = 10;
    private double _rowGap = 8;
    private int? _spacingPreviewColumns;
    private const double ShadowDepth = 2;

    private Brush _headerBrush =
        new SolidColorBrush(Color.FromRgb(231, 231, 231));
    private Brush _mutedBrush =
        new SolidColorBrush(Color.FromRgb(169, 170, 172));
    private static readonly Brush ShadowBrush =
        new SolidColorBrush(Color.FromArgb(82, 0, 0, 0));
    private static readonly Pen SelectionPen =
        new(new SolidColorBrush(Color.FromRgb(37, 164, 244)), 2);

    private readonly List<SectionLayout> _sections = new();
    private IReadOnlyList<BrowseThumbnailIndexEntry> _index =
        Array.Empty<BrowseThumbnailIndexEntry>();
    private IReadOnlyList<PhotoFolderSummary> _folders =
        Array.Empty<PhotoFolderSummary>();
    private IReadOnlySet<long> _selectedImageIds = new HashSet<long>();
    private IReadOnlySet<long> _hiddenFolderIds = new HashSet<long>();
    private BrowseThumbnailPageCache? _pageCache;
    private double _thumbnailWidth = 180;
    private double _thumbnailHeight;
    private double _layoutWidth;
    private double _totalHeight;
    private double _viewportTop;
    private double _viewportHeight;
    private double _lastViewportTop;
    private int _scrollDirection = 1;
    private bool _showShadows;
    private Brush _galleryBackground = new SolidColorBrush(Color.FromRgb(32,33,34));

    public BrowseGallerySurface()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);
    }

    public event EventHandler<BrowseImageClickedEventArgs>? ImageClicked;
    public event EventHandler<BrowseImageClickedEventArgs>? ImageOpened;
    public event EventHandler<BrowseImageClickedEventArgs>? ImageContextRequested;
    public event Action<object?, (long ImageId, double RelativeY)>? LayoutReflowed;

    public double ThumbnailWidth => _thumbnailWidth;

    public int VisibleColumnCount
    {
        get { EnsureLayout(); return _sections.FirstOrDefault()?.ColumnCount ?? 1; }
    }

    public double ThumbnailRowHeight => _thumbnailHeight + _rowGap;

    public void SetGalleryBackground(Brush background)
    {
        _galleryBackground = background;
        InvalidateVisual();
    }

    public void SetLightTheme(bool light)
    {
        _headerBrush = new SolidColorBrush(light ? Color.FromRgb(38, 38, 38) : Color.FromRgb(231, 231, 231));
        _mutedBrush = new SolidColorBrush(light ? Color.FromRgb(93, 93, 93) : Color.FromRgb(169, 170, 172));
        InvalidateVisual();
    }

    public void SetData(
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        IReadOnlyList<PhotoFolderSummary> folders,
        BrowseThumbnailPageCache pageCache)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(pageCache);

        // A catalogue refresh is not necessarily a new gallery layout.
        // Preserve visible sections and decoded images when the index and
        // folder layout have not changed.
        var sameIndex = ReferenceEquals(_index, index);
        var sameFolders = _folders.Count == folders.Count &&
            _folders.Zip(folders).All(pair =>
                pair.First.Id == pair.Second.Id &&
                pair.First.ImageCount == pair.Second.ImageCount &&
                string.Equals(pair.First.LocalPath, pair.Second.LocalPath,
                    StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(_pageCache, pageCache))
        {
            if (_pageCache is not null)
                _pageCache.PageReady -= PageCache_PageReady;
            _pageCache = pageCache;
            _pageCache.PageReady += PageCache_PageReady;
        }

        _index = index;
        _folders = folders;
        if (!sameIndex || !sameFolders)
            InvalidateLayout();
        else
            InvalidateVisual();
    }

    public void SetSelection(IReadOnlySet<long> selectedImageIds)
    {
        _selectedImageIds = selectedImageIds ?? new HashSet<long>();
        InvalidateVisual();
    }

    public void SetThumbnailWidth(double width)
    {
        var normalized = Math.Clamp(width, 72, 600);
        if (Math.Abs(_thumbnailWidth - normalized) < 0.1)
        {
            return;
        }

        _thumbnailWidth = normalized;
        InvalidateLayout();
    }

    public bool SetHiddenFolders(IReadOnlySet<long> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);
        if (_hiddenFolderIds.Count == hidden.Count &&
            hidden.All(_hiddenFolderIds.Contains))
            return false;

        _hiddenFolderIds = hidden;
        InvalidateLayout();
        return true;
    }

    public void BeginSpacingPreview()
    {
        EnsureLayout();
        _spacingPreviewColumns = _sections.FirstOrDefault()?.ColumnCount;
        InvalidateLayout();
    }

    public void EndSpacingPreview()
    {
        _spacingPreviewColumns = null;
        InvalidateLayout();
    }

    public void SetThumbnailSpacing(double spacing)
    {
        spacing = Math.Clamp(spacing, 0, 40);
        if (Math.Abs(_cellGap - spacing) < 0.1) return;
        _cellGap = spacing;
        _rowGap = spacing;
        InvalidateLayout();
    }

    public void SetShadowEnabled(bool enabled)
    {
        if (_showShadows == enabled)
        {
            return;
        }

        _showShadows = enabled;
        InvalidateVisual();
    }

    public void SetViewport(double verticalOffset, double viewportHeight)
    {
        _lastViewportTop = _viewportTop;
        _viewportTop = Math.Max(0, verticalOffset);
        _viewportHeight = Math.Max(1, viewportHeight);

        if (_viewportTop > _lastViewportTop + 0.5)
        {
            _scrollDirection = 1;
        }
        else if (_viewportTop < _lastViewportTop - 0.5)
        {
            _scrollDirection = -1;
        }

        RequestViewportPages();
        InvalidateVisual();
    }

    public long? GetFolderIdAtOffset(double verticalOffset)
    {
        EnsureLayout();
        if (_sections.Count == 0)
        {
            return null;
        }

        var y = Math.Clamp(verticalOffset, 0, Math.Max(0, _totalHeight - 1));
        var section = FindSectionAt(y);
        return section?.Folder.Id;
    }

    public double? GetImageOffset(int imageIndex)
    {
        EnsureLayout();
        if (imageIndex < 0 || imageIndex >= _index.Count)
        {
            return null;
        }

        foreach (var section in _sections)
        {
            if (imageIndex >= section.StartImageIndex &&
                imageIndex < section.StartImageIndex + section.ImageCount)
            {
                var withinFolder = imageIndex - section.StartImageIndex;
                var row = withinFolder / section.ColumnCount;
                return Math.Max(0, section.ImagesTop + row * section.RowHeight
                    - Math.Max(0, _viewportHeight - _thumbnailHeight) / 2);
            }
        }

        return null;
    }

    public double? GetFolderOffset(long photoFolderId)
    {
        EnsureLayout();
        var section = _sections.FirstOrDefault(candidate => candidate.Folder.Id == photoFolderId);
        return section?.Top;
    }

    public (int First, int Last)? GetVisibleIndexRange()
    {
        EnsureLayout();
        return GetIndexRangeForViewport(_viewportTop, _viewportHeight);
    }

    // A collapsed folder may separate adjacent visible sections by hundreds
    // of thousands of index entries. Never treat that gap as visible cache work.
    public IReadOnlyList<(int First, int Last)> GetVisibleIndexRanges()
    {
        EnsureLayout();
        return GetIndexRangesForViewport(_viewportTop, _viewportHeight);
    }

    public Task WarmVisibleAsync(CancellationToken cancellationToken = default)
    {
        if (_pageCache is null) return Task.CompletedTask;
        return _pageCache.WarmRangesAsync(GetVisibleIndexRanges(), cancellationToken);
    }

    public (long ImageId, double RelativeY)? CaptureImageAnchor(double viewportTop)
    {
        EnsureLayout();
        return CaptureImageAnchorInCurrentLayout(viewportTop);
    }

    private (long ImageId, double RelativeY)? CaptureImageAnchorInCurrentLayout(double viewportTop)
    {
        if (_sections.Count == 0) return null;
        var section = FindSectionAt(Math.Max(0, viewportTop + 1));
        if (section is null || section.ImageCount == 0) return null;
        var row = Math.Clamp(
            (int)Math.Floor((viewportTop - section.ImagesTop) / section.RowHeight),
            0, section.RowCount - 1);
        var index = section.StartImageIndex + row * section.ColumnCount;
        if (index >= _index.Count) return null;
        return (_index[index].ImageId,
            viewportTop - (section.ImagesTop + row * section.RowHeight));
    }

    public double? GetImageAnchorOffset(long imageId, double relativeY)
    {
        EnsureLayout();
        for (var i = 0; i < _index.Count; i++)
        {
            if (_index[i].ImageId != imageId) continue;
            foreach (var section in _sections)
            {
                if (i < section.StartImageIndex ||
                    i >= section.StartImageIndex + section.ImageCount) continue;
                var row = (i - section.StartImageIndex) / section.ColumnCount;
                return section.ImagesTop + row * section.RowHeight + relativeY;
            }
            return null;
        }
        return null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? Math.Max(1, ActualWidth)
            : Math.Max(1, availableSize.Width);

        // Capture against the OLD layout, before its rows are rearranged.
        // The owner restores this same image after the new layout is measured.
        (long ImageId, double RelativeY)? anchor = null;
        if (_sections.Count > 0 && Math.Abs(width - _layoutWidth) >= 0.5)
            anchor = CaptureImageAnchorInCurrentLayout(_viewportTop);
        BuildLayout(width);
        if (anchor is { } imageAnchor)
            LayoutReflowed?.Invoke(this, imageAnchor);
        return new Size(width, Math.Max(1, _totalHeight));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        EnsureLayout();
        drawingContext.DrawRectangle(_galleryBackground, null,
            new Rect(0, Math.Max(0, _viewportTop), Math.Max(1, ActualWidth),
                Math.Max(1, _viewportHeight)));

        if (_sections.Count == 0 || _index.Count == 0)
        {
            return;
        }

        var viewportTop = Math.Max(0, _viewportTop - (_thumbnailHeight + _rowGap));
        var viewportBottom = _viewportTop + _viewportHeight + (_thumbnailHeight + _rowGap);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var section in _sections)
        {
            if (section.Bottom < viewportTop)
            {
                continue;
            }

            if (section.Top > viewportBottom)
            {
                break;
            }

            DrawSectionHeader(drawingContext, section, pixelsPerDip);
            DrawSectionImages(drawingContext, section, viewportTop, viewportBottom);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        EnsureLayout();

        var imageIndex = HitTestImageIndex(e.GetPosition(this));
        if (imageIndex is null || imageIndex.Value < 0 || imageIndex.Value >= _index.Count)
        {
            return;
        }

        var args = new BrowseImageClickedEventArgs(
            _index[imageIndex.Value].ImageId,
            Keyboard.Modifiers);
        if (e.ClickCount >= 2 &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            ImageOpened?.Invoke(this, args);
        }
        else
        {
            ImageClicked?.Invoke(this, args);
        }

        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        EnsureLayout();
        var imageIndex = HitTestImageIndex(e.GetPosition(this));
        if (imageIndex is not int index || index < 0 || index >= _index.Count)
            return;
        ImageContextRequested?.Invoke(this,
            new BrowseImageClickedEventArgs(_index[index].ImageId, Keyboard.Modifiers));
        e.Handled = true;
    }

    private void DrawSectionHeader(
        DrawingContext drawingContext,
        SectionLayout section,
        double pixelsPerDip)
    {
        var y = section.Top + HeaderTopMargin;
        var name = new FormattedText(
            section.Folder.DisplayName,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            15,
            _headerBrush,
            pixelsPerDip);
        drawingContext.DrawText(name, new Point(HorizontalMargin, y));

        var count = new FormattedText(
            $"{section.Folder.ImageCount:N0} Bilder",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            _mutedBrush,
            pixelsPerDip);
        drawingContext.DrawText(
            count,
            new Point(HorizontalMargin + name.Width + 10, y + 3));
    }

    private void DrawSectionImages(
        DrawingContext drawingContext,
        SectionLayout section,
        double viewportTop,
        double viewportBottom)
    {
        if (section.ImageCount <= 0)
        {
            return;
        }

        var firstRow = Math.Max(
            0,
            (int)Math.Floor((viewportTop - section.ImagesTop) / section.RowHeight));
        var lastRow = Math.Min(
            section.RowCount - 1,
            (int)Math.Ceiling((viewportBottom - section.ImagesTop) / section.RowHeight));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < section.ColumnCount; column++)
            {
                var relativeIndex = (row * section.ColumnCount) + column;
                if (relativeIndex >= section.ImageCount)
                {
                    break;
                }

                var imageIndex = section.StartImageIndex + relativeIndex;
                var entry = _index[imageIndex];
                // Thumbnails keep their selected size; surplus horizontal room
                // is shared by all slots, including balanced outer gutters.
                // This prevents the gallery collecting a large blank strip
                // at the right edge at certain zoom settings.
                var cellX = ImageColumnLeft(section, column);
                var cellY = section.ImagesTop + (row * section.RowHeight);
                var box = new Rect(cellX, cellY, _thumbnailWidth, _thumbnailHeight);

                if (_pageCache is not null &&
                    _pageCache.TryGet(imageIndex, out var bitmap) &&
                    bitmap is not null)
                {
                    var imageRect = FitInto(box, bitmap.PixelWidth, bitmap.PixelHeight);

                    if (_showShadows)
                    {
                        DrawEdgeShadow(drawingContext, imageRect);
                    }

                    drawingContext.DrawImage(bitmap, imageRect);

                    if (_selectedImageIds.Contains(entry.ImageId))
                    {
                        var selection = new Rect(
                            imageRect.X + 1,
                            imageRect.Y + 1,
                            Math.Max(0, imageRect.Width - 2),
                            Math.Max(0, imageRect.Height - 2));
                        drawingContext.DrawRectangle(null, SelectionPen, selection);
                    }
                }
            }
        }
    }

    private static void DrawEdgeShadow(DrawingContext drawingContext, Rect imageRect)
    {
        // Directional cast shadow: light comes from the upper left.
        // A stronger 3px offset edge plus a restrained outer falloff.
        var outer = new Rect(imageRect.X + 5, imageRect.Y + 5,
            imageRect.Width, imageRect.Height);
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)), null, outer);

        var core = new Rect(imageRect.X + 3, imageRect.Y + 3,
            imageRect.Width, imageRect.Height);
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(76, 0, 0, 0)), null, core);
    }

    private double ImageColumnLeft(SectionLayout section, int column)
    {
        var available = Math.Max(1, _layoutWidth - 2 * HorizontalMargin);
        if (_spacingPreviewColumns is not null)
        {
            // Preview shows the literal minimum distance without extra distribution.
            // Keep the same geometry for drawing and hit testing.
            var occupied = section.ColumnCount * _thumbnailWidth
                + (section.ColumnCount - 1) * _cellGap;
            return HorizontalMargin + Math.Max(0, available - occupied) / 2
                + column * (_thumbnailWidth + _cellGap);
        }

        // Normal browse mode distributes the remainder into N+1 equal gaps,
        // including the two outside margins (Picasa-style).
        var gap = Math.Max(0,
            (available - section.ColumnCount * _thumbnailWidth)
            / (section.ColumnCount + 1));
        return HorizontalMargin + (column + 1) * gap
            + column * _thumbnailWidth;
    }

    private int? HitTestImageIndex(Point point)
    {
        var section = FindSectionAt(point.Y);
        if (section is null || point.Y < section.ImagesTop || point.Y >= section.Bottom)
        {
            return null;
        }

        var relativeY = point.Y - section.ImagesTop;
        var row = (int)(relativeY / section.RowHeight);
        if (row < 0 || row >= section.RowCount)
        {
            return null;
        }

        // Use exactly the same spacing geometry as drawing.
        var column = -1;
        double xInsideCell = -1;
        for (var i = 0; i < section.ColumnCount; i++)
        {
            var relative = point.X - ImageColumnLeft(section, i);
            if (relative >= 0 && relative <= _thumbnailWidth)
            {
                column = i;
                xInsideCell = relative;
                break;
            }
        }
        if (column < 0) return null;
        var yInsideRow = relativeY - (row * section.RowHeight);
        if (xInsideCell < 0 || xInsideCell > _thumbnailWidth ||
            yInsideRow < 0 || yInsideRow > _thumbnailHeight)
        {
            return null;
        }

        var relativeIndex = (row * section.ColumnCount) + column;
        if (relativeIndex < 0 || relativeIndex >= section.ImageCount)
        {
            return null;
        }

        return section.StartImageIndex + relativeIndex;
    }

    private void RequestViewportPages()
    {
        if (_pageCache is null)
            return;
        var ranges = GetIndexRangesForViewport(_viewportTop, _viewportHeight);
        if (ranges.Count > 0)
            _pageCache.UpdateViewportRanges(ranges, _scrollDirection);
    }

    private (int First, int Last)? GetIndexRangeForViewport(
        double viewportTop, double viewportHeight)
    {
        var ranges = GetIndexRangesForViewport(viewportTop, viewportHeight);
        return ranges.Count == 0 ? null : (ranges[0].First, ranges[^1].Last);
    }

    private List<(int First, int Last)> GetIndexRangesForViewport(
        double viewportTop, double viewportHeight)
    {
        EnsureLayout();
        var ranges = new List<(int First, int Last)>();
        if (_sections.Count == 0 || _index.Count == 0)
            return ranges;

        var top = Math.Max(0, viewportTop);
        var bottom = top + Math.Max(1, viewportHeight);

        foreach (var section in _sections)
        {
            if (section.Bottom < top) continue;
            if (section.Top > bottom) break;
            if (section.ImageCount <= 0) continue;

            var firstRow = Math.Clamp(
                (int)Math.Floor((top - section.ImagesTop) / section.RowHeight),
                0, section.RowCount - 1);
            var lastRow = Math.Clamp(
                (int)Math.Ceiling((bottom - section.ImagesTop) / section.RowHeight),
                firstRow, section.RowCount - 1);
            var first = section.StartImageIndex + firstRow * section.ColumnCount;
            var last = Math.Min(
                section.StartImageIndex + section.ImageCount - 1,
                section.StartImageIndex + ((lastRow + 1) * section.ColumnCount) - 1);
            ranges.Add((first, last));
        }

        if (ranges.Count == 0)
        {
            var nearest = FindSectionAt(top) ?? _sections[0];
            if (nearest.ImageCount > 0)
                ranges.Add((nearest.StartImageIndex,
                    Math.Min(nearest.StartImageIndex + nearest.ImageCount - 1,
                        nearest.StartImageIndex + nearest.ColumnCount - 1)));
        }
        return ranges;
    }

    private SectionLayout? FindSectionAt(double y)
    {
        if (_sections.Count == 0)
        {
            return null;
        }

        var low = 0;
        var high = _sections.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var section = _sections[middle];
            if (y < section.Top)
            {
                high = middle - 1;
            }
            else if (y >= section.Bottom)
            {
                low = middle + 1;
            }
            else
            {
                return section;
            }
        }

        if (high >= 0 && high < _sections.Count)
        {
            return _sections[high];
        }

        return _sections[0];
    }

    private void EnsureLayout()
    {
        var width = Math.Max(1, ActualWidth);
        if (_sections.Count == 0 || Math.Abs(_layoutWidth - width) >= 0.5)
        {
            BuildLayout(width);
        }
    }

    private void BuildLayout(double width)
    {
        _layoutWidth = Math.Max(1, width);
        _thumbnailHeight = Math.Max(72, _thumbnailWidth * 0.72);
        _sections.Clear();

        if (_index.Count == 0)
        {
            _totalHeight = 1;
            return;
        }

        var folderById = _folders.ToDictionary(folder => folder.Id);
        var availableWidth = Math.Max(1, _layoutWidth - (HorizontalMargin * 2));
        // Choose the number of columns using the desired minimum gap.
        // Distribute leftover width evenly instead of leaving it all
        // unused on the right. Actual image dimensions remain unchanged.
        var minimumCellWidth = _thumbnailWidth + _cellGap;
        var fittingColumns = Math.Max(1,
            (int)((availableWidth + _cellGap) / minimumCellWidth));
        // Do not change the preview's column count as the slider moves unless
        // the requested spacing would otherwise clip images at the right edge.
        var columnCount = _spacingPreviewColumns is int previewColumns
            ? Math.Min(previewColumns, fittingColumns) : fittingColumns;
        var cellWidth = Math.Max(_thumbnailWidth, availableWidth / columnCount);
        var rowHeight = _thumbnailHeight + _rowGap;
        var y = 0d;
        var indexPosition = 0;

        while (indexPosition < _index.Count)
        {
            var folderId = _index[indexPosition].PhotoFolderId;
            var start = indexPosition;
            while (indexPosition < _index.Count && _index[indexPosition].PhotoFolderId == folderId)
            {
                indexPosition++;
            }

            var imageCount = indexPosition - start;
            if (_hiddenFolderIds.Contains(folderId)) continue;
            if (!folderById.TryGetValue(folderId, out var folder))
            {
                folder = new PhotoFolderSummary(
                    folderId,
                    0,
                    string.Empty,
                    string.Empty,
                    $"Ordner {folderId}",
                    imageCount);
            }

            var rows = Math.Max(1, (imageCount + columnCount - 1) / columnCount);
            var top = y;
            var imagesTop = top + HeaderHeight;
            var bottom = imagesTop + (rows * rowHeight) + HeaderTopMargin;

            _sections.Add(new SectionLayout(
                folder,
                start,
                imageCount,
                columnCount,
                rows,
                cellWidth,
                rowHeight,
                top,
                imagesTop,
                bottom));

            y = bottom;
        }

        _totalHeight = Math.Max(1, y);
    }

    private void InvalidateLayout()
    {
        _sections.Clear();
        _layoutWidth = 0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void PageCache_PageReady(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        _ = Dispatcher.BeginInvoke(InvalidateVisual);
    }

    private static Rect FitInto(Rect box, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return box;
        }

        var scale = Math.Min(box.Width / pixelWidth, box.Height / pixelHeight);
        var width = Math.Max(1, pixelWidth * scale);
        var height = Math.Max(1, pixelHeight * scale);
        return new Rect(
            box.X + ((box.Width - width) / 2),
            box.Y + ((box.Height - height) / 2),
            width,
            height);
    }

    private sealed record SectionLayout(
        PhotoFolderSummary Folder,
        int StartImageIndex,
        int ImageCount,
        int ColumnCount,
        int RowCount,
        double CellWidth,
        double RowHeight,
        double Top,
        double ImagesTop,
        double Bottom);
}
