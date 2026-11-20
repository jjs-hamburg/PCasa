using System.IO;
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
            RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.HighQuality);
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
                ClipToBounds = false
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
    private readonly TextBlock _caption = new();
    private Layer _active = new();
    private Layer _incoming = new();
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _currentDecode;
    private CancellationTokenSource? _aheadDecode;
    private BitmapSource? _aheadPixels;
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

        var status = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _caption
        };
        _caption.FontSize = 13;
        _caption.Foreground = Brushes.White;
        _caption.TextTrimming = TextTrimming.CharacterEllipsis;
        _root.Children.Add(status);
        Panel.SetZIndex(status, 5);

        var hint = new TextBlock
        {
            Text = UiLanguage.T("Esc: Beenden   ←/→: Bildwechsel   Leertaste: Pause"),
            FontSize = 12, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 0, 12)
        };
        _root.Children.Add(hint);
        Panel.SetZIndex(hint, 5);

        _clock = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _clock.Tick += ClockTick;
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
            ShowCurrent();
            _clock.Start();
            Focus();
            Keyboard.Focus(this);
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
            _currentDecode?.Cancel();
            _aheadDecode?.Cancel();
            _currentDecode?.Dispose();
            _aheadDecode?.Dispose();
            _active.StopAnimations();
            _incoming.StopAnimations();
        };
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
                BlurRadius = _options.ShadowWidth,
                ShadowDepth = _options.ShadowOffset,
                // WPF's 315° casts the shadow down and right: the
                // virtual light source is in the top-left, as in thumbnails.
                Direction = 315,
                Opacity = _options.ShadowOpacity / 100.0
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
            ShowCaption();
            e.Handled = true;
        }
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
        ShowCaption();

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

        if (preview is not null)
        {
            Present(preview, _index);
            _awaitingPixels = false;
            _failedInRow = 0;
            if (hadPrefetchedOriginal)
            {
                // Already decoded at screen quality: do not read the NAS
                // original twice while a manual Right key is waiting.
                StartLookAhead();
                return;
            }
        }
        _ = DecodeCurrentAsync(entry.ImageId, _index, revision, token);
    }

    private async Task DecodeCurrentAsync(long id, int position,
        int revision, CancellationToken token)
    {
        BitmapSource? bitmap = null;
        try { bitmap = await DecodeAsync(id, token); }
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
                var layer = _transitioning ? _incoming : _active;
                layer.Picture.Source = bitmap;
                UpdateLayerGeometry(layer);
            }
            else
                Present(bitmap, position);
            _awaitingPixels = false;
            StartLookAhead();
        }
        else if (_shownIndex == position)
        {
            // An offline original must not erase its local packed thumbnail.
            _awaitingPixels = false;
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
        ShowCaption();
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

    private void ShowCaption()
    {
        var fileName = _sequence.At(_index).FileName;
        var state = _paused ? "Ⅱ" : "▶";
        _caption.Text = $"{state}  {_index + 1:N0} / {_sequence.Count:N0} · {fileName}";
    }

    private void Present(BitmapSource image, int position)
    {
        if (_shownIndex == position)
        {
            var layer = _transitioning ? _incoming : _active;
            layer.Picture.Source = image;
            UpdateLayerGeometry(layer);
            return;
        }
        FinishTransition();
        _incoming.Picture.Source = image;
        UpdateLayerGeometry(_incoming);
        _incoming.Frame.Visibility = Visibility.Visible;
        Panel.SetZIndex(_incoming.Frame, 2);
        Panel.SetZIndex(_active.Frame, 1);
        _shownIndex = position;
        _awaitingPixels = false;
        _lastChangeUtc = DateTime.UtcNow;
        ShowCaption();

        var type = _options.Transition;
        if (_active.Picture.Source is null || type == "none" ||
            _options.TransitionMilliseconds <= 0)
        {
            SwapLayers();
            return;
        }

        _transitioning = true;
        var serial = ++_transitionVersion;
        var duration = TimeSpan.FromMilliseconds(_options.TransitionMilliseconds);
        var width = _stage.ActualWidth > 0 ? _stage.ActualWidth : 1000;
        if (type == "zoom")
        {
            Animate(_incoming.Scale, ScaleTransform.ScaleXProperty, 1.07, 1, duration);
            Animate(_incoming.Scale, ScaleTransform.ScaleYProperty, 1.07, 1, duration);
        }
        else if (type == "slide")
        {
            Animate(_incoming.Shift, TranslateTransform.XProperty, width, 0, duration);
            Animate(_active.Shift, TranslateTransform.XProperty, 0, -width, duration);
        }
        else if (type == "projector")
        {
            // A collapsing old frame and expanding new frame with horizontal
            // push, rather than a second static dissolve.
            Animate(_incoming.Scale, ScaleTransform.ScaleYProperty, .12, 1, duration);
            Animate(_incoming.Shift, TranslateTransform.XProperty, width * .18, 0, duration);
            Animate(_active.Scale, ScaleTransform.ScaleYProperty, 1, .12, duration);
            Animate(_active.Shift, TranslateTransform.XProperty, 0, -width * .18, duration);
        }

        // WPF starts the animation clock on its next render pass. With
        // a still-uninitialized (1.0) frame, the very first visible paint
        // otherwise shows the final image without the projector transform.
        // Set the initial alpha explicitly; the clock then interpolates.
        _incoming.Frame.Opacity = 0;
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
    }
}
