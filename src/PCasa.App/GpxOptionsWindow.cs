using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PCasa.App.Services;

namespace PCasa.App;

// One settings owner, no metadata or track files are copied into PCasa.
internal sealed class GpxOptionsWindow : Window
{
    private readonly TextBox _directory = new() { MinWidth = 310 };
    private readonly TextBox _offset = new() { Width = 100 };
    private readonly TextBox _gap = new() { Width = 100 };
    private readonly ComboBox _timeZone = new() { MinWidth = 310 };
    public GpxOptions? Result { get; private set; }

    internal GpxOptionsWindow(Window owner, GpxOptions settings)
    {
        Owner = owner;
        Title = "PCasa – GPX";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");
        bool english = UiLanguage.Current.Code == "en";
        var root = new StackPanel { Margin = new Thickness(16) };
        Content = root;

        root.Children.Add(new TextBlock { Text = english ? "GPX directory" : "GPX-Verzeichnis" });
        var directoryRow = new DockPanel { Margin = new Thickness(0, 5, 0, 12) };
        var browse = new Button { Content = english ? "Browse…" : "Durchsuchen…",
            Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
        DockPanel.SetDock(browse, Dock.Right);
        directoryRow.Children.Add(browse);
        _directory.Text = settings.DirectoryPath;
        directoryRow.Children.Add(_directory);
        root.Children.Add(directoryRow);
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = english ? "Choose GPX folder" : "GPX-Verzeichnis auswählen",
                InitialDirectory = Directory.Exists(_directory.Text) ? _directory.Text : PcasaDataPaths.SharedDataRoot };
            if (picker.ShowDialog(this) == true) _directory.Text = picker.FolderName;
        };

        root.Children.Add(new TextBlock {
            Text = english ? "Camera clock correction (seconds; positive if camera is ahead)" :
                "Zeitkorrektur der Kamera (Sekunden; positiv, wenn die Kamera vorgeht)" });
        _offset.Text = settings.CameraOffsetSeconds.ToString(CultureInfo.CurrentCulture);
        _offset.Margin = new Thickness(0, 5, 0, 12);
        _offset.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_offset);

        root.Children.Add(new TextBlock {
            Text = english ? "Camera time zone" : "Zeitzone der Kamera" });
        _timeZone.DisplayMemberPath = "DisplayName";
        _timeZone.SelectedValuePath = "Id";
        _timeZone.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        _timeZone.SelectedValue = settings.CameraTimeZoneId;
        _timeZone.Margin = new Thickness(0, 5, 0, 12);
        root.Children.Add(_timeZone);

        root.Children.Add(new TextBlock {
            Text = english ? "Maximum gap for interpolation (seconds)" :
                "Maximale Zeitlücke für Interpolation (Sekunden)" });
        _gap.Text = settings.MaximumGapSeconds.ToString(CultureInfo.CurrentCulture);
        _gap.HorizontalAlignment = HorizontalAlignment.Left;
        _gap.Margin = new Thickness(0, 5, 0, 14);
        root.Children.Add(_gap);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = english ? "Cancel" : "Abbrechen",
            MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; };
        ok.Click += (_, _) =>
        {
            if (!double.TryParse(_offset.Text, NumberStyles.Float,
                    CultureInfo.CurrentCulture, out var seconds) ||
                !int.TryParse(_gap.Text, NumberStyles.Integer,
                    CultureInfo.CurrentCulture, out var gap))
            {
                PcasaDialog.Show(this, english ? "Invalid number." : "Ungültige Zahl.",
                    "GPX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var next = new GpxOptions
            {
                DirectoryPath = _directory.Text.Trim(),
                CameraOffsetSeconds = seconds,
                CameraTimeZoneId = (string?)_timeZone.SelectedValue ?? TimeZoneInfo.Local.Id,
                MaximumGapSeconds = gap
            };
            if (!next.IsValid())
            {
                PcasaDialog.Show(this, english ? "Invalid GPX options." : "Ungültige GPX-Einstellungen.",
                    "GPX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try { Directory.CreateDirectory(next.DirectoryPath); next.Save(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PcasaDialog.Show(this, ex.Message, "GPX", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Result = next;
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
    }
}
