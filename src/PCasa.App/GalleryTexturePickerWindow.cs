using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

// One graphical selection surface, independent of the gallery's job model.
// Clicking a preview returns its filename and closes the dialog immediately.
public sealed class GalleryTexturePickerWindow : Window
{
    private readonly WrapPanel _tiles = new();
    private readonly TextBlock _emptyMessage = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(8),
        FontSize = 13
    };
    private readonly string? _selectedName;

    public string? SelectedFileName { get; private set; }

    public GalleryTexturePickerWindow(string? selectedName)
    {
        _selectedName = selectedName;
        Title = "Galerietextur auswählen";
        Width = 725;
        Height = 530;
        MinWidth = 450;
        MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var body = new DockPanel { Margin = new Thickness(14) };
        Content = body;

        var explanation = new TextBlock
        {
            Text = "JPG-Textur anklicken, um sie direkt als Galeriehintergrund zu übernehmen.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 8)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        body.Children.Add(explanation);

        var folder = new TextBlock
        {
            Text = GalleryTextureLibrary.DirectoryPath,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 12)
        };
        folder.SetResourceReference(TextBlock.ForegroundProperty, "PcasaMutedTextBrush");
        DockPanel.SetDock(folder, Dock.Top);
        body.Children.Add(folder);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);

        var openFolder = new Button { Content = "Ordner öffnen", Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        openFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(GalleryTextureLibrary.DirectoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = GalleryTextureLibrary.DirectoryPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Texturordner", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        footer.Children.Add(openFolder);

        var refresh = new Button { Content = "Aktualisieren", Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        refresh.Click += (_, _) => Populate();
        footer.Children.Add(refresh);

        var close = new Button { Content = "Schließen", Margin = new Thickness(4), Padding = new Thickness(10, 5, 10, 5) };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        body.Children.Add(scroll);
        var layout = new StackPanel();
        layout.Children.Add(_emptyMessage);
        layout.Children.Add(_tiles);
        scroll.Content = layout;

        Loaded += (_, _) => Populate();
    }

    private void Populate()
    {
        _tiles.Children.Clear();
        var files = GalleryTextureLibrary.GetFiles();
        _emptyMessage.Text = files.Count == 0
            ? "Keine JPG-Dateien vorhanden. Über „Ordner öffnen“ kannst Du eigene Texturen hinzufügen."
            : "";
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var chosen = string.Equals(name, _selectedName, StringComparison.OrdinalIgnoreCase);
            try
            {
                var image = GalleryTextureLibrary.LoadBitmap(path);
                var tile = new Border
                {
                    Height = 105,
                    Width = 189,
                    BorderThickness = new Thickness(chosen ? 2 : 1),
                    BorderBrush = chosen ? Brushes.IndianRed : Brushes.Gray,
                    Background = GalleryTextureLibrary.CreateBrush(image,
                        VisualTreeHelper.GetDpi(this))
                };
                var caption = new TextBlock
                {
                    Text = name,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(2, 6, 2, 0),
                    MaxHeight = 36
                };
                var label = new TextBlock
                {
                    Text = chosen ? "✓ Ausgewählt" : "",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0)
                };
                var content = new StackPanel();
                content.Children.Add(tile);
                content.Children.Add(caption);
                content.Children.Add(label);
                var option = new Button
                {
                    Content = content,
                    Tag = name,
                    Width = 207,
                    Height = 174,
                    Padding = new Thickness(6),
                    Margin = new Thickness(4),
                    VerticalAlignment = VerticalAlignment.Top
                };
                option.Click += (_, _) =>
                {
                    SelectedFileName = name;
                    DialogResult = true;
                };
                _tiles.Children.Add(option);
            }
            catch (Exception ex)
            {
                var invalid = new TextBlock
                {
                    Text = name + "\nNicht lesbar: " + ex.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Width = 185,
                    Margin = new Thickness(8)
                };
                _tiles.Children.Add(invalid);
            }
        }
    }
}
