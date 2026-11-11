using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

// Restore candidate is ALWAYS left. The live image stays fixed on the right.
internal sealed class UndoCompareWindow : Window
{
    private readonly IReadOnlyList<UndoVersion> _versions;
    private readonly Image _candidate = new() { Stretch = Stretch.None };
    private readonly Image _current = new() { Stretch = Stretch.None };
    private readonly ScrollViewer _leftScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ScrollViewer _rightScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _description = new();
    private readonly Button _older = new(), _newer = new();
    private readonly ScaleTransform _scaleLeft = new(), _scaleRight = new();
    private bool _synchronizing;
    private int _index;
    private double _zoom = 1;

    internal UndoVersion? SelectedVersion =>
        DialogResult == true ? _versions[_index] : null;

    internal UndoCompareWindow(Window owner, string originalPath,
        IReadOnlyList<UndoVersion> versions)
    {
        Owner = owner;
        _versions = versions.OrderByDescending(v => v.SavedAtUtc).ToArray();
        if (_versions.Count == 0) throw new ArgumentException("No saved versions.");
        var english = UiLanguage.Current.Code == "en";
        Title = (english ? "Restore image – " : "Bild wiederherstellen – ") +
            Path.GetFileName(originalPath);
        Width = Math.Min(1450, SystemParameters.WorkArea.Width - 65);
        Height = Math.Min(920, SystemParameters.WorkArea.Height - 65);
        MinWidth = 740; MinHeight = 470;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty,"PcasaPanelBrush");
        SetResourceReference(ForegroundProperty,"PcasaTextBrush");

        var root = new Grid { Margin = new Thickness(10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1,GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new TextBlock {
            Text = english ? "Restore candidate                         Current image" :
                "Wiederherstellungsversion                         Aktuelles Bild",
            FontSize = 16,FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4,2,0,10)
        };
        root.Children.Add(heading);

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        Grid.SetRow(panes,1);
        root.Children.Add(panes);
        _candidate.LayoutTransform = _scaleLeft;
        _current.LayoutTransform = _scaleRight;
        _leftScroll.Content = _candidate;
        _rightScroll.Content = _current;
        _leftScroll.Background = Brushes.Black;
        _rightScroll.Background = Brushes.Black;
        panes.Children.Add(_leftScroll);
        Grid.SetColumn(_rightScroll,2);
        panes.Children.Add(_rightScroll);
        _leftScroll.ScrollChanged += (_, e) => Synchronize(_leftScroll,_rightScroll,e);
        _rightScroll.ScrollChanged += (_, e) => Synchronize(_rightScroll,_leftScroll,e);

        var footer = new DockPanel { Margin = new Thickness(0,10,0,0) };
        Grid.SetRow(footer,2);
        root.Children.Add(footer);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions,Dock.Right);
        footer.Children.Add(actions);
        var cancel = new Button { Content = english ? "Cancel" : "Abbrechen",
            MinWidth = 110, IsCancel = true, Margin = new Thickness(4,0,4,0) };
        var restore = new Button { Content = english ? "Restore" : "Wiederherstellen",
            MinWidth = 125, IsDefault = true, Margin = new Thickness(4,0,0,0) };
        cancel.Click += (_,_) => DialogResult = false;
        restore.Click += (_,_) =>
        {
            if (PcasaDialog.Show(this,
                english ? "Restore the selected version? The current state will be backed up first." :
                    "Ausgewählte Version wiederherstellen? Der aktuelle Zustand wird vorher gesichert.",
                Title,MessageBoxButton.YesNo,MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes)
                DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(restore);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(navigation,Dock.Left);
        footer.Children.Add(navigation);
        _older.Content = "‹";
        _older.Width = 36;
        _older.Click += (_,_) => Navigate(1);
        _newer.Content = "›";
        _newer.Width = 36;
        _newer.Click += (_,_) => Navigate(-1);
        navigation.Children.Add(_older);
        navigation.Children.Add(_description);
        _description.MinWidth = 225;
        _description.Margin = new Thickness(10,0,10,0);
        _description.VerticalAlignment = VerticalAlignment.Center;
        navigation.Children.Add(_newer);
        var zoom = new Slider { Minimum = 0.1, Maximum = 4,
            Value = 1, Width = 135, Margin = new Thickness(20,0,5,0) };
        zoom.ValueChanged += (_,_) => SetZoom(zoom.Value);
        navigation.Children.Add(zoom);
        KeyDown += (_,e) =>
        {
            if (e.Key == Key.Left) { Navigate(1); e.Handled = true; }
            else if (e.Key == Key.Right) { Navigate(-1); e.Handled = true; }
        };
        _current.Source = Load(originalPath);
        ShowVersion();
    }

    private static BitmapSource Load(string file)
    {
        using var stream = File.OpenRead(file);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void ShowVersion()
    {
        _candidate.Source = Load(_versions[_index].SnapshotPath);
        var version = _versions[_index];
        _description.Text = $"{_index+1} / {_versions.Count}  " +
            version.SavedAtUtc.LocalDateTime.ToString("g");
        _older.IsEnabled = _index < _versions.Count - 1;
        _newer.IsEnabled = _index > 0;
    }

    private void Navigate(int delta)
    {
        var next = Math.Clamp(_index + delta,0,_versions.Count-1);
        if (next == _index) return;
        _index = next;
        ShowVersion();
    }

    private void SetZoom(double scale)
    {
        _zoom = scale;
        _scaleLeft.ScaleX = _scaleLeft.ScaleY = _zoom;
        _scaleRight.ScaleX = _scaleRight.ScaleY = _zoom;
    }

    private void Synchronize(ScrollViewer from,ScrollViewer to,ScrollChangedEventArgs e)
    {
        if (_synchronizing) return;
        _synchronizing = true;
        try
        {
            to.ScrollToHorizontalOffset(from.HorizontalOffset);
            to.ScrollToVerticalOffset(from.VerticalOffset);
        }
        finally { _synchronizing = false; }
    }
}
