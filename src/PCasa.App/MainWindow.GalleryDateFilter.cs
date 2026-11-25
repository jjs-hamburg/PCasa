using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // Date filters refine the existing local gallery, never schedule a scan.
    // They are session-only so an old date range cannot hide a new library.
    private DateTime? _galleryDateFrom, _galleryDateUntil;
    private bool _syncingGalleryDate;
    private Popup? _galleryDatePopup;
    private bool DateFilterActive => _galleryDateFrom.HasValue;

    private CultureInfo GalleryDateCulture => CultureInfo.GetCultureInfo(
        UiLanguage.Current.Code == "en" ? "en-GB" : "de-DE");

    private void SyncGalleryDateFilterUi()
    {
        if (GalleryDateFromButton is null || GalleryDateUntilButton is null ||
            GalleryDateUntilCheck is null || GalleryDateClearButton is null) return;
        _syncingGalleryDate = true;
        try
        {
            GalleryDateFromButton.Content = _galleryDateFrom?.ToString("d", GalleryDateCulture) ?? "–";
            GalleryDateUntilButton.Content = _galleryDateUntil?.ToString("d", GalleryDateCulture) ?? "–";
            GalleryDateUntilCheck.IsEnabled = DateFilterActive;
            GalleryDateUntilCheck.IsChecked = _galleryDateUntil.HasValue;
            GalleryDateUntilButton.Visibility = _galleryDateUntil.HasValue
                ? Visibility.Visible : Visibility.Collapsed;
            GalleryDateClearButton.IsEnabled = DateFilterActive;
        }
        finally { _syncingGalleryDate = false; }
    }

    private async void GalleryDateUntil_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingGalleryDate || _galleryDateFrom is not DateTime from ||
            GalleryDateUntilCheck is null) return;
        _galleryDateUntil = GalleryDateUntilCheck.IsChecked == true
            ? (from > DateTime.Today ? from : DateTime.Today) : null;
        await ApplyGalleryDateFilterAsync();
    }

    private async void GalleryDateClear_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryDatePopup is { } popup) popup.IsOpen = false;
        _galleryDateFrom = _galleryDateUntil = null;
        await ApplyGalleryDateFilterAsync();
    }

    private async Task ApplyGalleryDateFilterAsync()
    {
        SyncGalleryDateFilterUi();
        if (!_browseInitialized || _browsePageCache is null) return;
        try
        {
            await ApplyTagFilterAsync(scrollToTop: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PCasaDiagnostics.Write("Gallery.DateFilter", ex); }
    }

    private void GalleryDatePicker_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button anchor) return;
        if (_galleryDatePopup is { } old) old.IsOpen = false;
        var until = ReferenceEquals(anchor, GalleryDateUntilButton);
        var date = (until ? _galleryDateUntil : _galleryDateFrom) ?? DateTime.Today;
        var year = date.Year;
        var month = date.Month;
        var decade = Math.Max(1, year / 10 * 10);
        var century = year / 100 * 100;
        var mode = 0; // days, months, years
        var culture = GalleryDateCulture;
        var panel = new StackPanel { Width = 288, Margin = new Thickness(8) };
        var border = new Border { Child = panel, BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PcasaTopBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true, Child = border
        };
        _galleryDatePopup = popup;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_galleryDatePopup, popup)) _galleryDatePopup = null;
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

        async void Pick(int day)
        {
            var selected = new DateTime(year, month, day);
            popup.IsOpen = false;
            if (until) _galleryDateUntil = selected;
            else
            {
                _galleryDateFrom = selected;
                if (_galleryDateUntil is DateTime end && end < selected)
                    _galleryDateUntil = selected;
            }
            await ApplyGalleryDateFilterAsync();
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
                        () => { month = value; mode = 0; Draw(); }, value == month));
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
                        () => { year = value; mode = 0; Draw(); }, value == year));
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
                    candidate == date.Date,
                    !until || _galleryDateFrom is not DateTime min || candidate >= min));
            }
            panel.Children.Add(days);
        }

        Draw();
        popup.IsOpen = true;
    }
}
