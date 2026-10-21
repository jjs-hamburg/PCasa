using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _rightAnchorUpdating;
    private bool _placingWindow;
    private bool _manuallyPositioned;

    public BrowseCacheProgressWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionOnOwnerMonitor();
        LocationChanged += (_, _) => AnchorRightEdge();
    }

    private void PositionOnOwnerMonitor()
    {
        if (Owner is null || !IsLoaded) return;

        // The TOP of the dialog belongs at the beginning of the bottom
        // quarter of the screen (75% down), not at the center of the owner.
        // Determine the monitor containing PCasa, rather than always using
        // Windows' primary monitor.
        var handle = new WindowInteropHelper(Owner).Handle;
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var toDip = PresentationSource.FromVisual(Owner)?
                .CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = toDip.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
            var bottomRight = toDip.Transform(new Point(
                info.Monitor.Right, info.Monitor.Bottom));

            // On compact screens keep the whole window on screen.
            var target = topLeft.Y + (bottomRight.Y - topLeft.Y) * 0.75;
            Top = Math.Min(target, bottomRight.Y - Height);
        }
        else
        {
            var screen = SystemParameters.WorkArea;
            Top = screen.Top + 0.75 * screen.Height;
        }

        AnchorRightEdge();
        Owner.LocationChanged += Owner_LocationChanged;
        Owner.SizeChanged += Owner_SizeChanged;
    }

    private void Owner_LocationChanged(object? sender, EventArgs e)
    {
        // Re-anchor on the owner's current monitor if PCasa changes screens.
        if (!_placingWindow)
        {
            RepositionOnOwnerMonitor();
        }
    }

    private void Owner_SizeChanged(object sender, SizeChangedEventArgs e)
        => AnchorRightEdge();

    private void RepositionOnOwnerMonitor()
    {
        if (Owner is null) return;
        try
        {
            _placingWindow = true;
            // Keep event handlers single-registered across monitor changes.
            Owner.LocationChanged -= Owner_LocationChanged;
            Owner.SizeChanged -= Owner_SizeChanged;
            PositionOnOwnerMonitor();
        }
        finally
        {
            _placingWindow = false;
        }
    }

    private void AnchorRightEdge()
    {
        if (_rightAnchorUpdating || Owner is null || !IsLoaded) return;
        var left = Owner.Left + Owner.ActualWidth - Width - 12;
        if (Math.Abs(Left - left) < 0.5) return;
        try
        {
            _rightAnchorUpdating = true;
            Left = left;
        }
        finally
        {
            _rightAnchorUpdating = false;
        }
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
        _manuallyPositioned = true;
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
        }

        _finished = true;
        _previewLifetime.Cancel();
        _previewLifetime.Dispose();
        Close();
    }
}
