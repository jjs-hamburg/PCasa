using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PCasa.App.Services;

namespace PCasa.App;

internal sealed record GpxImportPreviewRow(long Id, string Name, double? Latitude,
    double? Longitude, string Status, bool HasGps);

internal sealed class GpxImportPreviewWindow : Window
{
    private readonly CheckBox _replace = new();
    private readonly TextBox _offset = new() { Width = 95 };
    private readonly TextBlock _offsetReadout = new() { Margin = new Thickness(8,0,0,0),
        VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heading = new() { FontSize = 15,
        FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,8) };
    private readonly DataGrid _grid = new();
    private readonly Button _apply = new();
    private readonly Func<double, IReadOnlyList<GpxImportPreviewRow>> _recalculate;
    private IReadOnlyList<GpxImportPreviewRow> _rows;

    internal bool ReplaceExisting => _replace.IsChecked == true;
    internal IReadOnlyList<GpxImportPreviewRow> Rows => _rows;

    internal GpxImportPreviewWindow(Window owner,
        IReadOnlyList<GpxImportPreviewRow> rows,
        Func<double, IReadOnlyList<GpxImportPreviewRow>> recalculate,
        double initialOffsetSeconds, DateTimeOffset first, DateTimeOffset last)
    {
        Owner = owner;
        _rows = rows;
        _recalculate = recalculate;
        var en = UiLanguage.Current.Code == "en";
        Title = en ? "PCasa – GPX assignment preview" : "PCasa – GPX-Zuordnung prüfen";
        Width = 800;
        Height = 570;
        MinWidth = 630;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        header.Children.Add(_heading);
        header.Children.Add(new TextBlock
        {
            Text = (en ? "GPX UTC: " : "GPX UTC: ") +
                first.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                " – " +
                last.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Margin = new Thickness(0,0,0,7)
        });
        var offsetRow = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0,0,0,9) };
        offsetRow.Children.Add(new TextBlock
        {
            Text = en ? "Camera clock correction (seconds): " :
                "Kamera-Zeitkorrektur (Sekunden): ",
            VerticalAlignment = VerticalAlignment.Center
        });
        _offset.Text = initialOffsetSeconds.ToString(CultureInfo.CurrentCulture);
        _offset.VerticalContentAlignment = VerticalAlignment.Center;
        offsetRow.Children.Add(_offset);
        offsetRow.Children.Add(_offsetReadout);
        offsetRow.Children.Add(new TextBlock
        {
            Text = en ? "  + camera ahead, − camera behind" :
                "  + Kamera geht vor, − Kamera geht nach",
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6,0,0,0)
        });
        header.Children.Add(offsetRow);

        var footer = new StackPanel { Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        _replace.Content = en ? "Overwrite existing GPS coordinates" :
            "Vorhandene GPS-Koordinaten überschreiben";
        _replace.IsChecked = false;
        footer.Children.Add(_replace);
        var controls = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0,10,0,0) };
        var cancel = new Button { Content = en ? "Cancel" : "Abbrechen",
            MinWidth = 110, IsCancel = true, Margin = new Thickness(0,0,8,0) };
        _apply.Content = en ? "Write GPS" : "GPS schreiben";
        _apply.MinWidth = 110;
        _apply.IsDefault = true;
        cancel.Click += (_,_) => DialogResult = false;
        _apply.Click += (_,_) =>
        {
            if (Rows.Any(x => x.Latitude.HasValue && x.Longitude.HasValue &&
                (!x.HasGps || ReplaceExisting))) DialogResult = true;
        };
        _replace.Checked += (_,_) => UpdateApply();
        _replace.Unchecked += (_,_) => UpdateApply();
        controls.Children.Add(cancel);
        controls.Children.Add(_apply);
        footer.Children.Add(controls);

        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "File" : "Datei",
            Binding = new Binding(nameof(GpxImportPreviewRow.Name)),
            Width = new DataGridLength(2,DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "Result" : "Ergebnis",
            Binding = new Binding(nameof(GpxImportPreviewRow.Status)),
            Width = new DataGridLength(1.7,DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "Latitude" : "Breitengrad",
            Binding = new Binding(nameof(GpxImportPreviewRow.Latitude)) { StringFormat = "F6" },
            Width = new DataGridLength(1,DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "Longitude" : "Längengrad",
            Binding = new Binding(nameof(GpxImportPreviewRow.Longitude)) { StringFormat = "F6" },
            Width = new DataGridLength(1,DataGridLengthUnitType.Star)
        });
        root.Children.Add(_grid);
        _offset.TextChanged += (_,_) =>
        {
            if (!double.TryParse(_offset.Text, NumberStyles.Float,
                    CultureInfo.CurrentCulture, out var seconds) ||
                !double.IsFinite(seconds) || Math.Abs(seconds) > 86400)
            {
                _offsetReadout.Text = "";
                _apply.IsEnabled = false;
                return;
            }
            _offsetReadout.Text = FormatOffset(seconds);
            try { SetRows(_recalculate(seconds)); }
            catch (ArgumentException) { _apply.IsEnabled = false; }
            catch (InvalidOperationException) { _apply.IsEnabled = false; }
        };
        _offsetReadout.Text = FormatOffset(initialOffsetSeconds);
        SetRows(rows);
    }

    private static string FormatOffset(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Abs(seconds));
        return (seconds < 0 ? "−" : "+") +
            $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }

    private void SetRows(IReadOnlyList<GpxImportPreviewRow> rows)
    {
        _rows = rows;
        _grid.ItemsSource = rows;
        var en = UiLanguage.Current.Code == "en";
        var matched = rows.Count(x => x.Latitude.HasValue && x.Longitude.HasValue);
        var existing = rows.Count(x => x.HasGps);
        var failed = rows.Count - matched;
        _heading.Text = en
            ? $"{rows.Count} photos · {matched} matched · {existing} with GPS · {failed} unmatched"
            : $"{rows.Count} Fotos · {matched} zuordenbar · {existing} mit GPS · {failed} ohne Zuordnung";
        UpdateApply();
    }

    private void UpdateApply() =>
        _apply.IsEnabled = Rows.Any(x =>
            x.Latitude.HasValue && x.Longitude.HasValue &&
            (!x.HasGps || ReplaceExisting));
}
