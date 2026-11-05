using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class GlobalTaskOrderFlow
{
    [STAThread]
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "PCasa-GlobalScheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PCasaScheduler? scheduler = null;
        PCasa.App.TaskManagerWindow? manager = null;
        var releaseWorker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFactory = new ManualResetEventSlim(false);
        try
        {
            var app = new PCasa.App.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var database = new LocalDatabase(Path.Combine(root, "pcasa.db"));
            database.Initialize();
            scheduler = new PCasaScheduler();
            scheduler.EnableDurablePackages(database);
            var journal = new DurablePackageJournal(database);
            var executed = new List<string>();
            var sync = new object();
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blocker = scheduler.EnqueueAsync(
                "global:test-blocker", SchedulerPriority.ManualAction,
                async _ =>
                {
                    started.TrySetResult();
                    await releaseWorker.Task;
                }, workKind: SchedulerWorkKind.Catalog);
            AwaitWithPump(() => started.Task.IsCompleted,
                "Manual P0 worker did not start.");

            // Control-plane changes cannot be enqueued behind the P0
            // worker being reprioritized. This was why a clicked P4
            // review could stay "waiting" long after its button was used.
            var priorityRoot = Path.Combine(root, "PendingReview");
            Directory.CreateDirectory(priorityRoot);
            var folderRepo = new FolderRepository(database);
            folderRepo.Add(priorityRoot);
            var monitored = folderRepo.GetAll().Single(folder =>
                string.Equals(folder.LocalPath, priorityRoot,
                    StringComparison.OrdinalIgnoreCase));
            var sourceState = folderRepo.GetSourceState(monitored.CanonicalPath);
            if (sourceState is null ||
                !folderRepo.CompleteInitialScan(
                    monitored.Id, sourceState.Value.Generation))
                throw new Exception("Could not prepare reviewed source.");
            var priorityWatch = Stopwatch.StartNew();
            var promoted = scheduler.ChangeTaskIntent(() =>
                folderRepo.RequestRootReviewNow(monitored.Id));
            priorityWatch.Stop();
            if (!promoted || priorityWatch.Elapsed > TimeSpan.FromMilliseconds(500) ||
                !folderRepo.GetUrgentRootReviewIds().Contains(monitored.Id) ||
                blocker.IsCompleted)
                throw new Exception(
                    "P4 promotion was blocked by the busy P0 worker.");

            var factoryEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            SchedulerTaskPackage GpsRecipe(string key, string _)
            {
                factoryEntered.TrySetResult();
                if (!releaseFactory.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("GPS test factory stayed blocked.");
                var pkg = new SchedulerTaskPackage(key);
                pkg.AddStep(token =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (sync) executed.Add(key);
                    return Task.CompletedTask;
                });
                return pkg;
            }
            SchedulerTaskPackage WriteRecipe(string key, string _)
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
            scheduler.RegisterDurablePackageType("gps-edit-v2", GpsRecipe);
            scheduler.RegisterDurablePackageType("global-write-test", WriteRecipe);
            var acceptedSignals = 0;
            scheduler.TaskAccepted += () => Interlocked.Increment(ref acceptedSignals);
            var watch = Stopwatch.StartNew();
            var gps = scheduler.EnqueueDurableRecipeAsync(
                "gps:edit:filmstrip", "gps-edit-v2", "{}",
                SchedulerPriority.ManualAction);
            watch.Stop();
            if (watch.Elapsed > TimeSpan.FromMilliseconds(500) ||
                !journal.Pending().Any(x => x.Key == "gps:edit:filmstrip") ||
                acceptedSignals != 1)
                throw new Exception("GPS registration waited for validation or file preparation.");
            AwaitWithPump(() => factoryEntered.Task.IsCompleted,
                "Background GPS factory did not begin.");
            if (gps.IsCompleted)
                throw new Exception("Blocked GPS factory should not have completed yet.");

            var tags = scheduler.EnqueueDurablePackageAsync(
                WriteRecipe("tag:edit:waiting", "{}"),
                "global-write-test", "{}", SchedulerPriority.ManualAction);
            const string actualScan = "background-scan:88:1:C:\\TestRoot";
            const string reviewKey = "automatic:88:RescanRoot:C:\\TestRoot";
            scheduler.RegisterTaskAlias(actualScan, reviewKey);
            var review = scheduler.EnqueueAsync(actualScan,
                SchedulerPriority.Idle, token =>
                {
                    token.ThrowIfCancellationRequested();
                    lock (sync) executed.Add(reviewKey);
                    return Task.CompletedTask;
                }, workKind: SchedulerWorkKind.CatalogScan);
            if (acceptedSignals != 2)
                throw new Exception("Durable admission event was not raised.");

            manager = new PCasa.App.TaskManagerWindow(
                scheduler, database,
                automaticScans: () => [
                    new PCasa.App.AutomaticScanTask(
                        reviewKey, "Bestand überprüfen", @"C:\TestRoot",
                        "–", "Wartet", 4, 90)
                ],
                moveGlobalTask: (source,target,keys) => Task.FromResult(
                    scheduler.MoveTaskGlobally(source,target,keys)));
            manager.Show();
            var grid = Descendants<DataGrid>(manager).Single();
            AwaitWithPump(() =>
                grid.Items.Cast<object>().Any(item => RowKey(item) == "gps:edit:filmstrip") &&
                grid.Items.Cast<object>().Any(item => RowKey(item) == reviewKey),
                "GPS or queued root review is invisible before factory completion.");
            var gpsRow = grid.Items.Cast<object>().Single(item =>
                RowKey(item) == "gps:edit:filmstrip");
            if (!Equals(gpsRow.GetType().GetProperty("Title")?.GetValue(gpsRow),
                    "GPS schreiben"))
                throw new Exception("Accepted GPS v2 shows a technical recipe instead of a task title.");
            var initial = VisibleKeys(grid);
            var reviewIndex = Array.IndexOf(initial, reviewKey);
            if (reviewIndex < 1)
                throw new Exception("Expected idle review below P0 jobs initially.");
            grid.SelectedItem = grid.Items.Cast<object>()
                .Single(item => RowKey(item) == reviewKey);
            Pump(manager);
            var up = Descendants<Button>(manager).Single(b =>
                Equals(b.Content, "Nach oben"));
            var down = Descendants<Button>(manager).Single(b =>
                Equals(b.Content, "Nach unten"));
            if (!up.IsEnabled)
                throw new Exception("Global Move up remains disabled across priorities.");
            up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, up));
            AwaitWithPump(() =>
                Array.IndexOf(VisibleKeys(grid), reviewKey) == reviewIndex - 1,
                "Move up did not move the review in the visible global queue.");
            var next = VisibleKeys(grid);
            grid.SelectedItem = grid.Items.Cast<object>()
                .Single(item => RowKey(item) == reviewKey);
            Pump(manager);
            if (!down.IsEnabled)
                throw new Exception("Global Move down remains disabled.");
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, down));
            AwaitWithPump(() =>
                Array.IndexOf(VisibleKeys(grid), reviewKey) == reviewIndex,
                "Move down was a no-op.");
            var all = VisibleKeys(grid);
            var firstKey = all[0];
            if (!scheduler.MoveTaskGlobally(reviewKey,firstKey,VisibleOrders(grid)))
                throw new Exception("Cross-type scheduler move rejected a root review above P0.");
            AwaitWithPump(() => VisibleKeys(grid).FirstOrDefault() == reviewKey,
                "Scheduler rank change did not refresh the Taskmanager.");
            var persisted = new SchedulerTaskOrderStore(database).ReadAll();
            if (!persisted.TryGetValue(reviewKey, out var reviewStored) ||
                persisted[firstKey].Order <= reviewStored.Order)
                throw new Exception("Global order did not persist in SQLite.");
            // The original executor alias receives the SAME rank when
            // admitted, not a second watcher task created by Taskmanager.
            var placement = scheduler.EffectiveTaskPlacement(
                reviewKey, SchedulerPriority.Idle, 90);
            if (placement.Priority != SchedulerPriority.ManualAction ||
                placement.QueueOrder != reviewStored.Order)
                throw new Exception("Physical scanner did not inherit user-selected rank.");

            releaseFactory.Set();
            releaseWorker.TrySetResult();
            AwaitWithPump(() => Task.WhenAll(gps,tags,review,blocker).IsCompleted,
                "Global reorder stranded a scheduler lane.");
            Task.WhenAll(gps,tags,review,blocker).GetAwaiter().GetResult();
            lock (sync)
            {
                if (executed.Count != 3 || executed[0] != reviewKey)
                    throw new Exception("Executor ignored global order: " +
                        string.Join(" -> ", executed));
            }
            Console.WriteLine("PASS: GPS accepted before eligibility, visible in TM; " +
                "free cross-priority up/down and real persisted execution order.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: Global scheduler regression: " + ex);
            return 1;
        }
        finally
        {
            releaseFactory.Set();
            releaseWorker.TrySetResult();
            manager?.Close();
            if (scheduler is not null)
            {
                try
                {
                    var cleanup = Task.Run(async () => await scheduler.DisposeAsync());
                    if (!cleanup.Wait(TimeSpan.FromSeconds(5)))
                        Console.Error.WriteLine("Scheduler disposal still pending.");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
            }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? RowKey(object item) =>
        item.GetType().GetProperty("Key")?.GetValue(item)?.ToString();

    private static string[] VisibleKeys(DataGrid grid) =>
        grid.Items.Cast<object>().Select(RowKey)
            .Where(key => key is not null).Select(key => key!).ToArray();

    private static SchedulerTaskOrderItem[] VisibleOrders(DataGrid grid) =>
        grid.Items.Cast<object>().Where(item => RowKey(item) is not null)
            .Select(item => new SchedulerTaskOrderItem(
                RowKey(item)!,
                (SchedulerPriority)(int)(item.GetType()
                    .GetProperty("Priority")?.GetValue(item) ?? 4)))
            .ToArray();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root,i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void AwaitWithPump(Func<bool> predicate,string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (predicate()) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException(message);
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
