using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PCasa.App;

public partial class MainWindow
{
    private Window? _schedulerMonitorWindow;

    private void SchedulerMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerMonitorWindow is { IsVisible: true })
        {
            _schedulerMonitorWindow.Activate();
            return;
        }

        var list = new ListView { Margin = new Thickness(10) };
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "ID", DisplayMemberBinding = new System.Windows.Data.Binding("Id"), Width = 55 });
        grid.Columns.Add(new GridViewColumn { Header = "Auftrag", DisplayMemberBinding = new System.Windows.Data.Binding("Key"), Width = 320 });
        grid.Columns.Add(new GridViewColumn { Header = "Priorität", DisplayMemberBinding = new System.Windows.Data.Binding("Priority"), Width = 95 });
        grid.Columns.Add(new GridViewColumn { Header = "Status", DisplayMemberBinding = new System.Windows.Data.Binding("State"), Width = 100 });
        grid.Columns.Add(new GridViewColumn { Header = "Eingang", DisplayMemberBinding = new System.Windows.Data.Binding("SubmittedAt"), Width = 175 });
        grid.Columns.Add(new GridViewColumn { Header = "Laufzeit", DisplayMemberBinding = new System.Windows.Data.Binding("Duration"), Width = 100 });
        grid.Columns.Add(new GridViewColumn { Header = "Fehler", DisplayMemberBinding = new System.Windows.Data.Binding("Error"), Width = 220 });
        list.View = grid;

        var window = new Window
        {
            Title = "PCasa – Scheduler-Monitor (nur Anzeige)",
            Owner = this,
            Width = 1130,
            Height = 440,
            MinWidth = 650,
            MinHeight = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = list
        };
        _schedulerMonitorWindow = window;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        void Refresh()
        {
            var snapshot = _scheduler.Snapshot();
            list.ItemsSource = snapshot.OrderByDescending(item => item.Id).Take(250).ToArray();
        }
        timer.Tick += (_, _) => Refresh();
        window.Closed += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_schedulerMonitorWindow, window))
                _schedulerMonitorWindow = null;
        };
        Refresh();
        timer.Start();
        window.Show();
    }
}
