using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private bool _editorPanelsReady;
    private string _editorPanel = "Quick";
    private string _selectedEditMask = "all";
    private BitmapSource? _maskPreviewOriginal;
    private CancellationTokenSource? _maskPreviewCancellation;
    private int _maskPreviewRevision;

    private void InitializeEditorPanels()
    {
        if (_editorPanelsReady) return;
        _editorPanelsReady = true;
        SwitchEditorPanel("Quick");
        EditorMaskAll.IsChecked = true;
        RefreshEditorMaskStatusText();
    }

    private void EditorTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string panel })
            SwitchEditorPanel(panel);
    }

    private void SwitchEditorPanel(string panel)
    {
        if (panel != "Masks") EndMaskPreview();
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
            tab.IsChecked = key == panel;
            section.Visibility = key == panel
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void EditorMask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mask } toggle ||
            !PhotoEditMasks.Modes.Contains(mask, StringComparer.Ordinal))
            return;
        if (_advancedToneActive || _brightnessActive)
            ApplyEditTool();
        _selectedEditMask = mask;
        RefreshEditorMaskStatusText();
        foreach (var option in new[] { EditorMaskAll, EditorMaskSky,
            EditorMaskSubject, EditorMaskBackground,
            EditorMaskMaskHighlights, EditorMaskMaskShadows })
            option.IsChecked = ReferenceEquals(option, toggle);
        if (_maskPreviewOriginal is not null)
            QueueMaskPreview();
    }

    private void RefreshEditorMaskStatusText()
    {
        if (!_editorPanelsReady) return;
        if (_selectedEditMask == "all")
        {
            EditorActiveMaskText.Text = UiLanguage.T("Maske: gesamtes Bild");
            return;
        }
        var title = _selectedEditMask switch
        {
            "sky" => "Himmel (einfach)",
            "subject" => "Motiv (einfach)",
            "background" => "Hintergrund (einfach)",
            "mask-highlights" => "Helle Bereiche",
            "mask-shadows" => "Dunkle Bereiche",
            _ => "Alle"
        };
        EditorActiveMaskText.Text = UiLanguage.T("Aktive Maske: ") +
            UiLanguage.T(title);
    }

    private void EditorMaskPreview_Click(object sender, RoutedEventArgs e)
    {
        if (EditorMaskPreviewButton.IsChecked == true)
        {
            if (!_viewerOpen || ActiveViewerImage.Source is not BitmapSource source)
            {
                EditorMaskPreviewButton.IsChecked = false;
                return;
            }
            _maskPreviewOriginal = source;
            QueueMaskPreview();
        }
        else EndMaskPreview();
    }

    private void QueueMaskPreview()
    {
        if (_maskPreviewOriginal is not { } source) return;
        _maskPreviewCancellation?.Cancel();
        _maskPreviewCancellation?.Dispose();
        _maskPreviewCancellation = new CancellationTokenSource();
        var token = _maskPreviewCancellation.Token;
        var revision = ++_maskPreviewRevision;
        var mode = _selectedEditMask;
        _ = ShowMaskPreviewAsync(source, mode, revision, token);
    }

    private async Task ShowMaskPreviewAsync(
        BitmapSource source, string mode, int revision, CancellationToken token)
    {
        try
        {
            var tinted = await Task.Run(() =>
                PhotoEditMasks.Preview(source, mode, token), token);
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
        EditorMaskPreviewButton.IsChecked = false;
        if (original is not null && _viewerOpen)
        {
            SetViewerBitmap(original);
            ApplyViewerImageLayout();
        }
    }
}
