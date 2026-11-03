using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
        bool Durable, bool Movable, bool Retryable, bool Deletable);

    private readonly PCasaScheduler _scheduler;
    private readonly DurablePackageJournal _journal;
    private readonly LocalDatabase _database;
    private readonly DataGrid _list = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<string, string> _initialDirectoryByKey = new();
    private readonly CheckBox _running = new(), _waiting = new(),
        _errors = new(), _completed = new();
    private readonly Button _up = new(), _down = new(),
        _restart = new(), _delete = new();
    private readonly TextBlock _detail = new();
    private List<TaskRow> _all = [];
    private bool _reading;
    private bool _acting;

    public TaskManagerWindow(PCasaScheduler scheduler, LocalDatabase database)
    {
        _scheduler = scheduler;
        _database = database;
        _journal = new DurablePackageJournal(database);
        Title = "PCasa – " + UiLanguage.T("Taskmanager");
        Width = 1050; Height = 570;
        MinWidth = 840; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "PcasaPanelBrush");
        SetResourceReference(ForegroundProperty, "PcasaTextBrush");

        var outer = new Grid { Margin = new Thickness(14, 10, 14, 11) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
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
        _list.SelectionMode = DataGridSelectionMode.Single;
        _list.SelectionUnit = DataGridSelectionUnit.FullRow;
        _list.HeadersVisibility = DataGridHeadersVisibility.Column;
        _list.RowHeaderWidth = 0;
        _list.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _list.FontSize = 12;
        _list.EnableRowVirtualization = true;
        _list.EnableColumnVirtualization = true;
        _list.Margin = new Thickness(7);
        AddColumn("Aufgabe", nameof(TaskRow.Title), 1.25);
        AddColumn("Verzeichnis", nameof(TaskRow.Directory), 2.4);
        AddColumn("Fortschritt", nameof(TaskRow.Progress), .8);
        AddColumn("Status", nameof(TaskRow.Status), .8);
        _list.SelectionChanged += (_, _) => UpdateActions();
        var listFrame = Frame(_list);
        Grid.SetRow(listFrame, 2);
        outer.Children.Add(listFrame);

        var footer = new Grid { Margin = new Thickness(4, 8, 4, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _detail.VerticalAlignment = VerticalAlignment.Center;
        _detail.FontSize = 11;
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        _detail.Margin = new Thickness(4, 0, 10, 0);
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "PcasaTextBrush");
        footer.Children.Add(_detail);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Button(_up, "Nach oben", () => Move(-1), actions);
        Button(_down, "Nach unten", () => Move(1), actions);
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
        Grid.SetRow(footer, 3);
        outer.Children.Add(footer);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(850)
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        Loaded += (_, _) => { _refreshTimer.Start(); _ = RefreshAsync(); };
        Closed += (_, _) => _refreshTimer.Stop();
        UpdateActions();
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

    private void AddColumn(string label, string property, double weight)
    {
        var column = new DataGridTextColumn
        {
            Header = UiLanguage.T(label),
            Binding = new Binding(property),
            Width = new DataGridLength(weight, DataGridLengthUnitType.Star)
        };
        if (property == nameof(TaskRow.Directory))
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,
                TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
                new Binding(nameof(TaskRow.Directory))));
            column.ElementStyle = textStyle;
        }
        _list.Columns.Add(column);
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

    private TaskRow? Selection => _list.SelectedItem as TaskRow;

    private void ApplyFilters()
    {
        var key = Selection?.Key;
        _list.ItemsSource = _all.Where(row => row.Status switch
        {
            "Läuft" => _running.IsChecked == true,
            "Wartet" => _waiting.IsChecked == true,
            "Fehler" => _errors.IsChecked == true,
            "Abgeschlossen" => _completed.IsChecked == true,
            _ => false
        }).ToList();
        if (key is not null)
            _list.SelectedItem = _list.Items.Cast<TaskRow>()
                .FirstOrDefault(item => item.Key == key);
        UpdateActions();
    }

    private void UpdateActions()
    {
        var row = Selection;
        _up.IsEnabled = !_acting && row is { Movable: true };
        _down.IsEnabled = !_acting && row is { Movable: true };
        _restart.IsEnabled = !_acting && row is { Retryable: true };
        _delete.IsEnabled = !_acting && row is { Deletable: true };
        _detail.Text = row?.Error ?? row?.Directory ?? "";
        _detail.ToolTip = _detail.Text;
    }

    private async Task RefreshAsync()
    {
        if (_reading || _acting || !IsLoaded || Dispatcher.HasShutdownStarted) return;
        _reading = true;
        try
        {
            var showCompleted = _completed.IsChecked == true;
            var snapshots = _scheduler.Snapshot();
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
                foreach (var record in active.Concat(done))
                {
                    latest.TryGetValue(record.Key, out var snapshot);
                    var status = record.State switch
                    {
                        "completed" => "Abgeschlossen",
                        "deferred" => "Fehler",
                        _ when snapshot?.State == SchedulerJobState.Running => "Läuft",
                        _ when snapshot?.State == SchedulerJobState.Failed => "Fehler",
                        _ => "Wartet"
                    };
                    var work = TaskCaption(record);
                    var path = DirectoryFor(record, snapshot?.CurrentPath);
                    var total = CountRecipeImages(record.Payload);
                    var progress = snapshot is { Total: > 0 }
                        ? $"{snapshot.Processed:N0}/{snapshot.Total:N0}"
                        : total > 0
                            ? $"{Math.Clamp(record.CompletedSteps - (record.Type.StartsWith("tag-edit-", StringComparison.Ordinal) ? 1 : 0), 0, total):N0}/{total:N0}"
                            : record.CompletedSteps > 0
                                ? record.CompletedSteps.ToString("N0") : "–";
                    var isWaiting = status == "Wartet";
                    rows.Add(new TaskRow(record.Key, work, path, progress,
                        status, record.Error ?? snapshot?.Error,
                        (int)record.Priority, record.QueueOrder, true,
                        isWaiting, status == "Fehler" && record.State == "deferred",
                        record.State is "queued" or "deferred" or "running"));
                }
                var keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var snapshot in snapshots)
                {
                    if (keys.Contains(snapshot.Key) ||
                        snapshot.State is SchedulerJobState.Canceled ||
                        snapshot.WorkKind is SchedulerWorkKind.CatalogRead or
                            SchedulerWorkKind.ThumbnailRead or SchedulerWorkKind.ViewerRead)
                        continue;
                    if (snapshot.Priority is SchedulerPriority.Interactive &&
                        snapshot.WorkKind == SchedulerWorkKind.Catalog)
                        continue; // transient UI publication, not an operation
                    var status = snapshot.State switch
                    {
                        SchedulerJobState.Running => "Läuft",
                        SchedulerJobState.Queued or SchedulerJobState.YieldRequested => "Wartet",
                        SchedulerJobState.Failed => "Fehler",
                        SchedulerJobState.Completed => "Abgeschlossen",
                        _ => "Wartet"
                    };
                    if (status == "Abgeschlossen" && !showCompleted) continue;
                    rows.Add(new TaskRow(snapshot.Key, BackgroundCaption(snapshot.Key),
                        CurrentDirectory(snapshot.CurrentPath), snapshot.Total > 0
                            ? $"{snapshot.Processed:N0}/{snapshot.Total:N0}" : "–",
                        status, snapshot.Error, (int)snapshot.Priority,
                        snapshot.Priority == SchedulerPriority.ManualAction
                            ? -snapshot.Id : snapshot.Id, false, false, false, false));
                }
                return rows.OrderBy(row => row.Status == "Abgeschlossen" ? 1 : 0)
                    .ThenBy(row => row.Priority)
                    .ThenBy(row => row.Order).ThenBy(row => row.Key)
                    .ToList();
            });
            if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
            _all = prepared;
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _detail.Text = ex.Message;
            PCasaDiagnostics.Write("Taskmanager.Read", ex);
        }
        finally { _reading = false; }
    }

    private string DirectoryFor(DurablePackageRecord item, string? currentPath)
    {
        if (!string.IsNullOrWhiteSpace(currentPath))
            return CurrentDirectory(currentPath);
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
                        member.Name.Contains("path", StringComparison.OrdinalIgnoreCase))
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

    private void Move(int direction)
    {
        var row = Selection;
        if (row is not { Movable: true }) return;
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
        var prompt = UiLanguage.Format(
            "Auftrag '{0}' wirklich löschen? Bereits erfolgreich geschriebene Dateien werden nicht zurückgesetzt.",
            row.Title);
        if (MessageBox.Show(this, prompt, UiLanguage.T("Task löschen"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
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
            await RefreshAsyncAfterAction();
            _list.SelectedItem = _list.Items.Cast<TaskRow>()
                .FirstOrDefault(item => item.Key == key);
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
