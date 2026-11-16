using System.Windows.Input;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class MainWindow
{
    // This is a throttled VIEW of the existing Taskmanager projection.
    // It adds no queue, scheduler policy or background scanner.
    private SchedulerTaskView? _taskActivityProjection;
    private DispatcherTimer? _taskActivityTimer;
    private bool _taskActivityReading;
    private bool _taskActivityRefreshRequested;

    private void InitializeTaskActivityIndicator()
    {
        _taskActivityProjection = _scheduler.CreateTaskView(_database,
            false, false, false, false, false);
        _taskActivityTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _taskActivityTimer.Tick += TaskActivityTimer_Tick;
        _scheduler.TaskViewChanged += TaskActivityChanged;
        Loaded += (_, _) => TaskActivityChanged();
        Closed += (_, _) =>
        {
            _scheduler.TaskViewChanged -= TaskActivityChanged;
            _taskActivityTimer?.Stop();
        };
    }

    private void TaskActivityText_Click(object sender, MouseButtonEventArgs e)
    {
        TaskManagerMenu_Click(sender, e);
        e.Handled = true;
    }

    private void TaskActivityChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                _ = Dispatcher.BeginInvoke(new Action(TaskActivityChanged));
            return;
        }
        if (!IsLoaded || Dispatcher.HasShutdownStarted) return;
        if (_taskActivityReading)
        {
            _taskActivityRefreshRequested = true;
            return;
        }
        // Do not postpone forever during frequent scanner progress events.
        if (_taskActivityTimer is { IsEnabled: false } timer)
            timer.Start();
    }

    private async void TaskActivityTimer_Tick(object? sender, EventArgs e)
    {
        _taskActivityTimer?.Stop();
        if (_taskActivityReading || Dispatcher.HasShutdownStarted) return;
        _taskActivityReading = true;
        try
        {
            // The existing Taskmanager already snapshots automatic intents
            // on Dispatcher and joins them with the scheduler/journal off it.
            var automatic = SnapshotAutomaticScans();
            var rows = await Task.Run(() =>
                _taskActivityProjection!.Snapshot(automatic, false).Rows);
            if (Dispatcher.HasShutdownStarted) return;
            var running = rows.Count(row => row.Status == "Läuft");
            var waiting = rows.Count(row => row.Status == "Wartet");
            var paused = rows.Count(row => row.Status == "Angehalten");
            var errors = rows.Count(row => row.Status == "Fehler");
            TaskActivityText.Text = running + " " + UiLanguage.T("läuft") +
                " · " + waiting + " " + UiLanguage.T("wartet") +
                " · " + paused + " " + UiLanguage.T("angehalten") +
                " · " + errors + " " + UiLanguage.T("Fehler");
            TaskActivityText.ToolTip = rows.Count == 0
                ? UiLanguage.T("Keine Aufträge aktiv. Taskmanager öffnen.")
                : string.Join(Environment.NewLine,
                    rows.Take(8).Select(row =>
                        row.Title + " – " + row.LocalizedStatus +
                        (row.Progress == "–" ? "" : " · " + row.Progress))) +
                    Environment.NewLine + UiLanguage.T("Klicken: Taskmanager öffnen");
        }
        catch (Exception ex)
        {
            PCasaDiagnostics.Write("TaskActivity.Status", ex);
            if (!Dispatcher.HasShutdownStarted)
            {
                TaskActivityText.Text = UiLanguage.T("Auftragsstatus nicht verfügbar");
                TaskActivityText.ToolTip = ex.Message;
            }
        }
        finally
        {
            _taskActivityReading = false;
            if (_taskActivityRefreshRequested && !Dispatcher.HasShutdownStarted)
            {
                _taskActivityRefreshRequested = false;
                TaskActivityChanged();
            }
        }
    }
}
