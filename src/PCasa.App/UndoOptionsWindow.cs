using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

internal sealed class UndoOptionsWindow : Window
{
    private readonly TextBox _path = new() { MinWidth = 320 };
    private readonly TextBox _versions = new() { Width = 90 };
    private readonly TextBox _gigabytes = new() { Width = 90 };
    private readonly TextBox _days = new() { Width = 90 };
    private readonly CheckBox _limitDays = new();
    internal UndoOptions? Result { get; private set; }

    internal UndoOptionsWindow(Window owner, UndoOptions options)
    {
        Owner = owner;
        var en = UiLanguage.Current.Code == "en";
        Title = en ? "PCasa – Undo options" : "PCasa – Undo-Einstellungen";
        Width = 555;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"PcasaPanelBrush");
        SetResourceReference(ForegroundProperty,"PcasaTextBrush");
        var root = new StackPanel { Margin = new Thickness(17) };
        Content = root;
        root.Children.Add(new TextBlock { Text = en ? "Undo directory" : "Undo-Verzeichnis" });
        var pathRow = new DockPanel { Margin = new Thickness(0,5,0,13) };
        var browse = new Button { Content = en ? "Browse…" : "Durchsuchen…",
            Margin = new Thickness(6,0,0,0), Padding = new Thickness(9,3,9,3) };
        DockPanel.SetDock(browse,Dock.Right);
        pathRow.Children.Add(browse);
        _path.Text = options.DirectoryPath;
        pathRow.Children.Add(_path);
        root.Children.Add(pathRow);
        browse.Click += (_,_) =>
        {
            var folder = new OpenFolderDialog { Title = en ? "Undo directory" : "Undo-Verzeichnis",
                InitialDirectory = Directory.Exists(_path.Text) ? _path.Text :
                    PcasaDataPaths.SharedDataRoot };
            if (folder.ShowDialog(this) == true) _path.Text = folder.FolderName;
        };
        root.Children.Add(new TextBlock {
            Text = en ? "Maximum saved states per photo" :
                "Maximale gespeicherte Zustände pro Foto" });
        _versions.Text = options.MaximumVersionsPerImage.ToString(CultureInfo.CurrentCulture);
        _versions.Margin = new Thickness(0,5,0,13);
        _versions.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_versions);
        root.Children.Add(new TextBlock {
            Text = en ? "Maximum total Undo storage (GB)" :
                "Maximaler Speicherplatz für Undo (GB)" });
        _gigabytes.Text = (options.MaximumBytes / 1073741824.0).ToString("0.##",
            CultureInfo.CurrentCulture);
        _gigabytes.Margin = new Thickness(0,5,0,13);
        _gigabytes.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_gigabytes);
        _limitDays.Content = en ? "Limit retention period (days)" :
            "Maximale Aufbewahrungsdauer (Tage)";
        _limitDays.IsChecked = options.MaximumAgeDays.HasValue;
        root.Children.Add(_limitDays);
        _days.Text = (options.MaximumAgeDays ?? 90).ToString(CultureInfo.CurrentCulture);
        _days.Margin = new Thickness(22,5,0,13);
        _days.HorizontalAlignment = HorizontalAlignment.Left;
        _days.IsEnabled = options.MaximumAgeDays.HasValue;
        _limitDays.Checked += (_,_)=>_days.IsEnabled=true;
        _limitDays.Unchecked += (_,_)=>_days.IsEnabled=false;
        root.Children.Add(_days);
        var footer = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = en ? "Cancel" : "Abbrechen",
            MinWidth = 90, IsCancel = true, Margin = new Thickness(0,0,8,0) };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        cancel.Click += (_,_)=>DialogResult=false;
        ok.Click += (_,_)=>
        {
            if (!int.TryParse(_versions.Text,out var versionCount) ||
                !double.TryParse(_gigabytes.Text,NumberStyles.Float,
                    CultureInfo.CurrentCulture,out var gb) ||
                (_limitDays.IsChecked == true &&
                 !int.TryParse(_days.Text,out _)))
            {
                PcasaDialog.Show(this,en?"Invalid number.":"Ungültige Zahl.","Undo",
                    MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            var retention = _limitDays.IsChecked == true ? int.Parse(_days.Text) : (int?)null;
            var bytes = gb is >= 0.001 and <= 90949 ? (long)(gb * 1073741824) : 0;
            var next = new UndoOptions { DirectoryPath = _path.Text.Trim(),
                MaximumVersionsPerImage = versionCount, MaximumBytes = bytes,
                MaximumAgeDays = retention };
            if (!next.IsValid())
            {
                PcasaDialog.Show(this,en?"Invalid Undo options.":"Ungültige Undo-Einstellungen.",
                    "Undo",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            try { Directory.CreateDirectory(next.DirectoryPath); next.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PcasaDialog.Show(this,ex.Message,"Undo",
                    MessageBoxButton.OK,MessageBoxImage.Error);
                return;
            }
            Result=next;
            DialogResult=true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(ok);
        root.Children.Add(footer);
    }
}
