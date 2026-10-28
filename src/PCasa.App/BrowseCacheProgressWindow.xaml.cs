using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class BrowseCacheProgressWindow : Window
{
    // Windows reports screen coordinates in physical pixels. Convert them
    // to WPF device-independent units, including mixed-DPI multimonitor setups.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private readonly ThumbnailImageLoader _loader = new();
    private readonly CancellationTokenSource _previewLifetime = new();
    private DateTime _lastPreviewUpdate = DateTime.MinValue;
    private bool _previewBusy;
    private string? _displayedPreviewImagePath;
    private string? _pendingLocalPreviewPath;
    private string? _pendingOriginalPreviewPath;
    private bool _finished;
    public event EventHandler? ProgressPresentationChanged;
    public bool IsManualPresentationActive => _manualPresentationActive;
    public string ProgressStatusText =>
        PhaseText.Text + (string.IsNullOrWhiteSpace(CountText.Text)
            ? string.Empty : " · " + CountText.Text);

    private void PublishProgressStatus()
        => ProgressPresentationChanged?.Invoke(this, EventArgs.Empty);

    private bool _manualPresentationActive;
    // Presentation-only: actual NAS work may replace a waiting manual caption.
    private bool _showingScanDuringManualWait;
    public bool ShowingScanDuringManualWait => _showingScanDuringManualWait;
    private string? _manualCurrentImagePath;
    private bool _finishRequestedWhileManual;

    // One presentation owner with a temporarily overlaid manual packet.
    // Scanner/cache progress received during the overlay must remain visible
    // when the manual job completes; otherwise the old "1 / 1" caption stays
    // frozen until the next (possibly much later) NAS progress callback.
    private enum BackgroundView { None, Scan, CachePreparing, CacheBuilding }
    private BackgroundView _backgroundView;
    private ScanProgress? _lastScanProgress;
    private BrowseCacheBuildProgress? _lastCacheProgress;
    // Owned borderless WPF windows can lose their rendered composition when
    // the taskbar minimizes/restores the owner. Restore only an intentionally
    // visible progress window; a user-hidden background window must stay hidden.
    private bool _hiddenByUser;
    private bool _ownerWasMinimized;
    private bool _restoreAfterOwnerMinimize;
    private IntPtr _anchoredMonitor;
    private bool _hasScreenPlacement;
    private bool _placingWindow;
    private bool _anchorScheduled;
    private bool _verticalDragActive;
    private bool _verticalPositionAdjusted;
    private double _dragStartPointerY;
    private double _dragStartTop;
    private double _monitorTop;
    private double _workTop;
    private double _workBottom;

    public BrowseCacheProgressWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PositionOnOwnerMonitor();
            if (Owner is null) return;
            Owner.LocationChanged += Owner_LocationChanged;
            Owner.SizeChanged += Owner_SizeChanged;
            Owner.StateChanged += Owner_StateChanged;
        };
    }

    private void PositionOnOwnerMonitor(bool force = false)
    {
        if (Owner is null || !IsLoaded) return;

        // Follow the screen containing PCasa, never PCasa's window edge.
        // Resizing/moving PCasa within a screen leaves this position alone.
        var handle = new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        if (!force && _hasScreenPlacement && monitor == _anchoredMonitor) return;

        // Preserve a manually selected vertical position on monitor changes.
        // X is ALWAYS computed from the monitor's work-area right edge.
        var previousVerticalOffset = Top - _monitorTop;
        var retainManualTop = _hasScreenPlacement && _verticalPositionAdjusted;

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var toDip = PresentationSource.FromVisual(Owner)?
                .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var monitorTop = toDip.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
            var monitorBottom = toDip.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
            var workTopLeft = toDip.Transform(new Point(info.Work.Left, info.Work.Top));
            var workBottomRight = toDip.Transform(new Point(info.Work.Right, info.Work.Bottom));

            // Retain the agreed bottom-quarter height; respect the taskbar
            // and leave 12 DIP to the right work-area boundary.
            _monitorTop = monitorTop.Y;
            _workTop = workTopLeft.Y;
            _workBottom = workBottomRight.Y;
            var targetTop = retainManualTop
                ? _monitorTop + previousVerticalOffset
                : monitorTop.Y + (monitorBottom.Y - monitorTop.Y) * 0.75;
            Top = Math.Clamp(targetTop, _workTop,
                Math.Max(_workTop, _workBottom - Height));
            Left = Math.Clamp(workBottomRight.X - Width - 12,
                workTopLeft.X, Math.Max(workTopLeft.X, workBottomRight.X - Width));
        }
        else
        {
            var work = SystemParameters.WorkArea;
            _monitorTop = work.Top;
            _workTop = work.Top;
            _workBottom = work.Bottom;
            Top = Math.Clamp(retainManualTop ? work.Top + previousVerticalOffset
                    : work.Top + 0.75 * work.Height,
                work.Top, Math.Max(work.Top, work.Bottom - Height));
            Left = Math.Clamp(work.Right - Width - 12, work.Left,
                Math.Max(work.Left, work.Right - Width));
        }

        _anchoredMonitor = monitor;
        _hasScreenPlacement = true;
    }

    private void Owner_LocationChanged(object? sender, EventArgs e)
        => ScheduleOwnerReanchor();

    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
        => ScheduleOwnerReanchor();

    private void Owner_StateChanged(object? sender, EventArgs e)
    {
        if (_finished || Owner is null) return;
        if (Owner.WindowState == WindowState.Minimized)
        {
            _ownerWasMinimized = true;
            _restoreAfterOwnerMinimize = !_hiddenByUser;
            // WPF may already have hidden an owned window by this point.
            // Explicitly hiding here makes the state transition deterministic.
            if (_restoreAfterOwnerMinimize && IsVisible) Hide();
            return;
        }

        if (!_ownerWasMinimized)
        {
            ScheduleOwnerReanchor();
            return;
        }

        _ownerWasMinimized = false;
        var restore = _restoreAfterOwnerMinimize && !_hiddenByUser;
        _restoreAfterOwnerMinimize = false;
        if (!restore)
        {
            ScheduleOwnerReanchor();
            return;
        }

        // Wait until Windows has restored the owner's HWND and WPF has
        // processed its StateChanged/SizeChanged events. Re-show the SAME
        // progress window, preserving scan text, thumbnail and job identity.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_finished || Owner is null ||
                Owner.WindowState == WindowState.Minimized || _hiddenByUser)
                return;
            if (IsVisible) Hide();
            Show();
            PositionOnOwnerMonitor(force: true);
            InvalidateMeasure();
            InvalidateVisual();
            UpdateLayout();
        }), DispatcherPriority.ContextIdle);
    }

    private void ScheduleOwnerReanchor()
    {
        if (_anchorScheduled || _placingWindow || _verticalDragActive ||
            _finished || Owner is null)
            return;
        _anchorScheduled = true;
        // WPF SizeChanged can precede final maximized-window layout. Re-anchor
        // at Loaded priority after the owner's new bounds become available.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _anchorScheduled = false;
            if (_finished || Owner is null || !IsLoaded) return;
            _placingWindow = true;
            try { PositionOnOwnerMonitor(); }
            finally { _placingWindow = false; }
        }), DispatcherPriority.Loaded);
    }

    // Manual work overrides the presentation of any preempted scan, using
    // the scheduler's own job state and progress. Underlying scanner status
    // remains in the scheduler and resumes without a competing UI model.
    public void BeginManualOperation(string name, int total)
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        _manualPresentationActive = true;
        _showingScanDuringManualWait = false;
        _hiddenByUser = false;
        PhaseText.Text = UiLanguage.T(name) + " · " + UiLanguage.T("angefordert");
        Progress.IsIndeterminate = true;
        CountText.Text = $"0 / {total:N0}";
        CurrentFileText.Text = UiLanguage.T("Warte auf Scheduler");
        CurrentFileText.ToolTip = null;
        FolderPathText.Text = string.Empty;
        FolderPathText.ToolTip = null;
        _manualCurrentImagePath = null;
        CurrentThumbnail.Source = null;
        if (!IsVisible) Show();
        PublishProgressStatus();
    }

    public void UpdateManualOperation(SchedulerJobSnapshot progress)
    {
        if (_finished || !_manualPresentationActive ||
            Dispatcher.HasShutdownStarted || _showingScanDuringManualWait) return;
        var phase = UiLanguage.T(progress.Phase ?? "Dateioperation läuft");
        PhaseText.Text = progress.State switch
        {
            SchedulerJobState.Queued => phase + " · " + UiLanguage.T("wartet"),
            SchedulerJobState.YieldRequested => phase + " · " + UiLanguage.T("pausiert"),
            _ => phase
        };
        Progress.IsIndeterminate = progress.Total == 0;
        if (progress.Total > 0)
        {
            Progress.Maximum = progress.Total;
            Progress.Value = Math.Min(progress.Total, progress.Processed);
            CountText.Text = $"{progress.Processed:N0} / {progress.Total:N0}";
        }
        else
        {
            // A recursive NAS scan has no known total until enumeration ends.
            CountText.Text = UiLanguage.Format("{0:N0} geprüft", progress.Processed);
        }
        if (progress.CurrentPath is not { Length: > 0 })
        {
            // A retry/yield may switch away from the previous original.
            // Do not leave its thumbnail/path attached to a waiting job.
            _manualCurrentImagePath = null;
            CurrentThumbnail.Source = null;
            CurrentFileText.ToolTip = null;
            FolderPathText.Text = string.Empty;
            FolderPathText.ToolTip = null;
            CurrentFileText.Text = progress.State switch
            {
                SchedulerJobState.Running => "Auftrag wird bearbeitet",
                SchedulerJobState.YieldRequested => "Wartet auf neueren Auftrag",
                SchedulerJobState.Queued => "Warte auf Scheduler",
                SchedulerJobState.Failed => progress.Error ?? "Auftrag fehlgeschlagen",
                _ => CurrentFileText.Text
            };
        }
        if (progress.CurrentPath is { Length: > 0 })
        {
            if (!string.Equals(_manualCurrentImagePath, progress.CurrentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _manualCurrentImagePath = progress.CurrentPath;
                // Never label the previous photo as the new original.
                CurrentThumbnail.Source = null;
            }
            CurrentFileText.Text = Path.GetFileName(progress.CurrentPath);
            CurrentFileText.ToolTip = progress.CurrentPath;
            FolderPathText.Text = Path.GetDirectoryName(progress.CurrentPath) ?? string.Empty;
            FolderPathText.ToolTip = progress.CurrentPath;
        }
        PublishProgressStatus();
    }

    // A thumbnail from PCasa's local browse-cache may arrive after the
    // original has advanced. Never display a preview for a different file.
    public void ShowManualThumbnail(string originalPath,
        System.Windows.Media.Imaging.BitmapSource? thumbnail)
    {
        if (!_manualPresentationActive || _finished ||
            !string.Equals(_manualCurrentImagePath, originalPath,
                StringComparison.OrdinalIgnoreCase)) return;
        CurrentThumbnail.Source = thumbnail;
    }

    // Tag writers often submit many separate durable packages. Show the
    // entire currently active tag-writing series, not just the most recently
    // scheduled package. This changes presentation only; the scheduler still
    // owns every job and its persisted position.
    public void UpdateTagAggregateProgress(int processed, int total)
    {
        if (_finished || !_manualPresentationActive ||
            Dispatcher.HasShutdownStarted || _showingScanDuringManualWait) return;
        total = Math.Max(0, total);
        processed = Math.Clamp(processed, 0, total);
        Progress.IsIndeterminate = total == 0;
        if (total > 0)
        {
            Progress.Maximum = total;
            Progress.Value = processed;
        }
        CountText.Text = $"{processed:N0} / {total:N0}";
        CountText.ToolTip =
            UiLanguage.T("Gesamtfortschritt aller Tag-Pakete dieser Arbeitsserie");
        PublishProgressStatus();
    }

    public void EndManualOperation()
    {
        if (!_manualPresentationActive) return;
        _manualPresentationActive = false;
        _showingScanDuringManualWait = false;
        _manualCurrentImagePath = null;
        if (_finishRequestedWhileManual)
        {
            _finishRequestedWhileManual = false;
            Finish();
            return;
        }

        // The window was borrowed from an ongoing NAS scan/cache job. Restore
        // its last actual progress without waiting for another network event.
        // This is presentation of its existing data, not a second work queue.
        switch (_backgroundView)
        {
            case BackgroundView.Scan when _lastScanProgress is { } scan:
                ShowScanProgress(scan);
                break;
            case BackgroundView.CachePreparing:
                // No cache image has been processed yet. Do not resurrect a
                // meaningless permanent "0 / 0" window after manual work.
                if (IsVisible) Hide();
                break;
            case BackgroundView.CacheBuilding when _lastCacheProgress is { } cache:
                ShowCacheProgress(cache);
                break;
            default:
                PhaseText.Text = UiLanguage.T("Hintergrundvorgang läuft");
                Progress.IsIndeterminate = true;
                CurrentFileText.Text = UiLanguage.T("Warte auf Hintergrundfortschritt");
                FolderPathText.Text = string.Empty;
                FolderPathText.ToolTip = null;
                CurrentThumbnail.Source = null;
                break;
        }
    }

    public void UpdateScan(ScanProgress progress,
        bool showWhileManualWaiting = false)
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        _backgroundView = BackgroundView.Scan;
        _lastScanProgress = progress;
        if (_manualPresentationActive && !showWhileManualWaiting) return;
        if (_manualPresentationActive)
            _showingScanDuringManualWait = true;
        ShowScanProgress(progress);
    }

    private void ShowScanProgress(ScanProgress progress)
    {
        PhaseText.Text = UiLanguage.T("Bilder werden eingelesen");
        Progress.IsIndeterminate = true;
        var processed = progress.Updated + progress.Unchanged + progress.Failed;
        CountText.Text = UiLanguage.Format("Neu: {0:N0} · geprüft: {1:N0}", progress.Added, processed) +
            (progress.Failed > 0 ? UiLanguage.Format(" · Fehler: {0:N0}", progress.Failed) : string.Empty);
        CountText.ToolTip = UiLanguage.Format("{0:N0} gefunden · {1:N0} unverändert · {2:N0} geändert · {3:N0} Cache-Reparaturen", progress.ImageFilesSeen, progress.Unchanged, progress.Changed, progress.CacheRepaired);
        // The scanner has already committed this local thumbnail.
        // An original on S:\\ over VPN is not a valid preview source.
        UpdateCurrentImage(progress.CurrentFile, progress.CurrentThumbnailPath);
        PublishProgressStatus();
    }

    public void BeginCachePhase()
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        _backgroundView = BackgroundView.CachePreparing;
        _lastCacheProgress = null;
        if (_manualPresentationActive) return;
        // Cache work can be queued or blocked behind an unrelated NAS read.
        // A 0/0 popup is NOT a running-job progress indicator; reveal the
        // existing window only on the first real cache progress event.
        if (IsVisible) Hide();
    }

    public void Update(BrowseCacheBuildProgress progress)
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        _backgroundView = BackgroundView.CacheBuilding;
        _lastCacheProgress = progress;
        if (_manualPresentationActive) return;
        // A startup notification without any counted images is not work
        // progress and must not open a misleading 0 / 0 dialog.
        if (progress.TotalImages == 0 && progress.ProcessedImages == 0)
            return;
        ShowCacheProgress(progress);
        PublishProgressStatus();
        if (!_hiddenByUser && !IsVisible &&
            Owner?.WindowState != WindowState.Minimized)
            Show();
    }

    private void ShowCacheProgress(BrowseCacheBuildProgress progress)
    {
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, progress.TotalImages);
        Progress.Value = Math.Min(Progress.Maximum, progress.ProcessedImages);
        CountText.Text = $"{progress.ProcessedImages:N0} / {progress.TotalImages:N0}" +
            (progress.FailedImages > 0
                ? UiLanguage.Format(" · {0:N0} Fehler", progress.FailedImages) : string.Empty);

        UpdateCurrentImage(progress.CurrentPath, progress.CurrentThumbnailPath);
    }

    private void UpdateCurrentImage(string? currentPath, string? localThumb)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (!string.Equals(_displayedPreviewImagePath, currentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                _displayedPreviewImagePath = currentPath;
                _pendingLocalPreviewPath = null;
                _pendingOriginalPreviewPath = null;
                // Do not show the previous photo under the next filename.
                CurrentThumbnail.Source = null;
            }
            CurrentFileText.Text = Path.GetFileName(currentPath);
            CurrentFileText.ToolTip = currentPath;
            FolderPathText.Text = Path.GetDirectoryName(currentPath) ?? string.Empty;
            FolderPathText.ToolTip = currentPath;
        }

        if (!string.IsNullOrWhiteSpace(localThumb) &&
            !string.IsNullOrWhiteSpace(currentPath))
        {
            // Latest-image-only UI preview: cache filenames come from a
            // successful scanner commit. Never read originals via NAS/VPN.
            // Coalesce rapid progress updates while a previous preview loads;
            // otherwise the last reported image could remain blank.
            _pendingOriginalPreviewPath = currentPath;
            _pendingLocalPreviewPath = localThumb;
            if (!_previewBusy)
                _ = LoadPreviewAsync(_previewLifetime.Token);
        }
    }

    private async Task LoadPreviewAsync(CancellationToken cancellation)
    {
        _previewBusy = true;
        try
        {
            while (!_finished && !cancellation.IsCancellationRequested &&
                   _pendingLocalPreviewPath is not null)
            {
                var remaining = TimeSpan.FromMilliseconds(550) -
                    (DateTime.UtcNow - _lastPreviewUpdate);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cancellation);
                // Pick the MOST RECENT image after any delay. Stale requests
                // never force their pixels onto a newer filename.
                var path = _pendingLocalPreviewPath;
                var original = _pendingOriginalPreviewPath;
                _pendingLocalPreviewPath = null;
                _pendingOriginalPreviewPath = null;
                if (path is null) continue;
                _lastPreviewUpdate = DateTime.UtcNow;
                var bitmap = await Task.Run(() =>
                    _loader.Load(path, 92, cancellation), cancellation);
                if (!_finished && !cancellation.IsCancellationRequested &&
                    string.Equals(_displayedPreviewImagePath, original,
                        StringComparison.OrdinalIgnoreCase))
                    CurrentThumbnail.Source = bitmap;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // A missing local thumbnail does not interrupt catalog scanning.
        }
        finally
        {
            _previewBusy = false;
        }
    }

    // Pointer capture moves only Top; Left never follows pointer X.
    // Buttons retain their normal click behavior, including child content.
    private void DialogDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not UIElement surface)
            return;

        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase)
                return;
            if (ReferenceEquals(source, surface))
                break;
            source = VisualTreeHelper.GetParent(source);
        }

        _dragStartTop = Top;
        _dragStartPointerY = ScreenPointerY(e);
        _verticalDragActive = surface.CaptureMouse();
        if (_verticalDragActive) e.Handled = true;
    }

    private void DialogDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_verticalDragActive) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndVerticalDrag(sender);
            return;
        }

        ApplyVerticalDrag(ScreenPointerY(e));
        e.Handled = true;
    }

    private void DialogDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_verticalDragActive) return;
        EndVerticalDrag(sender);
        e.Handled = true;
    }

    private void DialogDrag_LostMouseCapture(object sender, MouseEventArgs e)
        => _verticalDragActive = false;

    private void EndVerticalDrag(object sender)
    {
        _verticalDragActive = false;
        if (sender is UIElement element && element.IsMouseCaptured)
            element.ReleaseMouseCapture();
    }

    private double ScreenPointerY(MouseEventArgs e)
    {
        var screenPixels = PointToScreen(e.GetPosition(this));
        var toDip = PresentationSource.FromVisual(this)?
            .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return toDip.Transform(screenPixels).Y;
    }

    private void ApplyVerticalDrag(double pointerY)
    {
        Top = Math.Clamp(_dragStartTop + pointerY - _dragStartPointerY,
            _workTop, Math.Max(_workTop, _workBottom - Height));
        _verticalPositionAdjusted = true;
    }

    public void ShowFromStatus()
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        _hiddenByUser = false;
        if (Owner?.WindowState != WindowState.Minimized)
        {
            if (!IsVisible) Show();
            Activate();
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        _hiddenByUser = true;
        Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            _hiddenByUser = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void Finish()
    {
        if (_finished) return;
        if (_manualPresentationActive)
        {
            _finishRequestedWhileManual = true;
            return;
        }
        if (Owner is not null)
        {
            Owner.LocationChanged -= Owner_LocationChanged;
            Owner.SizeChanged -= Owner_SizeChanged;
            Owner.StateChanged -= Owner_StateChanged;
        }

        _finished = true;
        _previewLifetime.Cancel();
        _previewLifetime.Dispose();
        Close();
    }
}
