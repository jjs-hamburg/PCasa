using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

public sealed class SlideshowSettingsWindow : Window
{
    private readonly SlideshowSettings _values;
    private readonly ComboBox _display;
    private readonly ComboBox _background;
    private readonly ComboBox _transition;
    private readonly TextBox _customFile;
    private readonly TextBox _interval;
    private readonly TextBox _duration;
    private readonly TextBox _gallerySize;
    private readonly TextBox _borderSize;
    private readonly TextBox _shadowSize;
    private readonly TextBox _shadowOffset;
    private readonly TextBox _shadowOpacity;
    private readonly CheckBox _preload;
    private readonly CheckBox _border;
    private readonly CheckBox _shadow;

    public SlideshowSettings Result => _values;

    public SlideshowSettingsWindow(SlideshowSettings current)
    {
        _values = current.Copy().Normalize();
        Title = UiLanguage.T("Slideshow-Einstellungen");
        Width = 550;
        Height = 680;
        MinWidth = 470;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("PcasaPanelBrush") as Brush
            ?? Brushes.DimGray;
        Foreground = Application.Current.TryFindResource("PcasaTextBrush") as Brush
            ?? Brushes.White;

        var root = new DockPanel { Margin = new Thickness(18) };
        Content = root;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        var cancel = new Button
        {
            Content = UiLanguage.T("Abbrechen"), Width = 105, Height = 32,
            Margin = new Thickness(0, 0, 8, 0), IsCancel = true
        };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button
        {
            Content = UiLanguage.T("Speichern"), Width = 110, Height = 32,
            IsDefault = true
        };
        save.Click += (_, _) => Save();
        actions.Children.Add(cancel);
        actions.Children.Add(save);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);
        var fields = new StackPanel { Orientation = Orientation.Vertical };
        scroll.Content = fields;
        _display = Choice(fields, "Darstellung", _values.DisplayMode,
            ("gallery", "Galerie"), ("fullscreen", "Vollbild"));
        _gallerySize = Number(fields, "Galeriegröße (%)", _values.GalleryPercent);
        _background = Choice(fields, "Hintergrund", _values.BackgroundMode,
            ("gallery", "Wie Galerie (Textur)"), ("black", "Schwarz"),
            ("custom", "Eigenes Bild"));
        var fileRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 8, 8)
        };
        fields.Children.Add(fileRow);
        _customFile = new TextBox
        {
            Width = 350,
            Text = _values.CustomBackgroundPath ?? "",
            Margin = new Thickness(0, 0, 8, 0)
        };
        var browse = new Button { Content = UiLanguage.T("Durchsuchen..."), Width = 105 };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog
            {
                Title = UiLanguage.T("Hintergrundbild wählen"),
                Filter = "Bilder (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp"
            };
            if (dialog.ShowDialog(this) == true)
            {
                _customFile.Text = dialog.FileName;
                Select(_background, "custom");
            }
        };
        fileRow.Children.Add(_customFile);
        fileRow.Children.Add(browse);
        _interval = Number(fields, "Bildwechsel (Sekunden)", _values.IntervalSeconds);
        _preload = Toggle(fields, "Nächstes Bild vorausladen", _values.PreloadNext);
        _transition = Choice(fields, "Übergang", _values.Transition,
            ("none", "Aus"), ("fade", "Überblenden"),
            ("zoom", "Sanfter Zoom"), ("slide", "Schieben"),
            ("projector", "Projektor"));
        _duration = Number(fields, "Übergangsdauer (ms)", _values.TransitionMilliseconds);
        _border = Toggle(fields, "Weißer Rahmen", _values.WhiteBorder);
        _borderSize = Number(fields, "Rahmenbreite (px)", _values.BorderWidth);
        _shadow = Toggle(fields, "Schatten", _values.Shadow);
        _shadowSize = Number(fields, "Schattenbreite (px)", _values.ShadowWidth);
        _shadowOffset = Number(fields, "Schattenversatz (px)", _values.ShadowOffset);
        _shadowOpacity = Number(fields, "Schattenstärke (%)", _values.ShadowOpacity);
    }

    private static TextBlock Caption(string label) => new()
    {
        Text = UiLanguage.T(label),
        VerticalAlignment = VerticalAlignment.Center,
        Width = 220, Margin = new Thickness(0, 0, 10, 0)
    };

    private static StackPanel Row(StackPanel fields, string label)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 5)
        };
        row.Children.Add(Caption(label));
        fields.Children.Add(row);
        return row;
    }

    private static ComboBox Choice(StackPanel fields, string label, string value,
        params (string Value, string Label)[] options)
    {
        var row = Row(fields, label);
        var choice = new ComboBox { MinWidth = 205, Height = 28 };
        foreach (var option in options)
            choice.Items.Add(new ComboBoxItem
            {
                Tag = option.Value, Content = UiLanguage.T(option.Label)
            });
        Select(choice, value);
        row.Children.Add(choice);
        return choice;
    }

    private static void Select(ComboBox choice, string value)
    {
        choice.SelectedItem = choice.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, value)) ?? choice.Items[0];
    }

    private static string Chosen(ComboBox choice)
        => (choice.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static TextBox Number(StackPanel fields, string label, int value)
    {
        var row = Row(fields, label);
        var text = new TextBox
        {
            Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width = 78, Height = 27,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        row.Children.Add(text);
        return text;
    }

    private static CheckBox Toggle(StackPanel fields, string label, bool value)
    {
        var row = Row(fields, label);
        var toggle = new CheckBox
        {
            IsChecked = value, VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(toggle);
        return toggle;
    }

    private static int ReadInt(TextBox box, int fallback) =>
        int.TryParse(box.Text, out var number) ? number : fallback;

    private void Save()
    {
        _values.DisplayMode = Chosen(_display);
        _values.GalleryPercent = ReadInt(_gallerySize, 85);
        _values.BackgroundMode = Chosen(_background);
        _values.CustomBackgroundPath = string.IsNullOrWhiteSpace(_customFile.Text)
            ? null : _customFile.Text.Trim();
        if (_values.BackgroundMode == "custom" &&
            (_values.CustomBackgroundPath is null ||
             !File.Exists(_values.CustomBackgroundPath)))
        {
            PcasaDialog.Show(this, UiLanguage.T("Hintergrundbild nicht gefunden."),
                UiLanguage.T("Slideshow"), MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        _values.IntervalSeconds = ReadInt(_interval, 5);
        _values.PreloadNext = _preload.IsChecked == true;
        _values.Transition = Chosen(_transition);
        _values.TransitionMilliseconds = ReadInt(_duration, 700);
        _values.WhiteBorder = _border.IsChecked == true;
        _values.BorderWidth = ReadInt(_borderSize, 10);
        _values.Shadow = _shadow.IsChecked == true;
        _values.ShadowWidth = ReadInt(_shadowSize, 26);
        _values.ShadowOffset = ReadInt(_shadowOffset, 30);
        _values.ShadowOpacity = ReadInt(_shadowOpacity, 60);
        _values.Normalize();
        try { _values.Save(); }
        catch (Exception ex)
        {
            PcasaDialog.Show(this, ex.Message,
                UiLanguage.T("Slideshow-Einstellungen"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
