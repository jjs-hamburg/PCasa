using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// Compact local-database control surface. File picking and root remapping
/// use native Windows dialogs; paths are never typed in editable text boxes.
/// </summary>
public sealed class DatabaseManagementWindow : Window
{
    private readonly DatabaseMaintenance _maintenance;
    private readonly Func<DatabaseMaintenance.Request, bool> _startMaintenance;
    private readonly TextBlock _status;
    private bool _busy;
    private bool _backupBusy;
    private Grid? _operationButtons;

    public DatabaseManagementWindow(DatabaseMaintenance maintenance,
        Func<DatabaseMaintenance.Request, bool> startMaintenance)
    {
        _maintenance = maintenance;
        _startMaintenance = startMaintenance;
        Title = "PCasa – " + UiLanguage.T("Datenbankverwaltung");
        Width = 720;
        MinWidth = 640;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 80;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        var root = new StackPanel { Margin = new Thickness(19, 16, 19, 17) };
        Content = root;
        root.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Datenbankverwaltung"),
            FontSize = 19, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });
        _status = new TextBlock
        {
            Text = UiLanguage.T("Datenbankinformationen werden gelesen …"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            MinHeight = 108
        };
        root.Children.Add(_status);
        var buttons = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        _operationButtons = buttons;
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(left);
        Grid.SetColumn(right, 1);
        buttons.Children.Add(right);
        root.Children.Add(buttons);
        Add(left, "Datenbank sichern …", Backup);
        Add(right, "Datenbank wiederherstellen …", Restore);
        Add(left, "Datenbank optimieren", Optimize);
        Add(right, "Datenbank zurücksetzen …", Reset);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var explanation = new TextBlock
        {
            Text = UiLanguage.T("Originalfotos werden durch die Datenbankverwaltung nie verändert."),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(explanation, Dock.Left);
        footer.Children.Add(explanation);
        var close = new Button
        {
            Content = UiLanguage.T("Schließen"), Width = 92, Height = 30,
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        root.Children.Add(footer);
        Loaded += async (_, _) => await RefreshInfoAsync();
        Closing += (_, args) =>
        {
            // Keep the visible status window alive until the SQLite snapshot
            // and corresponding cache archive have been completely published.
            if (_backupBusy) args.Cancel = true;
        };
    }

    private static void Add(Panel parent, string label, Action action)
    {
        var button = new Button
        {
            Content = UiLanguage.T(label),
            Height = 39, Margin = new Thickness(0, 0, 0, 10),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private async Task RefreshInfoAsync()
    {
        try
        {
            var summary = await Task.Run(_maintenance.ReadSummary);
            if (_backupBusy) return; // An older info read must not erase the live backup status.
            _status.Text = UiLanguage.Format(
                "Datenbank: {0}\nFotos: {1:N0}    Überwachte Verzeichnisse: {2:N0}\n" +
                "Datenbankgröße: {3:N1} MB    Vorschaudaten: {4:N1} MB\n" +
                "Offene Aufträge: {5:N0}    Vorgemerkte Tag-Änderungen: {6:N0}",
                summary.Path, summary.Images, summary.Roots,
                summary.DatabaseBytes / 1048576.0,
                summary.CacheBytes / 1048576.0,
                summary.OpenTasks, summary.OpenWrites);
        }
        catch (Exception ex)
        {
            if (!_backupBusy)
                _status.Text = UiLanguage.T("Datenbankinformationen nicht verfügbar: ") +
                               ex.Message;
        }
    }

    private void Begin(DatabaseMaintenance.Request request)
    {
        if (_busy) return;
        _busy = true;
        if (!_startMaintenance(request))
            _busy = false;
        else
            Close();
    }

    private async void Backup()
    {
        if (_busy) return;
        var picker = new SaveFileDialog
        {
            Title = UiLanguage.T("PCasa-Datenbank sichern"),
            Filter = "PCasa-Sicherung (*.pcasa.zip)|*.pcasa.zip",
            FileName = "PCasa-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".pcasa.zip",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (picker.ShowDialog(this) != true) return;
        await BackupToAsync(picker.FileName);
    }

    // A backup is READ-ONLY and runs with PCasa open. The SQLite online
    // backup and thumbnail archive share the existing cache lock, so the
    // referenced thumbnail packs cannot be compacted away mid-snapshot.
    // Restore/reset still require the separate offline maintenance process.
    internal async Task BackupToAsync(string destination)
    {
        if (_busy) return;
        _busy = _backupBusy = true;
        if (_operationButtons is not null) _operationButtons.IsEnabled = false;
        _status.Text = UiLanguage.T(
            "Sicherung läuft: Datenbank und Vorschaudaten werden konsistent gespeichert …");
        try
        {
            await Task.Run(() => _maintenance.Backup(destination));
            _status.Text = UiLanguage.T("Sicherung erfolgreich erstellt: ") + destination;
            MessageBox.Show(this, UiLanguage.T("Sicherung erfolgreich erstellt.") +
                Environment.NewLine + destination,
                UiLanguage.T("Datenbank sichern"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = UiLanguage.T("Sicherung fehlgeschlagen: ") + ex.Message;
            MessageBox.Show(this, ex.Message,
                UiLanguage.T("Datenbank sichern"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = _backupBusy = false;
            if (_operationButtons is not null) _operationButtons.IsEnabled = true;
        }
    }

    private async void Restore()
    {
        if (_busy) return;
        var picker = new OpenFileDialog
        {
            Title = UiLanguage.T("PCasa-Sicherung auswählen"),
            Filter = "PCasa-Sicherung (*.pcasa.zip)|*.pcasa.zip|ZIP (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        _busy = true;
        _status.Text = UiLanguage.T("Sicherung wird geprüft …");
        try
        {
            var folders = await Task.Run(() => _maintenance.InspectBackup(picker.FileName));
            var select = new DatabaseRestoreFoldersWindow(folders) { Owner = this };
            if (select.ShowDialog() == true)
            {
                var pending = select.Selections;
                var included = pending.Count(choice => choice.Include);
                var approve = MessageBox.Show(this,
                    UiLanguage.Format(
                        "{0:N0} Verzeichnisse werden übernommen. Alle alten PCasa-Aufträge, " +
                        "Scanfortschritte und noch nicht in Originaldateien geschriebene Änderungen " +
                        "werden verworfen. Die Originaldateien bleiben unberührt.\n\n" +
                        "PCasa wird zur Wiederherstellung neu gestartet. Fortfahren?",
                        included),
                    UiLanguage.T("Datenbank wiederherstellen"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (approve == MessageBoxResult.Yes)
                {
                    _busy = false;
                    Begin(new DatabaseMaintenance.Request(
                        "restore", picker.FileName, pending, 0));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, UiLanguage.T("Sicherung ungültig"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
            if (IsLoaded && IsVisible) await RefreshInfoAsync();
        }
    }

    private void Optimize()
    {
        if (MessageBox.Show(this,
            UiLanguage.T("PCasa wird zur Optimierung der lokalen Datenbank beendet und anschließend neu gestartet. Fortfahren?"),
            UiLanguage.T("Datenbank optimieren"),
            MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Begin(new DatabaseMaintenance.Request("optimize", null, [], 0));
    }

    private void Reset()
    {
        if (MessageBox.Show(this,
            UiLanguage.T("Die lokale PCasa-Datenbank einschließlich aller Katalogeinträge, " +
                         "Aufträge und Vorschaudaten wird gelöscht. Originalfotos und Programmeinstellungen bleiben erhalten.\n\nFortfahren?"),
            UiLanguage.T("Datenbank zurücksetzen"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (MessageBox.Show(this,
            UiLanguage.T("Letzte Bestätigung: Auch noch nicht ausgeführte GPS- und Tag-Aufträge " +
                         "werden unwiderruflich verworfen. Die lokale Datenbank wird vollständig neu angelegt.\n\nJetzt zurücksetzen?"),
            UiLanguage.T("Datenbank endgültig zurücksetzen"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Begin(new DatabaseMaintenance.Request("reset", null, [], 0));
    }
}

public sealed class DatabaseRestoreFoldersWindow : Window
{
    private sealed class Entry(DatabaseMaintenance.FolderInfo folder)
    {
        public DatabaseMaintenance.FolderInfo Folder { get; } = folder;
        public bool Include { get; set; } = true;
        public string Target { get; set; } = folder.LocalPath;
        public TextBlock? TargetLabel { get; set; }
    }

    private readonly List<Entry> _entries;
    public DatabaseMaintenance.FolderChoice[] Selections =>
        _entries.Select(e => new DatabaseMaintenance.FolderChoice(
            e.Folder.Id, e.Folder.LocalPath, e.Folder.CanonicalPath,
            e.Target, e.Include)).ToArray();

    public DatabaseRestoreFoldersWindow(
        IReadOnlyList<DatabaseMaintenance.FolderInfo> folders)
    {
        _entries = folders.Select(f => new Entry(f)).ToList();
        Title = "PCasa – " + UiLanguage.T("Verzeichnisse wiederherstellen");
        Width = 980;
        Height = Math.Min(630, SystemParameters.WorkArea.Height - 70);
        MinWidth = 780;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        var body = new DockPanel { Margin = new Thickness(15) };
        Content = body;
        var heading = new TextBlock
        {
            Text = UiLanguage.T("Verzeichnisse aus der Sicherung auswählen. " +
                "Für einen neuen Zielpfad auf „…“ klicken; die Pfade sind nicht editierbar."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 2, 4, 15)
        };
        DockPanel.SetDock(heading, Dock.Top);
        body.Children.Add(heading);
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = UiLanguage.T("Abbrechen"),
            MinWidth = 100, Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var accept = new Button { Content = UiLanguage.T("Weiter"),
            MinWidth = 100, Height = 32, Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true };
        accept.Click += (_, _) =>
        {
            if (!Selections.Any(x => x.Include))
            {
                MessageBox.Show(this,
                    UiLanguage.T("Bitte mindestens ein Verzeichnis auswählen."),
                    "PCasa", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        };
        bottom.Children.Add(cancel);
        bottom.Children.Add(accept);
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        var scroller = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        body.Children.Add(scroller);
        var items = new StackPanel();
        scroller.Content = items;
        foreach (var entry in _entries)
            AddEntry(items, entry);
    }

    private void AddEntry(Panel items, Entry entry)
    {
        var panel = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(5, 9, 5, 9)
        };
        panel.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        items.Children.Add(panel);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(41) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(83) });
        panel.Child = row;
        var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 5, 0, 0) };
        row.Children.Add(check);
        var pathBlock = new StackPanel();
        Grid.SetColumn(pathBlock, 1);
        row.Children.Add(pathBlock);
        var old = new TextBlock { Text = entry.Folder.LocalPath,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Folder.LocalPath };
        pathBlock.Children.Add(old);
        var target = new TextBlock
        {
            Text = "→  " + entry.Target,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.Target,
            Margin = new Thickness(0, 5, 0, 0)
        };
        entry.TargetLabel = target;
        pathBlock.Children.Add(target);
        var browse = new Button { Content = "…", Width = 35, Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiLanguage.T("Neues Zielverzeichnis mit der Maus auswählen") };
        Grid.SetColumn(browse, 2);
        row.Children.Add(browse);
        var original = new Button { Content = UiLanguage.T("Original"),
            Width = 73, Height = 30,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = UiLanguage.T("Ursprünglichen Pfad wiederherstellen") };
        Grid.SetColumn(original, 3);
        row.Children.Add(original);
        check.Checked += (_, _) => {
            entry.Include = true;
            browse.IsEnabled = original.IsEnabled = true;
        };
        check.Unchecked += (_, _) => {
            entry.Include = false;
            browse.IsEnabled = original.IsEnabled = false;
        };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = UiLanguage.T("Neues Zielverzeichnis auswählen"),
                Multiselect = false
            };
            if (Directory.Exists(entry.Target))
                dialog.InitialDirectory = entry.Target;
            if (dialog.ShowDialog(this) == true)
            {
                entry.Target = dialog.FolderName;
                target.Text = "→  " + entry.Target;
                target.ToolTip = entry.Target;
            }
        };
        original.Click += (_, _) =>
        {
            entry.Target = entry.Folder.LocalPath;
            target.Text = "→  " + entry.Target;
            target.ToolTip = entry.Target;
        };
    }
}
