using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

// The modal dialog edits a detached snapshot only. The existing appearance
// and viewer settings remain the single source of truth after acceptance.
public sealed record AppearanceDialogValues(
    string Theme, string Background, string? Texture,
    string FolderStatus, bool RestorePosition, bool HideCollapsed,
    bool FileNames, bool BoldFileNames, bool Icons, bool Shadows,
    bool Tooltips, string SequenceScope, int CaptionSize);

public sealed class AppearanceSettingsWindow : Window
{
    private readonly Dictionary<string, RadioButton> _choices = new();
    private readonly Dictionary<string, Border> _textureFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _textureItems = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _textureStrip = new();
    private readonly Action<Window> _showLegend;
    private string? _texture;

    public AppearanceDialogValues Values { get; private set; }
    public CheckBox RestorePositionCheck { get; } = new();
    public CheckBox HideCollapsedCheck { get; } = new();
    public CheckBox FileNamesCheck { get; } = new();
    public CheckBox BoldFileNamesCheck { get; } = new();
    public CheckBox IconsCheck { get; } = new();
    public CheckBox ShadowsCheck { get; } = new();
    public CheckBox TooltipsCheck { get; } = new();
    public Button ConfirmButton { get; } = new();
    public Button CancelButton { get; } = new();

    public AppearanceSettingsWindow(AppearanceDialogValues values, Action<Window> showLegend)
    {
        Values = values;
        _texture = values.Texture;
        _showLegend = showLegend;
        Title = "PCasa – " + UiLanguage.T("Darstellung");
        Width = 1000;
        Height = 656;
        MinWidth = 830;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var whole = new Grid { Margin = new Thickness(14, 10, 14, 11) };
        whole.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        whole.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        whole.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = whole;
        whole.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Darstellung"),
            FontSize = 19, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 0, 10)
        });

        var outerScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(outerScroll, 1);
        whole.Children.Add(outerScroll);
        var groups = new Grid();
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.23, GridUnitType.Star) });
        groups.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        groups.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outerScroll.Content = groups;

        var background = Section(groups, 0, 0, "Galeriehintergrund");
        foreach (var option in new[]
        {
            ("Auto", "Standard (zum Design)"),
            ("Dark", "Einfarbig dunkel"),
            ("Light", "Einfarbig hell"),
            ("Texture", "Textur")
        })
            Radio(background, "background", option.Item1, option.Item2, values.Background);

        // The gallery's existing JPG directory is the one texture source.
        // All selectable JPGs are displayed in the strip; no folder picker.
        var stripRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        stripRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stripRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stripRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = new Button
        {
            Content = "‹", FontSize = 23, Width = 29, Height = 75,
            ToolTip = UiLanguage.T("Vorherige Texturen")
        };
        previous.Click += (_, _) => ScrollTextures(-250);
        stripRow.Children.Add(previous);
        _textureStrip.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _textureStrip.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _textureStrip.PanningMode = PanningMode.HorizontalOnly;
        _textureStrip.Margin = new Thickness(4, 0, 4, 0);
        _textureStrip.PreviewMouseWheel += (_, e) =>
        {
            ScrollTextures(-e.Delta);
            e.Handled = true;
        };
        _textureStrip.Content = _textureItems;
        Grid.SetColumn(_textureStrip, 1);
        stripRow.Children.Add(_textureStrip);
        var next = new Button
        {
            Content = "›", FontSize = 23, Width = 29, Height = 75,
            ToolTip = UiLanguage.T("Weitere Texturen")
        };
        next.Click += (_, _) => ScrollTextures(250);
        Grid.SetColumn(next, 2);
        stripRow.Children.Add(next);
        background.Children.Add(stripRow);
        PopulateTextures();

        var options = Section(groups, 0, 1, "Galerieoptionen");
        AddCheck(options, RestorePositionCheck, "Galerieposition beim Start wiederherstellen", values.RestorePosition);
        AddCheck(options, HideCollapsedCheck, "Eingeklappte Ordner in Galerie ausblenden", values.HideCollapsed);
        AddCheck(options, FileNamesCheck, "Dateinamen unter Thumbnails", values.FileNames);
        AddCheck(options, BoldFileNamesCheck, "Dateinamen fett", values.BoldFileNames);
        AddCheck(options, ShadowsCheck, "Thumbnail-Schatten", values.Shadows);
        AddCheck(options, IconsCheck, "GPS-, Tag- und RAW-Symbole", values.Icons);
        AddCheck(options, TooltipsCheck, "Tooltips", values.Tooltips);

        var caption = Section(groups, 1, 0, "Schriftgröße der Bildunterschriften");
        var captionRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(captionRow, "caption", "10", "Bisher (10)", values.CaptionSize.ToString());
        Radio(captionRow, "caption", "12", "Größer (12)", values.CaptionSize.ToString());
        Radio(captionRow, "caption", "14", "Sehr groß (14)", values.CaptionSize.ToString());
        caption.Children.Add(captionRow);

        var sequence = Section(groups, 1, 1, "Bildfolge (Betrachter / Slideshow)");
        var seqRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(seqRow, "sequence", "CurrentFolder", "Aktueller Ordner", values.SequenceScope);
        Radio(seqRow, "sequence", "Gallery", "Galerieansicht", values.SequenceScope);
        sequence.Children.Add(seqRow);

        // Retain the existing theme and folder-status functionality rather
        // than silently removing appearance settings from PCasa.
        var theme = Section(groups, 2, 0, "Design");
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(themeRow, "theme", "Light", "Hell", values.Theme);
        Radio(themeRow, "theme", "Dark", "Dunkel", values.Theme);
        Radio(themeRow, "theme", "System", "System", values.Theme);
        theme.Children.Add(themeRow);

        var folder = Section(groups, 2, 1, "Ordnerstatus");
        var statusRow = new WrapPanel { Orientation = Orientation.Horizontal };
        Radio(statusRow, "status", "Always", "Immer anzeigen", values.FolderStatus);
        Radio(statusRow, "status", "Problems", "Nur Aktivität und Probleme", values.FolderStatus);
        Radio(statusRow, "status", "None", "Nie anzeigen", values.FolderStatus);
        folder.Children.Add(statusRow);
        var legend = new Button
        {
            Content = UiLanguage.T("Ordnerstatus-Legende"), MinHeight = 25,
            Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        legend.Click += (_, _) => _showLegend(this);
        folder.Children.Add(legend);

        var footer = new DockPanel { Margin = new Thickness(1, 9, 1, 0), LastChildFill = false };
        Grid.SetRow(footer, 2);
        whole.Children.Add(footer);
        var reset = new Button
        {
            Content = UiLanguage.T("Zurücksetzen"), MinWidth = 95, Height = 30,
            Padding = new Thickness(9, 3, 9, 3)
        };
        reset.Click += (_, _) => ResetChoices();
        DockPanel.SetDock(reset, Dock.Left);
        footer.Children.Add(reset);
        CancelButton.Content = UiLanguage.T("Abbrechen");
        CancelButton.IsCancel = true;
        CancelButton.MinWidth = 93;
        CancelButton.Height = 30;
        CancelButton.Padding = new Thickness(9, 3, 9, 3);
        CancelButton.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(CancelButton, Dock.Right);
        footer.Children.Add(CancelButton);
        ConfirmButton.Content = UiLanguage.T("OK");
        ConfirmButton.IsDefault = true;
        ConfirmButton.MinWidth = 93;
        ConfirmButton.Height = 30;
        ConfirmButton.Padding = new Thickness(9, 3, 9, 3);
        ConfirmButton.Margin = new Thickness(0, 0, 8, 0);
        ConfirmButton.Click += (_, _) => Accept();
        DockPanel.SetDock(ConfirmButton, Dock.Right);
        footer.Children.Add(ConfirmButton);
    }

    private static StackPanel Section(Grid grid, int row, int column, string label)
    {
        var outer = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };
        outer.Children.Add(new TextBlock
        {
            Text = UiLanguage.T(label), FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var inner = new StackPanel();
        outer.Children.Add(inner);
        var frame = new Border
        {
            Child = outer, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3, 2, 3, 3)
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        frame.SetResourceReference(Border.BackgroundProperty, "PcasaPanelAltBrush");
        Grid.SetRow(frame, row);
        Grid.SetColumn(frame, column);
        grid.Children.Add(frame);
        return inner;
    }

    private void Radio(Panel panel, string group, string value, string label, string selected)
    {
        var radio = new RadioButton
        {
            Content = UiLanguage.T(label),
            GroupName = "Appearance_" + group,
            IsChecked = value == selected,
            Tag = value,
            FontSize = 12, MinHeight = 25,
            Margin = group is "caption" or "sequence" or "theme" or "status"
                ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _choices[group + ":" + value] = radio;
        panel.Children.Add(radio);
    }

    private static void AddCheck(Panel panel, CheckBox check, string label, bool selected)
    {
        check.Content = UiLanguage.T(label);
        check.IsChecked = selected;
        check.FontSize = 12; check.MinHeight = 25;
        check.VerticalContentAlignment = VerticalAlignment.Center;
        panel.Children.Add(check);
    }

    private void PopulateTextures()
    {
        _textureItems.Children.Clear();
        _textureFrames.Clear();
        foreach (var path in GalleryTextureLibrary.GetFiles())
        {
            var name = Path.GetFileName(path);
            try
            {
                using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 112;
                bitmap.StreamSource = input;
                bitmap.EndInit();
                bitmap.Freeze();
                var thumb = new Image
                {
                    Source = bitmap, Width = 88, Height = 65,
                    Stretch = Stretch.UniformToFill
                };
                var border = new Border
                {
                    Child = thumb, Width = 92, Height = 69,
                    Padding = new Thickness(1),
                    Margin = new Thickness(2),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(3)
                };
                _textureFrames[name] = border;
                var button = new Button
                {
                    Content = border, Tag = name, Padding = new Thickness(0),
                    BorderThickness = new Thickness(0), Margin = new Thickness(1),
                    ToolTip = name, Width = 99, Height = 74
                };
                button.Click += (_, _) =>
                {
                    _texture = name;
                    _choices["background:Texture"].IsChecked = true;
                    UpdateTextureSelection();
                };
                _textureItems.Children.Add(button);
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Appearance.TexturePreview", ex);
            }
        }
        UpdateTextureSelection();
    }

    private void ScrollTextures(double amount)
    {
        _textureStrip.ScrollToHorizontalOffset(
            Math.Clamp(_textureStrip.HorizontalOffset + amount,
                0, _textureStrip.ScrollableWidth));
    }

    private void UpdateTextureSelection()
    {
        foreach (var (name, frame) in _textureFrames)
            frame.BorderBrush = string.Equals(name, _texture,
                StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(42, 157, 241))
                : new SolidColorBrush(Color.FromRgb(82, 84, 86));
    }

    private string Choice(string group, params string[] values)
        => values.First(value => _choices[group + ":" + value].IsChecked == true);

    private void ResetChoices()
    {
        foreach (var (group, option) in new[]
        {
            ("background", "Auto"), ("theme", "Dark"),
            ("caption", "10"), ("status", "Always"),
            ("sequence", "Gallery")
        })
            _choices[group + ":" + option].IsChecked = true;
        RestorePositionCheck.IsChecked = false;
        HideCollapsedCheck.IsChecked = false;
        FileNamesCheck.IsChecked = true;
        BoldFileNamesCheck.IsChecked = false;
        IconsCheck.IsChecked = true;
        ShadowsCheck.IsChecked = false;
        TooltipsCheck.IsChecked = true;
    }

    private void Accept()
    {
        Values = new AppearanceDialogValues(
            Choice("theme", "Light", "Dark", "System"),
            Choice("background", "Auto", "Dark", "Light", "Texture"),
            _texture,
            Choice("status", "Always", "Problems", "None"),
            RestorePositionCheck.IsChecked == true,
            HideCollapsedCheck.IsChecked == true,
            FileNamesCheck.IsChecked == true,
            BoldFileNamesCheck.IsChecked == true,
            IconsCheck.IsChecked == true,
            ShadowsCheck.IsChecked == true,
            TooltipsCheck.IsChecked == true,
            Choice("sequence", "CurrentFolder", "Gallery"),
            int.Parse(Choice("caption", "10", "12", "14")));
        DialogResult = true;
    }
}
