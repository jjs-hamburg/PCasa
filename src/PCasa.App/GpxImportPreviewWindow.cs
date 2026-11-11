using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PCasa.App.Services;

namespace PCasa.App;

internal sealed record GpxImportPreviewRow(long Id, string Name, double? Latitude,
    double? Longitude, string Status, bool HasGps);

internal sealed class GpxImportPreviewWindow : Window
{
    private readonly CheckBox _replace = new();
    internal bool ReplaceExisting => _replace.IsChecked == true;

    internal GpxImportPreviewWindow(Window owner, IReadOnlyList<GpxImportPreviewRow> rows)
    {
        Owner = owner;
        var en = UiLanguage.Current.Code == "en";
        Title = en ? "PCasa – GPX assignment preview" : "PCasa – GPX-Zuordnung prüfen";
        Width = 770;
        Height = 550;
        MinWidth = 600;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"PcasaPanelBrush");
        SetResourceReference(ForegroundProperty,"PcasaTextBrush");
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        int matched = rows.Count(x => x.Latitude.HasValue);
        int existing = rows.Count(x => x.HasGps);
        var heading = new TextBlock
        {
            Text = en
                ? $"{rows.Count} photos · {matched} matched · {existing} with GPS"
                : $"{rows.Count} Fotos · {matched} zugeordnet · {existing} bereits mit GPS",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0,0,0,9)
        };
        DockPanel.SetDock(heading,Dock.Top);
        root.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(footer,Dock.Bottom);
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
        var apply = new Button { Content = en ? "Write GPS" : "GPS schreiben",
            MinWidth = 110, IsDefault = true };
        apply.IsEnabled = matched > 0;
        cancel.Click += (_,_) => DialogResult = false;
        apply.Click += (_,_) =>
        {
            if (rows.All(x => !x.Latitude.HasValue ||
                (x.HasGps && !ReplaceExisting))) return;
            DialogResult = true;
        };
        _replace.Checked += (_,_) => apply.IsEnabled = matched > 0;
        _replace.Unchecked += (_,_) => apply.IsEnabled =
            rows.Any(x => x.Latitude.HasValue && !x.HasGps);
        apply.IsEnabled = rows.Any(x => x.Latitude.HasValue && !x.HasGps);
        controls.Children.Add(cancel);
        controls.Children.Add(apply);
        footer.Children.Add(controls);
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            ItemsSource = rows,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None
        };
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "File" : "Datei",
            Binding = new System.Windows.Data.Binding(nameof(GpxImportPreviewRow.Name)),
            Width = new DataGridLength(2,DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = en ? "Result" : "Ergebnis",
            Binding = new System.Windows.Data.Binding(nameof(GpxImportPreviewRow.Status)),
            Width = new DataGridLength(1.4,DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Latitude",
            Binding = new System.Windows.Data.Binding(nameof(GpxImportPreviewRow.Latitude))
                { StringFormat = "F6" },
            Width = new DataGridLength(1,DataGridLengthUnitType.Star)
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Longitude",
            Binding = new System.Windows.Data.Binding(nameof(GpxImportPreviewRow.Longitude))
                { StringFormat = "F6" },
            Width = new DataGridLength(1,DataGridLengthUnitType.Star)
        });
        root.Children.Add(grid);
    }
}
