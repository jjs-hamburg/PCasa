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
    {
        var button = new Button {
            Width = 83, Height = 65, Padding = new Thickness(2),
            Margin = new Thickness(3),
            ToolTip = UiLanguage.T(tooltip)
        };
        button.Content = new StackPanel {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = {
                new TextBlock { Text = symbol, FontSize = 23,
                    TextAlignment = TextAlignment.Center },
                new TextBlock { Text = UiLanguage.T(label),
                    FontSize = 10, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 78 }
            }
        };
        button.Click += (_, _) => ApplyAutomaticImageEdit(kind);
        ViewerToolsList.Children.Add(button);
    }

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
