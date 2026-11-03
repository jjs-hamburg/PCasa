using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class TaskManagerFlow
{
    [STAThread]
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PCasa-Taskmanager-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        PCasaScheduler? scheduler = null;
        PCasa.App.TaskManagerWindow? manager = null;
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var app = new PCasa.App.App();
            app.InitializeComponent();
            var database = new LocalDatabase(Path.Combine(dir, "pcasa.db"));
            database.Initialize();
            var journal = new DurablePackageJournal(database);
            scheduler = new PCasaScheduler();
            scheduler.EnableDurablePackages(database);
            var executed = new List<string>();
            object sync = new();
            SchedulerTaskPackage Make(string key, string payload)
            {
                var pkg = new SchedulerTaskPackage(key);
                pkg.AddStep(token =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (sync) executed.Add(key);
                    return Task.CompletedTask;
                });
                return pkg;
            }
            scheduler.RegisterDurablePackageType("taskmanager-test-v1", Make);
            scheduler.RecoverDurablePackages();

            // Occupy only the real manual lane. A newer P0 may ask it to
            // yield, but the blocking test step ignores cancellation until
            // released, just like non-interruptible native file I/O.
            var blockerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync("taskmanager:blocker",
                SchedulerPriority.ManualAction, async _ =>
                {
                    blockerStarted.TrySetResult();
                    await releaseBlocker.Task;
                });
            blockerStarted.Task.Wait(TimeSpan.FromSeconds(10));
            var a = scheduler.EnqueueDurablePackageAsync(
                Make("taskmanager:a", "{}"), "taskmanager-test-v1", "{}",
                SchedulerPriority.ManualAction);
            var b = scheduler.EnqueueDurablePackageAsync(
                Make("taskmanager:b", "{}"), "taskmanager-test-v1", "{}",
                SchedulerPriority.ManualAction);
            var c = scheduler.EnqueueDurablePackageAsync(
                Make("taskmanager:c", "{}"), "taskmanager-test-v1", "{}",
                SchedulerPriority.ManualAction);
            var order = journal.Pending().Where(item =>
                item.Type == "taskmanager-test-v1").Select(item => item.Key).ToArray();
            if (!order.SequenceEqual(new[] { "taskmanager:c", "taskmanager:b", "taskmanager:a" }))
                throw new Exception("Existing P0 newest-first order was lost.");
            if (!scheduler.MoveDurableTask("taskmanager:a", -1))
                throw new Exception("Task manager could not move a queued durable job.");
            order = new DurablePackageJournal(database).Pending()
                .Where(item => item.Type == "taskmanager-test-v1")
                .Select(item => item.Key).ToArray();
            if (!order.SequenceEqual(new[] { "taskmanager:c", "taskmanager:a", "taskmanager:b" }))
                throw new Exception("Moved task order did not persist in the SQLite journal.");

            if (!scheduler.DeleteDurableTaskAsync("taskmanager:b").GetAwaiter().GetResult() ||
                journal.Pending().Any(item => item.Key == "taskmanager:b"))
                throw new Exception("Deleting a queued task did not durably cancel it.");

            // A started tag package is allowed to hold user-visible tag
            // intent in SQLite. Deleting it must NOT silently orphan data.
            journal.Put("taskmanager:protected", "tag-edit-v2", "{}",
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            journal.SetState("taskmanager:protected", "running", 1);
            var refused = false;
            try
            {
                scheduler.DeleteDurableTaskAsync("taskmanager:protected")
                    .GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { refused = true; }
            if (!refused || journal.Pending().All(row =>
                    row.Key != "taskmanager:protected"))
                throw new Exception("Taskmanager silently deleted committed tag intent.");
            journal.Cancel("taskmanager:protected");

            manager = new PCasa.App.TaskManagerWindow(scheduler, database);
            manager.Show();
            Pump(manager);
            var checks = Descendants<CheckBox>(manager).ToArray();
            var labels = checks.Select(box => box.Content?.ToString()).ToArray();
            if (!labels.SequenceEqual(new[] { "Läuft", "Wartet", "Fehler", "Abgeschlossen" }) ||
                checks.Where((_, index) => index < 3).Any(box => box.IsChecked != true) ||
                checks[3].IsChecked != false)
                throw new Exception("Task status filters do not use the requested compact default.");
            var grid = Descendants<DataGrid>(manager).Single();
            if (!grid.Columns.Select(col => col.Header?.ToString())
                    .SequenceEqual(new[] { "Aufgabe", "Verzeichnis", "Fortschritt", "Status" }) ||
                grid.CanUserSortColumns)
                throw new Exception("Taskmanager lost its four plain-text columns / dispatch order.");
            Until(manager, () => grid.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                    == "taskmanager:a"), "Queued task is absent from its live scheduler view.");
            manager.Close();
            manager = null;

            releaseBlocker.TrySetResult();
            Task.WhenAll(a, c, blocker).Wait(TimeSpan.FromSeconds(15));
            lock (sync)
                if (!executed.SequenceEqual(new[] { "taskmanager:c", "taskmanager:a" }))
                    throw new Exception("Scheduler ignored the persisted task manager order: " +
                        string.Join(", ", executed));
            try { b.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            if (b.Status != TaskStatus.Canceled)
                throw new Exception("Deleted task was still executed.");

            var deferred = journal.Put("taskmanager:retry", "taskmanager-test-v1",
                "{}", SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            journal.Defer(deferred.Key, 0, "Unreachable original",
                TimeSpan.FromHours(1));
            if (!scheduler.RetryDurableTask(deferred.Key))
                throw new Exception("Manual retry did not reactivate a deferred job.");
            UntilBackground(() => journal.Pending().All(item => item.Key != deferred.Key),
                "Manually retried task did not complete.");
            if (!journal.RecentCompleted().Any(item =>
                    item.Key == deferred.Key && item.CompletedSteps == 1))
                throw new Exception("Retried durable task lost its checkpoint.");
            Console.WriteLine(
                "PASS: Compact taskmanager, filters, P0 order, durable move/delete, protected tags and retry.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: Taskmanager regression: " + ex);
            return 1;
        }
        finally
        {
            releaseBlocker.TrySetResult();
            manager?.Close();
            if (scheduler is not null)
            {
                try { scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Until(Window owner, Func<bool> predicate, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            Pump(owner);
            if (predicate()) return;
            Thread.Sleep(30);
        }
        throw new TimeoutException(message);
    }

    private static void UntilBackground(Func<bool> predicate, string message)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < until)
        {
            if (predicate()) return;
            Thread.Sleep(30);
        }
        throw new TimeoutException(message);
    }

    private static void Pump(Window owner)
    {
        owner.UpdateLayout();
        var frame = new DispatcherFrame();
        owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
