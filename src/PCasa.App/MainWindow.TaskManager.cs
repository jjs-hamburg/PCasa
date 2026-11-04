using System.Windows;
using PCasa.App.Services;

namespace PCasa.App;

// Read-only projection of the existing automatic scan queue and current
// scheduler execution. The taskmanager never owns a second scanner or queue.
public sealed record AutomaticScanTask(
    string Key, string Title, string Directory, string Progress,
    string Status, int Priority, long Order);

public partial class MainWindow
{
    private TaskManagerWindow? _taskManagerWindow;

    private void TaskManagerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_taskManagerWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        var manager = new TaskManagerWindow(_scheduler, _database,
            RetryOriginalWriteAsync, DiscardOriginalWriteAsync,
            SnapshotAutomaticScans, StopAutomaticScanAsync)
        {
            Owner = this
        };
        _taskManagerWindow = manager;
        manager.Closed += (_, _) =>
        {
            if (ReferenceEquals(_taskManagerWindow, manager))
                _taskManagerWindow = null;
        };
        manager.Show();
    }
}
