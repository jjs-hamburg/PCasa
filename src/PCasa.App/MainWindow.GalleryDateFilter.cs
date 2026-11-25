using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    private DateTime? _galleryDateFrom, _galleryDateUntil;
    private GalleryDateParts _galleryDateStartParts = new(DateTime.Today.Year);
    private GalleryDateParts? _galleryDateEndParts;
    private GallerySortField _galleryDateField = GallerySortField.Captured;
    private bool DateFilterActive => _galleryDateFrom.HasValue;

    private async Task OpenGalleryDateFilterAsync()
    {
        var dialog = new GalleryDateFilterWindow(_galleryDateField,
            DateFilterActive ? _galleryDateStartParts : new GalleryDateParts(DateTime.Today.Year),
            DateFilterActive ? _galleryDateEndParts : null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            SyncGalleryAttributeFilterUi();
            return;
        }
        _galleryDateStartParts = dialog.Start;
        _galleryDateEndParts = dialog.End;
        var bounds = dialog.Start.Bounds(dialog.End);
        await ApplyGalleryDateRangeAsync(dialog.Field, bounds.From, bounds.Until);
    }

    private Task ApplyGalleryDateRangeAsync(GallerySortField field, DateTime from, DateTime? until)
    {
        _galleryDateField = field;
        _galleryDateFrom = from.Date;
        _galleryDateUntil = (until ?? from).Date;
        return ChangeGalleryAttributeFilterAsync("Date", false, force: true);
    }
}

internal sealed class GalleryDateFilterWindow : Window
{
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo(
        UiLanguage.Current.Code == "en" ? "en-GB" : "de-DE");
    private Popup? _popup;
    private GalleryDateParts _start, _end;
    private readonly CheckBox _hasUntil;
    private readonly TextBlock _fromLabel;
    private readonly StackPanel _endRow;
    private readonly Button[] _startButtons, _endButtons;
    private readonly Button _ok;
    public GallerySortField Field { get; private set; }
    public GalleryDateParts Start => _start;
    public GalleryDateParts? End => _hasUntil.IsChecked == true ? _end : null;

    public GalleryDateFilterWindow(GallerySortField field, GalleryDateParts start, GalleryDateParts? end)
    {
        Field = field;
        _start = start;
        _end = end ?? start;
        Title = UiLanguage.T("Datumsfilter");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("PcasaPanelBrush") as Brush ?? Brushes.DimGray;
        Foreground = Application.Current.TryFindResource("PcasaTextBrush") as Brush ?? Brushes.White;
        var root = new StackPanel { Margin = new Thickness(12) };
        Content = root;
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label) in new[] {
            (GallerySortField.Captured, "Aufnahmedatum"),
            (GallerySortField.Created, "Erstellungsdatum"),
            (GallerySortField.Modified, "Änderungsdatum") })
        {
            var radio = new RadioButton { Content = UiLanguage.T(label), IsChecked = field == kind,
                GroupName = "DateField", Margin = new Thickness(0,0,12,0) };
            radio.Checked += (_, _) => Field = kind;
            kinds.Children.Add(radio);
        }
        root.Children.Add(kinds);
        var dates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,10,0,0) };
        _fromLabel = new TextBlock { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        dates.Children.Add(_fromLabel);
        _startButtons = BuildFields(dates, false);
        _hasUntil = new CheckBox { Content = UiLanguage.T("Bis"), IsChecked = end.HasValue,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0,0,0) };
        dates.Children.Add(_hasUntil);
        root.Children.Add(dates);
        _endRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,5,0,0) };
        _endRow.Children.Add(new TextBlock { Text = UiLanguage.T("Bis"), Width = 32,
            VerticalAlignment = VerticalAlignment.Center });
        _endButtons = BuildFields(_endRow, true);
        root.Children.Add(_endRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        actions.Children.Add(new Button { Content = UiLanguage.T("Abbrechen"), Width = 98,
            Height = 30, IsCancel = true, Margin = new Thickness(0,0,8,0) });
        _ok = new Button { Content = UiLanguage.T("OK"), Width = 76, Height = 30, IsDefault = true };
        _ok.Click += (_, _) => { if (_ok.IsEnabled) DialogResult = true; };
        actions.Children.Add(_ok);
        root.Children.Add(actions);
        _hasUntil.Checked += (_, _) => { _end = _start; Sync(); };
        _hasUntil.Unchecked += (_, _) => Sync();
        Closed += (_, _) => { if (_popup is { } popup) popup.IsOpen = false; };
        Sync();
    }

    private Button[] BuildFields(StackPanel row, bool end)
    {
        var labels = new[] { "Tag", "Monat", "Jahr" };
        var widths = new[] { 55d, 79d, 78d };
        var buttons = new Button[3];
        for (var i = 0; i < buttons.Length; i++)
        {
            var field = i;
            var column = new StackPanel { Margin = new Thickness(0,0,4,0) };
            if (!end)
                column.Children.Add(new TextBlock { Text = UiLanguage.T(labels[i]),
                    HorizontalAlignment = HorizontalAlignment.Center });
            var button = new Button { Width = widths[i], Height = 28 };
            button.Click += (_, _) => OpenChoices(button, field, end);
            buttons[i] = button;
            column.Children.Add(button);
            row.Children.Add(column);
        }
        return buttons;
    }

    private void Sync()
    {
        void ShowParts(Button[] buttons, GalleryDateParts value)
        {
            buttons[0].Content = value.Day?.ToString("00") ?? "–";
            buttons[0].IsEnabled = value.Month.HasValue;
            buttons[1].Content = value.Month is int m
                ? _culture.DateTimeFormat.GetAbbreviatedMonthName(m) : "–";
            buttons[2].Content = value.Year.ToString(_culture);
        }
        ShowParts(_startButtons, _start);
        ShowParts(_endButtons, _end);
        var range = _hasUntil.IsChecked == true;
        _fromLabel.Text = range ? UiLanguage.T("Von") : "";
        _endRow.Visibility = range ? Visibility.Visible : Visibility.Collapsed;
        _ok.IsEnabled = !range || _start.FirstDay <= _end.LastDay;
    }

    private void OpenChoices(Button anchor, int field, bool end)
    {
        if (_popup is { } old) old.IsOpen = false;
        var value = end ? _end : _start;
        var panel = new StackPanel { Width = 256, Margin = new Thickness(6) };
        var border = new Border { Child = panel, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PcasaPanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, Child = border };
        _popup = popup;
        popup.Closed += (_, _) => { if (ReferenceEquals(_popup, popup)) _popup = null; };
        Button Choice(string label, Action action, bool selected = false)
        {
            var button = new Button { Content = label, Height = 28, Margin = new Thickness(1),
                FontWeight = selected ? FontWeights.Bold : FontWeights.Normal };
            button.Click += (_, _) => action();
            return button;
        }
        void Pick(GalleryDateParts chosen)
        {
            if (end) _end = chosen; else _start = chosen;
            // Commit on every selection, including a year; no second click.
            popup.IsOpen = false;
            Sync();
        }
        if (field == 0)
        {
            var days = new UniformGrid { Columns = 7 };
            days.Children.Add(Choice("–", () => Pick(value.WithDay(null)), !value.Day.HasValue));
            for (int i = 1; i <= DateTime.DaysInMonth(value.Year, value.Month!.Value); i++)
            {
                var day = i;
                days.Children.Add(Choice(day.ToString("00"), () => Pick(value.WithDay(day)), value.Day == day));
            }
            panel.Children.Add(days);
        }
        else if (field == 1)
        {
            var months = new UniformGrid { Columns = 3 };
            months.Children.Add(Choice("–", () => Pick(value.WithMonth(null)), !value.Month.HasValue));
            for (int i = 1; i <= 12; i++)
            {
                var month = i;
                months.Children.Add(Choice(_culture.DateTimeFormat.GetAbbreviatedMonthName(month),
                    () => Pick(value.WithMonth(month)), value.Month == month));
            }
            panel.Children.Add(months);
        }
        else
        {
            var century = value.Year / 100 * 100;
            var decade = value.Year / 10 * 10;
            void DrawYears()
            {
                panel.Children.Clear();
                var header = new DockPanel { LastChildFill = true };
                var prev = Choice("‹", () => { century = Math.Max(0, century-100); decade = century; DrawYears(); });
                var next = Choice("›", () => { century = Math.Min(9900, century+100); decade = century; DrawYears(); });
                DockPanel.SetDock(prev, Dock.Left); DockPanel.SetDock(next, Dock.Right);
                prev.Width = next.Width = 28;
                header.Children.Add(prev); header.Children.Add(next);
                header.Children.Add(new TextBlock { Text = century + " – " + (century+99),
                    TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(header);
                var decades = new UniformGrid { Columns = 5 };
                for (int i = 0; i < 10; i++)
                {
                    var d = century + i*10;
                    decades.Children.Add(Choice(d.ToString(), () => { decade = d; DrawYears(); }, decade == d));
                }
                panel.Children.Add(decades);
                var years = new UniformGrid { Columns = 5 };
                for (int i = Math.Max(1, decade); i <= Math.Min(9999, decade+9); i++)
                {
                    var year = i;
                    years.Children.Add(Choice(year.ToString(), () => Pick(value.WithYear(year)), value.Year == year));
                }
                panel.Children.Add(years);
            }
            DrawYears();
        }
        popup.IsOpen = true;
    }
}
