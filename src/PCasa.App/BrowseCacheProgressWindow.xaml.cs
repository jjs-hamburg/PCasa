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
    private bool _finished;
    private IntPtr _anchoredMonitor;
    private bool _hasScreenPlacement;
    private bool _placingWindow;
    private bool _anchorScheduled;

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

    private void PositionOnOwnerMonitor()
    {
        if (Owner is null || !IsLoaded) return;

        // Follow the screen containing PCasa, never PCasa's window edge.
        // Resizing/moving PCasa within a screen leaves this position alone.
        var handle = new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        if (_hasScreenPlacement && monitor == _anchoredMonitor) return;

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
            var targetTop = monitorTop.Y + (monitorBottom.Y - monitorTop.Y) * 0.75;
            Top = Math.Clamp(targetTop, workTopLeft.Y,
                Math.Max(workTopLeft.Y, workBottomRight.Y - Height));
            Left = Math.Clamp(workBottomRight.X - Width - 12,
                workTopLeft.X, Math.Max(workTopLeft.X, workBottomRight.X - Width));
        }
        else
        {
            var work = SystemParameters.WorkArea;
            Top = Math.Clamp(work.Top + 0.75 * work.Height, work.Top,
                Math.Max(work.Top, work.Bottom - Height));
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
        => ScheduleOwnerReanchor();

    private void ScheduleOwnerReanchor()
    {
        if (_anchorScheduled || _placingWindow || _finished || Owner is null)
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

    public void UpdateScan(ScanProgress progress)
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        PhaseText.Text = "Bilder werden eingelesen";
        Progress.IsIndeterminate = true;
        var processed = progress.Updated + progress.Unchanged + progress.Failed;
        CountText.Text = $"Neu: {progress.Added:N0} · geprüft: {processed:N0}" +
            (progress.Failed > 0 ? $" · Fehler: {progress.Failed:N0}" : string.Empty);
        CountText.ToolTip = $"{progress.ImageFilesSeen:N0} gefunden · " +
            $"{progress.Unchanged:N0} unverändert · {progress.Changed:N0} geändert · " +
            $"{progress.CacheRepaired:N0} Cache-Reparaturen";
        UpdateCurrentImage(progress.CurrentFile, progress.CurrentFile);
    }

    public void BeginCachePhase()
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        PhaseText.Text = "Vorschaudaten werden vorbereitet";
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        CountText.Text = "0 / 0";
        CurrentFileText.Text = "Vorbereitung …";
    }

    public void Update(BrowseCacheBuildProgress progress)
    {
        if (_finished || Dispatcher.HasShutdownStarted) return;
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(1, progress.TotalImages);
        Progress.Value = Math.Min(Progress.Maximum, progress.ProcessedImages);
        CountText.Text = $"{progress.ProcessedImages:N0} / {progress.TotalImages:N0}" +
            (progress.FailedImages > 0
                ? $" · {progress.FailedImages:N0} Fehler" : string.Empty);

        UpdateCurrentImage(progress.CurrentPath, progress.CurrentThumbnailPath);
    }

    private void UpdateCurrentImage(string? currentPath, string? localThumb)
    {
        if (!string.IsNullOrEmpty(currentPath))
        {
            CurrentFileText.Text = Path.GetFileName(currentPath);
            CurrentFileText.ToolTip = currentPath;
        }

        if (!string.IsNullOrWhiteSpace(localThumb) && !_previewBusy &&
            DateTime.UtcNow - _lastPreviewUpdate >= TimeSpan.FromMilliseconds(850))
        {
            _lastPreviewUpdate = DateTime.UtcNow;
            _ = LoadPreviewAsync(localThumb, _previewLifetime.Token);
        }
    }

    private async Task LoadPreviewAsync(string localPath, CancellationToken cancellation)
    {
        _previewBusy = true;
        try
        {
            var bitmap = await Task.Run(() =>
                _loader.Load(localPath, 92, cancellation), cancellation);
            if (!_finished && !cancellation.IsCancellationRequested)
                CurrentThumbnail.Source = bitmap;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A missing thumbnail must not interrupt the cache conversion.
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private void DialogDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase)
            return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void Finish()
    {
        if (_finished) return;
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
