using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

public sealed class ExternalEditorsWindow : Window
{
    private readonly List<ExternalEditorProgram> _programs;
    private readonly ListBox _list = new() { DisplayMemberPath = nameof(ExternalEditorProgram.Name) };
    private readonly TextBox _name = new();
    private readonly TextBox _exe = new();
    private readonly TextBox _formats = new();
    private int _editingIndex = -1;
    private bool _binding;
    public ExternalEditorSettings? SavedSettings { get; private set; }

    public ExternalEditorsWindow(ExternalEditorSettings existing)
    {
        Title = UiLanguage.T("Externe Programme verwalten");
        Width = 780;
        Height = 475;
        MinWidth = 670;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(40, 43, 47));
        Foreground = Brushes.White;
        _programs = existing.Programs.ToList();

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var left = new DockPanel();
        Grid.SetColumn(left, 0);
        root.Children.Add(left);
        DockPanel.SetDock(left, Dock.Top);
        left.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Programme (Reihenfolge im Menü)"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var controls = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        left.Children.Add(controls);
        AddButton(controls, "Hinzufügen", () => Add(new ExternalEditorProgram(
            UiLanguage.T("Neues Programm"), "", "photo")));
        AddButton(controls, "Entfernen", Remove);
        AddButton(controls, "↑", () => Move(-1));
        AddButton(controls, "↓", () => Move(1));
        _list.Background = new SolidColorBrush(Color.FromRgb(55, 58, 62));
        _list.Foreground = Brushes.White;
        _list.BorderBrush = Brushes.Gray;
        _list.SelectionChanged += (_, _) => SelectEntry();
        left.Children.Add(_list);

        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        right.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Bearbeitungsprogramm"),
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        AddField(right, UiLanguage.T("Name im Menü"), _name);
        var exeRow = new DockPanel();
        AddField(right, UiLanguage.T("Programmdatei (*.exe)"), exeRow);
        var browse = MakeButton(UiLanguage.T("Durchsuchen …"), Browse);
        DockPanel.SetDock(browse, Dock.Right);
        exeRow.Children.Add(browse);
        _exe.Margin = new Thickness(0, 0, 7, 0);
        exeRow.Children.Add(_exe);
        AddField(right, UiLanguage.T("Dateiformate"), _formats);
        right.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Beispiele: photo, video, *, .jpg;.png;.arw"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 3, 0, 14)
        });
        right.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Topaz-Vorlagen (Programmdatei anschließend auswählen):"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var templates = new WrapPanel();
        right.Children.Add(templates);
        AddButton(templates, "Topaz Photo", () => Add(
            new ExternalEditorProgram("Topaz Photo", "", "photo")));
        AddButton(templates, "Topaz Gigapixel", () => Add(
            new ExternalEditorProgram("Topaz Gigapixel", "", "photo")));
        AddButton(templates, "Topaz Video", () => Add(
            new ExternalEditorProgram("Topaz Video", "", "video")));

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 3);
        root.Children.Add(footer);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        AddButton(buttons, "Abbrechen", () => { DialogResult = false; Close(); });
        AddButton(buttons, "Speichern", Save);
        footer.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Programme werden nur auf ausdrücklichen Befehl gestartet."),
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center
        });
        RefreshList();
        if (_programs.Count > 0) _list.SelectedIndex = 0;
        else Bind(-1);
    }

    private static void AddField(Panel parent, string text, UIElement value)
    {
        parent.Children.Add(new TextBlock
        {
            Text = text, Margin = new Thickness(0, 5, 0, 4),
            Foreground = Brushes.White
        });
        if (value is Control control) control.MinHeight = 28;
        parent.Children.Add(value);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Content = UiLanguage.T(text),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 5, 5),
            MinHeight = 29
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static void AddButton(Panel panel, string text, Action action)
        => panel.Children.Add(MakeButton(text, action));

    private void RefreshList()
    {
        var selected = _list.SelectedIndex;
        _binding = true;
        _list.ItemsSource = null;
        _list.ItemsSource = _programs;
        _binding = false;
        if (selected >= 0 && selected < _programs.Count)
            _list.SelectedIndex = selected;
    }

    private void CommitCurrent()
    {
        if (_binding || _editingIndex < 0 || _editingIndex >= _programs.Count)
            return;
        _programs[_editingIndex] = new ExternalEditorProgram(
            _name.Text.Trim(), _exe.Text.Trim().Trim('"'), _formats.Text.Trim());
    }

    private void Bind(int index)
    {
        _binding = true;
        _editingIndex = index;
        var program = index >= 0 && index < _programs.Count
            ? _programs[index] : new ExternalEditorProgram("", "", "photo");
        _name.Text = program.Name;
        _exe.Text = program.ExecutablePath;
        _formats.Text = program.Formats;
        _binding = false;
    }

    private void SelectEntry()
    {
        if (_binding) return;
        var selected = _list.SelectedIndex;
        CommitCurrent();
        Bind(selected);
    }

    private void Add(ExternalEditorProgram program)
    {
        CommitCurrent();
        _programs.Add(program);
        _editingIndex = -1;
        RefreshList();
        _list.SelectedIndex = _programs.Count - 1;
    }

    private void Remove()
    {
        if (_list.SelectedIndex < 0) return;
        var previous = _list.SelectedIndex;
        _editingIndex = -1;
        _programs.RemoveAt(previous);
        RefreshList();
        _list.SelectedIndex = _programs.Count == 0 ? -1 :
            Math.Min(previous, _programs.Count - 1);
    }

    private void Move(int by)
    {
        CommitCurrent();
        var i = _list.SelectedIndex;
        var next = i + by;
        if (i < 0 || next < 0 || next >= _programs.Count) return;
        (_programs[i], _programs[next]) = (_programs[next], _programs[i]);
        _editingIndex = -1;
        RefreshList();
        _list.SelectedIndex = next;
    }

    private void Browse()
    {
        var picker = new OpenFileDialog
        {
            Title = UiLanguage.T("Programmdatei auswählen"),
            Filter = "Windows-Programme (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true) _exe.Text = picker.FileName;
    }

    private void Save()
    {
        try
        {
            CommitCurrent();
            if (_programs.Any(p => string.IsNullOrWhiteSpace(p.Name)))
                throw new InvalidDataException(UiLanguage.T("Bitte einen Programmnamen eingeben."));
            foreach (var program in _programs.Where(p =>
                         !string.IsNullOrWhiteSpace(p.ExecutablePath)))
            {
                if (!Path.IsPathFullyQualified(program.ExecutablePath) ||
                    !File.Exists(program.ExecutablePath))
                    throw new FileNotFoundException(
                        UiLanguage.T("Programmdatei nicht gefunden: ") +
                        program.ExecutablePath);
            }
            var settings = new ExternalEditorSettings { Programs = _programs.ToList() };
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
