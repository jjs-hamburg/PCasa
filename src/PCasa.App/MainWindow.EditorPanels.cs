using System.Windows;
using System.Windows.Controls;

namespace PCasa.App;

// Alpha.31: The unsupported mask tools were retired. Ordinary edits use the
// full image; legacy mask recipe replay remains readable by PhotoEditSession
// until all previously queued durable writes have completed.
public partial class MainWindow
{
    private bool _editorPanelsReady;
    private void InitializeEditorPanels()
    {
        if (_editorPanelsReady) return;
        _editorPanelsReady = true;
        EditorToolTabs.SelectedIndex = 0;
        SwitchEditorPanel("Quick");
    }

    private void EditorTabs_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (!_editorPanelsReady || !ReferenceEquals(e.Source, EditorToolTabs))
            return;
        if (EditorToolTabs.SelectedItem is TabItem { Tag: string name })
            SwitchEditorPanel(name);
    }

    private void SwitchEditorPanel(string panel)
    {
        foreach (var (key, tab, section) in new[]
        {
            ("Quick", EditorTabQuick, ViewerPanelQuick),
            ("Light", EditorTabLight, ViewerPanelLight),
            ("Color", EditorTabColor, ViewerPanelColor),
            ("Detail", EditorTabDetail, ViewerPanelDetail)
        })
        {
            if (key == panel && !tab.IsSelected) tab.IsSelected = true;
            section.Visibility = key == panel
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
