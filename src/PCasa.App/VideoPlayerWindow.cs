using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PCasa.App;

/// <summary>Read-only video player. Never edits or creates video files.</summary>
public sealed class VideoPlayerWindow : Window
{
    private readonly MediaElement _player;
    private readonly Slider _position;
    private readonly TextBlock _elapsed;
    private readonly Button _playPause;
    private readonly DispatcherTimer _clock;
    private bool _seeking;
    private bool _playing;
    private int _metadataRotation;
    private bool _videoOpened;

    public VideoPlayerWindow(string path)
    {
        Title = Path.GetFileName(path) + " – PCasa";
        Width = 1000;
        Height = 680;
        MinWidth = 420;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;

        var root = new DockPanel();
        Content = root;
        var controls = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(12, 6, 12, 10),
            Background = Brushes.Black
        };
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.Add(controls);

        _position = new Slider { Minimum = 0, Maximum = 1, IsEnabled = false };
        controls.Children.Add(_position);

        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        controls.Children.Add(buttons);
        _playPause = new Button { Content = Services.UiLanguage.T("Pause"), Width = 80, Height = 30 };
        _playPause.Click += (_, _) => TogglePlayback();
        DockPanel.SetDock(_playPause, Dock.Left);
        buttons.Children.Add(_playPause);

        _elapsed = new TextBlock
        {
            Text = "00:00 / --:--", Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0)
        };
        DockPanel.SetDock(_elapsed, Dock.Left);
        buttons.Children.Add(_elapsed);

        var fullscreen = new Button { Content = Services.UiLanguage.T("Vollbild"), Width = 90, Height = 30 };
        fullscreen.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        DockPanel.SetDock(fullscreen, Dock.Right);
        buttons.Children.Add(fullscreen);

        var volume = new Slider
        {
            Minimum = 0, Maximum = 1, Value = 0.75, Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Services.UiLanguage.T("Lautstärke")
        };
        DockPanel.SetDock(volume, Dock.Right);
        buttons.Children.Add(volume);

        _player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform,
            Volume = 0.75,
            Source = new Uri(Path.GetFullPath(path), UriKind.Absolute),
            ScrubbingEnabled = true
        };
        root.Children.Add(_player);
        volume.ValueChanged += (_, e) => _player.Volume = e.NewValue;

        _player.MediaOpened += (_, _) =>
        {
            _videoOpened = true;
            ApplyVideoRotation();
            if (_player.NaturalDuration.HasTimeSpan)
            {
                _position.Maximum = Math.Max(0.01, _player.NaturalDuration.TimeSpan.TotalSeconds);
                _position.IsEnabled = true;
            }
            UpdateClock();
        };
        _player.MediaFailed += (_, e) =>
        {
            _playing = false;
            _playPause.Content = Services.UiLanguage.T("Abspielen");
            MessageBox.Show(this,
                Services.UiLanguage.T("Dieses Video kann mit den installierten Windows-Codecs nicht abgespielt werden.") + "\n" +
                e.ErrorException?.Message, "PCasa – " + Services.UiLanguage.T("Videowiedergabe"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        _player.MediaEnded += (_, _) =>
        {
            _playing = false;
            _playPause.Content = "Abspielen";
        };

        _position.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => _seeking = true));
        _position.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) =>
            {
                _player.Position = TimeSpan.FromSeconds(_position.Value);
                _seeking = false;
            }));
        _position.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_seeking && _position.IsEnabled)
                _player.Position = TimeSpan.FromSeconds(_position.Value);
        };
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => UpdateClock();

        Loaded += (_, _) =>
        {
            _ = ReadAndApplyVideoRotationAsync(path);
            _player.Play();
            _playing = true;
            _clock.Start();
        };
        Closed += (_, _) =>
        {
            _clock.Stop();
            _player.Stop();
            _player.Close();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space) { TogglePlayback(); e.Handled = true; }
            if (e.Key == Key.Escape && WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                e.Handled = true;
            }
        };
    }

    private async Task ReadAndApplyVideoRotationAsync(string path)
    {
        try
        {
            var degrees = await Task.Run(() => ReadVideoRotation(path));
            if (!IsLoaded) return;
            _metadataRotation = degrees;
            ApplyVideoRotation();
        }
        catch (Exception ex)
        {
            Services.PCasaDiagnostics.Write("Video.Rotation", ex);
        }
    }

    private void ApplyVideoRotation()
    {
        if (!_videoOpened) return;
        // Windows MediaElement may ignore QuickTime rotation metadata;
        // do not double-rotate when the codec already exposes portrait axes.
        var turn = _metadataRotation is 90 or 270 &&
            _player.NaturalVideoWidth > _player.NaturalVideoHeight;
        _player.LayoutTransform = new RotateTransform(
            turn || _metadataRotation == 180 ? _metadataRotation : 0);
    }

    private static int ReadVideoRotation(string path)
    {
        var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(executable)) return 0;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-s3");
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add("-Rotation");
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start()) return 0;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Video rotation metadata timed out.");
        }
        if (process.ExitCode != 0)
            throw new IOException(errors.GetAwaiter().GetResult().Trim());
        if (!double.TryParse(output.GetAwaiter().GetResult().Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;
        var degrees = ((int)Math.Round(value) % 360 + 360) % 360;
        return degrees is 90 or 180 or 270 ? degrees : 0;
    }

    private void TogglePlayback()
    {
        if (_playing) _player.Pause();
        else _player.Play();
        _playing = !_playing;
        _playPause.Content = Services.UiLanguage.T(_playing ? "Pause" : "Abspielen");
    }

    private void UpdateClock()
    {
        if (!_seeking && _position.IsEnabled)
            _position.Value = Math.Clamp(_player.Position.TotalSeconds, 0, _position.Maximum);
        static string TimeText(TimeSpan t) => t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        var total = _player.NaturalDuration.HasTimeSpan
            ? TimeText(_player.NaturalDuration.TimeSpan) : "--:--";
        _elapsed.Text = TimeText(_player.Position) + " / " + total;
    }
}
