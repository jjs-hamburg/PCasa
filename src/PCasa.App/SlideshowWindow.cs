using System.IO;
using System.Windows.Forms.Integration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

// Presentation of the EXISTING PhotoSequence. Never traverses the folder
// tree or submits new catalog/scheduler work. Disk/NAS decoding and the
// optional single-picture look-ahead are fully cancellable foreground reads.
public sealed class SlideshowWindow : Window
{
    private sealed class Layer
    {
        public readonly Image Picture;
        public readonly Border Frame;
        public readonly ScaleTransform Scale = new();
        public readonly TranslateTransform Shift = new();

        public Layer()
        {
            Picture = new Image
            {
                // The enclosing frame already has the exact source
                // aspect ratio; Fill cannot distort the photograph here.
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            // BitmapCache and the compositor animate this layer. During
            // crossfades avoid costly high-quality resampling of a full
            // screen image on the UI render path.
            RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.LowQuality);
            var transforms = new TransformGroup();
            transforms.Children.Add(Scale);
            transforms.Children.Add(Shift);
            Frame = new Border
            {
                Child = Picture,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = transforms,
                RenderTransformOrigin = new Point(.5, .5),
                Visibility = Visibility.Collapsed,
                ClipToBounds = false,
                // Render picture, white border and shadow once into a
                // compositor cache instead of recalculating each frame.
                CacheMode = new BitmapCache { RenderAtScale = 1 }
            };
        }

        public void StopAnimations()
        {
            Frame.BeginAnimation(UIElement.OpacityProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Shift.BeginAnimation(TranslateTransform.XProperty, null);
            Frame.Opacity = 1;
            Scale.ScaleX = Scale.ScaleY = 1;
            Shift.X = 0;
        }
    }

    private readonly PhotoSequence _sequence;
    private readonly LocalDatabase _database;
    private readonly Func<int, BitmapSource?> _localPreview;
    private readonly SlideshowSettings _options;
    private readonly Grid _root = new();
    private readonly Grid _stage = new();
    private readonly WindowsFormsHost _gpuHost = new();
    private readonly System.Windows.Forms.Panel _gpuWindow = new()
    {
        Dock = System.Windows.Forms.DockStyle.Fill,
        TabStop = false
    };
    private Direct3DSlideshowSurface? _gpu;
    private Layer _active = new();
    private Layer _incoming = new();
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _currentDecode;
    private CancellationTokenSource? _aheadDecode;
    private BitmapSource? _aheadPixels;
    // A higher-resolution decode may finish during a cached WPF fade.
    // Apply it only AFTER the transition to avoid remeasuring its frame
    // and repainting a large image in the middle of animation.
    private BitmapSource? _afterTransitionPixels;
    private long _aheadImageId = -1;
    private int _requestVersion;
    private int _transitionVersion;
    private int _index;
    private int _shownIndex = -1;
    private int _failedInRow;
    private bool _paused;
    private bool _transitioning;
    private bool _awaitingPixels;
    private bool _closed;
    private DateTime _lastChangeUtc = DateTime.UtcNow;
    private DateTime _transitionStartedUtc;
    private int _transitionRenderedFrames;

    public int CurrentImageIndex => _index;
    public bool IsPaused => _paused;

    public SlideshowWindow(Window owner, PhotoSequence sequence, int startIndex,
        LocalDatabase database, Brush galleryBackground,
        SlideshowSettings options, Func<int, BitmapSource?> localPreview)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(localPreview);
        if (sequence.Count == 0) throw new ArgumentException(
            "The slideshow requires a nonempty gallery sequence.", nameof(sequence));

        Owner = owner;
        _sequence = sequence;
        _database = database;
        _localPreview = localPreview;
        _options = options.Copy().Normalize();
        _index = Math.Clamp(startIndex, 0, sequence.Count - 1);
        if (VideoFormats.IsVideo(sequence.At(_index).FileName))
        {
            var firstStill = NextStillIndex(sequence, _index, +1);
            if (firstStill >= 0) _index = firstStill;
        }

        Title = UiLanguage.T("Slideshow");
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Focusable = true;
        Background = ResolveBackground(galleryBackground);
        Cursor = Cursors.Arrow;
        Content = _root;

        _root.Children.Add(_stage);
        _stage.HorizontalAlignment = HorizontalAlignment.Center;
        _stage.VerticalAlignment = VerticalAlignment.Center;
        ConfigureLayer(_active);
        ConfigureLayer(_incoming);
        _stage.Children.Add(_active.Frame);
        _stage.Children.Add(_incoming.Frame);
        // Native HWND has its own GPU flip chain; WPF layers remain a fallback
        // only when the Direct3D device cannot be created.
        _gpuHost.Child = _gpuWindow;
        _root.Children.Add(_gpuHost);
        _gpuWindow.Resize += (_, _) =>
        {
            if (_gpu is not { IsReady: true }) return;
            _gpu.Resize(_gpuWindow.ClientSize.Width, _gpuWindow.ClientSize.Height);
            RefreshGpuBackground();
        };
        _gpuWindow.MouseDown += (_, _) => Close();
        _gpuWindow.KeyDown += (_, e) =>
        {
            switch (e.KeyCode)
            {
                case System.Windows.Forms.Keys.Escape: Close(); break;
                case System.Windows.Forms.Keys.Right: Navigate(+1); break;
                case System.Windows.Forms.Keys.Left: Navigate(-1); break;
                case System.Windows.Forms.Keys.Space: _paused = !_paused; break;
            }
        };

        // Picture-only slideshow: no unrequested text or filename overlays.
        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _clock.Tick += ClockTick;
        // Measure actual compositor frame delivery during transitions.
        // A timer tick is NOT a rendered animation frame.
        CompositionTarget.Rendering += CountTransitionFrame;
        PreviewKeyDown += SlideshowKeyDown;
        // Just as a click on PCasa's ordinary picture viewer leaves that
        // view. Key commands still control the slideshow independently.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_closed) return;
            e.Handled = true;
            Close();
        };
        _root.SizeChanged += (_, _) => UpdateFrameGeometry();
        Loaded += (_, _) =>
        {
            UpdateFrameGeometry();
            InitializeGpu();
            ShowCurrent();
            _clock.Start();
            Focus();
            Keyboard.Focus(this);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
            CompositionTarget.Rendering -= CountTransitionFrame;
            _currentDecode?.Cancel();
            _aheadDecode?.Cancel();
            _currentDecode?.Dispose();
            _aheadDecode?.Dispose();
            _gpu?.Dispose();
            _gpu = null;
            _active.StopAnimations();
            _incoming.StopAnimations();
        };
    }

    private void InitializeGpu()
    {
        var renderer = new Direct3DSlideshowSurface(Dispatcher);
        try
        {
            if (!renderer.Start(_gpuWindow.Handle,
                    _gpuWindow.ClientSize.Width, _gpuWindow.ClientSize.Height,
                    _options))
            {
                PCasaDiagnostics.WriteMessage("Slideshow.Direct3D11",
                    "Direct3D11 unavailable: " + renderer.StartupError?.Message);
                renderer.Dispose();
                _gpuHost.Visibility = Visibility.Collapsed;
                return;
            }
            _gpu = renderer;
            _stage.Visibility = Visibility.Collapsed;
            RefreshGpuBackground();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Slideshow.Direct3D11.Initialize", ex);
            renderer.Dispose();
            _gpuHost.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshGpuBackground()
    {
        if (_gpu is not { IsReady: true }) return;
        var width = _gpuWindow.ClientSize.Width;
        var height = _gpuWindow.ClientSize.Height;
        if (width < 1 || height < 1) return;
        // WPF paints the gallery texture ONCE at startup/resize. Each
        // transition frame is then rendered exclusively by Direct3D.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(Background, null,
                new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        _gpu.SetBackground(bitmap);
    }

    private Brush ResolveBackground(Brush galleryBackground)
    {
        if (_options.BackgroundMode == "black") return Brushes.Black;
        if (_options.BackgroundMode == "custom" &&
            !string.IsNullOrWhiteSpace(_options.CustomBackgroundPath))
        {
            try
            {
                using var stream = new FileStream(_options.CustomBackgroundPath,
                    FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException)
            { PCasaDiagnostics.Write("Slideshow.Background", ex); }
        }
        // Same actual brush instance as the gallery, not a duplicate
        // texture preference or a second rendering/migration code path.
        return galleryBackground;
    }

    private void ConfigureLayer(Layer layer)
    {
        if (_options.DisplayMode == "fullscreen") return;
        if (_options.WhiteBorder)
        {
            layer.Frame.BorderBrush = Brushes.White;
            layer.Frame.BorderThickness = new Thickness(_options.BorderWidth);
        }
        if (_options.Shadow)
        {
            layer.Frame.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                // A denser, tighter shadow is more legible on textured
                // backgrounds and cheaper to rasterize than a broad blur.
                BlurRadius = _options.ShadowWidth * .8,
                ShadowDepth = _options.ShadowOffset,
                // WPF's 315° casts the shadow down and right: the
                // virtual light source is in the top-left, as in thumbnails.
                Direction = 315,
                Opacity = Math.Min(1, _options.ShadowOpacity * 1.35 / 100.0)
            };
        }
    }

    // The frame border is drawn exactly around the FITTED PHOTO, not
    // around the stage's empty letterbox area. Both portrait and landscape
    // retain their pixel aspect ratio on every window size.
    public static Size FitPhotoFrame(int pixelWidth, int pixelHeight,
        double availableWidth, double availableHeight, double borderWidth)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 ||
            !(availableWidth > 0) || !(availableHeight > 0) ||
            !double.IsFinite(availableWidth) ||
            !double.IsFinite(availableHeight))
            return new Size(0, 0);
        var border = Math.Max(0, borderWidth);
        var contentWidth = Math.Max(1, availableWidth - 2 * border);
        var contentHeight = Math.Max(1, availableHeight - 2 * border);
        var scale = Math.Min(contentWidth / pixelWidth, contentHeight / pixelHeight);
        return new Size(pixelWidth * scale + 2 * border,
            pixelHeight * scale + 2 * border);
    }

    private void UpdateLayerGeometry(Layer layer)
    {
        if (layer.Picture.Source is not BitmapSource bitmap) return;
        var border = _options.DisplayMode == "gallery" && _options.WhiteBorder
            ? _options.BorderWidth : 0;
        var size = FitPhotoFrame(bitmap.PixelWidth, bitmap.PixelHeight,
            _stage.Width, _stage.Height, border);
        if (!(size.Width > 0) || !(size.Height > 0)) return;
        layer.Frame.Width = size.Width;
        layer.Frame.Height = size.Height;
    }

    private void UpdateFrameGeometry()
    {
        var width = _root.ActualWidth;
        var height = _root.ActualHeight;
        if (!(width > 0) || !(height > 0)) return;
        var ratio = _options.DisplayMode == "fullscreen"
            ? 1 : _options.GalleryPercent / 100.0;
        _stage.Width = Math.Max(1, width * ratio);
        _stage.Height = Math.Max(1, height * ratio);
        UpdateLayerGeometry(_active);
        UpdateLayerGeometry(_incoming);
    }

    public static int NextStillIndex(PhotoSequence sequence, int start, int direction)
    {
        if (sequence.Count == 0 || direction == 0) return -1;
        for (var count = 0; count < sequence.Count; count++)
        {
            start = sequence.Move(start, direction);
            if (!VideoFormats.IsVideo(sequence.At(start).FileName))
                return start;
        }
        return -1;
    }

    private void SlideshowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left || e.Key == Key.Right)
        {
            Navigate(e.Key == Key.Right ? +1 : -1);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            _paused = !_paused;
            _lastChangeUtc = DateTime.UtcNow;
            e.Handled = true;
        }
    }

    private void CountTransitionFrame(object? sender, EventArgs args)
    {
        if (_transitioning) _transitionRenderedFrames++;
    }

    private void ClockTick(object? sender, EventArgs args)
    {
        if (_paused || _transitioning || _awaitingPixels ||
            DateTime.UtcNow - _lastChangeUtc <
                TimeSpan.FromSeconds(_options.IntervalSeconds)) return;
        Navigate(+1);
    }

    private void Navigate(int direction)
    {
        if (direction == 0) return;
        var next = NextStillIndex(_sequence, _index, direction);
        if (next < 0 || next == _index) return;
        FinishTransition();
        _index = next;
        _lastChangeUtc = DateTime.UtcNow;
        ShowCurrent();
    }

    private void ShowCurrent()
    {
        _currentDecode?.Cancel();
        _currentDecode?.Dispose();
        _currentDecode = new CancellationTokenSource();
        var token = _currentDecode.Token;
        var revision = ++_requestVersion;
        var entry = _sequence.At(_index);
        _awaitingPixels = true;
        _afterTransitionPixels = null;

        BitmapSource? preview = null;
        var hadPrefetchedOriginal = _aheadImageId == entry.ImageId &&
            _aheadPixels is not null;
        if (hadPrefetchedOriginal)
            preview = _aheadPixels;
        else
        {
            try { preview = _localPreview(_sequence.GalleryPositionAt(_index)); }
            catch (Exception ex) { PCasaDiagnostics.Write("Slideshow.Preview", ex); }
        }
        // A stale prefetch is not allowed to delay manual navigation.
        _aheadDecode?.Cancel();
        _aheadDecode?.Dispose();
        _aheadDecode = null;
        _aheadPixels = null;
        _aheadImageId = -1;

        // Use the local thumbnail IMMEDIATELY even for manual navigation
        // while the NAS is slow. Its full-resolution replacement is deferred
        // until AFTER the GPU transition, never performed mid-fade.
        if (preview is not null)
        {
            Present(preview, _index);
            _awaitingPixels = false;
            _failedInRow = 0;
            if (hadPrefetchedOriginal)
            {
                StartLookAhead();
                return;
            }
        }
        _ = DecodeCurrentAsync(entry.ImageId, _index, revision, token, preview);
    }

    private async Task DecodeCurrentAsync(long id, int position,
        int revision, CancellationToken token, BitmapSource? localFallback)
    {
        BitmapSource? bitmap = null;
        try
        {
            // The low-resolution LOCAL preview is already on screen. Avoid
            // competing 4K decode/resize work while WPF animates the cached
            // frames. The next prefetched original takes priority when ready.
            if (localFallback is not null && _transitioning)
                await Task.Delay(_options.TransitionMilliseconds, token);
            bitmap = await DecodeAsync(id, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { PCasaDiagnostics.Write("Slideshow.Decode", ex); }
        if (token.IsCancellationRequested || revision != _requestVersion ||
            _closed || !IsLoaded || Dispatcher.HasShutdownStarted) return;

        if (bitmap is not null)
        {
            _failedInRow = 0;
            if (_shownIndex == position)
            {
                // During A/B transitions the new image lives in _incoming,
                // not in the disappearing old _active layer.
                if (_transitioning)
                    _afterTransitionPixels = bitmap;
                else
                {
                    _active.Picture.Source = bitmap;
                    UpdateLayerGeometry(_active);
                    _gpu?.Replace(bitmap);
                }
            }
            else
                Present(bitmap, position);
            _awaitingPixels = false;
            StartLookAhead();
        }
        else if (localFallback is not null)
        {
            if (_shownIndex != position)
                Present(localFallback, position);
            _awaitingPixels = false;
            _failedInRow = 0;
            StartLookAhead();
        }
        else
        {
            _failedInRow++;
            if (_failedInRow < _sequence.Count)
                Navigate(+1);
            else
            {
                _paused = true;
                _awaitingPixels = false;
            }
        }
        _lastChangeUtc = DateTime.UtcNow;
    }

    private Task<BitmapSource?> DecodeAsync(long id, CancellationToken token)
    {
        var size = Math.Clamp((int)Math.Ceiling(Math.Max(
            SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight)),
            960, 4096);
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var image = new PhotoViewerImageRepository(_database).Get(id);
            if (image is null) return (BitmapSource?)null;
            return PhotoViewerImageLoader.Load(image, size, token);
        }, token);
    }

    private void StartLookAhead()
    {
        if (!_options.PreloadNext || _sequence.Count < 2) return;
        var next = NextStillIndex(_sequence, _index, +1);
        if (next < 0 || next == _index) return;
        var id = _sequence.At(next).ImageId;
        _aheadDecode?.Cancel();
        _aheadDecode?.Dispose();
        var cancellation = new CancellationTokenSource();
        _aheadDecode = cancellation;
        _ = PrefetchAsync(id, cancellation.Token);
    }

    private async Task PrefetchAsync(long id, CancellationToken token)
    {
        try
        {
            var image = await DecodeAsync(id, token);
            if (token.IsCancellationRequested || _closed || !IsLoaded ||
                Dispatcher.HasShutdownStarted) return;
            _aheadImageId = id;
            _aheadPixels = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Slideshow.Prefetch", ex); }
    }

    private void Present(BitmapSource image, int position)
    {
        if (_shownIndex == position)
        {
            var layer = _transitioning ? _incoming : _active;
            layer.Picture.Source = image;
            UpdateLayerGeometry(layer);
            _gpu?.Replace(image);
            return;
        }
        FinishTransition();
        _incoming.Picture.Source = image;
        UpdateLayerGeometry(_incoming);
        // Frame must not paint even once before the opacity clock is ready.
        _incoming.Frame.Opacity = 0;
        _incoming.Frame.Visibility = Visibility.Visible;
        Panel.SetZIndex(_incoming.Frame, 2);
        Panel.SetZIndex(_active.Frame, 1);
        _shownIndex = position;
        _awaitingPixels = false;
        _lastChangeUtc = DateTime.UtcNow;

        var type = _options.Transition;
        if (_active.Picture.Source is null || type == "none" ||
            _options.TransitionMilliseconds <= 0)
        {
            _gpu?.Present(image, "none", 0, () => { });
            SwapLayers();
            return;
        }

        _transitioning = true;
        _transitionStartedUtc = DateTime.UtcNow;
        _transitionRenderedFrames = 0;
        var serial = ++_transitionVersion;
        var duration = TimeSpan.FromMilliseconds(_options.TransitionMilliseconds);
        if (_gpu is { IsReady: true })
        {
            // ONE vsync-paced GPU clock moves both textures together.
            // WPF never renders or animates a frame in this mode.
            _gpu.Present(image, type, _options.TransitionMilliseconds, () =>
            {
                if (_transitioning && serial == _transitionVersion)
                    FinishTransition();
            });
            return;
        }
        if (type == "zoom")
        {
            // Zoom inward so the next border never peeks outside first.
            Animate(_incoming.Scale, ScaleTransform.ScaleXProperty, .97, 1, duration);
            Animate(_incoming.Scale, ScaleTransform.ScaleYProperty, .97, 1, duration);
        }
        else if (type == "slide")
        {
            // Both pictures are centered in the same stage. The travel
            // distance must include HALF of each frame plus a visible
            // right-hand gap. One screen width makes unequal aspect-ratio
            // pictures touch or overlap during the push.
            var distance = Direct3DSlideshowSurface.SlideTravel(
                _root.ActualWidth, _active.Frame.Width, _incoming.Frame.Width);
            _incoming.Frame.BeginAnimation(UIElement.OpacityProperty, null);
            _incoming.Frame.Opacity = 1;
            _incoming.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            _incoming.Shift.X = distance;
            var slide = new DoubleAnimation(distance, 0, duration)
            {
            };
            slide.Completed += (_, _) =>
            {
                if (_transitioning && serial == _transitionVersion)
                    FinishTransition();
            };
            _incoming.Shift.BeginAnimation(TranslateTransform.XProperty, slide);
            Animate(_active.Shift, TranslateTransform.XProperty, 0, -distance, duration);
            // Translate the two cached frames on the compositor, without
            // a second full-screen alpha animation over the textured stage.
            return;
        }

        // Cached compositor layers prevent repeated high-quality picture
        // scaling and CPU drop-shadow rasterization during each frame.
        var fade = new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (_transitioning && serial == _transitionVersion)
                FinishTransition();
        };
        _incoming.Frame.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void Animate(Animatable target, DependencyProperty property,
        double from, double to, TimeSpan duration)
    {
        // Make the initial frame deterministic even before composition has
        // advanced the newly attached animation clock. Never retain a
        // previous transition's base transform after rapid manual Next.
        target.BeginAnimation(property, null);
        target.SetValue(property, from);
        target.BeginAnimation(property, new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void FinishTransition()
    {
        if (!_transitioning) return;
        ++_transitionVersion;
        _transitioning = false;
        _gpu?.Complete();
        var elapsed = (DateTime.UtcNow - _transitionStartedUtc).TotalSeconds;
        if (_gpu is null && elapsed > 0.01)
            PCasaDiagnostics.WriteMessage("Slideshow.TransitionFrames",
                $"mode={_options.Transition}; frames={_transitionRenderedFrames}; " +
                $"seconds={elapsed:F2}; fps={_transitionRenderedFrames / elapsed:F1}");
        SwapLayers();
    }

    private void SwapLayers()
    {
        _active.StopAnimations();
        _incoming.StopAnimations();
        _active.Frame.Visibility = Visibility.Collapsed;
        (_active, _incoming) = (_incoming, _active);
        _active.Frame.Visibility = Visibility.Visible;
        _incoming.Frame.Visibility = Visibility.Collapsed;
        _incoming.Picture.Source = null;
        if (_afterTransitionPixels is { } refined)
        {
            _afterTransitionPixels = null;
            _active.Picture.Source = refined;
            UpdateLayerGeometry(_active);
            _gpu?.Replace(refined);
        }
    }
}
