using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;
using TaskRow = PCasa.App.Services.SchedulerTaskViewItem;

namespace PCasa.App;

/// <summary>
/// Read-only view of the ONE scheduler/journal plus three commands routed
/// back into that same owner. No local task queue, priority list or retry
/// executor exists in this window.
/// </summary>
public sealed class TaskManagerWindow : Window
{
    private readonly PCasaScheduler _scheduler;
    private readonly SchedulerTaskView _taskView;
    private readonly Func<OriginalWriteFailureRepository.FailedFile, Task>? _retryFailedFile;
    private readonly Func<OriginalWriteFailureRepository.FailedFile, Task>? _discardFailedFile;
    private readonly Func<IReadOnlyList<AutomaticScanTask>>? _automaticScans;
    private readonly Func<string, Task<bool>>? _stopAutomaticScan;
    private readonly Func<string>? _idleScanStatus;
    private readonly Func<string,string,Task<bool>>? _moveAutomaticScan;
    private readonly Func<string,Task<bool>>? _resumeAutomaticScan;
    private readonly Func<string,Task<bool>>? _startAutomaticNow;
    private readonly Func<string,Task<bool>>? _pauseInitialImport;
    private readonly Func<string,string,IReadOnlyList<SchedulerTaskOrderItem>,Task<bool>>?
        _moveGlobalTask;
    private Dictionary<string, IReadOnlyList<OriginalWriteFailureRepository.FailedFile>> _failedRows = new();
    private readonly DataGrid _list = new();
    private readonly ObservableCollection<TaskRow> _visible = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly CheckBox _running = new(), _waiting = new(),
        _errors = new(), _completed = new();
    private readonly Button _up = new(), _down = new(),
        _startNow = new(), _restart = new(), _pauseResume = new(), _delete = new();
    private readonly Button _pauseResume;
    private readonly TextBlock _detail = new();
    private List<TaskRow> _all = [];
    private bool _reading;
    private bool _acting;
    private bool _refreshRequested;
    // A one-shot coalescer, never a polling loop. Each tick is caused by
    // an actual scheduler change notification.
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
        Func<string,Task<bool>>? pauseInitialImport = null,
        Func<string,string,IReadOnlyList<SchedulerTaskOrderItem>,Task<bool>>?
            moveGlobalTask = null)
    {
        _scheduler = scheduler;
        _taskView = scheduler.CreateTaskView(database,
            moveAutomaticScan is not null, resumeAutomaticScan is not null,
            stopAutomaticScan is not null, retryFailedFile is not null,
            discardFailedFile is not null);
        _retryFailedFile = retryFailedFile;
        _discardFailedFile = discardFailedFile;
        _automaticScans = automaticScans;
        _stopAutomaticScan = stopAutomaticScan;
        _idleScanStatus = idleScanStatus;
        _moveAutomaticScan = moveAutomaticScan;
        _resumeAutomaticScan = resumeAutomaticScan;
        _startAutomaticNow = startAutomaticNow;
        _pauseInitialImport = pauseInitialImport;
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
        AddColumn("Status", nameof(TaskRow.LocalizedStatus), new DataGridLength(92));
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
        Button(_pauseResume, "Pausieren", PauseResume, actions);
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
            Interval = TimeSpan.FromMilliseconds(200)
        };
        // Single-shot: never periodically query SQLite without a change.
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RequestRefresh();
        };
        Loaded += (_, _) =>
        {
            _scheduler.TaskViewChanged += SchedulerTaskViewChanged;
            RequestRefresh();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _scheduler.TaskViewChanged -= SchedulerTaskViewChanged;
        };
        UpdateActions();
    }

    private void SchedulerTaskViewChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(SchedulerTaskViewChanged));
            return;
        }
        if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
        // Debounce bursty progress callbacks without dropping the FINAL event.
        // The timer does not run unless the scheduler actually changed.
        if (!_refreshTimer.IsEnabled)
            _refreshTimer.Start();
    }

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
            if (IsLoaded)
                RequestRefresh();
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
                _moveAutomaticScan is not null))))
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
                .Select(row => new SchedulerTaskOrderItem(
                    row.Key, (SchedulerPriority)row.Priority)).ToArray();
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
            // Buttons operate on the FILTERED visible neighbors, not on
            // hidden task rows. The scheduler still receives the complete
            // unfiltered accepted order so invisible tasks keep their rank.
            var visible = _visible.Where(item =>
                item.Status != "Abgeschlossen").ToArray();
            var globalIndex = Array.FindIndex(
                visible, item => item.Key == row.Key);
            return globalIndex >= 0 && globalIndex + direction >= 0 &&
                globalIndex + direction < visible.Length;
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
        _startNow.IsEnabled = !_acting && row is { Status: "Wartet" } &&
            _startAutomaticNow is not null &&
            (IsRootReview(row.Key) || IsInitialImport(row.Key));
        // Resume is no longer disguised as "Neu starten"; that button
        // means retry after an actual failure, never resume a paused scan.
        _restart.IsEnabled = !_acting && row is { Status: "Fehler", Retryable: true };
        var canResume = row is { Status: "Angehalten" } &&
            IsInitialImport(row.Key) && _resumeAutomaticScan is not null;
        var canPause = row is { Status: "Läuft" or "Wartet" } &&
            IsInitialImport(row.Key) && _pauseInitialImport is not null;
        _pauseResume.Content = UiLanguage.T(canResume ? "Fortsetzen" : "Pausieren");
        _pauseResume.IsEnabled = !_acting && (canResume || canPause);
        _pauseResume.ToolTip = UiLanguage.T(
            canResume ? "Erstimport mit gespeichertem Fortschritt fortsetzen" :
            canPause ? "Erstimport am nächsten sicheren Punkt pausieren" :
            "Für diese Auftragsart ist sicheres Pausieren noch nicht verfügbar");
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
                : _moveGlobalTask is not null
                    // A global move is always available for non-edge rows.
                    // The old lane-specific explanation is not applicable
                    // when the single scheduler owns every task position.
                    ? row.Status == "Läuft"
                        ? UiLanguage.T("Läuft; Änderungen der Reihenfolge werden am nächsten sicheren Übergabepunkt wirksam.")
                        : row.Directory
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
            // The window reads ONE scheduler-owned projection and applies
            // filters only. No independent journal/scan/error joins here.
            var automaticScans = _automaticScans?.Invoke() ??
                Array.Empty<AutomaticScanTask>();
            var showCompleted = _completed.IsChecked == true;
            var prepared = await Task.Run(() =>
                _taskView.Snapshot(automaticScans, showCompleted));
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

    private void StartNow()
    {
        var row = Selection;
        if (row is not { Status: "Wartet" } || _startAutomaticNow is null ||
            (!IsRootReview(row.Key) && !IsInitialImport(row.Key))) return;
        _ = PerformAsync(row.Key, () => _startAutomaticNow(row.Key));
    }

    private void Move(int direction)
    {
        var row = Selection;
        if (row is null || !CanMove(row, direction)) return;
        if (_moveGlobalTask is not null)
        {
            var visible = _visible.Where(item =>
                item.Status != "Abgeschlossen").ToArray();
            var index = Array.FindIndex(visible, item => item.Key == row.Key);
            var neighbor = visible[index + direction];
            var accepted = _all.Where(item => item.Status != "Abgeschlossen")
                .Select(item => new SchedulerTaskOrderItem(
                    item.Key, (SchedulerPriority)item.Priority)).ToArray();
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

    private void PauseResume()
    {
        var row = Selection;
        if (row is null || !IsInitialImport(row.Key)) return;
        if (row.Status == "Angehalten" && _resumeAutomaticScan is not null)
        {
            _ = PerformAsync(row.Key, () => _resumeAutomaticScan(row.Key));
            return;
        }
        if (row.Status is "Läuft" or "Wartet" && _pauseInitialImport is not null)
            _ = PerformAsync(row.Key, () => _pauseInitialImport(row.Key));
    }

    private void Delete()
    {
        var row = Selection;
        if (row is not { Deletable: true }) return;
        var prompt = IsInitialImport(row.Key)
            ? UiLanguage.Format(
                "Scanauftrag '{0}' aus der Warteschlange löschen? Bereits eingelesene Fotos, Katalogeinträge und bestätigte Scanpositionen bleiben erhalten.",
                row.Title)
            : !row.Durable && !_failedRows.ContainsKey(row.Key)
                ? UiLanguage.Format(
                    "Auftrag '{0}' stoppen? Der bestätigte Einlesefortschritt bleibt erhalten.",
                    row.Title)
                : UiLanguage.Format(
                    "Auftrag '{0}' wirklich löschen? Bereits erfolgreich geschriebene Dateien werden nicht zurückgesetzt.",
                    row.Title);
        if (PcasaDialog.Show(this, prompt, UiLanguage.T("Task löschen"),
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
            PcasaDialog.Show(this, ex.Message, UiLanguage.T("Taskmanager"),
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
