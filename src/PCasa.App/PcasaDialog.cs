using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// One themed owner for ordinary PCasa confirmations, warnings and errors.
/// This replaces native Windows MessageBox chrome without changing the
/// confirmation semantics of existing callers.
/// </summary>
public static class PcasaDialog
{
    public static MessageBoxResult Show(string message, string caption,
        MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, message, caption, buttons, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string message,
        string caption, MessageBoxButton buttons, MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var app = Application.Current;
        if (app is null)
            return MessageBox.Show(owner, message, caption, buttons, icon, defaultResult);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() =>
                Show(owner, message, caption, buttons, icon, defaultResult));

        owner ??= app.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? app.MainWindow is { IsVisible: true } main ? main : null;

        var safeDefault = defaultResult == MessageBoxResult.None
            ? buttons switch
            {
                MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
                    => MessageBoxResult.No,
                MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
                _ => MessageBoxResult.OK
            }
            : defaultResult;
        var answer = MessageBoxResult.None;
        var dialog = new Window
        {
            Title = string.IsNullOrWhiteSpace(caption) ? "PCasa" : caption,
            Width = 565,
            Height = 286,
            MinWidth = 430,
            MinHeight = 225,
            MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 70),
            WindowStartupLocation = owner is not null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResizeWithGrip,
            ShowInTaskbar = owner is null,
            Owner = owner
        };
        dialog.SetResourceReference(Control.BackgroundProperty, "PcasaPanelBrush");
        dialog.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");

        var grid = new Grid { Margin = new Thickness(18, 15, 18, 15) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        dialog.Content = grid;

        var heading = new TextBlock
        {
            Text = dialog.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 19,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 14)
        };
        grid.Children.Add(heading);

        var body = new DockPanel { LastChildFill = true };
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        var kind = icon switch
        {
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Error => "×",
            MessageBoxImage.Question => "?",
            _ => "i"
        };
        var symbol = new TextBlock
        {
            Text = kind,
            Width = 40,
            Height = 40,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            FontSize = 25,
            Margin = new Thickness(0, 1, 14, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        symbol.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        DockPanel.SetDock(symbol, Dock.Left);
        body.Children.Add(symbol);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        body.Children.Add(scroll);
        scroll.Content = new TextBox
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };

        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 17, 0, 0)
        };
        Grid.SetRow(buttonsPanel, 2);
        grid.Children.Add(buttonsPanel);
        var choices = buttons switch
        {
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK }
        };
        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = UiLanguage.T(choice switch
                {
                    MessageBoxResult.Yes => "Ja",
                    MessageBoxResult.No => "Nein",
                    MessageBoxResult.Cancel => "Abbrechen",
                    _ => "OK"
                }),
                MinWidth = 95,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = choice == safeDefault,
                IsCancel = choice == MessageBoxResult.Cancel ||
                    (choice == MessageBoxResult.No &&
                     buttons == MessageBoxButton.YesNo)
            };
            if (app.TryFindResource("PcasaStatusButtonStyle") is Style style)
                button.Style = style;
            button.Click += (_, _) =>
            {
                answer = choice;
                dialog.Close();
            };
            buttonsPanel.Children.Add(button);
        }
        dialog.Closed += (_, _) =>
        {
            if (answer == MessageBoxResult.None)
                answer = buttons == MessageBoxButton.OK ? MessageBoxResult.OK :
                    buttons == MessageBoxButton.YesNo ? MessageBoxResult.No :
                    MessageBoxResult.Cancel;
        };
        dialog.ShowDialog();
        return answer == MessageBoxResult.None ? safeDefault : answer;
    }
}
