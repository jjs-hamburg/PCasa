using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _brightnessActive;
    private double _brightnessAmount;
    private BitmapSource? _brightnessPreviewBase;
    private CancellationTokenSource? _brightnessPreviewCancellation;
    private int _brightnessPreviewRevision;

    // One registration path for every edit tool: identical icon-button
    // geometry, captions OUTSIDE the button and live language switching.
    private readonly List<(TextBlock Caption, Button Button,
        string Key, string TooltipKey)> _editorToolLabels = [];

    private void AddEditorTool(UIElement icon, string label, string tooltip,
        Action action, bool automatic = false)
    {
        var button = new Button
        {
            Width = 74, Height = 49, Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Content = icon
        };
        button.Click += (_, _) => action();
        var caption = new TextBlock
        {
            Width = 82, Height = 31, FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 0, 0)
        };
        var item = new StackPanel
        {
            Width = 86, Margin = new Thickness(1, 2, 1, 3)
        };
        item.Children.Add(button);
        item.Children.Add(caption);
        (automatic ? ViewerAutoToolsList.Children : ViewerToolsList.Children)
            .Add(item);
        _editorToolLabels.Add((caption, button, label, tooltip));
        RefreshEditorToolLabels();
    }

    private void RefreshEditorToolLabels()
    {
        foreach (var (caption, button, label, tooltip) in _editorToolLabels)
        {
            caption.Text = UiLanguage.T(label);
            button.ToolTip = UiLanguage.T(tooltip);
        }
        UpdateEditHistoryTooltips();
    }

    private void InitializeAutoImageTools()
    {
        AddAutoImageButton("✦", "Auf gut Glück!", "auto-fix",
            "Helligkeit, Kontrast und Farbstich automatisch korrigieren");
        AddAutoImageButton("◐", "Kontrast (automatisch)", "auto-contrast",
            "Kontrast automatisch optimieren");
        AddAutoImageButton("◉", "Farbe (automatisch)", "auto-color",
            "Farbstich automatisch korrigieren");

        BrightnessResetButton.Click += (_, _) => BrightnessSlider.Value = 0;
        BrightnessSlider.ValueChanged += (_, _) =>
        {
            if (!_brightnessActive && Math.Abs(BrightnessSlider.Value) < .00001)
                return;
            if (!_brightnessActive && !BeginBrightness())
            {
                BrightnessSlider.Value = 0;
                return;
            }
            _brightnessAmount = BrightnessSlider.Value;
            BrightnessValueText.Text =
                _brightnessAmount.ToString("+0.00;-0.00;0.00",
                    System.Globalization.CultureInfo.CurrentCulture) + " EV";
            QueueBrightnessPreview();
            UpdateEditControls();
        };
    }

    private void AddAutoImageButton(string symbol, string label,
        string kind, string tooltip)
        => AddEditorTool(
            new TextBlock
            {
                Text = symbol, FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }, label, tooltip, () => ApplyAutomaticImageEdit(kind),
            automatic: true);

    private void ApplyAutomaticImageEdit(string kind)
    {
        if (!EnsureEditSession() || _cropSavePending) return;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) ApplyEditTool();
        if (_photoEditSession is null) return;
        _photoEditSession.Apply(new PhotoEditStep(kind));
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
    }

    private bool BeginBrightness()
    {
        if (_cropSavePending || !EnsureEditSession()) return false;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        _brightnessPreviewBase = _photoEditSession!.Render();
        _brightnessAmount = BrightnessSlider.Value;
        _brightnessActive = true;
        // Slider remains visible in the main editor; its changes never
        // replace or resize the editor panel.
        UpdateEditControls();
        return true;
    }

    // A preview is made from an immutable in-memory BitmapSource on a worker
    // thread. Rapid slider changes cancel/supersede older previews; only the
    // last result may update the WPF viewer. No NAS read or JPEG write here.
    private async void QueueBrightnessPreview()
    {
        if (!_brightnessActive || _brightnessPreviewBase is null) return;
        _brightnessPreviewCancellation?.Cancel();
        _brightnessPreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _brightnessPreviewCancellation = cancellation;
        var token = cancellation.Token;
        var revision = ++_brightnessPreviewRevision;
        var amount = _brightnessAmount;
        var source = _brightnessPreviewBase;
        try
        {
            // Coalesce mouse events without delaying the final slider value.
            await Task.Delay(25, token);
            token.ThrowIfCancellationRequested();
            var rendered = await Task.Run(() =>
                PhotoEditSession.Render(source,
                    [new PhotoEditStep("brightness", Amount: amount)]), token);
            if (token.IsCancellationRequested || !_brightnessActive ||
                revision != _brightnessPreviewRevision ||
                Dispatcher.HasShutdownStarted)
                return;
            SetViewerBitmap(rendered);
            ApplyViewerImageLayout();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Image.BrightnessPreview", ex);
        }
    }

    private void LeaveBrightness()
    {
        _brightnessActive = false;
        ++_brightnessPreviewRevision;
        _brightnessPreviewCancellation?.Cancel();
        _brightnessPreviewCancellation?.Dispose();
        _brightnessPreviewCancellation = null;
        _brightnessPreviewBase = null;
        BrightnessSlider.Value = 0;
        BrightnessValueText.Text = "0,00 EV";
        if (_photoEditSession is not null)
        {
            SetViewerBitmap(_photoEditSession.Render());
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }
}
