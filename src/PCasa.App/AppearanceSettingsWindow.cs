using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    public AppearanceSettingsWindow(AppearanceDialogValues values)
    {
        Values = values;
        _texture = values.Texture;
        Title = "PCasa – " + UiLanguage.T("Darstellung");
        Width = 1000;
        // Shrink-wrap the options instead of leaving a large fixed-height
        // blank strip before the footer. On small screens the existing
        // ScrollViewer takes over.
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(430, SystemParameters.WorkArea.Height - 70);
        MinWidth = 830;
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
        // Independent vertical columns avoid the unused vertical bands caused
        // by aligning unlike-sized cards into three shared grid rows.
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        groups.Children.Add(left);
        groups.Children.Add(right);
        outerScroll.Content = groups;

        // The approved order: Design ABOVE gallery background.
        var theme = Section(left, "Design");
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(themeRow, "theme", "Light", "Hell", values.Theme);
        Radio(themeRow, "theme", "Dark", "Dunkel", values.Theme);
        Radio(themeRow, "theme", "System", "System", values.Theme);
        theme.Children.Add(themeRow);

        var background = Section(left, "Galeriehintergrund");
        foreach (var option in new[]
        {
            ("Auto", "Standard (zum Design)"),
            ("Dark", "Einfarbig dunkel"),
            ("Light", "Einfarbig hell"),
            ("Texture", "Textur")
        })
            Radio(background, "background", option.Item1, option.Item2, values.Background);

        // The gallery's existing texture directory is the one texture source.
        // All supported textures are displayed in the strip; no folder picker.
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
        var openTextureFolder = new Button
        {
            Content = UiLanguage.T("Texturen Ordner"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 8, 0, 0)
        };
        openTextureFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(GalleryTextureLibrary.DirectoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + GalleryTextureLibrary.DirectoryPath + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                PcasaDialog.Show(this, ex.Message, UiLanguage.T("Texturordner"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        background.Children.Add(openTextureFolder);
        PopulateTextures();

        // One filename card owns all three choices, including the font sizes.
        var names = Section(left, "Dateinamen");
        AddCheck(names, FileNamesCheck, "Dateinamen unter Thumbnails", values.FileNames);
        AddCheck(names, BoldFileNamesCheck, "Dateinamen fett", values.BoldFileNames);
        names.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Schriftgröße"), FontSize = 11,
            Margin = new Thickness(0, 5, 0, 2)
        });
        var captionRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(captionRow, "caption", "10", "Standard (10)", values.CaptionSize.ToString());
        Radio(captionRow, "caption", "12", "Größer (12)", values.CaptionSize.ToString());
        Radio(captionRow, "caption", "14", "Sehr groß (14)", values.CaptionSize.ToString());
        names.Children.Add(captionRow);

        var options = Section(right, "Galerieoptionen");
        AddCheck(options, RestorePositionCheck, "Galerieposition beim Start wiederherstellen", values.RestorePosition);
        AddCheck(options, HideCollapsedCheck, "Eingeklappte Ordner in Galerie ausblenden", values.HideCollapsed);
        AddCheck(options, ShadowsCheck, "Thumbnail-Schatten", values.Shadows);
        AddCheck(options, IconsCheck, "GPS-, Tag- und RAW-Symbole", values.Icons);
        AddCheck(options, TooltipsCheck, "Tooltips", values.Tooltips);

        var sequence = Section(right, "Bildfolge (Betrachter / Slideshow)");
        var seqRow = new StackPanel { Orientation = Orientation.Horizontal };
        Radio(seqRow, "sequence", "CurrentFolder", "Aktueller Ordner", values.SequenceScope);
        Radio(seqRow, "sequence", "Gallery", "Galerieansicht", values.SequenceScope);
        sequence.Children.Add(seqRow);

        var folder = Section(right, "Ordnerstatus");
        var statusRow = new WrapPanel { Orientation = Orientation.Horizontal };
        Radio(statusRow, "status", "Always", "Immer anzeigen", values.FolderStatus);
        Radio(statusRow, "status", "Problems", "Nur Aktivität und Probleme", values.FolderStatus);
        Radio(statusRow, "status", "None", "Nie anzeigen", values.FolderStatus);
        folder.Children.Add(statusRow);
        // Permanent compact legend. The same status semantics as the
        // folder tree; distinguish statuses by GLYPH, not by color alone.
        // Two columns keep it legible without growing the dialog width.
        var statusLegend = new StackPanel
        {
            Margin = new Thickness(1, 5, 0, 0)
        };
        statusLegend.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Legende"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        var legendGrid = new UniformGrid { Columns = 2 };
        foreach (var (symbol, description, pending) in new[]
        {
            ("✓", "Abgeschlossen", false),
            ("↻", "Wird aktualisiert", false),
            ("◷", "Ausstehend", false),
            ("⊘", "Nicht erreichbar", false),
            ("!", "Fehler", false),
            ("?", "Unbekannt", false),
            ("●", "Offene Schreibvorgänge", true)
        })
        {
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 19,
                VerticalAlignment = VerticalAlignment.Center
            };
            var glyph = new TextBlock
            {
                Text = symbol, Width = 19, FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (pending)
                glyph.SetResourceReference(TextBlock.ForegroundProperty,
                    "PcasaDeleteIconBrush");
            item.Children.Add(glyph);
            item.Children.Add(new TextBlock
            {
                Text = UiLanguage.T(description),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            legendGrid.Children.Add(item);
        }
        statusLegend.Children.Add(legendGrid);
        folder.Children.Add(statusLegend);

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

    private static StackPanel Section(Panel parent, string label)
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
        parent.Children.Add(frame);
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
