using System.IO;
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
