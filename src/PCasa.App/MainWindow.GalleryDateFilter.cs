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
    private GallerySortField _galleryDateField = GallerySortField.Captured;
    private bool DateFilterActive => _galleryDateFrom.HasValue;

    private async Task OpenGalleryDateFilterAsync()
    {
        var dialog = new GalleryDateFilterWindow(_galleryDateField,
            _galleryDateFrom ?? DateTime.Today, _galleryDateUntil) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            SyncGalleryAttributeFilterUi();
            return;
        }
        await ApplyGalleryDateRangeAsync(dialog.Field, dialog.From, dialog.Until);
    }

    private Task ApplyGalleryDateRangeAsync(GallerySortField field, DateTime from, DateTime? until)
    {
        _galleryDateField = field;
        _galleryDateFrom = from.Date;
        _galleryDateUntil = until?.Date;
        return ChangeGalleryAttributeFilterAsync("Date", false, force: true);
    }
}

internal sealed class GalleryDateFilterWindow : Window
{
    private readonly CultureInfo _culture = CultureInfo.GetCultureInfo(
        UiLanguage.Current.Code == "en" ? "en-GB" : "de-DE");
    private Popup? _popup;
    private DateTime _from, _until;
    private readonly CheckBox _hasUntil;
    private readonly Button _fromButton, _untilButton, _ok;
    public GallerySortField Field { get; private set; }
    public DateTime From => _from;
    public DateTime? Until => _hasUntil.IsChecked == true ? _until : null;

    public GalleryDateFilterWindow(GallerySortField field, DateTime from, DateTime? until)
    {
        Field = field;
        _from = from.Date;
        _until = until?.Date ?? (from > DateTime.Today ? from.Date : DateTime.Today);
        Title = UiLanguage.T("Datumsfilter");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Application.Current.TryFindResource("PcasaPanelBrush") as Brush ?? Brushes.DimGray;
        Foreground = Application.Current.TryFindResource("PcasaTextBrush") as Brush ?? Brushes.White;
        var root = new StackPanel { Margin = new Thickness(14) };
        Content = root;
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (kind, label) in new[] {
            (GallerySortField.Captured, "Aufnahmedatum"),
            (GallerySortField.Created, "Erstellungsdatum"),
            (GallerySortField.Modified, "Änderungsdatum") })
        {
            var radio = new RadioButton
            {
                Content = UiLanguage.T(label), IsChecked = field == kind,
                GroupName = "DateField", Margin = new Thickness(0,0,12,0)
            };
            radio.Checked += (_, _) => Field = kind;
            kinds.Children.Add(radio);
        }
        root.Children.Add(kinds);
        var dates = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,12) };
        dates.Children.Add(new TextBlock { Text = UiLanguage.T("Von"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,6,0) });
        _fromButton = new Button { Content = _from.ToString("d", _culture), Width = 108, Height = 28 };
        _untilButton = new Button { Content = _until.ToString("d", _culture), Width = 108, Height = 28,
            Visibility = until.HasValue ? Visibility.Visible : Visibility.Collapsed };
        _hasUntil = new CheckBox { Content = UiLanguage.T("Bis"), IsChecked = until.HasValue,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14,0,6,0) };
        dates.Children.Add(_fromButton);
        dates.Children.Add(_hasUntil);
        dates.Children.Add(_untilButton);
        root.Children.Add(dates);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = UiLanguage.T("Abbrechen"), Width = 98, Height = 30, IsCancel = true, Margin = new Thickness(0,0,8,0) };
        _ok = new Button { Content = UiLanguage.T("OK"), Width = 76, Height = 30, IsDefault = true };
        _ok.Click += (_, _) => { if (Until is null || Until >= From) DialogResult = true; };
        actions.Children.Add(cancel);
        actions.Children.Add(_ok);
        root.Children.Add(actions);
        _fromButton.Click += (_, _) => OpenCalendar(_fromButton, _from, value => { _from = value; ValidateRange(); });
        _untilButton.Click += (_, _) => OpenCalendar(_untilButton, _until, value => { _until = value; ValidateRange(); });
        _hasUntil.Checked += (_, _) => { _untilButton.Visibility = Visibility.Visible; ValidateRange(); };
        _hasUntil.Unchecked += (_, _) => { _untilButton.Visibility = Visibility.Collapsed; ValidateRange(); };
        Closed += (_, _) => { if (_popup is { } popup) popup.IsOpen = false; };
        ValidateRange();
    }

    private void ValidateRange() => _ok.IsEnabled = Until is null || Until >= From;

    private void OpenCalendar(Button anchor, DateTime date, Action<DateTime> selected)
    {
        if (_popup is { } old) old.IsOpen = false;
        var year = date.Year;
        var month = date.Month;
        var decade = Math.Max(1, year / 10 * 10);
        var century = year / 100 * 100;
        var mode = 0; // days, months, years
        var culture = _culture;
        var panel = new StackPanel { Width = 288, Margin = new Thickness(8) };
        var border = new Border { Child = panel, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PcasaTopBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, Child = border
        };
        _popup = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup)) _popup = null;
        };

        Button Choice(string text, Action action, bool selected = false, bool enabled = true)
        {
            var b = new Button
            {
                Content = text, MinHeight = 29, Margin = new Thickness(1),
                Padding = new Thickness(2), IsEnabled = enabled,
                FontWeight = selected ? FontWeights.Bold : FontWeights.Normal
            };
            if (selected) b.BorderThickness = new Thickness(2);
            b.Click += (_, _) => action();
            return b;
        }

        void Move(int direction)
        {
            if (mode == 2)
            {
                century = Math.Clamp(century + direction * 100, 0, 9900);
                decade = Math.Max(1, century);
            }
            else if (mode == 1) year = Math.Clamp(year + direction, 1, 9999);
            else
            {
                var absolute = Math.Clamp((year - 1) * 12 + month - 1 + direction, 0, 9999 * 12 - 1);
                year = absolute / 12 + 1;
                month = absolute % 12 + 1;
            }
            Draw();
        }

        void Pick(int day, bool close = true)
        {
            date = new DateTime(year, month, day);
            selected(date);
            anchor.Content = date.ToString("d", culture);
            if (close) popup.IsOpen = false;
        }

        void Draw()
        {
            panel.Children.Clear();
            var header = new DockPanel { LastChildFill = true };
            var prev = Choice("‹", () => Move(-1));
            var next = Choice("›", () => Move(1));
            prev.Width = next.Width = 28;
            DockPanel.SetDock(prev, Dock.Left);
            DockPanel.SetDock(next, Dock.Right);
            header.Children.Add(prev);
            header.Children.Add(next);
            var headings = new UniformGrid { Columns = 2 };
            headings.Children.Add(Choice(culture.DateTimeFormat.GetMonthName(month),
                () => { mode = mode == 1 ? 0 : 1; Draw(); }, mode == 1));
            headings.Children.Add(Choice(year.ToString(culture), () =>
            {
                mode = mode == 2 ? 0 : 2;
                decade = Math.Max(1, year / 10 * 10);
                century = year / 100 * 100;
                Draw();
            }, mode == 2));
            header.Children.Add(headings);
            panel.Children.Add(header);
            if (mode == 1)
            {
                var months = new UniformGrid { Columns = 3 };
                for (var i = 1; i <= 12; i++)
                {
                    var value = i;
                    months.Children.Add(Choice(culture.DateTimeFormat.GetAbbreviatedMonthName(value),
                        () => { month = value; mode = 0; Pick(Math.Min(date.Day, DateTime.DaysInMonth(year, month)), false); Draw(); }, value == month));
                }
                panel.Children.Add(months);
                return;
            }
            if (mode == 2)
            {
                var decades = new UniformGrid { Columns = 5 };
                for (var i = 0; i < 10; i++)
                {
                    var start = century + i * 10;
                    var value = Math.Max(1, start);
                    decades.Children.Add(Choice(start.ToString(culture),
                        () => { decade = value; Draw(); }, decade == value));
                }
                panel.Children.Add(decades);
                panel.Children.Add(new Separator { Margin = new Thickness(0, 3, 0, 3) });
                var years = new UniformGrid { Columns = 5 };
                var first = decade == 1 ? 1 : decade;
                var last = Math.Min(9999, decade == 1 ? 9 : decade + 9);
                for (var i = first; i <= last; i++)
                {
                    var value = i;
                    years.Children.Add(Choice(value.ToString(culture),
                        () => { year = value; mode = 0; Pick(Math.Min(date.Day, DateTime.DaysInMonth(year, month)), false); Draw(); }, value == year));
                }
                panel.Children.Add(years);
                return;
            }
            var days = new UniformGrid { Columns = 7 };
            for (var i = 0; i < 7; i++)
            {
                var weekday = (DayOfWeek)((i + 1) % 7);
                days.Children.Add(new TextBlock
                {
                    Text = culture.DateTimeFormat.GetShortestDayName(weekday),
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(1, 5, 1, 3)
                });
            }
            var offset = ((int)new DateTime(year, month, 1).DayOfWeek + 6) % 7;
            for (var i = 0; i < offset; i++) days.Children.Add(new Border());
            for (var i = 1; i <= DateTime.DaysInMonth(year, month); i++)
            {
                var value = i;
                var candidate = new DateTime(year, month, value);
                days.Children.Add(Choice(value.ToString(culture), () => Pick(value),
                    candidate == date.Date));
            }
            panel.Children.Add(days);
        }

        Draw();
        popup.IsOpen = true;
    }
}
