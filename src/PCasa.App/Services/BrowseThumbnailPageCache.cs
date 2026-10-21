using System.Windows.Media.Imaging;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed class BrowseThumbnailPageCache : IDisposable
{
    private const int PageSize = 32;
    private const long MaximumDecodedBytes = 512L * 1024 * 1024;
    private static readonly int PageLoadConcurrency =
        Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private readonly object _sync = new();
    private readonly PCasaScheduler? _scheduler;
    public BrowseThumbnailPageCache(PCasaScheduler? scheduler = null) => _scheduler = scheduler;
    private readonly PackedThumbnailReader _reader = new();
    private readonly SemaphoreSlim _pageLoadGate =
        new(PageLoadConcurrency, PageLoadConcurrency);
    private readonly Dictionary<int, PageState> _pages = new();
    private readonly LinkedList<int> _lru = new();

    private IReadOnlyList<BrowseThumbnailIndexEntry> _index =
        Array.Empty<BrowseThumbnailIndexEntry>();
    private long _decodedBytes;
    private int _generation;
    private bool _disposed;

    public event EventHandler? PageReady;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _index.Count;
            }
        }
    }

    public void SetIndex(IReadOnlyList<BrowseThumbnailIndexEntry> index)
    {
        ArgumentNullException.ThrowIfNull(index);

        lock (_sync)
        {
            ThrowIfDisposed();
            // The index describes image identity and packed byte locations.
            // Reapplying the same data must not discard already decoded pages.
            if (ReferenceEquals(_index, index) ||
                (_index.Count == index.Count && _index.SequenceEqual(index)))
                return;
            _generation++;

            foreach (var page in _pages.Values)
            {
                page.Cancellation.Cancel();
                page.Cancellation.Dispose();
            }

            _pages.Clear();
            _lru.Clear();
            _decodedBytes = 0;
            _index = index;
        }
    }

    public bool TryGet(int imageIndex, out BitmapSource? bitmap)
    {
        lock (_sync)
        {
            if (_disposed || imageIndex < 0 || imageIndex >= _index.Count)
            {
                bitmap = null;
                return false;
            }

            var pageNumber = imageIndex / PageSize;
            if (!_pages.TryGetValue(pageNumber, out var page) ||
                page.Bitmaps is null)
            {
                bitmap = null;
                return false;
            }

            TouchLoadedPage(page);
            bitmap = page.Bitmaps[imageIndex - (pageNumber * PageSize)];
            return bitmap is not null;
        }
    }

    public void UpdateViewport(
        int firstVisibleIndex, int lastVisibleIndex, int scrollDirection)
        => UpdateViewportRanges(
            new[] { (First: firstVisibleIndex, Last: lastVisibleIndex) },
            scrollDirection);

    public void UpdateViewportRanges(
        IReadOnlyList<(int First, int Last)> visibleRanges, int scrollDirection)
    {
        ArgumentNullException.ThrowIfNull(visibleRanges);
        List<int> visiblePages;
        List<int> prefetchPages;
        HashSet<int> desiredPages;

        lock (_sync)
        {
            if (_disposed || _index.Count == 0 || visibleRanges.Count == 0)
                return;

            var pageNumbers = new SortedSet<int>();
            foreach (var (first, last) in visibleRanges)
            {
                if (first < 0 || last < first || first >= _index.Count)
                    continue;
                var firstPage = first / PageSize;
                var lastPage = Math.Min(last, _index.Count - 1) / PageSize;
                for (var page = firstPage; page <= lastPage; page++)
                    pageNumbers.Add(page);
            }
            if (pageNumbers.Count == 0) return;

            visiblePages = pageNumbers.ToList();
            var maximumPage = (_index.Count - 1) / PageSize;
            var behind = scrollDirection >= 0 ? 2 : 6;
            var ahead = scrollDirection >= 0 ? 6 : 2;
            prefetchPages = new List<int>();

            for (var page = Math.Max(0, pageNumbers.Min - behind);
                 page < pageNumbers.Min; page++)
                prefetchPages.Add(page);
            for (var page = pageNumbers.Max + 1;
                 page <= Math.Min(maximumPage, pageNumbers.Max + ahead); page++)
                prefetchPages.Add(page);

            desiredPages = visiblePages.Concat(prefetchPages).ToHashSet();
            foreach (var state in _pages.Values.ToArray())
            {
                if (state.Bitmaps is null && !desiredPages.Contains(state.PageNumber))
                    state.Cancellation.Cancel();
            }
        }

        // Schedule visible pages before speculative prefetch pages. A collapsed
        // interval between visible sections is never iterated over.
        foreach (var page in scrollDirection >= 0
                     ? visiblePages : visiblePages.AsEnumerable().Reverse())
            RequestPage(page);

        foreach (var page in scrollDirection >= 0
                     ? prefetchPages.OrderBy(page => page)
                     : prefetchPages.OrderByDescending(page => page))
            RequestPage(page);
    }

    public Task WarmRangeAsync(
        int firstIndex, int lastIndex, CancellationToken cancellationToken = default)
        => WarmRangesAsync(new[] { (First: firstIndex, Last: lastIndex) }, cancellationToken);

    public async Task WarmRangesAsync(
        IReadOnlyList<(int First, int Last)> ranges,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        IReadOnlyList<Task> tasks;
        lock (_sync)
        {
            if (_disposed || _index.Count == 0 || ranges.Count == 0)
                return;

            var pages = new SortedSet<int>();
            foreach (var (first, last) in ranges)
            {
                if (first < 0 || last < first || first >= _index.Count)
                    continue;
                for (var page = first / PageSize;
                     page <= Math.Min(last, _index.Count - 1) / PageSize; page++)
                    pages.Add(page);
            }
            tasks = pages.Select(RequestPageLocked).ToArray();
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    private void RequestPage(int pageNumber)
    {
        lock (_sync)
        {
            if (_disposed || pageNumber < 0)
            {
                return;
            }

            _ = RequestPageLocked(pageNumber);
        }
    }

    private Task RequestPageLocked(int pageNumber)
    {
        var maximumPage = _index.Count == 0 ? -1 : (_index.Count - 1) / PageSize;
        if (pageNumber > maximumPage)
        {
            return Task.CompletedTask;
        }

        if (_pages.TryGetValue(pageNumber, out var existing))
        {
            if (existing.Bitmaps is not null)
            {
                TouchLoadedPage(existing);
            }

            return existing.LoadTask ?? Task.CompletedTask;
        }

        var cancellation = new CancellationTokenSource();
        var state = new PageState(pageNumber, cancellation);
        _pages.Add(pageNumber, state);

        var generation = _generation;
        var index = _index;
        state.LoadTask = _scheduler is null
            ? Task.Run(() => LoadPageAsync(state, index, generation), CancellationToken.None)
            : LoadScheduledPageAsync(state, index, generation);
        return state.LoadTask;
    }

    private async Task LoadScheduledPageAsync(
        PageState state, IReadOnlyList<BrowseThumbnailIndexEntry> index, int generation)
    {
        try
        {
            await _scheduler!.EnqueueAsync(
                $"thumbnail-page:{generation}:{state.PageNumber}",
                SchedulerPriority.UserAction,
                _ => LoadPageAsync(state, index, generation),
                cancellationToken: state.Token,
                workKind: SchedulerWorkKind.ThumbnailRead);
        }
        catch (OperationCanceledException) { /* Viewport or index changed. */ }
        catch (ObjectDisposedException) { /* Window is closing. */ }
        finally
        {
            // A scheduler job can be canceled BEFORE LoadPageAsync ever ran.
            // In that case its own cancellation handler never removes the
            // placeholder. Without this cleanup a later revisit sees a
            // completed LoadTask and permanently skips the missing page.
            lock (_sync)
            {
                if (!_disposed && state.Token.IsCancellationRequested &&
                    state.Bitmaps is null &&
                    _pages.TryGetValue(state.PageNumber, out var current) &&
                    ReferenceEquals(current, state))
                {
                    _pages.Remove(state.PageNumber);
                    state.Cancellation.Dispose();
                }
            }
        }
    }

    private async Task LoadPageAsync(
        PageState state,
        IReadOnlyList<BrowseThumbnailIndexEntry> index,
        int generation)
    {
        var entered = false;
        try
        {
            await _pageLoadGate.WaitAsync(state.Token);
            entered = true;

            var start = state.PageNumber * PageSize;
            var count = Math.Min(PageSize, index.Count - start);
            if (count <= 0)
            {
                return;
            }

            var bitmaps = new BitmapSource?[count];
            long estimatedBytes = 0;

            for (var offset = 0; offset < count; offset++)
            {
                state.Token.ThrowIfCancellationRequested();
                try
                {
                    var bitmap = _reader.Load(
                        index[start + offset],
                        state.Token);
                    bitmaps[offset] = bitmap;
                    if (bitmap is not null)
                    {
                        estimatedBytes += Math.Max(
                            1L,
                            (long)bitmap.PixelWidth * Math.Max(1, bitmap.PixelHeight) * 4L);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // One damaged cache entry must not prevent the rest of the page
                    // from becoming usable. A later cache rebuild can repair it.
                }
            }

            var publish = false;
            lock (_sync)
            {
                if (!_disposed &&
                    generation == _generation &&
                    !state.Token.IsCancellationRequested &&
                    _pages.TryGetValue(state.PageNumber, out var current) &&
                    ReferenceEquals(current, state))
                {
                    state.Bitmaps = bitmaps;
                    state.EstimatedBytes = estimatedBytes;
                    _decodedBytes += estimatedBytes;
                    TouchLoadedPage(state);
                    EvictIfNeeded(state.PageNumber);
                    publish = true;
                }
            }

            if (publish)
            {
                PageReady?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync)
            {
                if (_pages.TryGetValue(state.PageNumber, out var current) &&
                    ReferenceEquals(current, state) &&
                    state.Bitmaps is null)
                {
                    _pages.Remove(state.PageNumber);
                    state.Cancellation.Dispose();
                }
            }
        }
        finally
        {
            if (entered)
            {
                _pageLoadGate.Release();
            }
        }
    }

    private void TouchLoadedPage(PageState page)
    {
        if (page.LruNode is not null)
        {
            _lru.Remove(page.LruNode);
        }

        page.LruNode = _lru.AddFirst(page.PageNumber);
    }

    private void EvictIfNeeded(int protectedPage)
    {
        while (_decodedBytes > MaximumDecodedBytes && _lru.Last is { } last)
        {
            var pageNumber = last.Value;
            if (pageNumber == protectedPage && _lru.Count == 1)
            {
                break;
            }

            _lru.RemoveLast();
            if (!_pages.TryGetValue(pageNumber, out var page) || page.Bitmaps is null)
            {
                continue;
            }

            _decodedBytes -= page.EstimatedBytes;
            page.Bitmaps = null;
            page.EstimatedBytes = 0;
            page.LruNode = null;
            page.Cancellation.Dispose();
            _pages.Remove(pageNumber);
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var page in _pages.Values)
            {
                page.Cancellation.Cancel();
                page.Cancellation.Dispose();
            }

            _pages.Clear();
            _lru.Clear();
            _decodedBytes = 0;
            _index = Array.Empty<BrowseThumbnailIndexEntry>();
        }

        // Do not dispose the semaphore/pack reader here while cancelled workers may
        // still be unwinding. The main window is closing and the process owns these
        // short-lived resources; avoiding a disposal race is more important.
    }

    private sealed class PageState
    {
        public PageState(int pageNumber, CancellationTokenSource cancellation)
        {
            PageNumber = pageNumber;
            Cancellation = cancellation;
            Token = cancellation.Token;
        }

        public int PageNumber { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token { get; }
        public Task? LoadTask { get; set; }
        public BitmapSource?[]? Bitmaps { get; set; }
        public long EstimatedBytes { get; set; }
        public LinkedListNode<int>? LruNode { get; set; }
    }
}
