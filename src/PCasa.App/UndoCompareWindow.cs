using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

namespace PCasa.App;

// Restore candidate is ALWAYS left. The live image stays fixed on the right.
internal sealed class UndoCompareWindow : Window
{
    private readonly IReadOnlyList<UndoVersion> _versions;
    private readonly Image _candidate = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _candidateDimensions = MakeDimensionsLabel();
    private readonly TextBlock _currentDimensions = MakeDimensionsLabel();
    private readonly Image _current = new() { Stretch = Stretch.Uniform };
    private readonly Image _splitCandidate = new() { Stretch = Stretch.Fill };
    private readonly Image _splitCurrent = new() { Stretch = Stretch.Fill };
    private readonly Grid _splitPanel = new() { Visibility = Visibility.Collapsed,
        ClipToBounds = true };
    private readonly Grid _splitFrame = new() {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    private readonly Thumb _divider = new() { Width = 24, Cursor = Cursors.SizeWE };
    private double _dividerGrabOffset;
    private BitmapSource? _currentBitmap;
    private readonly Grid _sidePanel = new();
    private readonly Button _singleMode = new(), _sideMode = new(), _splitMode = new();
    private int _mode = 1; // Existing restore dialog opens side by side.
    private double _splitFraction = 0.5;
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

        var titleBar = new DockPanel { Margin = new Thickness(4,2,0,10) };
        root.Children.Add(titleBar);
        var modes = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(modes, Dock.Right);
        titleBar.Children.Add(modes);
        AddCompareButton(modes, _singleMode, "M3.5,3.5 L20.5,3.5 L20.5,16.5 L3.5,16.5 Z",
            english ? "Single" : "Einzelansicht", 0);
        AddCompareButton(modes, _sideMode,
            "M2.5,3.5 L10,3.5 L10,16.5 L2.5,16.5 Z M14,3.5 L21.5,3.5 L21.5,16.5 L14,16.5 Z",
            english ? "Side by side" : "Nebeneinander", 1);
        AddCompareButton(modes, _splitMode,
            "M3.5,3.5 L20.5,3.5 L20.5,16.5 L3.5,16.5 Z M12,3.5 L12,16.5",
            english ? "Split" : "Schieber", 2);
        titleBar.Children.Add(new TextBlock {
            Text = english ? "Choose restore version" : "Wiederherstellungsversion wählen",
            FontSize = 13, FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        });

        var panes = _sidePanel;
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        Grid.SetRow(panes,1);
        root.Children.Add(panes);
        _candidate.LayoutTransform = _scaleLeft;
        _current.LayoutTransform = _scaleRight;
        // Dimensions belong immediately BELOW their own image, not to a
        // centered caption spanning the entire compare viewport.
        var candidateImageWithDimensions = new StackPanel {
            HorizontalAlignment = HorizontalAlignment.Center };
        candidateImageWithDimensions.Children.Add(_candidate);
        candidateImageWithDimensions.Children.Add(_candidateDimensions);
        var currentImageWithDimensions = new StackPanel {
            HorizontalAlignment = HorizontalAlignment.Center };
        currentImageWithDimensions.Children.Add(_current);
        currentImageWithDimensions.Children.Add(_currentDimensions);
        _leftScroll.Content = candidateImageWithDimensions;
        _rightScroll.Content = currentImageWithDimensions;
        _leftScroll.Background = Brushes.Black;
        _rightScroll.Background = Brushes.Black;
        panes.Children.Add(_leftScroll);
        Grid.SetColumn(_rightScroll,2);
        panes.Children.Add(_rightScroll);
        _leftScroll.SizeChanged += (_, _) => FitImagesToPanes();
        _rightScroll.SizeChanged += (_, _) => FitImagesToPanes();
        // In full-image mode, holding the mouse shows the whole restore
        // candidate. Each image keeps its own aspect, including after a crop.
        _rightScroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_mode != 0 || _candidate.Source is null) return;
            _current.Source = _candidate.Source;
            e.Handled = true;
        };
        _rightScroll.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_mode != 0) return;
            _current.Source = _currentBitmap;
            e.Handled = true;
        };
        _rightScroll.MouseLeave += (_, _) =>
        {
            if (_mode == 0) _current.Source = _currentBitmap;
        };
        _leftScroll.ScrollChanged += (_, e) => Synchronize(_leftScroll,_rightScroll,e);
        _rightScroll.ScrollChanged += (_, e) => Synchronize(_rightScroll,_leftScroll,e);
        // Version/current labels are deliberately small and light, just
        // like in the editor comparison; they are not dialog headings.
        panes.Children.Add(MakeLabel(
            english ? "Restore version" : "Wiederherstellung", 0));
        panes.Children.Add(MakeLabel(
            english ? "Current image" : "Aktuelles Bild", 2));
        _splitPanel.SizeChanged += (_, _) => UpdateSplitGeometry();
        _splitPanel.Children.Add(_splitFrame);
        _splitFrame.Children.Add(_splitCurrent);
        _splitFrame.Children.Add(_splitCandidate);
        var sliderCanvas = new Canvas { Background = Brushes.Transparent };
        _splitPanel.Children.Add(sliderCanvas);
        _divider.Height = double.NaN;
        _divider.SetBinding(HeightProperty, new System.Windows.Data.Binding(
            "ActualHeight") { Source = _splitPanel });
        var dividerVisual = new FrameworkElementFactory(typeof(Grid));
        dividerVisual.SetValue(Panel.BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(1,0,0,0)));
        var lineVisual = new FrameworkElementFactory(typeof(Border));
        lineVisual.SetValue(WidthProperty, 2d);
        lineVisual.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        lineVisual.SetValue(Border.BackgroundProperty, Brushes.WhiteSmoke);
        dividerVisual.AppendChild(lineVisual);
        _divider.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = dividerVisual };
        _divider.DragStarted += (_, _) =>
        {
            _dividerGrabOffset = Mouse.GetPosition(_splitPanel).X -
                DividerScreenX();
        };
        _divider.DragDelta += (_, _) =>
        {
            var imageWidth = _splitFrame.Width * _zoom;
            if (!(imageWidth > 0)) return;
            _splitFraction = Math.Clamp(.5 +
                (Mouse.GetPosition(_splitPanel).X - _dividerGrabOffset -
                 _splitPanel.ActualWidth / 2) / imageWidth, .02, .98);
            UpdateSplitGeometry();
        };
        _divider.DragCompleted += (_, _) => _dividerGrabOffset = 0;
        sliderCanvas.Children.Add(_divider);
        _splitPanel.Children.Add(MakeOverlayLabel(
            english ? "Restore version" : "Wiederherstellung", HorizontalAlignment.Left));
        _splitPanel.Children.Add(MakeOverlayLabel(
            english ? "Current image" : "Aktuelles Bild", HorizontalAlignment.Right));
        // The split is a SIBLING of the side-by-side grid. Otherwise
        // hiding the side grid would also hide its split child.
        Grid.SetRow(_splitPanel,1);
        root.Children.Add(_splitPanel);
        SetCompareMode(1);

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
        // The durable UndoHistoryService.Restore already creates and verifies
        // a pre-restore snapshot. A second confirmation is redundant.
        restore.Click += (_,_) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(restore);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(navigation,Dock.Left);
        footer.Children.Add(navigation);
        _older.Content = "‹";
        _older.Width = 36;
        _older.Click += (_,_) => Navigate(-1);
        _newer.Content = "›";
        _newer.Width = 36;
        _newer.Click += (_,_) => Navigate(1);
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
            if (e.Key == Key.Left) { Navigate(-1); e.Handled = true; }
            else if (e.Key == Key.Right) { Navigate(1); e.Handled = true; }
        };
        _current.Source = Load(originalPath);
        _currentBitmap = (BitmapSource)_current.Source;
        _currentDimensions.Text = FormatDimensions(_currentBitmap);
        _splitCurrent.Source = _current.Source;
        ShowVersion();
    }

    private void FitImagesToPanes()
    {
        // Limit the natural WPF image size to the visible viewport before
        // applying the user's synchronized zoom transformation.
        _candidate.MaxWidth = Math.Max(1, _leftScroll.ActualWidth - 18);
        _candidate.MaxHeight = Math.Max(1, _leftScroll.ActualHeight - 18);
        _current.MaxWidth = Math.Max(1, _rightScroll.ActualWidth - 18);
        _current.MaxHeight = Math.Max(1, _rightScroll.ActualHeight - 18);
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
        return ExifOrientation.Apply(bitmap, stream);
    }

    private void ShowVersion()
    {
        _candidate.Source = Load(_versions[_index].SnapshotPath);
        _candidateDimensions.Text = FormatDimensions((BitmapSource)_candidate.Source);
        _splitCandidate.Source = _candidate.Source;
        UpdateSplitGeometry();
        var version = _versions[_index];
        _description.Text = $"{_index+1} / {_versions.Count}  " +
            version.SavedAtUtc.LocalDateTime.ToString("g");
        _older.IsEnabled = _index > 0;
        _newer.IsEnabled = _index < _versions.Count - 1;
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
        _splitFrame.RenderTransformOrigin = new Point(0.5,0.5);
        _splitFrame.RenderTransform = new ScaleTransform(_zoom,_zoom);
        UpdateSplitGeometry();
    }

    private void AddCompareButton(Panel parent, Button button,
        string geometry, string caption, int mode)
    {
        button.Width = 37;
        button.Height = 29;
        button.Margin = new Thickness(2,0,0,0);
        button.ToolTip = caption;
        var canvas = new Canvas { Width = 24, Height = 20 };
        canvas.Children.Add(new System.Windows.Shapes.Path {
            Data = Geometry.Parse(geometry),
            Stroke = Brushes.LightGray,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round
        });
        button.Content = canvas;
        button.Click += (_, _) => SetCompareMode(mode);
        parent.Children.Add(button);
    }

    private static TextBlock MakeDimensionsLabel()
        => new() {
            FontSize = 11,
            FontWeight = FontWeights.Light,
            Foreground = Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 1),
            IsHitTestVisible = false
        };

    private static string FormatDimensions(BitmapSource image)
        => $"{image.PixelWidth:N0} × {image.PixelHeight:N0} Pixel";

    private static Border MakeLabel(string caption, int column)
    {
        var label = MakeOverlayLabel(caption, HorizontalAlignment.Left);
        Grid.SetColumn(label, column);
        return label;
    }

    private static Border MakeOverlayLabel(string caption, HorizontalAlignment side)
        => new() {
            Child = new TextBlock {
                Text = caption, FontSize = 11, FontWeight = FontWeights.Light,
                Foreground = Brushes.WhiteSmoke },
            Background = new SolidColorBrush(Color.FromArgb(170, 26, 26, 28)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4, 8, 4),
            IsHitTestVisible = false,
            HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top,
            Margin = side == HorizontalAlignment.Right
                ? new Thickness(0,8,11,0) : new Thickness(11,8,0,0)
        };

    private void SetCompareMode(int mode)
    {
        _mode = mode;
        _current.Source = _currentBitmap;
        _sidePanel.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        _splitPanel.Visibility = mode == 2 ? Visibility.Visible : Visibility.Collapsed;
        _leftScroll.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        _rightScroll.Visibility = mode == 2 ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(_rightScroll, mode == 0 ? 0 : 2);
        Grid.SetColumnSpan(_rightScroll, mode == 0 ? 3 : 1);
        _singleMode.BorderThickness = mode == 0 ? new Thickness(3) : new Thickness(1);
        _sideMode.BorderThickness = mode == 1 ? new Thickness(3) : new Thickness(1);
        _splitMode.BorderThickness = mode == 2 ? new Thickness(3) : new Thickness(1);
        UpdateSplitGeometry();
    }

    private void UpdateSplitGeometry()
    {
        if (_splitCurrent.Source is not BitmapSource edited ||
            _splitPanel.ActualWidth <= 0 || _splitPanel.ActualHeight <= 0)
            return;
        var width = _splitPanel.ActualWidth;
        var height = _splitPanel.ActualHeight;
        var fit = Math.Min(width / edited.PixelWidth, height / edited.PixelHeight);
        var frameWidth = Math.Max(1, edited.PixelWidth * fit);
        var frameHeight = Math.Max(1, edited.PixelHeight * fit);
        _splitFrame.Width = frameWidth;
        _splitFrame.Height = frameHeight;
        _splitCandidate.Width = _splitCurrent.Width = frameWidth;
        _splitCandidate.Height = _splitCurrent.Height = frameHeight;
        // The split fraction is a picture coordinate. The divider follows
        // the same image location through Fit, zoom and resize.
        _splitCandidate.Clip = new RectangleGeometry(
            new Rect(0, 0, _splitFraction * frameWidth, frameHeight));
        Canvas.SetLeft(_divider, DividerScreenX() - _divider.Width / 2);
    }

    private double DividerScreenX()
        => _splitPanel.ActualWidth / 2 +
            (_splitFraction - .5) * _splitFrame.Width * _zoom;

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
