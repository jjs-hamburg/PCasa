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
            var dialog = CreateBackgroundPicker();
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
        _values.ShadowOffset = ReadInt(_shadowOffset, 12);
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
