using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

public sealed record ShutdownPendingFile(string Path, string? ThumbnailPath);
public sealed record ShutdownPendingOperation(
    string Key, string Action, string Status,
    IReadOnlyList<ShutdownPendingFile> Files,
    long? DraftImageId = null, bool CanCancel = true,
    string? ActionLabel = null);

/// <summary>
/// Only the agreed shutdown review: affected files with cached thumbnails,
/// per-task one-click cancellation, Don't Exit and Exit.
/// </summary>
public sealed class ShutdownPendingWorkWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly Button _exit = new();
    private int _cancellationInFlight;

    public ShutdownPendingWorkWindow(
        IReadOnlyList<ShutdownPendingOperation> operations,
        Func<ShutdownPendingOperation, Task<bool>> cancelOperation)
    {
        Title = UiLanguage.T("PCasa beenden");
        Width = 620;
        Height = 490;
        MinWidth = 490;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var stay = new Button { Content = UiLanguage.T("Nicht beenden"),
            MinWidth = 115, Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 8, 0) };
        stay.Click += (_, _) => { DialogResult = false; };
        buttons.Children.Add(stay);
        _exit.Content = UiLanguage.T("Beenden");
        _exit.MinWidth = 95;
        _exit.Padding = new Thickness(12, 7, 12, 7);
        _exit.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(_exit);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var label = new TextBlock { Text = UiLanguage.T("Nicht abgeschlossene Änderungen"),
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(label, Dock.Top);
        root.Children.Add(label);
        var explanation = new TextBlock
        {
            Text = UiLanguage.T("Beim Beenden werden noch ausstehende Dateioperationen verworfen. Bereits gespeicherte Dateien bleiben erhalten."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(explanation, Dock.Top);
        root.Children.Add(explanation);

        var scrolling = new ScrollViewer { VerticalScrollBarVisibility =
            ScrollBarVisibility.Auto, HorizontalScrollBarVisibility =
            ScrollBarVisibility.Disabled };
        root.Children.Add(scrolling);
        scrolling.Content = _list;

        foreach (var item in operations)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 8) };
            var background = new Border { Padding = new Thickness(8),
                BorderThickness = new Thickness(0, 0, 0, 1), Child = row };
            background.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
            var abort = new Button {
                Content = UiLanguage.T(item.ActionLabel ?? "Abbrechen"),
                IsEnabled = item.CanCancel,
                MinWidth = 88, Padding = new Thickness(7, 5, 7, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0) };
            DockPanel.SetDock(abort, Dock.Right);
            row.Children.Add(abort);
            var detail = new StackPanel();
            row.Children.Add(detail);
            var heading = new TextBlock { Text = item.Action + " · " + item.Status,
                FontSize = 12, FontWeight = FontWeights.SemiBold };
            detail.Children.Add(heading);
            foreach (var image in item.Files)
            {
                var fileRow = new StackPanel { Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 4, 0, 0) };
                var picture = ReadLocalThumbnail(image.ThumbnailPath);
                if (picture is not null)
                    fileRow.Children.Add(new Image { Source = picture,
                        Width = 36, Height = 32, Stretch = Stretch.Uniform,
                        Margin = new Thickness(0, 0, 8, 0) });
                var name = new TextBlock { Text = Path.GetFileName(image.Path),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 380 };
                fileRow.Children.Add(name);
                detail.Children.Add(fileRow);
            }
            _list.Children.Add(background);
            abort.Click += async (_, _) =>
            {
                if (abort.IsEnabled == false) return;
                abort.IsEnabled = false;
                _cancellationInFlight++;
                _exit.IsEnabled = false;
                try
                {
                    if (await cancelOperation(item))
                        _list.Children.Remove(background);
                    else
                        abort.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    abort.IsEnabled = true;
                    MessageBox.Show(this, ex.Message, Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    _cancellationInFlight--;
                    _exit.IsEnabled = _cancellationInFlight == 0;
                }
            };
        }
    }

    private static BitmapSource? ReadLocalThumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var file = ThumbnailCache.ResolveExistingLocalPath(path);
        if (file is null) return null;
        try
        {
            using var input = new FileStream(file, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 40;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (IOException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
