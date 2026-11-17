using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

// Approved Alpha.33 layout: wider application list; no headings, templates
// or explanatory prose. Add/remove are +/-, arrows reorder the actual list.
public sealed class ExternalEditorsWindow : Window
{
    private readonly List<ExternalEditorProgram> _programs;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(ExternalEditorProgram.Name) };
    private readonly TextBox _name = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _formats = new();
    private readonly CheckBox _panorama = new() { Margin = new Thickness(0, 7, 0, 3) };
    private readonly CheckBox _psd = new() { Margin = new Thickness(0, 4, 0, 3) };
    private readonly CheckBox _raw = new() { Content = "RAW", Margin = new Thickness(0, 4, 0, 3) };
    private readonly CheckBox _multi = new() { Margin = new Thickness(0, 4, 0, 3) };
    private int _editingIndex = -1;
    private bool _changing;
    public ExternalEditorSettings? SavedSettings { get; private set; }

    public ExternalEditorsWindow(ExternalEditorSettings existing)
    {
        Title = UiLanguage.T("Externe Programme verwalten");
        Width = 840;
        Height = 420;
        MinWidth = 680;
        MinHeight = 315;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        _programs = existing.Programs.ToList();

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition {
            Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        root.ColumnDefinitions.Add(new ColumnDefinition {
            Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var left = new DockPanel();
        Grid.SetColumn(left, 0);
        root.Children.Add(left);
        var controls = new StackPanel {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        left.Children.Add(controls);
        AddButton(controls, "+", () => AddProgram(), 38);
        AddButton(controls, "−", Remove, 38);
        AddButton(controls, "↑", () => Move(-1), 38);
        AddButton(controls, "↓", () => Move(1), 38);
        _list.SetResourceReference(Control.BackgroundProperty, "PcasaPanelAltBrush");
        _list.SetResourceReference(Control.ForegroundProperty, "PcasaTextBrush");
        _list.SetResourceReference(Control.BorderBrushProperty, "PcasaDividerBrush");
        _list.SelectionChanged += (_, _) => SelectProgram();
        // The list is LAST and therefore fills the remaining DockPanel width.
        left.Children.Add(_list);

        var right = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        AddField(right, "Name im Menü", _name);
        AddField(right, "Programmdatei (*.exe)", MakeBrowseRow());
        AddField(right, "Dateiformate", _formats);
        _panorama.Content = UiLanguage.T("Panorama");
        _psd.Content = "PSD";
        right.Children.Add(_panorama);
        right.Children.Add(_psd);
        right.Children.Add(_raw);
        _multi.Content = UiLanguage.T("Mehrfachbearbeitung");
        right.Children.Add(_multi);

        var footer = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 3);
        root.Children.Add(footer);
        AddButton(footer, "Abbrechen", () => { DialogResult = false; Close(); }, 90);
        AddButton(footer, "Speichern", Save, 90);
        RefreshList(0);
    }

    private UIElement MakeBrowseRow()
    {
        var row = new DockPanel();
        var browse = MakeButton(UiLanguage.T("Durchsuchen …"), Browse);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        _exe.Margin = new Thickness(0, 0, 7, 0);
        row.Children.Add(_exe);
        return row;
    }

    private static void AddField(Panel parent, string label, UIElement editor)
    {
        var caption = new TextBlock {
            Text = UiLanguage.T(label), Margin = new Thickness(0, 8, 0, 4) };
        parent.Children.Add(caption);
        if (editor is Control control) control.MinHeight = 28;
        parent.Children.Add(editor);
    }

    private static Button MakeButton(string label, Action action)
    {
        var button = new Button {
            Content = label, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 4), MinHeight = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private static void AddButton(Panel parent, string label, Action action, double width)
    {
        var button = MakeButton(UiLanguage.T(label), action);
        button.MinWidth = width;
        parent.Children.Add(button);
    }

    private void CommitFields()
    {
        if (_changing || _editingIndex < 0 || _editingIndex >= _programs.Count) return;
        _programs[_editingIndex] = new ExternalEditorProgram(
            _name.Text.Trim(), _exe.Text.Trim().Trim('"'), _formats.Text.Trim(),
            _panorama.IsChecked == true, _psd.IsChecked == true,
            _raw.IsChecked == true, _multi.IsChecked == true);
    }

    private void RefreshList(int selected)
    {
        _changing = true;
        _list.ItemsSource = null;
        _list.ItemsSource = _programs;
        _list.SelectedIndex = _programs.Count == 0
            ? -1 : Math.Clamp(selected, 0, _programs.Count - 1);
        _changing = false;
        LoadFields(_list.SelectedIndex);
    }

    private void LoadFields(int index)
    {
        _editingIndex = index;
        var entry = index is >= 0 && index < _programs.Count
            ? _programs[index] : new ExternalEditorProgram("", "", "photo");
        _changing = true;
        _name.Text = entry.Name;
        _exe.Text = entry.ExecutablePath;
        _formats.Text = entry.Formats;
        _panorama.IsChecked = entry.Panorama;
        _psd.IsChecked = entry.Psd;
        _raw.IsChecked = entry.Raw;
        _multi.IsChecked = entry.MultiEdit;
        _name.IsEnabled = _exe.IsEnabled = _formats.IsEnabled =
            _panorama.IsEnabled = _psd.IsEnabled = _raw.IsEnabled =
            _multi.IsEnabled = index >= 0;
        _changing = false;
    }

    private void SelectProgram()
    {
        if (_changing) return;
        var selected = _list.SelectedIndex;
        CommitFields();
        LoadFields(selected);
    }

    private void AddProgram()
    {
        CommitFields();
        _programs.Add(new ExternalEditorProgram("", "", "photo"));
        RefreshList(_programs.Count - 1);
        _name.Focus();
    }

    private void Remove()
    {
        var index = _list.SelectedIndex;
        if (index < 0) return;
        _editingIndex = -1;
        _programs.RemoveAt(index);
        RefreshList(index);
    }

    private void Move(int direction)
    {
        CommitFields();
        var i = _list.SelectedIndex;
        var next = i + direction;
        if (i < 0 || next < 0 || next >= _programs.Count) return;
        (_programs[i], _programs[next]) = (_programs[next], _programs[i]);
        RefreshList(next);
    }

    private void Browse()
    {
        if (_editingIndex < 0) return;
        var dialog = new OpenFileDialog {
            Title = UiLanguage.T("Programmdatei auswählen"),
            Filter = "Windows-Programme (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
            CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _exe.Text = dialog.FileName;
    }

    private void Save()
    {
        try
        {
            CommitFields();
            foreach (var entry in _programs)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException(UiLanguage.T(
                        "Bitte einen Programmnamen eingeben."));
                if (!Path.IsPathFullyQualified(entry.ExecutablePath) ||
                    !File.Exists(entry.ExecutablePath))
                    throw new FileNotFoundException(UiLanguage.T(
                        "Programmdatei nicht gefunden: ") + entry.ExecutablePath);
            }
            var settings = new ExternalEditorSettings {
                Programs = _programs.ToList() };
            settings.Save();
            SavedSettings = settings;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                UiLanguage.T("Externe Programme verwalten"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
