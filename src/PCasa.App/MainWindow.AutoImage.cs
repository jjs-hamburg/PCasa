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
        AddAutoImageButton("◐", "Kontrast", "auto-contrast",
            "Kontrast automatisch optimieren");
        AddAutoImageButton("◉", "Farbe", "auto-color",
            "Farbstich automatisch korrigieren");

        var brighten = new Button {
            Width = 67, Height = 59, Padding = new Thickness(2),
            Margin = new Thickness(3), ToolTip = UiLanguage.T(
                "Aufhellen oder abdunkeln")
        };
        brighten.Content = new StackPanel {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = {
                new TextBlock { Text = "☀", FontSize = 23,
                    TextAlignment = TextAlignment.Center },
                new TextBlock { Text = UiLanguage.T("Aufhellen"),
                    FontSize = 10, TextAlignment = TextAlignment.Center }
            }
        };
        brighten.Click += (_, _) => BeginBrightness();
        ViewerToolsList.Children.Add(brighten);
        BrightnessBackButton.Click += (_, _) => LeaveBrightness();
        BrightnessResetButton.Click += (_, _) => BrightnessSlider.Value = 0;
        BrightnessSlider.ValueChanged += (_, _) =>
        {
            if (!_brightnessActive) return;
            _brightnessAmount = BrightnessSlider.Value;
            BrightnessValueText.Text =
                _brightnessAmount.ToString("+0.00;-0.00;0.00",
                    System.Globalization.CultureInfo.CurrentCulture) + " EV";
            QueueBrightnessPreview();
        };
    }

    private void AddAutoImageButton(string symbol, string label,
        string kind, string tooltip)
    {
        var button = new Button {
            Width = 67, Height = 59, Padding = new Thickness(2),
            Margin = new Thickness(3),
            ToolTip = UiLanguage.T(tooltip)
        };
        button.Content = new StackPanel {
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = {
                new TextBlock { Text = symbol, FontSize = 23,
                    TextAlignment = TextAlignment.Center },
                new TextBlock { Text = UiLanguage.T(label),
                    FontSize = 9.4, TextAlignment = TextAlignment.Center }
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
        if (_brightnessActive) LeaveBrightness();
        if (_photoEditSession is null) return;
        _photoEditSession.Apply(new PhotoEditStep(kind));
        SetViewerBitmap(_photoEditSession.Render());
        ApplyViewerImageLayout();
        UpdateEditControls();
    }

    private void BeginBrightness()
    {
        if (!EnsureEditSession() || _cropSavePending) return;
        if (_cropActive) EndCrop();
        if (_straightenActive) LeaveStraighten();
        if (_brightnessActive) LeaveBrightness();
        _brightnessPreviewBase = _photoEditSession!.Render();
        _brightnessAmount = 0;
        _brightnessActive = true;
        BrightnessSlider.Value = 0;
        BrightnessValueText.Text = "0,00 EV";
        ViewerToolsOverview.Visibility = Visibility.Collapsed;
        ViewerCropOptions.Visibility = Visibility.Collapsed;
        ViewerStraightenOptions.Visibility = Visibility.Collapsed;
        ViewerBrightnessOptions.Visibility = Visibility.Visible;
        UpdateEditControls();
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
        ViewerBrightnessOptions.Visibility = Visibility.Collapsed;
        ViewerToolsOverview.Visibility = Visibility.Visible;
        if (_photoEditSession is not null)
        {
            SetViewerBitmap(_photoEditSession.Render());
            ApplyViewerImageLayout();
        }
        UpdateEditControls();
    }
}
