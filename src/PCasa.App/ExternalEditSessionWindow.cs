using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// An explicit handoff session, independent of the lifetime of Topaz or any
/// other external editor (launchers may exit before their UI closes).
/// Only an explicit, confirmed output selection may enter PCasa's P0 import.
/// </summary>
public sealed class ExternalEditSessionWindow : Window
{
    private readonly string _source;
    private readonly string _workDirectory;
    private readonly string _workCopy;
    private readonly long _originalLength;
    private readonly DateTime _originalWriteTime;
    private readonly Func<string[], Task> _import;
    private readonly ListBox _results = new();
    private readonly HashSet<string> _imported = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;

    public ExternalEditSessionWindow(string editorName, string source,
        string workDirectory, string workCopy, Func<string[], Task> import)
    {
        _source = source;
        _workDirectory = workDirectory;
        _workCopy = workCopy;
        _import = import;
        var originalInfo = new FileInfo(_workCopy);
        _originalLength = originalInfo.Length;
        _originalWriteTime = originalInfo.LastWriteTimeUtc;

        Title = UiLanguage.T("Bearbeiten mit …") + " – " + editorName;
        Width = 740;
        Height = 440;
        MinWidth = 540;
        MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(39, 42, 46));
        Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var footer = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        AddButton(footer, "Arbeitsordner öffnen", () =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _workDirectory,
                UseShellExecute = true
            });
        });
        AddButton(footer, "Ergebnisse suchen", RefreshResults);
        AddButton(footer, "Andere Datei auswählen …", () => _ = ChooseOtherAsync());
        AddButton(footer, "Ergebnis übernehmen", () => _ = ImportSelectedAsync());
        AddButton(footer, "Schließen", Close);

        var body = new StackPanel();
        root.Children.Add(body);
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("PCasa hat eine lokale Arbeitskopie geöffnet. Das Original bleibt unverändert."),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Exportiere das Ergebnis in den Arbeitsordner. Danach Ergebnisse suchen und das gewünschte Ergebnis übernehmen. Andere Exportordner sind ebenfalls möglich."),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 12)
        });
        body.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(_source),
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Gefundene Ausgabedateien:"),
            Margin = new Thickness(0, 0, 0, 5)
        });
        _results.SelectionMode = SelectionMode.Extended;
        _results.Background = new SolidColorBrush(Color.FromRgb(55, 58, 62));
        _results.Foreground = Brushes.White;
        _results.MinHeight = 135;
        body.Children.Add(_results);
        body.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Die Arbeitsdateien bleiben erhalten, bis du sie im Arbeitsordner selbst entfernst."),
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 9, 0, 0)
        });
        RefreshResults();
    }

    private static void AddButton(Panel host, string caption, Action click)
    {
        var button = new Button
        {
            Content = UiLanguage.T(caption),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(3, 0, 0, 4)
        };
        button.Click += (_, _) => click();
        host.Children.Add(button);
    }

    private void RefreshResults()
    {
        try
        {
            var available = Directory.EnumerateFiles(_workDirectory)
                .Where(FolderScanner.IsSupportedImagePath)
                .Where(path => !_imported.Contains(path))
                .Where(path =>
                {
                    if (!path.Equals(_workCopy, StringComparison.OrdinalIgnoreCase))
                        return true;
                    var info = new FileInfo(path);
                    return info.Length != _originalLength ||
                        info.LastWriteTimeUtc != _originalWriteTime;
                })
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _results.ItemsSource = available;
            if (available.Length > 0) _results.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, UiLanguage.T("Bearbeiten mit …"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ChooseOtherAsync()
    {
        var picker = new OpenFileDialog
        {
            Title = UiLanguage.T("Exportierte Datei auswählen"),
            InitialDirectory = _workDirectory,
            Filter = "Fotos und Videos|*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp;*.webp;*.heic;*.heif;*.avif;*.jxl;*.gif;*.mp4;*.mov;*.mkv;*.avi;*.m4v;*.wmv;*.mts;*.m2ts;*.webm|Alle Dateien|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) == true)
            await ImportAsync(picker.FileNames);
    }

    private async Task ImportSelectedAsync()
    {
        var selected = _results.SelectedItems.Cast<string>().ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, UiLanguage.T("Bitte eine Ausgabedatei auswählen."),
                UiLanguage.T("Bearbeiten mit …"), MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        await ImportAsync(selected);
    }

    private async Task ImportAsync(string[] paths)
    {
        if (_busy) return;
        try
        {
            var files = paths.Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) return;
            if (files.Any(p => p.Equals(Path.GetFullPath(_source),
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    UiLanguage.T("Das PCasa-Original darf nicht importiert oder überschrieben werden."));
            if (files.Any(p => !FolderScanner.IsSupportedImagePath(p)))
                throw new InvalidDataException(
                    UiLanguage.T("Ein Ausgabeformat wird von PCasa nicht unterstützt."));
            var origin = Path.GetDirectoryName(Path.GetFullPath(_source))!;
            if (files.Any(p => Path.GetDirectoryName(p)!.Equals(
                    origin, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(UiLanguage.T(
                    "Die Ausgabe liegt bereits im Originalordner. Bitte zuerst einen anderen Exportordner wählen."));
            var decision = MessageBox.Show(this,
                UiLanguage.Format(
                    "{0} Datei(en) als neue Bilder/Videos in den PCasa-Ordner übernehmen? Das Original bleibt unverändert.",
                    files.Length),
                UiLanguage.T("Ausgabe übernehmen"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (decision != MessageBoxResult.Yes) return;
            _busy = true;
            await _import(files);
            foreach (var path in files) _imported.Add(path);
            RefreshResults();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, UiLanguage.T("Bearbeiten mit …"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
    }
}
