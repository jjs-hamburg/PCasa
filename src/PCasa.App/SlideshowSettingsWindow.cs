using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private readonly Slider _interval;
    private readonly Slider _duration;
    private readonly Slider _gallerySize;
    private readonly Slider _borderSize;
    private readonly Slider _shadowSize;
    private readonly Slider _shadowOffset;
    private readonly Slider _shadowOpacity;
    private const double NumericSliderWidth = 258;
    private readonly CheckBox _preload;
    private readonly CheckBox _border;
    private readonly CheckBox _shadow;

    public SlideshowSettings Result => _values;

    public SlideshowSettingsWindow(SlideshowSettings current)
    {
        _values = current.Copy().Normalize();
        Title = UiLanguage.T("Slideshow-Einstellungen");
        Width = 680;
        Height = 720;
        MinWidth = 630;
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
        _gallerySize = Number(fields, "Galeriegröße (%)", _values.GalleryPercent,
            50, 100, 1, "%");
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
            var dialog = CreateBackgroundPicker();
            if (dialog.ShowDialog(this) == true)
            {
                _customFile.Text = dialog.FileName;
                Select(_background, "custom");
            }
        };
        fileRow.Children.Add(_customFile);
        fileRow.Children.Add(browse);
        _interval = Number(fields, "Bildwechsel (Sekunden)", _values.IntervalSeconds,
            1, 300, 1, "s");
        _preload = Toggle(fields, "Nächstes Bild vorausladen", _values.PreloadNext);
        _transition = Choice(fields, "Übergang", _values.Transition,
            ("none", "Aus"), ("fade", "Überblenden"),
            ("zoom", "Sanfter Zoom"), ("slide", "Schieben"),
            ("projector", "Projektor"), ("cube", "3D-Würfelrotation"),
            ("cards", "Kartenwechsel"), ("zoomfade", "Zoom-Überblendung"),
            ("wipe-left", "Wischen nach links"),
            ("wipe-right", "Wischen nach rechts"),
            ("wipe-up", "Wischen nach oben"),
            ("wipe-down", "Wischen nach unten"));
        _duration = Number(fields, "Übergangsdauer (ms)", _values.TransitionMilliseconds,
            0, 3000, 50, "ms");
        _border = Toggle(fields, "Weißer Rahmen", _values.WhiteBorder);
        _borderSize = Number(fields, "Rahmenbreite (px)", _values.BorderWidth,
            0, 100, 1, "px");
        _shadow = Toggle(fields, "Schatten", _values.Shadow);
        _shadowSize = Number(fields, "Schattenbreite (px)", _values.ShadowWidth,
            0, 150, 1, "px");
        _shadowOffset = Number(fields, "Schattenversatz (px)", _values.ShadowOffset,
            0, 100, 1, "px");
        _shadowOpacity = Number(fields, "Schattenstärke (%)", _values.ShadowOpacity,
            0, 100, 1, "%");
    }

    internal static OpenFileDialog CreateBackgroundPicker() => new()
    {
        Title = UiLanguage.T("Hintergrundbild wählen"),
        Filter = "Bilder (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
        InitialDirectory = GalleryTextureLibrary.DirectoryPath
    };

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

    // WPF's installed dark theme can paint the collapsed ComboBox
    // selection and popup text in the SAME color as the background.
    // Give this dialog an explicit, accessible selection/display template
    // rather than modifying every ComboBox in PCasa.
    private static ControlTemplate ChoiceTemplate(Brush foreground,
        Brush background, Brush outline)
    {
        var template = new ControlTemplate(typeof(ComboBox));
        var root = new FrameworkElementFactory(typeof(Grid));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, background);
        frame.SetValue(Border.BorderBrushProperty, outline);
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        root.AppendChild(frame);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.Name = "SelectedCaption";
        text.SetValue(TextBlock.ForegroundProperty, foreground);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.MarginProperty, new Thickness(10, 0, 30, 0));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(UIElement.IsHitTestVisibleProperty, false);
        text.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Content")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        root.AppendChild(text);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "▾");
        arrow.SetValue(TextBlock.ForegroundProperty, foreground);
        arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        arrow.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
        arrow.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(arrow);

        // A transparent, real toggle control retains mouse, keyboard
        // and ComboBox.IsDropDownOpen semantics.
        var toggle = new FrameworkElementFactory(typeof(ToggleButton));
        toggle.SetValue(Control.FocusableProperty, false);
        toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        var toggleTemplate = new ControlTemplate(typeof(ToggleButton));
        var hitTarget = new FrameworkElementFactory(typeof(Border));
        hitTarget.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        toggleTemplate.VisualTree = hitTarget;
        toggle.SetValue(Control.TemplateProperty, toggleTemplate);
        toggle.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
        root.AppendChild(toggle);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.StaysOpenProperty, false);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        popup.SetBinding(Popup.PlacementTargetProperty, new Binding
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        var listFrame = new FrameworkElementFactory(typeof(Border));
        listFrame.SetValue(Border.BackgroundProperty, background);
        listFrame.SetValue(Border.BorderBrushProperty, outline);
        listFrame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        listFrame.SetValue(Border.PaddingProperty, new Thickness(2));
        listFrame.SetBinding(FrameworkElement.MinWidthProperty,
            new Binding("ActualWidth")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled);
        scroll.SetValue(FrameworkElement.MaxHeightProperty, 250d);
        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        scroll.AppendChild(items);
        listFrame.AppendChild(scroll);
        popup.AppendChild(listFrame);
        root.AppendChild(popup);
        template.VisualTree = root;
        return template;
    }

    private static ComboBox Choice(StackPanel fields, string label, string value,
        params (string Value, string Label)[] options)
    {
        var row = Row(fields, label);
        var foreground = Application.Current.TryFindResource("PcasaTextBrush") as Brush
            ?? Brushes.White;
        var background = Application.Current.TryFindResource("PcasaPanelAltBrush") as Brush
            ?? Brushes.Black;
        var outline = Application.Current.TryFindResource("PcasaDividerBrush") as Brush
            ?? Brushes.Gray;
        var choice = new ComboBox
        {
            MinWidth = 205, Height = 28,
            Foreground = foreground, Background = background, BorderBrush = outline,
            Template = ChoiceTemplate(foreground, background, outline)
        };
        var itemTemplate = new ControlTemplate(typeof(ComboBoxItem));
        var itemFrame = new FrameworkElementFactory(typeof(Border));
        itemFrame.Name = "ChoiceItemFrame";
        itemFrame.SetValue(Border.PaddingProperty, new Thickness(9, 6, 9, 6));
        itemFrame.SetValue(Border.BackgroundProperty, background);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        itemFrame.AppendChild(content);
        itemTemplate.VisualTree = itemFrame;
        foreach (var (triggerProperty, brush) in new[]
        {
            (ComboBoxItem.IsMouseOverProperty,
                Application.Current.TryFindResource("PcasaHoverBrush") as Brush
                    ?? Brushes.Gray),
            (ComboBoxItem.IsSelectedProperty,
                Application.Current.TryFindResource("PcasaSelectionBrush") as Brush
                    ?? Brushes.DarkGray)
        })
        {
            var trigger = new Trigger { Property = triggerProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, brush,
                "ChoiceItemFrame"));
            itemTemplate.Triggers.Add(trigger);
        }
        foreach (var option in options)
            choice.Items.Add(new ComboBoxItem
            {
                Tag = option.Value,
                Content = UiLanguage.T(option.Label),
                Foreground = foreground,
                Background = background,
                Template = itemTemplate
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

    // Exactly the same slider width in every numeric row. The number
    // alongside the track is a live readout, not a second editable value.
    // Keyboard arrows retain single-step precision for long intervals.
    private static Slider Number(StackPanel fields, string label, int value,
        int minimum, int maximum, int step, string unit)
    {
        var row = Row(fields, label);
        var slider = new Slider
        {
            Width = NumericSliderWidth, Height = 29,
            Minimum = minimum, Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            TickFrequency = step, SmallChange = step,
            LargeChange = step * 10,
            IsSnapToTickEnabled = true,
            AutoToolTipPlacement = AutoToolTipPlacement.TopLeft,
            AutoToolTipPrecision = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        var valueLabel = new TextBlock
        {
            Width = 75, Margin = new Thickness(12, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        valueLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value))
        {
            Source = slider, StringFormat = "{0:0} " + unit
        });
        row.Children.Add(slider);
        row.Children.Add(valueLabel);
        return slider;
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

    private static int ReadInt(Slider slider) =>
        (int)Math.Round(slider.Value, MidpointRounding.AwayFromZero);

    private void Save()
    {
        _values.DisplayMode = Chosen(_display);
        _values.GalleryPercent = ReadInt(_gallerySize);
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
        _values.IntervalSeconds = ReadInt(_interval);
        _values.PreloadNext = _preload.IsChecked == true;
        _values.Transition = Chosen(_transition);
        _values.TransitionMilliseconds = ReadInt(_duration);
        _values.WhiteBorder = _border.IsChecked == true;
        _values.BorderWidth = ReadInt(_borderSize);
        _values.Shadow = _shadow.IsChecked == true;
        _values.ShadowWidth = ReadInt(_shadowSize);
        _values.ShadowOffset = ReadInt(_shadowOffset);
        _values.ShadowOpacity = ReadInt(_shadowOpacity);
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
