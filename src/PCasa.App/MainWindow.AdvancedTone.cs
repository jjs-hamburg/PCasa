using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _advancedToneToolsReady;
    private bool _advancedToneActive;
    private string? _advancedToneKind;
    private double _advancedToneAmount;
    private BitmapSource? _advancedTonePreviewBase;
    private CancellationTokenSource? _advancedTonePreviewCancellation;
    private int _advancedTonePreviewRevision;
    private bool _ignoreAdvancedSliderChanges;
    private readonly Dictionary<string, Slider> _advancedToneSliders = new();

    private void InitializeAdvancedToneTools()
    {
        if (_advancedToneToolsReady) return;
        _advancedToneToolsReady = true;
        var controls = new (string Kind, Slider Slider, Button Reset)[]
        {
            ("denoise-luma", DenoiseLumaSlider, DenoiseLumaResetButton),
            ("denoise-chroma", DenoiseChromaSlider, DenoiseChromaResetButton),
            ("sharpen", SharpenSlider, SharpenResetButton),
            ("highlights", HighlightsSlider, HighlightsResetButton),
            ("shadows", ShadowsSlider, ShadowsResetButton),
            ("contrast", ContrastSlider, ContrastResetButton),
            ("temperature", TemperatureSlider, TemperatureResetButton),
            ("tint", TintSlider, TintResetButton),
            ("saturation", SaturationSlider, SaturationResetButton)
        };
        foreach (var (kind, slider, reset) in controls)
        {
            _advancedToneSliders.Add(kind, slider);
            slider.ValueChanged += (_, _) => ChangeAdvancedTone(kind, slider.Value);
            reset.Click += (_, _) => slider.Value = 0;
        }
        SharpenRadiusSlider.ValueChanged += (_, _) =>
        {
            if (_advancedToneActive && _advancedToneKind == "sharpen")
                QueueAdvancedTonePreview();
        };
        SharpenDetailSlider.ValueChanged += (_, _) =>
        {
            if (_advancedToneActive && _advancedToneKind == "sharpen")
                QueueAdvancedTonePreview();
        };
    }

    private void ChangeAdvancedTone(string kind, double amount)
    {
        if (_ignoreAdvancedSliderChanges || _cropSavePending ||
            _viewerImageInfo is not { } viewer ||
            _pendingEditImageIds.Contains(viewer.Id))
            return;

        // A slider is one transaction step, even when dragged through
        // hundreds of positions. Switching sliders commits the last value
        // to the shared Undo history before the next live preview starts.
        if (_advancedToneActive && _advancedToneKind != kind)
            ApplyEditTool();

        if (!_advancedToneActive)
        {
            if (Math.Abs(amount) < .00001) return;
            if (_cropActive || _straightenActive || _brightnessActive)
                ApplyEditTool();
            if (!EnsureEditSession())
            {
                ResetAdvancedSlider(kind);
                return;
            }
            _advancedTonePreviewBase = _photoEditSession!.Render();
            _advancedToneKind = kind;
            _advancedToneActive = true;
        }

        _advancedToneAmount = amount;
        QueueAdvancedTonePreview();
        UpdateEditControls();
    }

    private void ResetAdvancedSlider(string kind)
    {
        if (!_advancedToneSliders.TryGetValue(kind, out var slider)) return;
        _ignoreAdvancedSliderChanges = true;
        try { slider.Value = 0; }
        finally { _ignoreAdvancedSliderChanges = false; }
    }

    private void LeaveAdvancedTone()
    {
        _advancedToneActive = false;
        var kind = _advancedToneKind;
        _advancedToneKind = null;
        _advancedToneAmount = 0;
        _advancedTonePreviewBase = null;
        ++_advancedTonePreviewRevision;
        _advancedTonePreviewCancellation?.Cancel();
        _advancedTonePreviewCancellation?.Dispose();
        _advancedTonePreviewCancellation = null;
        if (kind is not null) ResetAdvancedSlider(kind);
    }

    private void QueueAdvancedTonePreview()
    {
        if (!_advancedToneActive || _advancedTonePreviewBase is null ||
            _advancedToneKind is null)
            return;
        _advancedTonePreviewCancellation?.Cancel();
        _advancedTonePreviewCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _advancedTonePreviewCancellation = cancellation;
        var token = cancellation.Token;
        var revision = ++_advancedTonePreviewRevision;
        var kind = _advancedToneKind;
        var amount = _advancedToneAmount;
        var source = _advancedTonePreviewBase;
        var radius = kind == "sharpen" ? SharpenRadiusSlider.Value : 1;
        var detail = kind == "sharpen" ? SharpenDetailSlider.Value : .5;
        _ = RenderAdvancedTonePreviewAsync(source, kind, amount,
            radius, detail, revision, token);
    }

    private async Task RenderAdvancedTonePreviewAsync(
        BitmapSource source, string kind, double amount,
        double radius, double detail, int revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(40, token); // Coalesce rapid slider movement.
            token.ThrowIfCancellationRequested();
            var bitmap = Math.Abs(amount) < .00001 ? source : await Task.Run(
                () => PhotoEditSession.Render(source,
                    [new PhotoEditStep(kind, Amount: amount,
                        Radius: radius, Detail: detail)], token), token);
            if (token.IsCancellationRequested || !_advancedToneActive ||
                revision != _advancedTonePreviewRevision ||
                Dispatcher.HasShutdownStarted) return;
            SetViewerBitmap(bitmap);
            ApplyViewerImageLayout();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Image.AdvancedPreview", ex);
        }
    }
}
