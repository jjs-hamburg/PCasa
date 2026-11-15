using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _editorPanelsReady;
    private string _editorPanel = "Quick";
    private string _selectedEditMask = "all";
    private bool _editMaskEnabled;
    private long _maskImageId = -1;
    private readonly List<PhotoMaskStroke> _manualSubjectStrokes = [];
    private bool _maskPainting;
    private bool _maskDragging;
    private bool _maskErasing;
    private Point _lastMaskStamp = new(double.NaN, double.NaN);
    private BitmapSource? _maskPreviewOriginal;
    private CancellationTokenSource? _maskPreviewCancellation;
    private int _maskPreviewRevision;

    // A missing/disabled mask means full-image editing. UI state never
    // leaks into the durable edit recipe; each step owns an immutable copy.
    private string ActiveEditMask => _editMaskEnabled ? _selectedEditMask : "all";
    private PhotoMaskStroke[]? ActiveEditMaskStrokes =>
        _editMaskEnabled && (_selectedEditMask is "subject" or "background") &&
        _manualSubjectStrokes.Count > 0
            ? _manualSubjectStrokes.ToArray() : null;

    private void InitializeEditorPanels()
    {
        if (_editorPanelsReady) return;
        _editorPanelsReady = true;
        EditorToolTabs.SelectedIndex = 0;
        EditorMaskAll.IsChecked = true;
        EditorMaskEnabled.IsChecked = false;
        ViewerMaskPaintOverlay.SizeChanged += (_, _) => DrawMaskPaintMarks();
        SwitchEditorPanel("Quick");
        RefreshEditorMaskStatusText();
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_editorPanelsReady || !ReferenceEquals(e.Source, EditorToolTabs)) return;
        if (EditorToolTabs.SelectedItem is TabItem { Tag: string panel })
            SwitchEditorPanel(panel);
    }

    private void SwitchEditorPanel(string panel)
    {
        if (panel != "Masks")
        {
            StopMaskPainting();
            EndMaskPreview();
        }
        _editorPanel = panel;
        foreach (var (key, tab, section) in new[]
        {
            ("Quick", EditorTabQuick, ViewerPanelQuick),
            ("Light", EditorTabLight, ViewerPanelLight),
            ("Color", EditorTabColor, ViewerPanelColor),
            ("Detail", EditorTabDetail, ViewerPanelDetail),
            ("Masks", EditorTabMasks, ViewerPanelMasks)
        })
        {
            if (key == panel && !tab.IsSelected) tab.IsSelected = true;
            section.Visibility = key == panel
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ResetEditorMaskForImage(long imageId)
    {
        if (!_editorPanelsReady || _maskImageId == imageId) return;
        StopMaskPainting();
        EndMaskPreview();
        _maskImageId = imageId;
        _manualSubjectStrokes.Clear();
        _editMaskEnabled = false;
        _selectedEditMask = "all";
        _maskErasing = false;
        EditorMaskAll.IsChecked = true;
        foreach (var option in new[] { EditorMaskSky, EditorMaskSubject,
            EditorMaskBackground, EditorMaskMaskHighlights, EditorMaskMaskShadows })
            option.IsChecked = false;
        EditorMaskEnabled.IsChecked = false;
        EditorMaskEraseButton.IsChecked = false;
        RefreshEditorMaskStatusText();
    }

    private void EditorMask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode } ||
            !PhotoEditMasks.Modes.Contains(mode, StringComparer.Ordinal))
            return;
        SelectEditorMask(mode);
    }

    private void SelectEditorMask(string mode)
    {
        if (_advancedToneActive || _brightnessActive) ApplyEditTool();
        _selectedEditMask = mode;
        _editMaskEnabled = mode != "all";
        EditorMaskEnabled.IsChecked = _editMaskEnabled;
        foreach (var option in new[] { EditorMaskAll, EditorMaskSky,
            EditorMaskSubject, EditorMaskBackground, EditorMaskMaskHighlights,
            EditorMaskMaskShadows })
            option.IsChecked = string.Equals(option.Tag as string,
                mode, StringComparison.Ordinal);
        if (mode is not ("subject" or "background")) StopMaskPainting();
        RefreshEditorMaskStatusText();
        if (_maskPreviewOriginal is not null)
        {
            if (!_editMaskEnabled) EndMaskPreview();
            else QueueMaskPreview();
        }
    }

    private void EditorMaskEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedToneActive || _brightnessActive) ApplyEditTool();
        _editMaskEnabled = EditorMaskEnabled.IsChecked == true &&
            _selectedEditMask != "all";
        if (!_editMaskEnabled) EditorMaskEnabled.IsChecked = false;
        if (!_editMaskEnabled)
        {
            StopMaskPainting();
            EndMaskPreview();
        }
        RefreshEditorMaskStatusText();
    }

    private void RefreshEditorMaskStatusText()
    {
        if (!_editorPanelsReady) return;
        var mode = _selectedEditMask;
        if (!_editMaskEnabled || mode == "all")
            EditorActiveMaskText.Text = UiLanguage.T("Maske: aus");
        else
        {
            var title = mode switch
            {
                "sky" => "Himmel (einfach)",
                "subject" => "Motiv (manuell)",
                "background" => "Hintergrund (einfach)",
                "mask-highlights" => "Helle Bereiche",
                "mask-shadows" => "Dunkle Bereiche",
                _ => "Alle"
            };
            EditorActiveMaskText.Text = mode == "subject" &&
                _manualSubjectStrokes.Count == 0
                    ? UiLanguage.T("Motiv: bitte markieren")
                    : UiLanguage.T("Aktive Maske: ") + UiLanguage.T(title);
        }
        var paintable = _editMaskEnabled &&
            (mode is "subject" or "background");
        EditorMaskPaintButton.IsEnabled = paintable;
        EditorMaskEraseButton.IsEnabled = paintable && _maskPainting;
        EditorMaskClearButton.IsEnabled = _manualSubjectStrokes.Count > 0;
        EditorMaskPreviewButton.IsEnabled = _editMaskEnabled && !_maskPainting;
    }

    private void EditorMaskPaint_Click(object sender, RoutedEventArgs e)
    {
        if (_maskPainting) { StopMaskPainting(); return; }
        if (_selectedEditMask is not ("subject" or "background"))
            SelectEditorMask("subject");
        if (_advancedToneActive || _brightnessActive) ApplyEditTool();
        _editMaskEnabled = true;
        EditorMaskEnabled.IsChecked = true;
        EndMaskPreview();
        _maskPainting = true;
        ViewerMaskPaintOverlay.Visibility = Visibility.Visible;
        EditorMaskPaintButton.IsChecked = true;
        DrawMaskPaintMarks();
        RefreshEditorMaskStatusText();
    }

    private void EditorMaskErase_Click(object sender, RoutedEventArgs e)
    {
        _maskErasing = EditorMaskEraseButton.IsChecked == true;
    }

    private void EditorMaskClear_Click(object sender, RoutedEventArgs e)
    {
        if (_advancedToneActive || _brightnessActive) ApplyEditTool();
        _manualSubjectStrokes.Clear();
        DrawMaskPaintMarks();
        if (_maskPreviewOriginal is not null) QueueMaskPreview();
        RefreshEditorMaskStatusText();
    }

    private void StopMaskPainting()
    {
        _maskPainting = false;
        _maskDragging = false;
        if (!_editorPanelsReady) return;
        ViewerMaskPaintOverlay.ReleaseMouseCapture();
        ViewerMaskPaintOverlay.Visibility = Visibility.Collapsed;
        EditorMaskPaintButton.IsChecked = false;
        EditorMaskEraseButton.IsChecked = false;
        _maskErasing = false;
        RefreshEditorMaskStatusText();
    }

    private void MaskPaint_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_maskPainting || e.ChangedButton != MouseButton.Left) return;
        _maskDragging = true;
        _lastMaskStamp = new Point(double.NaN, double.NaN);
        StampMask(e.GetPosition(ViewerMaskPaintOverlay));
        ViewerMaskPaintOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void MaskPaint_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_maskDragging || e.LeftButton != MouseButtonState.Pressed) return;
        StampMask(e.GetPosition(ViewerMaskPaintOverlay));
        e.Handled = true;
    }

    private void MaskPaint_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maskDragging) return;
        _maskDragging = false;
        ViewerMaskPaintOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void StampMask(Point p)
    {
        var width = ViewerMaskPaintOverlay.ActualWidth;
        var height = ViewerMaskPaintOverlay.ActualHeight;
        if (width < 1 || height < 1 || p.X < 0 || p.Y < 0 ||
            p.X > width || p.Y > height || _manualSubjectStrokes.Count >= 1500)
            return;
        var radius = EditorMaskBrushSizeSlider.Value;
        var spacing = Math.Max(3, radius * Math.Min(width, height) * .28);
        if (double.IsFinite(_lastMaskStamp.X) &&
            (p - _lastMaskStamp).Length < spacing) return;
        var stroke = new PhotoMaskStroke(p.X / width, p.Y / height,
            radius, _maskErasing);
        _manualSubjectStrokes.Add(stroke);
        _lastMaskStamp = p;
        AddMaskStampVisual(stroke);
        RefreshEditorMaskStatusText();
    }

    private void DrawMaskPaintMarks()
    {
        if (!_editorPanelsReady) return;
        ViewerMaskPaintOverlay.Children.Clear();
        foreach (var stroke in _manualSubjectStrokes)
            AddMaskStampVisual(stroke);
    }

    private void AddMaskStampVisual(PhotoMaskStroke stroke)
    {
        var width = ViewerMaskPaintOverlay.ActualWidth;
        var height = ViewerMaskPaintOverlay.ActualHeight;
        if (width < 1 || height < 1) return;
        var radius = Math.Max(2, stroke.Radius * Math.Min(width, height));
        var ellipse = new Ellipse
        {
            Width = radius * 2, Height = radius * 2,
            StrokeThickness = 1.5,
            Stroke = stroke.Erase ? Brushes.White : Brushes.Orange,
            Fill = stroke.Erase
                ? new SolidColorBrush(Color.FromArgb(70, 0, 0, 0))
                : new SolidColorBrush(Color.FromArgb(75, 255, 165, 0)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(ellipse, stroke.X * width - radius);
        Canvas.SetTop(ellipse, stroke.Y * height - radius);
        ViewerMaskPaintOverlay.Children.Add(ellipse);
    }

    private void EditorMaskPreview_Click(object sender, RoutedEventArgs e)
    {
        if (EditorMaskPreviewButton.IsChecked == true)
        {
            if (!_editMaskEnabled || !_viewerOpen ||
                ActiveViewerImage.Source is not BitmapSource source)
            {
                EditorMaskPreviewButton.IsChecked = false;
                return;
            }
            StopMaskPainting();
            _maskPreviewOriginal = source;
            QueueMaskPreview();
        }
        else EndMaskPreview();
    }

    private void QueueMaskPreview()
    {
        if (_maskPreviewOriginal is not { } source || !_editMaskEnabled) return;
        _maskPreviewCancellation?.Cancel();
        _maskPreviewCancellation?.Dispose();
        _maskPreviewCancellation = new CancellationTokenSource();
        var token = _maskPreviewCancellation.Token;
        var revision = ++_maskPreviewRevision;
        var mode = ActiveEditMask;
        var strokes = ActiveEditMaskStrokes;
        _ = ShowMaskPreviewAsync(source, mode, strokes, revision, token);
    }

    private async Task ShowMaskPreviewAsync(BitmapSource source, string mode,
        PhotoMaskStroke[]? strokes, int revision, CancellationToken token)
    {
        try
        {
            var tinted = await Task.Run(() =>
                PhotoEditMasks.Preview(source, mode, token, strokes), token);
            if (!_viewerOpen || Dispatcher.HasShutdownStarted ||
                token.IsCancellationRequested || revision != _maskPreviewRevision ||
                _maskPreviewOriginal is null) return;
            SetViewerBitmap(tinted);
            ApplyViewerImageLayout();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Editor.MaskPreview", ex);
            if (!Dispatcher.HasShutdownStarted)
                EndMaskPreview();
        }
    }

    private void EndMaskPreview()
    {
        ++_maskPreviewRevision;
        _maskPreviewCancellation?.Cancel();
        _maskPreviewCancellation?.Dispose();
        _maskPreviewCancellation = null;
        var original = _maskPreviewOriginal;
        _maskPreviewOriginal = null;
        if (_editorPanelsReady) EditorMaskPreviewButton.IsChecked = false;
        if (original is not null && _viewerOpen)
        {
            SetViewerBitmap(original);
            ApplyViewerImageLayout();
        }
    }
}
