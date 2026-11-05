using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

namespace PCasa.App;

/// <summary>
/// Read-only view of the ONE scheduler/journal plus three commands routed
/// back into that same owner. No local task queue, priority list or retry
/// executor exists in this window.
/// </summary>
public sealed class TaskManagerWindow : Window
{
    private sealed record TaskRow(
        string Key, string Title, string Directory, string Progress,
        string Status, string? Error, int Priority, long Order,
        bool Durable, bool Movable, bool Retryable, bool Deletable,
        SchedulerWorkKind WorkKind = SchedulerWorkKind.Catalog);

    private readonly PCasaScheduler _scheduler;
    private readonly DurablePackageJournal _journal;
    private readonly LocalDatabase _database;
    private readonly OriginalWriteFailureRepository _originalFailures;
    private readonly Func<OriginalWriteFailureRepository.FailedFile, Task>? _retryFailedFile;
    private readonly Func<OriginalWriteFailureRepository.FailedFile, Task>? _discardFailedFile;
    private readonly Func<IReadOnlyList<AutomaticScanTask>>? _automaticScans;
    private readonly Func<string, Task<bool>>? _stopAutomaticScan;
    private readonly Func<string>? _idleScanStatus;
    private readonly Func<string,string,Task<bool>>? _moveAutomaticScan;
    private readonly Func<string,Task<bool>>? _resumeAutomaticScan;
    private readonly Func<string,Task<bool>>? _startAutomaticNow;
    private readonly Func<string,string,IReadOnlyList<string>,Task<bool>>?
        _moveGlobalTask;
    private Dictionary<string, IReadOnlyList<OriginalWriteFailureRepository.FailedFile>> _failedRows = new();
    private readonly DataGrid _list = new();
    private readonly ObservableCollection<TaskRow> _visible = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, string> _initialDirectoryByKey = new();
    private readonly CheckBox _running = new(), _waiting = new(),
        _errors = new(), _completed = new();
    private readonly Button _up = new(), _down = new(),
        _startNow = new(), _restart = new(), _delete = new();
    private readonly TextBlock _detail = new();
    private List<TaskRow> _all = [];
    private bool _reading;
    private bool _acting;
    private bool _refreshRequested;
    private Point? _dragOrigin;
    private string? _dragKey;
    private const string TaskDragFormat = "PCasa.TaskManager.TaskKey";

    public TaskManagerWindow(PCasaScheduler scheduler, LocalDatabase database,
        Func<OriginalWriteFailureRepository.FailedFile, Task>? retryFailedFile = null,
        Func<OriginalWriteFailureRepository.FailedFile, Task>? discardFailedFile = null,
        Func<IReadOnlyList<AutomaticScanTask>>? automaticScans = null,
        Func<string, Task<bool>>? stopAutomaticScan = null,
        Func<string>? idleScanStatus = null,
        Func<string,string,Task<bool>>? moveAutomaticScan = null,
        Func<string,Task<bool>>? resumeAutomaticScan = null,
        Func<string,Task<bool>>? startAutomaticNow = null,
        Func<string,string,IReadOnlyList<string>,Task<bool>>?
            moveGlobalTask = null)
    {
        _scheduler = scheduler;
        _database = database;
        _journal = new DurablePackageJournal(database);
        _originalFailures = new OriginalWriteFailureRepository(database);
        _retryFailedFile = retryFailedFile;
        _discardFailedFile = discardFailedFile;
        _automaticScans = automaticScans;
        _stopAutomaticScan = stopAutomaticScan;
        _idleScanStatus = idleScanStatus;
        _moveAutomaticScan = moveAutomaticScan;
        _resumeAutomaticScan = resumeAutomaticScan;
        _startAutomaticNow = startAutomaticNow;
        _moveGlobalTask = moveGlobalTask;
        Title = "PCasa – " + UiLanguage.T("Taskmanager");
        Width = 940; Height = 465;
        MinWidth = 760; MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var outer = new Grid { Margin = new Thickness(14, 10, 14, 11) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = outer;

        outer.Children.Add(new TextBlock
        {
            Text = UiLanguage.T("Taskmanager"), FontSize = 19,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(5, 0, 0, 10)
        });

        var filters = new WrapPanel { Margin = new Thickness(12, 7, 8, 7) };
        ConfigureFilter(_running, "Läuft", true, filters);
        ConfigureFilter(_waiting, "Wartet", true, filters);
        ConfigureFilter(_errors, "Fehler", true, filters);
        ConfigureFilter(_completed, "Abgeschlossen", false, filters);
        var filterFrame = Frame(filters);
        Grid.SetRow(filterFrame, 1);
        outer.Children.Add(filterFrame);

        _list.AutoGenerateColumns = false;
        _list.IsReadOnly = true;
        _list.CanUserAddRows = false;
        _list.CanUserDeleteRows = false;
        _list.CanUserReorderColumns = false;
        _list.CanUserSortColumns = false; // actual scheduler dispatch order
        _list.ItemsSource = _visible;
        _list.SelectionMode = DataGridSelectionMode.Single;
        _list.SelectionUnit = DataGridSelectionUnit.FullRow;
        _list.HeadersVisibility = DataGridHeadersVisibility.Column;
        _list.RowHeaderWidth = 0;
        _list.GridLinesVisibility = DataGridGridLinesVisibility.None;
        _list.RowHeight = 41;
        _list.FontSize = 12;
        ConfigureTaskTiles();
        _list.EnableRowVirtualization = true;
        _list.EnableColumnVirtualization = true;
        _list.Margin = new Thickness(7);
        AddColumn("Aufgabe", nameof(TaskRow.Title), new DataGridLength(220));
        AddColumn("Verzeichnis", nameof(TaskRow.Directory),
            new DataGridLength(1, DataGridLengthUnitType.Star));
        AddColumn("Fortschritt", nameof(TaskRow.Progress), new DataGridLength(92));
        AddColumn("Status", nameof(TaskRow.Status), new DataGridLength(92));
        _list.SelectionChanged += (_, _) => UpdateActions();
        // Reordering changes the scheduler's persisted dispatch sequence,
        // never merely the DataGrid's visual sort order.
        _list.AllowDrop = true;
        _list.PreviewMouseLeftButtonDown += TaskMouseDown;
        _list.PreviewMouseMove += TaskMouseMove;
        _list.DragOver += TaskDragOver;
        _list.Drop += TaskDrop;
        var listFrame = Frame(_list);
        Grid.SetRow(listFrame, 2);
        outer.Children.Add(listFrame);

        // The complete error is visible without fighting the action buttons
        // for horizontal space. The original source path remains in ToolTip.
        _detail.VerticalAlignment = VerticalAlignment.Center;
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.MaxHeight = 48;
        _detail.FontSize = 11;
        _detail.Margin = new Thickness(8, 5, 8, 5);
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        var detailFrame = Frame(_detail);
        detailFrame.MinHeight = 30;
        Grid.SetRow(detailFrame, 3);
        outer.Children.Add(detailFrame);
        var footer = new Grid { Margin = new Thickness(4, 7, 4, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Button(_up, "Nach oben", () => Move(-1), actions);
        Button(_down, "Nach unten", () => Move(1), actions);
        Button(_startNow, "Jetzt ausführen", StartNow, actions);
        Button(_restart, "Neu starten", Restart, actions);
        Button(_delete, "Löschen", Delete, actions);
        var close = new Button
        {
            Content = UiLanguage.T("Schließen"), MinWidth = 94,
            Height = 30, Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(6, 0, 0, 0), IsCancel = true
        };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        outer.Children.Add(footer);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _refreshTimer.Tick += (_, _) => RequestRefresh();
        Loaded += (_, _) =>
        {
            _scheduler.ProgressChanged += SchedulerProgressChanged;
            _scheduler.TaskAccepted += RequestRefresh;
            _scheduler.TaskOrderChanged += RequestRefresh;
            _refreshTimer.Start();
            RequestRefresh();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _scheduler.ProgressChanged -= SchedulerProgressChanged;
            _scheduler.TaskAccepted -= RequestRefresh;
            _scheduler.TaskOrderChanged -= RequestRefresh;
        };
        UpdateActions();
    }

    private void SchedulerProgressChanged(SchedulerJobSnapshot _) => RequestRefresh();

    // On accepting a durable manual job the journal entry already exists.
    // This is an immediate read of scheduler state, not a new WPF queue.
    public void RequestRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(RequestRefresh));
            return;
        }
        if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
        if (_reading || _acting)
        {
            _refreshRequested = true;
            return;
        }
        _ = RefreshAsync();
    }

    private static Border Frame(UIElement child)
    {
        var border = new Border
        {
            Child = child, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(3, 2, 3, 3)
        };
        border.SetResourceReference(Border.BorderBrushProperty, "PcasaDividerBrush");
        border.SetResourceReference(Border.BackgroundProperty, "PcasaPanelAltBrush");
        return border;
    }

    private void ConfigureFilter(CheckBox box, string title, bool value, Panel parent)
    {
        box.Content = UiLanguage.T(title);
        box.IsChecked = value;
        box.FontSize = 12;
        box.MinHeight = 27;
        box.VerticalContentAlignment = VerticalAlignment.Center;
        box.Margin = new Thickness(0, 0, 18, 0);
        box.Checked += (_, _) => ApplyFilters();
        box.Unchecked += (_, _) => ApplyFilters();
        parent.Children.Add(box);
    }

    private void AddColumn(string label, string property, DataGridLength width)
    {
        var column = new DataGridTextColumn
        {
            Header = UiLanguage.T(label),
            Binding = new Binding(property),
            Width = width
        };
        var textStyle = new Style(typeof(TextBlock));
        textStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty,
            VerticalAlignment.Center));
        textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,
            TextTrimming.CharacterEllipsis));
        if (property == nameof(TaskRow.Directory))
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new Binding(nameof(TaskRow.Directory))));
        column.ElementStyle = textStyle;
        _list.Columns.Add(column);
    }

    private void ConfigureTaskTiles()
    {
        // A row is one raised, compact tile. Reuse the application's theme
        // brushes and button-depth overlay instead of hardcoded dark colors.
        // The DataGrid is only the virtualized layout and selection owner.
        _list.RowStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="{x:Type DataGridRow}">
              <Setter Property="MinHeight" Value="39"/>
              <Setter Property="Template">
                <Setter.Value>
                  <ControlTemplate TargetType="{x:Type DataGridRow}">
                    <Border x:Name="Tile" Margin="2,2,2,2"
                            CornerRadius="5" BorderThickness="1"
                            BorderBrush="{DynamicResource PcasaDividerBrush}"
                            Background="{DynamicResource PcasaPanelAltBrush}"
                            Effect="{DynamicResource PcasaButtonShadow}">
                      <Grid>
                        <Border Background="{DynamicResource PcasaButtonDepthBrush}"
                                CornerRadius="4" IsHitTestVisible="False"/>
                        <DataGridCellsPresenter/>
                      </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Tile" Property="BorderBrush"
                                Value="{DynamicResource PcasaToolbarFrameBrush}"/>
                      </Trigger>
                      <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="Tile" Property="BorderBrush"
                                Value="{DynamicResource PcasaAccentBrush}"/>
                        <Setter TargetName="Tile" Property="Background"
                                Value="{DynamicResource PcasaSelectionBrush}"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Setter.Value>
              </Setter>
            </Style>
            """);
        _list.CellStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                   TargetType="{x:Type DataGridCell}">
              <Setter Property="Background" Value="Transparent"/>
              <Setter Property="BorderThickness" Value="0"/>
              <Setter Property="Padding" Value="7,0,5,0"/>
              <Setter Property="VerticalContentAlignment" Value="Center"/>
              <Setter Property="Foreground" Value="{DynamicResource PcasaTextBrush}"/>
              <Style.Triggers>
                <Trigger Property="IsSelected" Value="True">
                  <Setter Property="Background" Value="Transparent"/>
                  <Setter Property="Foreground" Value="{DynamicResource PcasaTextBrush}"/>
                </Trigger>
              </Style.Triggers>
            </Style>
            """);
    }

    private static void Button(Button control, string title, Action click, Panel parent)
    {
        control.Content = UiLanguage.T(title);
        control.Height = 30;
        control.MinWidth = title.StartsWith("Nach", StringComparison.Ordinal) ? 85 : 96;
        control.Margin = new Thickness(0, 0, 6, 0);
        control.Padding = new Thickness(8, 3, 8, 3);
        control.Click += (_, _) => click();
        parent.Children.Add(control);
    }

    // Dragging a tile changes the ACTUAL persisted scheduler order. An
    // existing worker must yield before the promoted task starts; simply
    // sorting a visual collection would misrepresent what is executing.
    private static TaskRow? TaskAt(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is DataGridRow { Item: TaskRow row }) return row;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : (source as FrameworkContentElement)?.Parent;
        }
        return null;
    }

    private void TaskMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = TaskAt(e.OriginalSource as DependencyObject);
        _dragOrigin = row is not null &&
            row.Status != "Abgeschlossen" &&
            ( _moveGlobalTask is not null ||
              (row.Movable && HasReorderPeer(row) &&
               (row.Durable || IsInitialImport(row.Key) ||
                IsRootReview(row.Key))) )
            ? e.GetPosition(_list) : null;
        _dragKey = _dragOrigin is not null ? row?.Key : null;
    }

    private void TaskMouseMove(object sender, MouseEventArgs e)
    {
        if (_acting || _dragOrigin is not Point origin || _dragKey is null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragOrigin = null;
            _dragKey = null;
            return;
        }
        var now = e.GetPosition(_list);
        if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var key = _dragKey;
        _dragOrigin = null;
        _dragKey = null;
        _refreshTimer.Stop();
        try
        {
            System.Windows.DragDrop.DoDragDrop(_list,
                new DataObject(TaskDragFormat, key), DragDropEffects.Move);
        }
        finally
        {
            if (IsLoaded && !_refreshTimer.IsEnabled)
                _refreshTimer.Start();
        }
    }

    private void TaskDragOver(object sender, DragEventArgs e)
    {
        var key = e.Data.GetDataPresent(TaskDragFormat)
            ? e.Data.GetData(TaskDragFormat) as string : null;
        var target = TaskAt(e.OriginalSource as DependencyObject);
        var source = _all.FirstOrDefault(row => row.Key == key);
        e.Effects = !_acting && source is not null &&
            target is not null && source.Key != target.Key &&
            source.Status != "Abgeschlossen" &&
            target.Status != "Abgeschlossen" &&
            (_moveGlobalTask is not null ||
             (source.Movable && target.Priority == source.Priority &&
              ((source.Durable && target.Durable &&
                SameSchedulerLane(source, target)) ||
               (IsInitialImport(source.Key) && IsInitialImport(target.Key) &&
                _moveAutomaticScan is not null) ||
               (IsRootReview(source.Key) && IsRootReview(target.Key) &&
                _moveAutomaticScan is not null)))
                ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TaskDrop(object sender, DragEventArgs e)
    {
        TaskDragOver(sender, e);
        if (e.Effects != DragDropEffects.Move) return;
        var key = e.Data.GetData(TaskDragFormat) as string;
        var target = TaskAt(e.OriginalSource as DependencyObject);
        if (key is null || target is null) return;
        var targetKey = target.Key;
        if (_moveGlobalTask is not null)
        {
            var ordered = _all.Where(row => row.Status != "Abgeschlossen")
                .Select(row => row.Key).ToArray();
            _ = PerformAsync(key, () => _moveGlobalTask(key, targetKey, ordered));
            return;
        }
        if ((IsInitialImport(key) || IsRootReview(key)) &&
            _moveAutomaticScan is not null)
            _ = PerformAsync(key, () => _moveAutomaticScan(key, targetKey));
        else
            _ = PerformAsync(key, () => Task.Run(() =>
            {
                if (!_scheduler.MoveDurableTaskTo(key, targetKey))
                    throw new InvalidOperationException(
                        "Die Reihenfolge konnte nicht geändert werden. " +
                        "Der Auftrag ist nicht mehr wartend oder gehört zu einer anderen Ausführungsgruppe.");
                return true;
            }));
    }

    private static bool IsInitialImport(string key)
        => key.StartsWith("initial:", StringComparison.Ordinal);

    private static bool IsRootReview(string key)
        => key.StartsWith("automatic:", StringComparison.Ordinal) &&
           key.Contains(":RescanRoot:", StringComparison.Ordinal);

    // A task can move only inside the execution lane where its scheduler
    // owner can actually change dispatch order. Independent lanes cannot
    // be represented as an editable single serial queue.
    private static bool SameSchedulerLane(TaskRow source, TaskRow target)
    {
        if (source.Priority != target.Priority) return false;
        static string Lane(SchedulerWorkKind kind) => kind switch
        {
            SchedulerWorkKind.CatalogRead => "read",
            SchedulerWorkKind.ViewerRead => "viewer",
            SchedulerWorkKind.ThumbnailRead => "thumbnail",
            SchedulerWorkKind.SourceProbe => "source-probe",
            SchedulerWorkKind.InitialImport => "initial",
            _ => "work"
        };
        return Lane(source.WorkKind) == Lane(target.WorkKind);
    }

    private TaskRow[] ReorderLane(TaskRow row)
    {
        if (IsInitialImport(row.Key))
            return _all.Where(other => IsInitialImport(other.Key) &&
                    other.Priority == row.Priority)
                .OrderBy(other => other.Order).ThenBy(other => other.Key).ToArray();
        if (IsRootReview(row.Key))
            return _all.Where(other => IsRootReview(other.Key) &&
                    other.Priority == row.Priority &&
                    other.Status == "Wartet")
                .OrderBy(other => other.Order).ThenBy(other => other.Key).ToArray();
        if (row.Durable)
            return _all.Where(other => other.Durable &&
                    other.Status is "Läuft" or "Wartet" &&
                    SameSchedulerLane(row, other))
                .OrderBy(other => other.Order).ThenBy(other => other.Key).ToArray();
        return [];
    }

    private bool HasReorderPeer(TaskRow row)
        => ReorderLane(row).Length > 1;

    private bool CanMove(TaskRow row, int direction)
    {
        if (_moveGlobalTask is not null)
        {
            if (row.Status == "Abgeschlossen") return false;
            var all = _all.Where(item => item.Status != "Abgeschlossen").ToArray();
            var index = Array.FindIndex(all, item => item.Key == row.Key);
            return index >= 0 && index + direction >= 0 &&
                index + direction < all.Length;
        }
        if (!row.Movable || row.Status is "Läuft" or "Fehler" or "Abgeschlossen")
            return false;
        var lane = ReorderLane(row);
        var index = Array.FindIndex(lane, other => other.Key == row.Key);
        if (index >= 0 && index + direction >= 0 &&
            index + direction < lane.Length) return true;
        // At the top of its own P4 queue, an explicit Up gesture promotes
        // the SAME review to P2. P0 manual writing remains protected.
        return direction == -1 && IsRootReview(row.Key) &&
            row.Priority == 4 && _startAutomaticNow is not null &&
            index == 0;
    }

    private TaskRow? Selection => _list.SelectedItem as TaskRow;

    private void ApplyFilters()
    {
        var key = Selection?.Key;
        var filtered = _all.Where(row => row.Status switch
        {
            "Läuft" => _running.IsChecked == true,
            "Wartet" or "Angehalten" or "Nicht erreichbar" => _waiting.IsChecked == true,
            "Fehler" => _errors.IsChecked == true,
            "Abgeschlossen" => _completed.IsChecked == true,
            _ => false
        }).ToArray();

        // Preserve the DataGrid's scroll position and row selection when
        // only the progress or error text changes. Reassigning ItemsSource
        // on every polling tick made large task lists jump unexpectedly.
        var sameKeys = filtered.Length == _visible.Count;
        if (sameKeys)
            for (var i = 0; i < filtered.Length; i++)
                if (!string.Equals(filtered[i].Key, _visible[i].Key,
                    StringComparison.Ordinal))
                {
                    sameKeys = false;
                    break;
                }
        if (sameKeys)
        {
            for (var i = 0; i < filtered.Length; i++)
                if (!_visible[i].Equals(filtered[i]))
                    _visible[i] = filtered[i];
        }
        else
        {
            _visible.Clear();
            foreach (var row in filtered)
                _visible.Add(row);
        }
        if (key is not null)
            _list.SelectedItem = _visible.FirstOrDefault(item => item.Key == key);
        UpdateActions();
    }

    private void UpdateActions()
    {
        var row = Selection;
        _up.IsEnabled = !_acting && row is not null && CanMove(row, -1);
        _down.IsEnabled = !_acting && row is not null && CanMove(row, 1);
        _startNow.IsEnabled = !_acting && row is
            { Priority: 4, Status: "Wartet" } &&
            IsRootReview(row.Key) && _startAutomaticNow is not null;
        _restart.IsEnabled = !_acting && row is { Retryable: true };
        _delete.IsEnabled = !_acting && row is { Deletable: true };
        const string moveInfo =
            "Aufträge frei verschieben. Die neue Reihenfolge wird im Scheduler gespeichert; " +
            "laufende Dateischreibvorgänge geben ihren Platz am nächsten sicheren Schritt frei.";
        _up.ToolTip = _down.ToolTip = UiLanguage.T(
            _moveGlobalTask is not null ? moveInfo :
            "Verschieben ist nur zwischen Aufträgen derselben Ausführungsgruppe möglich.");
        ToolTipService.SetShowOnDisabled(_up, true);
        ToolTipService.SetShowOnDisabled(_down, true);
        _detail.Text = row is null
            ? _all.Count == 0 ? _idleScanStatus?.Invoke() ?? "" : ""
            : row.Error is { Length: > 0 }
            ? UiLanguage.T("Fehler") + ": " + row.Error
            : row.Status == "Abgeschlossen"
                ? UiLanguage.T("Abgeschlossen")
                : row.Status == "Läuft"
                    ? UiLanguage.T("Dieser Auftrag läuft bereits; laufende Arbeit wird nicht durch Verschieben unterbrochen.")
                : row.Movable && !HasReorderPeer(row) &&
                    IsRootReview(row.Key) && row.Priority == 4 &&
                    _startAutomaticNow is not null
                    ? UiLanguage.T("Mit „Jetzt ausführen“ oder „Nach oben“ diese Prüfung bei nächster Gelegenheit vorziehen.")
                : row.Movable && !HasReorderPeer(row)
                    ? UiLanguage.T("Keine zweite Aufgabe in derselben Prioritäts- und Ausführungsgruppe zum Verschieben vorhanden.")
                : !row.Movable && row.Status is "Wartet" or "Angehalten"
                    ? UiLanguage.T("Dieser Auftrag wird von einem anderen Scheduler-Bereich verwaltet und lässt sich hier nicht verschieben.")
                : row.Directory;
        _detail.ToolTip = row is null ? null :
            _detail.Text + Environment.NewLine + row.Directory;
    }

    private async Task RefreshAsync()
    {
        if (_reading || _acting || !IsLoaded || Dispatcher.HasShutdownStarted) return;
        _reading = true;
        try
        {
            var showCompleted = _completed.IsChecked == true;
            var snapshots = _scheduler.Snapshot();
            // MainWindow owns the existing watcher/scan dispatcher. Read its
            // queue snapshot on the WPF dispatcher, before the background
            // database read. The taskmanager does not create a second queue.
            var automaticScans = _automaticScans?.Invoke() ??
                Array.Empty<AutomaticScanTask>();
            var prepared = await Task.Run(() =>
            {
                var active = _journal.Pending();
                var done = showCompleted
                    ? _journal.RecentCompleted()
                    : Array.Empty<DurablePackageRecord>();
                var latest = snapshots
                    .GroupBy(s => s.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First());
                var rows = new List<TaskRow>();
                var scanProgress = new FolderRepository(_database);
                var firstImports = scanProgress.GetPendingFirstImportQueue();
                var continuedImports = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var record in active.Concat(done))
                {
                    // Old transfer-reconcile-v1 records were temporary
                    // repair probes, not user-owned transfers. They have
                    // no meaningful restart, delete or priority action.
                    if (record.Type == "transfer-reconcile-v1") continue;
                    latest.TryGetValue(record.Key, out var snapshot);
                    var status = record.State switch
                    {
                        "completed" => "Abgeschlossen",
                        "deferred" => "Fehler",
                        _ when snapshot?.State == SchedulerJobState.Running => "Läuft",
                        _ when snapshot?.State == SchedulerJobState.Failed => "Fehler",
                        _ => "Wartet"
                    };
                    var path = DirectoryFor(record, snapshot?.CurrentPath);
                    var continuingImport = record.State != "completed" &&
                        AcknowledgesPendingImport(record) &&
                        firstImports.Any(first => string.Equals(
                            first.Directory, path, StringComparison.OrdinalIgnoreCase));
                    if (continuingImport) continuedImports.Add(path);
                    var work = continuingImport
                        ? UiLanguage.T("Bilder einlesen") : TaskCaption(record);
                    var total = CountRecipeImages(record.Payload);
                    // One confirmed scan counter, read from the SAME durable
                    // receipt as the floating progress window. A queued job
                    // restored after restart has no in-memory snapshot yet.
                    var progress = record.Type == "manual-folder-scan-v1"
                        ? UiLanguage.Format("{0:N0} geprüft",
                            Math.Max(snapshot?.Processed ?? 0,
                                scanProgress.GetConfirmedRegularScanCount(record.Key)))
                        : snapshot is { Total: > 0 }
                            ? $"{snapshot.Processed:N0}/{snapshot.Total:N0}"
                            : total > 0
                                ? $"{Math.Clamp(record.CompletedSteps - (record.Type.StartsWith("tag-edit-", StringComparison.Ordinal) ? 1 : 0), 0, total):N0}/{total:N0}"
                                : record.CompletedSteps > 0
                                    ? record.CompletedSteps.ToString("N0") : "–";
                    var isWaiting = status == "Wartet";
                    var movable = isWaiting;
                    var retryable = status == "Fehler" && record.State == "deferred";
                    // Tag write intents may already be staged in SQLite. The
                    // scheduler must protect them against accidental deletion;
                    // an unmanageable active task is not shown as actionable.
                    var deletable = record.State is "queued" or "deferred" or "running" &&
                        !(record.Type.StartsWith("tag-", StringComparison.Ordinal) &&
                            (record.CompletedSteps > 0 || record.State == "running" ||
                             record.Type.StartsWith("tag-recovery-", StringComparison.Ordinal)));
                    // Running but protected GPS/tag writes are STILL jobs.
                    // Action restrictions must never hide the task itself.
                    rows.Add(new TaskRow(record.Key, work, path, progress,
                        status, record.Error ?? snapshot?.Error,
                        (int)record.Priority, record.QueueOrder, true,
                        movable, retryable, deletable, record.WorkKind));
                }
                // Automatic discoveries and full-root reviews are REAL
                // scanner requests, not transient maintenance probes.
                // They run under this scheduler and can be stopped through
                // the original background owner without deleting catalog data.
                var firstByKey = firstImports.ToDictionary(
                    first => $"initial:{first.FolderId}", StringComparer.Ordinal);
                var liveKeys = new HashSet<string>(
                    automaticScans.Select(scan => scan.Key),
                    StringComparer.Ordinal);
                foreach (var scan in automaticScans)
                {
                    firstByKey.TryGetValue(scan.Key, out var first);
                    rows.Add(new TaskRow(scan.Key, scan.Title,
                        scan.Directory, scan.Progress, scan.Status,
                        null, scan.Priority, first?.QueueOrder ?? scan.Order,
                        false, _moveAutomaticScan is not null &&
                            ((first is not null) ||
                             (scan.Priority == 4 && IsRootReview(scan.Key) &&
                              scan.Status == "Wartet")),
                        false, _stopAutomaticScan is not null && scan.CanStop));
                }

                // An in-process database backup is a real scheduler
                // job; show its queued/running lifecycle directly from
                // scheduler snapshots. No duplicate UI task is created.
                foreach (var backup in latest.Values.Where(job =>
                    job.Key.StartsWith("database:backup:", StringComparison.Ordinal) &&
                    job.State is SchedulerJobState.Queued or
                        SchedulerJobState.Running or SchedulerJobState.YieldRequested))
                {
                    var destination = backup.Key.Length >
                        "database:backup:".Length + 33
                        ? backup.Key[("database:backup:".Length + 33)..]
                        : "–";
                    var fileProgress = backup.Total > 0
                        ? UiLanguage.Format("{0:N0}/{1:N0} Dateien",
                            backup.Processed, backup.Total)
                        : "–";
                    rows.Add(new TaskRow(backup.Key,
                        UiLanguage.T("Datenbank sichern"), destination,
                        fileProgress,
                        backup.State == SchedulerJobState.Running
                            ? "Läuft" : "Wartet",
                        backup.Error, (int)backup.Priority, backup.Id,
                        false, false, false, _stopAutomaticScan is not null,
                        backup.WorkKind));
                }

                // The pending initial-import flag is a durable job from
                // the moment the source was selected, NOT merely when
                // the UI scan pump gets around to starting it. Paused
                // imports remain visible without executing at startup.
                foreach (var first in firstImports)
                {
                    var key = $"initial:{first.FolderId}";
                    // One root, one import: a manually continued full-root
                    // scan is the real active scheduler job. Do not show its
                    // old pending marker as an unrelated second task.
                    if (liveKeys.Contains(key) ||
                        continuedImports.Contains(first.Directory)) continue;
                    rows.Add(new TaskRow(key, UiLanguage.T("Bilder einlesen"),
                        first.Directory, "–",
                        first.Paused ? "Angehalten" : "Wartet",
                        null, 2, first.QueueOrder,
                        false, _moveAutomaticScan is not null,
                        first.Paused && _resumeAutomaticScan is not null,
                        _stopAutomaticScan is not null));
                }

                // Unrelated transient scheduler snapshots (historical source
                // probes, cache refresh etc.) have no user action.
                // Parked individual originals are real errors, but NOT
                // additional scheduler tasks. Group by their original P0
                // package and delegate Retry/Discard to the SAME existing
                // write-error actions used by the error dialog.
                var failedGroups = _originalFailures.Unresolved()
                    .GroupBy(issue => issue.PackageKey, StringComparer.Ordinal)
                    .ToDictionary(group => "failed-files:" + group.Key,
                        group => (IReadOnlyList<OriginalWriteFailureRepository.FailedFile>)
                            group.ToArray(), StringComparer.Ordinal);
                foreach (var (key, failures) in failedGroups)
                {
                    if (_retryFailedFile is null && _discardFailedFile is null)
                        continue; // no control is available in this window
                    var first = failures[0];
                    var task = first.Kind == OriginalWriteFailureRepository.Gps
                        ? UiLanguage.T("GPS schreiben") : UiLanguage.T("Tags schreiben");
                    rows.Add(new TaskRow(key, task,
                        CurrentDirectory(first.Path),
                        UiLanguage.Format("{0:N0} fehlerhaft", failures.Count),
                        "Fehler", first.Error, 0, long.MaxValue / 4,
                        false, false, _retryFailedFile is not null,
                        _discardFailedFile is not null));
                }
                // Actual execution first. A paused high-priority task
                // cannot precede a running lower-priority task on screen.
                // One effective order comes from the scheduler. The display
                // does not invent a separate status-first queue: moving a
                // waiting task above a running one must be VISIBLE at once.
                var positioned = rows.Select(row =>
                {
                    var placement = _scheduler.EffectiveTaskPlacement(
                        row.Key, (SchedulerPriority)row.Priority, row.Order);
                    return row with {
                        Priority = (int)placement.Priority,
                        Order = placement.QueueOrder
                    };
                }).OrderBy(row => row.Status == "Abgeschlossen" ? 1 : 0)
                  .ThenBy(row => row.Priority)
                  .ThenBy(row => row.Order)
                  .ThenBy(row => row.Key)
                  .ToList();
                return (Rows: positioned, Failed: failedGroups);
            });
            if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
            _all = prepared.Rows;
            _failedRows = prepared.Failed;
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _detail.Text = ex.Message;
            PCasaDiagnostics.Write("Taskmanager.Read", ex);
        }
        finally
        {
            _reading = false;
            if (_refreshRequested && !_acting && IsLoaded &&
                !Dispatcher.HasShutdownStarted)
            {
                _refreshRequested = false;
                RequestRefresh();
            }
        }
    }

    private string DirectoryFor(DurablePackageRecord item, string? currentPath)
    {
        // The authoritative root of a manual scan is immutable in the
        // durable recipe. A yield/restart clears CurrentPath by design; it
        // must never erase the folder shown in this task's tile.
        if (item.Type == "manual-folder-scan-v1" &&
            DirectoryFromRecipe(item.Payload) is string root)
        {
            _initialDirectoryByKey[item.Key] = root;
            return root;
        }
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var current = CurrentDirectory(currentPath);
            if (current.Length > 0 && current != "–")
                _initialDirectoryByKey[item.Key] = current;
            return current;
        }
        if (_initialDirectoryByKey.TryGetValue(item.Key, out var cached))
            return cached;
        var path = FirstPathFromRecipe(item.Payload);
        if (path is null)
        {
            var id = FirstImageId(item.Payload);
            if (id > 0)
            {
                using var db = _database.OpenConnection();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT local_path FROM images WHERE id=$id;";
                cmd.Parameters.AddWithValue("$id", id);
                path = cmd.ExecuteScalar() as string;
            }
        }
        var dir = CurrentDirectory(path);
        if (dir.Length > 0)
            _initialDirectoryByKey[item.Key] = dir;
        return dir;
    }

    private static string CurrentDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "–";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) &&
                extension.Length <= 8 &&
                extension.All(ch => char.IsLetterOrDigit(ch) || ch == '.')
                ? Path.GetDirectoryName(path) ?? path : path;
        }
        catch { return path; }
    }

    private static bool AcknowledgesPendingImport(DurablePackageRecord record)
    {
        if (record.Type != "manual-folder-scan-v1") return false;
        try
        {
            using var json = JsonDocument.Parse(record.Payload);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("AcknowledgeTargetedPending", out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string? DirectoryFromRecipe(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("Directory", out var element) &&
                element.ValueKind == JsonValueKind.String)
            {
                var path = element.GetString();
                if (!string.IsNullOrWhiteSpace(path) &&
                    Path.IsPathFullyQualified(path))
                    return path;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string? FirstPathFromRecipe(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            return Search(json.RootElement);
        }
        catch (JsonException) { return null; }

        static string? Search(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in node.EnumerateObject())
                    if (member.Value.ValueKind == JsonValueKind.String &&
                        (member.Name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
                             member.Name.Contains("directory", StringComparison.OrdinalIgnoreCase)))
                    {
                        var value = member.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value) &&
                            Path.IsPathFullyQualified(value)) return value;
                    }
                foreach (var member in node.EnumerateObject())
                    if (Search(member.Value) is { } found) return found;
            }
            if (node.ValueKind == JsonValueKind.Array)
                foreach (var member in node.EnumerateArray())
                    if (Search(member) is { } found) return found;
            return null;
        }
    }

    private static long FirstImageId(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("ImageIds", out var ids) &&
                ids.ValueKind == JsonValueKind.Array &&
                ids.GetArrayLength() > 0 &&
                ids[0].TryGetInt64(out var id)) return id;
        }
        catch (JsonException) { }
        return 0;
    }

    private static int CountRecipeImages(string recipe)
    {
        try
        {
            using var json = JsonDocument.Parse(recipe);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("ImageIds", out var ids) &&
                   ids.ValueKind == JsonValueKind.Array
                ? ids.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    private static string TaskCaption(DurablePackageRecord item)
        => item.Type switch
        {
            "gps-edit-v1" => item.Payload.Contains("\"Mode\":2", StringComparison.Ordinal)
                ? UiLanguage.T("GPS entfernen") : UiLanguage.T("GPS schreiben"),
            "tag-edit-v1" or "tag-edit-v2" => UiLanguage.T("Tags schreiben"),
            "tag-recovery-v1" or "tag-recovery-v2" => UiLanguage.T("Tags wiederherstellen"),
            "manual-folder-scan-v1" => UiLanguage.T("Ordner aktualisieren"),
            "manual-scan-all-v1" => UiLanguage.T("Alle Ordner einlesen"),
            "gallery-transfer-v1" or "transfer-reconcile-v1" => UiLanguage.T("Dateien übertragen"),
            "gallery-delete-v1" => UiLanguage.T("Dateien löschen"),
            "create-gallery-folder-v1" => UiLanguage.T("Ordner anlegen"),
            _ => item.Type
        };

    private static string BackgroundCaption(string key)
        => key switch
        {
            var value when value.StartsWith("catalog:", StringComparison.Ordinal) =>
                UiLanguage.T("Katalog aktualisieren"),
            var value when value.StartsWith("tags:", StringComparison.Ordinal) =>
                UiLanguage.T("Tags überprüfen"),
            var value when value.Contains("cache", StringComparison.OrdinalIgnoreCase) =>
                UiLanguage.T("Vorschaubilder aktualisieren"),
            _ => UiLanguage.T("Hintergrundarbeit")
        };

    private void StartNow()
    {
        var row = Selection;
        if (row is not { Priority: 4, Status: "Wartet" } ||
            !IsRootReview(row.Key) || _startAutomaticNow is null) return;
        _ = PerformAsync(row.Key, () => _startAutomaticNow(row.Key));
    }

    private void Move(int direction)
    {
        var row = Selection;
        if (row is null || !CanMove(row, direction)) return;
        if (_moveGlobalTask is not null)
        {
            var all = _all.Where(item => item.Status != "Abgeschlossen").ToArray();
            var index = Array.FindIndex(all, item => item.Key == row.Key);
            var neighbor = all[index + direction];
            var accepted = all.Select(item => item.Key).ToArray();
            _ = PerformAsync(row.Key, () =>
                _moveGlobalTask(row.Key, neighbor.Key, accepted));
            return;
        }
        if (direction == -1 && IsRootReview(row.Key) &&
            ReorderLane(row).FirstOrDefault()?.Key == row.Key &&
            row.Priority == 4 && _startAutomaticNow is not null)
        {
            _ = PerformAsync(row.Key, () => _startAutomaticNow(row.Key));
            return;
        }
        if ((IsInitialImport(row.Key) || IsRootReview(row.Key)) &&
            _moveAutomaticScan is not null)
        {
            var lane = ReorderLane(row);
            var index = Array.FindIndex(lane, other => other.Key == row.Key);
            if (index < 0 || index + direction < 0 ||
                index + direction >= lane.Length) return;
            _ = PerformAsync(row.Key, () =>
                _moveAutomaticScan(row.Key, lane[index + direction].Key));
            return;
        }
        _ = PerformAsync(row.Key, () => Task.Run(() =>
        {
            if (!_scheduler.MoveDurableTask(row.Key, direction))
                throw new InvalidOperationException(
                    "Innerhalb dieser Prioritätsstufe ist kein weiterer Platz verfügbar.");
            return true;
        }));
    }

    private void Restart()
    {
        var row = Selection;
        if (row is not { Retryable: true }) return;
        if (IsInitialImport(row.Key) && _resumeAutomaticScan is not null)
        {
            _ = PerformAsync(row.Key, () => _resumeAutomaticScan(row.Key));
            return;
        }
        if (_failedRows.TryGetValue(row.Key, out var failedFiles))
        {
            _ = PerformAsync(row.Key, async () =>
            {
                foreach (var file in failedFiles)
                    await _retryFailedFile!(file);
                return true;
            });
            return;
        }
        _ = PerformAsync(row.Key, () => Task.Run(() =>
        {
            if (!_scheduler.RetryDurableTask(row.Key))
                throw new InvalidOperationException("Der Auftrag ist nicht mehr fehlerhaft.");
            return true;
        }));
    }

    private void Delete()
    {
        var row = Selection;
        if (row is not { Deletable: true }) return;
        var prompt = IsInitialImport(row.Key)
            ? UiLanguage.Format(
                "Scanauftrag '{0}' aus der Warteschlange löschen? Bereits eingelesene Fotos, Katalogeinträge und bestätigte Scanpositionen bleiben erhalten.",
                row.Title)
            : !row.Durable && !row.Key.StartsWith("failed-files:",
                StringComparison.Ordinal)
                ? UiLanguage.Format(
                    "Auftrag '{0}' stoppen? Der bestätigte Einlesefortschritt bleibt erhalten.",
                    row.Title)
                : UiLanguage.Format(
                    "Auftrag '{0}' wirklich löschen? Bereits erfolgreich geschriebene Dateien werden nicht zurückgesetzt.",
                    row.Title);
        if (MessageBox.Show(this, prompt, UiLanguage.T("Task löschen"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (_failedRows.TryGetValue(row.Key, out var failedFiles))
        {
            _ = PerformAsync(row.Key, async () =>
            {
                foreach (var file in failedFiles)
                    await _discardFailedFile!(file);
                return true;
            });
        }
        // Initial imports and watcher scans use the single original scan
        // owner, never the durable manual-package deleter. For an initial
        // import this delegate removes the queued intent even if held.
        else if (!row.Durable && _stopAutomaticScan is not null)
            _ = PerformAsync(row.Key, () => _stopAutomaticScan(row.Key));
        else
            _ = PerformAsync(row.Key, () => Task.Run(() => _scheduler.DeleteDurableTaskAsync(row.Key)));
    }

    private async Task PerformAsync(string key, Func<Task<bool>> action)
    {
        if (_acting) return;
        _acting = true;
        UpdateActions();
        try
        {
            await action();
            // ApplyFilters already preserves the currently selected task
            // by its stable key. Do NOT forcibly reselect the task whose
            // action just completed: another row may have been clicked
            // while an asynchronous SQLite update was in flight.
            await RefreshAsyncAfterAction();
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("Taskmanager.Action", ex);
            MessageBox.Show(this, ex.Message, UiLanguage.T("Taskmanager"),
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            _acting = false;
            UpdateActions();
        }
    }

    private async Task RefreshAsyncAfterAction()
    {
        _acting = false;
        try { await RefreshAsync(); }
        finally { _acting = true; }
    }
}
