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
        TaskCompletionSource? releaseStagedJob = null;
        try
        {
            var app = new PCasa.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var database = new LocalDatabase(Path.Combine(dir, "pcasa.db"));
            database.Initialize();
            var journal = new DurablePackageJournal(database);
            scheduler = new PCasaScheduler();
            scheduler.EnableDurablePackages(database);
            var executed = new List<string>();
            object sync = new();
            var scanStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var scanEntrances = 0;
            SchedulerTaskPackage Make(string key, string payload)
            {
                var pkg = new SchedulerTaskPackage(key);
                pkg.AddStep(async token =>
                {
                    if (key == "taskmanager:scan-running")
                    {
                        var first = false;
                        lock (sync) first = ++scanEntrances == 1;
                        if (first)
                        {
                            scanStarted.TrySetResult();
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                    }
                    token.ThrowIfCancellationRequested();
                    lock (sync) executed.Add(key);
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

            // Individual GPS/tag write failures remain in the existing
            // original-write ledger, not in a duplicate task queue. The
            // taskmanager must expose them as error rows whose actions
            // delegate back to that original ledger's handlers.
            var failures = new OriginalWriteFailureRepository(database);
            failures.RecordFailure(OriginalWriteFailureRepository.Gps,
                "taskmanager:failed-original", 987654,
                new IOException("Synthetic original write failure"), permanent: true);
            var retriedOriginals = 0;
            var discardedOriginals = 0;
            // A manual scan's Directory is part of its durable recipe. It
            // must remain visible while queued or after a process restart,
            // even when there is no current-file progress callback.
            const string preservedScanRoot =
                @"S:\Archiv.Negative\Negative\1982-Urlaub Frankreich-Portugal";
            journal.Put("taskmanager:path-check", "manual-folder-scan-v1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Directory = preservedScanRoot,
                    MonitoredRootId = (long?)null
                }), SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
            manager = new PCasa.App.TaskManagerWindow(scheduler, database,
                _ =>
                {
                    Interlocked.Increment(ref retriedOriginals);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    Interlocked.Increment(ref discardedOriginals);
                    return Task.CompletedTask;
                });
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
            // A real but transient scheduler job occupies the manual lane.
            // It has no taskmanager commands and must never appear as a
            // misleading, unselectable row (nor should its history entries).
            var displayedKeys = grid.Items.Cast<object>().Select(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()).ToArray();
            if (displayedKeys.Contains("taskmanager:blocker") ||
                displayedKeys.Any(key => key is not null &&
                    !key.StartsWith("taskmanager:", StringComparison.Ordinal) &&
                    !key.StartsWith("failed-files:", StringComparison.Ordinal)))
                throw new Exception("Non-actionable scheduler history leaked into the taskmanager.");
            if (grid.RowStyle is null || grid.CellStyle is null ||
                grid.GridLinesVisibility != DataGridGridLinesVisibility.None ||
                grid.Columns[2].Width.Value > 110 || grid.Columns[3].Width.Value > 110)
                throw new Exception("Taskmanager lost compact raised row layout.");

            Until(manager, () => grid.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                    == "taskmanager:path-check"),
                "Persisted manual scan is not shown.");
            var pathRow = grid.Items.Cast<object>().First(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                == "taskmanager:path-check");
            if (!string.Equals(pathRow.GetType().GetProperty("Directory")?.GetValue(pathRow)
                    as string, preservedScanRoot, StringComparison.Ordinal))
                throw new Exception("Queued manual scan lost the directory in its recipe.");
            Until(manager, () => grid.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                    == "failed-files:taskmanager:failed-original"),
                "Permanent original-file errors are missing from the taskmanager.");
            grid.SelectedItem = grid.Items.Cast<object>().First(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                == "failed-files:taskmanager:failed-original");
            Pump(manager);
            var retry = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Neu starten"));
            var discard = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Löschen"));
            if (!retry.IsEnabled || !discard.IsEnabled)
                throw new Exception("Original-write failure cannot be retried/discarded through the shared handlers.");
            retry.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, retry));
            Until(manager, () => Volatile.Read(ref retriedOriginals) == 1,
                "Retry did not use the original-write handler.");
            manager.Close();
            manager = null;
            journal.Cancel("taskmanager:path-check");

            releaseBlocker.TrySetResult();
            UntilBackground(() => Task.WhenAll(a, c, blocker).IsCompleted,
                "Scheduler did not finish reordered durable tasks.");
            Task.WhenAll(a, c, blocker).GetAwaiter().GetResult();
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
            // Reorder against a RUNNING durable scan, not just two queued
            // entries. The promoted scan must start before the old worker
            // resumes, using its original package instead of a new scan.
            var stageRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            releaseStagedJob = stageRelease;
            var stageStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stage = scheduler.EnqueueAsync("taskmanager:stage",
                SchedulerPriority.ManualAction, async _ =>
                {
                    stageStarted.TrySetResult();
                    await stageRelease.Task.WaitAsync(_);
                });
            UntilBackground(() => stageStarted.Task.IsCompleted,
                "Could not hold manual lane for reorder regression.");
            var promoted = scheduler.EnqueueDurablePackageAsync(
                Make("taskmanager:promoted", "{}"), "taskmanager-test-v1", "{}",
                SchedulerPriority.ManualAction);
            var runningScan = scheduler.EnqueueDurablePackageAsync(
                Make("taskmanager:scan-running", "{}"), "taskmanager-test-v1",
                "{}", SchedulerPriority.ManualAction,
                SchedulerWorkKind.CatalogScan);
            stageRelease.TrySetResult();
            UntilBackground(() => scanStarted.Task.IsCompleted,
                "Newest durable scan did not enter the running state.");
            if (!scheduler.MoveDurableTaskTo(
                "taskmanager:promoted", "taskmanager:scan-running"))
                throw new Exception("Could not promote queued scan above running P0 peer.");
            UntilBackground(() => Task.WhenAll(promoted, runningScan).IsCompleted,
                "Promoted task did not preempt the running same-priority scan.");
            Task.WhenAll(promoted, runningScan).GetAwaiter().GetResult();
            lock (sync)
                if (!executed.TakeLast(2).SequenceEqual(new[]
                    { "taskmanager:promoted", "taskmanager:scan-running" }))
                    throw new Exception("Promoted task did not run before yielded scan: " +
                        string.Join(", ", executed));
            try { stage.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
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
            releaseStagedJob?.TrySetResult();
            manager?.Close();
            if (scheduler is not null)
            {
                try { Task.Run(async () => await scheduler.DisposeAsync()).GetAwaiter().GetResult(); }
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
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
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
