using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// Video has no photo-editing sidebar. Its external-editor return uses the
/// same three explicit decisions without the old working-folder/import UI.
/// For photos these buttons are the existing Crop/Editor footer instead.
/// </summary>
internal sealed class ExternalVideoReviewWindow : Window
{
    private readonly Button _cancel = new();
    private readonly Button _copy = new();
    private readonly Button _save = new();
    private bool _busy;
    private bool _hasResult;
    public void SetHasResult(bool value)
    {
        _hasResult = value;
        if (!_busy)
            _copy.IsEnabled = _save.IsEnabled = value;
    }

    public ExternalVideoReviewWindow(string fileName,
        Func<Services.ExternalEditDecision, Task<bool>> finish,
        bool hasResult)
    {
        Title = UiLanguage.T("Externe Bearbeitung abschließen");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 580;
        Height = 184;
        MinWidth = 450;
        MaxHeight = 240;
        ResizeMode = ResizeMode.CanMinimize;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        var body = new DockPanel { Margin = new Thickness(18) };
        Content = body;
        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        Add(_cancel, 0, "Abbrechen", Services.ExternalEditDecision.Cancel);
        Add(_copy, 1, "Als Kopie speichern", Services.ExternalEditDecision.SaveCopy);
        Add(_save, 2, "Speichern", Services.ExternalEditDecision.Save);
        SetHasResult(hasResult);
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Externe Bearbeitung beendet: ") + fileName,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        void Add(Button control, int column, string label,
            Services.ExternalEditDecision decision)
        {
            control.Content = UiLanguage.T(label);
            control.Margin = new Thickness(4);
            control.Padding = new Thickness(6);
            control.MinHeight = 34;
            Grid.SetColumn(control, column);
            footer.Children.Add(control);
            control.Click += async (_, _) =>
            {
                if (_busy) return;
                _busy = true;
                _cancel.IsEnabled = _copy.IsEnabled = _save.IsEnabled = false;
                try
                {
                    if (await finish(decision)) Close();
                }
                finally
                {
                    _busy = false;
                    if (IsVisible)
                    {
                        _cancel.IsEnabled = true;
                        _copy.IsEnabled = _save.IsEnabled = _hasResult;
                    }
                }
            };
        }
    }
}
