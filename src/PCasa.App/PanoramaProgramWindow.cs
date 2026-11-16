using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>Only the approved program icons and names; no extra dialog steps.</summary>
public sealed class PanoramaProgramWindow : Window
{
    public ExternalEditorProgram? SelectedProgram { get; private set; }

    public PanoramaProgramWindow(IReadOnlyList<ExternalEditorProgram> programs)
    {
        Title = UiLanguage.T("Panorama erstellen");
        Width = 390;
        Height = Math.Min(450, 75 + programs.Count * 60);
        MinWidth = 310;
        MinHeight = 130;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var scroll = new ScrollViewer { Margin = new Thickness(12, 10, 12, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var programsList = new StackPanel();
        scroll.Content = programsList;
        Content = scroll;

        foreach (var program in programs)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center };
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(
                    program.ExecutablePath);
                if (icon is not null)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    source.Freeze();
                    row.Children.Add(new Image { Source = source,
                        Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) });
                }
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Panorama.ProgramIcon", ex);
            }
            row.Children.Add(new TextBlock { Text = program.Name,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
            var button = new Button { Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 7, 10, 7),
                MinHeight = 48, Margin = new Thickness(0, 0, 0, 6) };
            button.Click += (_, _) =>
            {
                SelectedProgram = program;
                DialogResult = true;
            };
            programsList.Children.Add(button);
        }
    }
}
