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

            // An empty taskmanager must state WHY the scheduler has no
            // actionable work: the periodic 30-day review may legitimately
            // not be due, or a source may be offline. Never display a blank
            // surface that makes a stalled scanner indistinguishable from
            // a completed maintenance cycle.
            var idle = new PCasa.App.TaskManagerWindow(scheduler, database,
                idleScanStatus: () =>
                    "Keine Bestandsprüfung fällig (30 Tage Mindestabstand)");
            try
            {
                idle.Show();
                var statusField = typeof(PCasa.App.TaskManagerWindow)
                    .GetField("_detail",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)!;
                var idleStatus = (TextBlock)statusField.GetValue(idle)!;
                Until(idle, () => idleStatus.Text.Contains(
                    "Keine Bestandsprüfung fällig", StringComparison.Ordinal),
                    "Empty Taskmanager did not disclose whether idle review is due.");
                var timer = (DispatcherTimer)typeof(PCasa.App.TaskManagerWindow)
                    .GetField("_refreshTimer",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)!.GetValue(idle)!;
                Thread.Sleep(300);
                Pump(idle);
                if (timer.IsEnabled)
                    throw new Exception("Taskmanager still polls its state while idle.");
                scheduler.NotifyTaskViewChanged();
                if (!timer.IsEnabled)
                    throw new Exception("Scheduler change did not schedule a view refresh.");
                Thread.Sleep(300);
                Pump(idle);
                if (timer.IsEnabled)
                    throw new Exception("Taskmanager change coalescer did not stop after one tick.");
                Console.WriteLine("PASS: Taskmanager refresh is event-driven, with no idle polling.");
            }
            finally { idle.Close(); }

            // Every first-import intent exists before its scanner starts.
            // Order and explicit Pause survive a restart, and the Taskmanager
            // must show BOTH the active and waiting records.
            var firstRepo = new FolderRepository(database);
            var firstA = Path.Combine(dir, "InitialImport_A");
            var firstB = Path.Combine(dir, "InitialImport_B");
            Directory.CreateDirectory(firstA);
            Directory.CreateDirectory(firstB);
            firstRepo.Add(firstA);
            firstRepo.Add(firstB);
            // Re-selecting a completed root is not a second import.
            var finishedPath = Path.Combine(dir, "AlreadyImported");
            Directory.CreateDirectory(finishedPath);
            firstRepo.Add(finishedPath);
            var finishedFolder = firstRepo.GetAll().Single(f =>
                string.Equals(f.LocalPath, finishedPath,
                    StringComparison.OrdinalIgnoreCase));
            var finishedSource = firstRepo.GetSourceState(
                finishedFolder.CanonicalPath);
            if (finishedSource is null ||
                !firstRepo.CompleteInitialScan(
                    finishedFolder.Id, finishedSource.Value.Generation))
                throw new Exception("Cannot acknowledge a completed import.");
            firstRepo.Add(finishedPath);
            if (firstRepo.GetPendingFirstImportQueue().Any(f =>
                    f.FolderId == finishedFolder.Id))
                throw new Exception("Re-selecting a completed root reopened its import.");
            var secondReviewedPath = Path.Combine(dir, "AlreadyReviewedB");
            Directory.CreateDirectory(secondReviewedPath);
            firstRepo.Add(secondReviewedPath);
            var reviewedB = firstRepo.GetAll().Single(f =>
                string.Equals(f.LocalPath, secondReviewedPath,
                    StringComparison.OrdinalIgnoreCase));
            var reviewedSourceB = firstRepo.GetSourceState(reviewedB.CanonicalPath);
            if (reviewedSourceB is null ||
                !firstRepo.CompleteInitialScan(
                    reviewedB.Id, reviewedSourceB.Value.Generation))
                throw new Exception("Cannot prepare second reviewed root.");
            var reviewOneKey = $"automatic:{finishedFolder.Id}:RescanRoot:{finishedPath}";
            var reviewTwoKey = $"automatic:{reviewedB.Id}:RescanRoot:{secondReviewedPath}";
            if (!firstRepo.GetRootReviewQueueOrder().Contains(finishedFolder.Id) ||
                !firstRepo.GetRootReviewQueueOrder().Contains(reviewedB.Id))
                throw new Exception("Review roots absent from persistent order.");

            var firstQueue = firstRepo.GetPendingFirstImportQueue();
            if (firstQueue.Count != 2)
                throw new Exception("Activated first-import roots were not durably queued.");
            var initialA = firstQueue.Single(item =>
                string.Equals(item.Directory, firstA, StringComparison.OrdinalIgnoreCase));
            var initialB = firstQueue.Single(item =>
                string.Equals(item.Directory, firstB, StringComparison.OrdinalIgnoreCase));
            if (!firstRepo.MovePendingFirstImport(initialB.FolderId, initialA.FolderId) ||
                !firstRepo.SetPendingFirstImportPaused(initialA.FolderId, true))
                throw new Exception("First-import queue cannot be reordered/paused.");
            // Re-selecting an unfinished root must retain pause and rank.
            firstRepo.Add(firstA);
            database.Initialize();
            var restoredFirstQueue = new FolderRepository(database).GetPendingFirstImportQueue();
            if (restoredFirstQueue[0].FolderId != initialB.FolderId ||
                !restoredFirstQueue.Single(item =>
                    item.FolderId == initialA.FolderId).Paused ||
                new FolderRepository(database).GetPendingInitialScans()
                    .Any(item => item.Id == initialA.FolderId))
                throw new Exception("Restart lost first-import order or resumed a held import.");

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
            // A live automatic P4 inventory is not a durable manual package.
            // It MUST nevertheless appear whenever the progress window shows
            // a scan, and have a real stop delegate back to the original owner.
            const string autoKey =
                @"automatic:9:RescanRoot:G:\onedrive\Bilder\Camera Roll";
            var stoppedAuto = 0;
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
                },
                () =>
                {
                    var ordered = firstRepo.GetRootReviewQueueOrder().ToList();
                    return new[] {
                        new PCasa.App.AutomaticScanTask(
                            autoKey, "Bestand überprüfen",
                            @"G:\onedrive\Bilder\Camera Roll",
                            "1.234 geprüft", "Läuft", 4, 0),
                        new PCasa.App.AutomaticScanTask(
                            reviewOneKey, "Bestand überprüfen",
                            finishedPath, "–", "Wartet", 4,
                            ordered.IndexOf(finishedFolder.Id) + 1L),
                        new PCasa.App.AutomaticScanTask(
                            reviewTwoKey, "Bestand überprüfen",
                            secondReviewedPath, "–", "Wartet", 4,
                            ordered.IndexOf(reviewedB.Id) + 1L)
                    };
                },
                key =>
                {
                    if (key == autoKey) Interlocked.Increment(ref stoppedAuto);
                    return Task.FromResult(key == autoKey);
                },
                idleScanStatus: null,
                moveAutomaticScan: (source, target) => Task.FromResult(
                    source.StartsWith("initial:", StringComparison.Ordinal)
                        ? firstRepo.MovePendingFirstImport(
                            long.Parse(source.AsSpan("initial:".Length)),
                            long.Parse(target.AsSpan("initial:".Length)))
                        : firstRepo.MoveRootReview(
                            long.Parse(source.Split(':')[1]),
                            long.Parse(target.Split(':')[1]))),
                resumeAutomaticScan: _ => Task.FromResult(true),
                startAutomaticNow: key => Task.FromResult(
                    firstRepo.RequestRootReviewNow(
                        long.Parse(key.Split(':')[1]))));
            manager.Show();
            Pump(manager);
            var checks = Descendants<CheckBox>(manager).ToArray();
            var labels = checks.Select(box => box.Content?.ToString()).ToArray();
            if (!labels.SequenceEqual(new[] { "Läuft", "Wartet", "Fehler", "Abgeschlossen" }) ||
                checks.Where((_, index) => index < 3).Any(box => box.IsChecked != true) ||
                checks[3].IsChecked != false)
                throw new Exception("Task status filters do not use the requested compact default.");
            var grid = Descendants<DataGrid>(manager).Single();
            if (grid.Columns[3] is not DataGridTextColumn statusColumn ||
                statusColumn.Binding is not System.Windows.Data.Binding statusBinding ||
                statusBinding.Path.Path != "LocalizedStatus")
                throw new Exception("Task status cell is not bound to localization.");
            UiLanguage.Current.Select("en");
            try
            {
                var localized = new SchedulerTaskViewItem("status-test",
                    "Update folder", dir, "–", "Wartet", null, 0, 0,
                    true, true, false, false);
                if (localized.LocalizedStatus != "Waiting" ||
                    (localized with { Status = "Angehalten" }).LocalizedStatus != "Paused")
                    throw new Exception("Task status is mixed German/English.");
            }
            finally { UiLanguage.Current.Select("de"); }
            if (!grid.Columns.Select(col => col.Header?.ToString())
                    .SequenceEqual(new[] { "Aufgabe", "Verzeichnis", "Fortschritt", "Status" }) ||
                grid.CanUserSortColumns)
                throw new Exception("Taskmanager lost its four plain-text columns / dispatch order.");
            Until(manager, () => grid.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                    == "taskmanager:a"), "Queued task is absent from its live scheduler view.");
            // A waiting first-import appears before ANY scanner callback.
            var pendingRows = grid.Items.Cast<object>().Where(item =>
                item.GetType().GetProperty("Key")?.GetValue(item) is string key &&
                key.StartsWith("initial:", StringComparison.Ordinal)).ToArray();
            if (pendingRows.Length != 2)
                throw new Exception("Queued initial imports are invisible in Taskmanager.");
            var pausedRow = pendingRows.Single(item =>
                (string?)item.GetType().GetProperty("Key")?.GetValue(item) ==
                $"initial:{initialA.FolderId}");
            if ((string?)pausedRow.GetType().GetProperty("Status")?.GetValue(pausedRow)
                    != "Angehalten" ||
                pausedRow.GetType().GetProperty("Retryable")?.GetValue(pausedRow)
                    is not true ||
                (string?)pendingRows[0].GetType().GetProperty("Key")
                    ?.GetValue(pendingRows[0]) != $"initial:{initialB.FolderId}")
                throw new Exception("Held first import lost its status or persisted dispatch order.");

            // An explicitly paused import is STILL a position in its
            // persistent P2 queue. Up/down must change its real dispatch
            // rank and the visual order, even while it remains held.
            // Status-group sorting previously hid a successful move.
            grid.SelectedItem = pausedRow;
            Pump(manager);
            var moveUp = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Nach oben"));
            var moveDown = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Nach unten"));
            if (!moveUp.IsEnabled || moveDown.IsEnabled)
                throw new Exception("Paused import move buttons do not reflect its actual queue neighbors.");
            moveUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moveUp));
            Until(manager, () =>
            {
                var importKeys = grid.Items.Cast<object>()
                    .Select(item => item.GetType().GetProperty("Key")?.GetValue(item)?.ToString())
                    .Where(key => key is not null && key.StartsWith("initial:", StringComparison.Ordinal))
                    .ToArray();
                return importKeys.SequenceEqual(new[] {
                    $"initial:{initialA.FolderId}", $"initial:{initialB.FolderId}" });
            }, "Moving a held first import did not update visible queue order.");
            if (firstRepo.GetPendingFirstImportQueue()[0].FolderId != initialA.FolderId)
                throw new Exception("Move up changed only the visible order, not SQLite.");
            // Return to original persisted order for the subsequent tests.
            Until(manager, () => moveDown.IsEnabled,
                "Down control is disabled after a successful move.");
            moveDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moveDown));
            var actingField = typeof(PCasa.App.TaskManagerWindow).GetField(
                "_acting", System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.Instance)!;
            Until(manager, () =>
                firstRepo.GetPendingFirstImportQueue()[0].FolderId == initialB.FolderId &&
                !(bool)actingField.GetValue(manager)!,
                "Move down did not finish its UI refresh and persisted order.");

            // A running manual scan and TWO waiting P4 root reviews:
            // selecting the lower P4 job must enable Move up and store the
            // real change, not merely alter a synthetic visual row order.
            var reviewRows = grid.Items.Cast<object>()
                .Where(item => item.GetType().GetProperty("Key")?.GetValue(item)
                    is string key && (key == reviewOneKey || key == reviewTwoKey))
                .ToArray();
            if (reviewRows.Length != 2)
                throw new Exception("Two separate waiting P4 reviews are missing.");
            var originalReviews = reviewRows.Select(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()).ToArray();
            var selectedReviewKey = (string)reviewRows[1].GetType()
                .GetProperty("Key")!.GetValue(reviewRows[1])!;
            grid.SelectedItem = grid.Items.Cast<object>().Single(item =>
                Equals(item.GetType().GetProperty("Key")?.GetValue(item),
                    selectedReviewKey));
            Pump(manager);
            if (!Equals(grid.SelectedItem?.GetType().GetProperty("Key")
                    ?.GetValue(grid.SelectedItem), selectedReviewKey))
                throw new Exception("Selecting P4 task was reset by stale async refresh.");
            if (!moveUp.IsEnabled || moveDown.IsEnabled)
                throw new Exception("Waiting P4 reordering buttons are disabled.");
            moveUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, moveUp));
            Until(manager, () => grid.Items.Cast<object>()
                .Select(item => item.GetType().GetProperty("Key")?.GetValue(item)?.ToString())
                .Where(key => key == reviewOneKey || key == reviewTwoKey)
                .SequenceEqual(originalReviews.Reverse()),
                "P4 move did not update visible task order.");
            var expectedFirst = originalReviews[1] == reviewOneKey
                ? finishedFolder.Id : reviewedB.Id;
            var otherReview = expectedFirst == finishedFolder.Id
                ? reviewedB.Id : finishedFolder.Id;
            var storedReviewOrder = firstRepo.GetRootReviewQueueOrder().ToList();
            if (storedReviewOrder.IndexOf(expectedFirst) >=
                storedReviewOrder.IndexOf(otherReview))
                throw new Exception("P4 move did not persist in SQLite.");
            database.Initialize();
            var reopenedReviews = new FolderRepository(database)
                .GetRootReviewQueueOrder().ToList();
            if (reopenedReviews.IndexOf(expectedFirst) >=
                reopenedReviews.IndexOf(otherReview))
                throw new Exception("P4 order was lost after SQLite reopen.");

            // A waiting root review may be explicitly requested NOW. The
            // action edits the same durable review receipt (P4 -> P2) and
            // must not create a second scan job or overtake P0 writes.
            var promotedKey = expectedFirst == finishedFolder.Id
                ? reviewOneKey : reviewTwoKey;
            grid.SelectedItem = grid.Items.Cast<object>().Single(item =>
                Equals(item.GetType().GetProperty("Key")?.GetValue(item),
                    promotedKey));
            Pump(manager);
            var startNow = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Jetzt ausführen"));
            if (!startNow.IsEnabled)
                throw new Exception("Waiting root review has no Start now command.");
            startNow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, startNow));
            Until(manager, () =>
                firstRepo.GetUrgentRootReviewIds().Contains(expectedFirst),
                "Promoted root review did not persist the user's P2 request.");
            database.Initialize();
            if (!new FolderRepository(database).GetUrgentRootReviewIds()
                .Contains(expectedFirst))
                throw new Exception("Promoted review priority was lost on restart.");

            // The formerly EMPTY taskmanager during a live background scan
            // is a regression. The row must have a real operation (Stop).
            var automaticRow = grid.Items.Cast<object>().SingleOrDefault(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString() == autoKey)
                ?? throw new Exception(
                    "Live automatic inventory is invisible while scanner is working.");
            if (!string.Equals(
                    automaticRow.GetType().GetProperty("Status")?.GetValue(automaticRow)
                    as string, "Läuft", StringComparison.Ordinal) ||
                !string.Equals(
                    automaticRow.GetType().GetProperty("Directory")?.GetValue(automaticRow)
                    as string, @"G:\onedrive\Bilder\Camera Roll", StringComparison.Ordinal) ||
                !Equals(automaticRow.GetType().GetProperty("Deletable")?.GetValue(automaticRow),
                    true))
                throw new Exception(
                    "Automatic inventory lost its real status, target or stop command.");
            // A user-selected global execution order is NOT sorted by
            // running/waiting status. Running scans may appear after a
            // promoted queued task; the status column still says "Läuft".
            var visibleOrder = grid.Items.Cast<object>()
                .Where(item => item.GetType().GetProperty("Status")
                    ?.GetValue(item)?.ToString() != "Abgeschlossen").ToArray();
            var schedulerOrder = visibleOrder
                .OrderBy(item => (int)item.GetType().GetProperty("Priority")!
                    .GetValue(item)!)
                .ThenBy(item => (long)item.GetType().GetProperty("Order")!
                    .GetValue(item)!)
                .ThenBy(item => item.GetType().GetProperty("Key")!
                    .GetValue(item)!.ToString())
                .ToArray();
            if (!visibleOrder.SequenceEqual(schedulerOrder))
                throw new Exception(
                    "Taskmanager invented a status-first order instead of scheduler order.");
            grid.SelectedItem = automaticRow;
            Pump(manager);
            var stopAction = Descendants<Button>(manager).Single(button =>
                Equals(button.Content, "Löschen"));
            if (!stopAction.IsEnabled)
                throw new Exception("Automatic scan row has no working stop action.");
            grid.SelectedItem = null;

            // A real but transient scheduler job occupies the manual lane.
            // It has no taskmanager commands and must never appear as a
            // misleading, unselectable row (nor should its history entries).
            var displayedKeys = grid.Items.Cast<object>().Select(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()).ToArray();
            if (displayedKeys.Contains("taskmanager:blocker") ||
                displayedKeys.Any(key => key is not null &&
                    !key.StartsWith("taskmanager:", StringComparison.Ordinal) &&
                    !key.StartsWith("automatic:", StringComparison.Ordinal) &&
                    !key.StartsWith("initial:", StringComparison.Ordinal) &&
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
                    == "taskmanager:failed-original"),
                "Permanent original-file errors are missing from the taskmanager.");
            grid.SelectedItem = grid.Items.Cast<object>().First(item =>
                item.GetType().GetProperty("Key")?.GetValue(item)?.ToString()
                == "taskmanager:failed-original");
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

            // A manually requested full-root scan is a continuation of the
            // pending import, never a second task tile for the same root.
            const string continuingKey = "taskmanager:continuing-import";
            journal.Put(continuingKey, "manual-folder-scan-v1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Directory = firstA,
                    MonitoredRootId = (long?)initialA.FolderId,
                    AcknowledgeTargetedPending = true
                }), SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
            var continuedView = new PCasa.App.TaskManagerWindow(scheduler, database);
            try
            {
                continuedView.Show();
                Until(continuedView, () => Descendants<DataGrid>(continuedView)
                    .Single().Items.Cast<object>().Any(row =>
                        row.GetType().GetProperty("Key")?.GetValue(row)
                        ?.ToString() == continuingKey),
                    "Continued import does not appear in the taskmanager.");
                var tasks = Descendants<DataGrid>(continuedView).Single()
                    .Items.Cast<object>().ToArray();
                if (tasks.Any(row =>
                        row.GetType().GetProperty("Key")?.GetValue(row)
                        ?.ToString() == $"initial:{initialA.FolderId}") ||
                    tasks.Count(row => string.Equals(
                        row.GetType().GetProperty("Directory")?.GetValue(row)
                            as string, firstA, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw new Exception("A single import appears as two separate tasks.");
            }
            finally
            {
                continuedView.Close();
                journal.Cancel(continuingKey);
            }

            // Deleting an explicitly held import removes the persistent
            // queue INTENT, never photos or the saved first-pass cursor.
            // The manager must enable Delete even though the row is held;
            // the row must disappear after the shared repository operation.
            var heldView = new PCasa.App.TaskManagerWindow(scheduler, database,
                stopAutomaticScan: key => Task.FromResult(
                    key == $"initial:{initialA.FolderId}" &&
                    firstRepo.DeletePendingFirstImport(initialA.FolderId)));
            try
            {
                var source = firstRepo.GetSourceState(
                    firstRepo.GetAll().Single(root =>
                        root.Id == initialA.FolderId).CanonicalPath);
                if (source is null || !firstRepo.SaveDirectoryFirstPass(
                    initialA.FolderId, source.Value.Generation, firstA,
                    "012345.jpg", completed: false))
                    throw new Exception("Could not save partial import cursor.");

                heldView.Show();
                var heldGrid = Descendants<DataGrid>(heldView).Single();
                Until(heldView, () => heldGrid.Items.Cast<object>().Any(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)
                    ?.ToString() == $"initial:{initialA.FolderId}"),
                    "Paused import is missing before deletion.");
                var held = heldGrid.Items.Cast<object>().Single(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)
                    ?.ToString() == $"initial:{initialA.FolderId}");
                heldGrid.SelectedItem = held;
                Pump(heldView);
                if (!Descendants<Button>(heldView).Single(button =>
                        Equals(button.Content, "Löschen")).IsEnabled)
                    throw new Exception("Cannot delete an explicitly held import.");

                if (!firstRepo.DeletePendingFirstImport(initialA.FolderId))
                    throw new Exception("Deleting a held import did not remove intent.");
                Until(heldView, () => heldGrid.Items.Cast<object>().All(item =>
                    item.GetType().GetProperty("Key")?.GetValue(item)
                    ?.ToString() != $"initial:{initialA.FolderId}"),
                    "Deleted import remains in the manager.");
                database.Initialize(); // simulate reopening the persistent catalog
                var reopened = new FolderRepository(database);
                if (reopened.GetPendingFirstImportQueue()
                        .Any(row => row.FolderId == initialA.FolderId) ||
                    reopened.GetPendingInitialScans()
                        .Any(row => row.Id == initialA.FolderId))
                    throw new Exception("Deleted import returned after restart.");
                if (!reopened.HasIncompleteImport(initialA.FolderId))
                    throw new Exception(
                        "Deleting a task incorrectly marked its import completed.");
                if (reopened.GetDueRootReviewIds(DateTime.UtcNow)
                        .Contains(initialA.FolderId))
                    throw new Exception(
                        "Deleted import was immediately requeued as a P4 review.");
                var recovered = reopened.GetDirectoryFirstPass(
                    initialA.FolderId, source.Value.Generation, firstA);
                if (!recovered.Cursors.TryGetValue(firstA, out var file) ||
                    file != "012345.jpg")
                    throw new Exception("Deleting an import erased confirmed progress.");
                firstRepo.Add(firstA); // reselect must not recreate the deleted job
                if (firstRepo.GetPendingFirstImportQueue()
                    .Any(row => row.FolderId == initialA.FolderId))
                    throw new Exception("Re-selecting root requeued deleted import.");
                if (!firstRepo.GetPendingFirstImportQueue()
                    .Any(row => row.FolderId == initialB.FolderId))
                    throw new Exception("Deleting one root removed another import.");
                // Only an explicitly completed full-root scan may now
                // acknowledge the still-incomplete source, and that must
                // clear its deletion marker for future activation.
                if (!firstRepo.CompleteInitialScan(
                        initialA.FolderId, source.Value.Generation) ||
                    firstRepo.HasIncompleteImport(initialA.FolderId))
                    throw new Exception("Explicit completion failed after deletion.");
            }
            finally { heldView.Close(); }

            // A freshly restored scan may have zero volatile progress while
            // its durable receipt already confirmed thousands of photos.
            // The compact floating presenter must use the SAME status and
            // count as the taskmanager, including after a scheduler yield.
            var progressWindow = new PCasa.App.BrowseCacheProgressWindow();
            try
            {
                progressWindow.BeginManualOperation("Ordner aktualisieren", 0);
                var waitingScan = new SchedulerJobSnapshot(123,
                    "taskmanager:path-check", SchedulerPriority.ManualAction,
                    SchedulerJobState.Queued, DateTimeOffset.UtcNow, null)
                {
                    Phase = "Bilder einlesen",
                    Processed = 0,
                    CurrentPath = preservedScanRoot + @"\obsolete.jpg",
                    WorkKind = SchedulerWorkKind.CatalogScan
                };
                foreach (var state in new[] {
                    SchedulerJobState.Queued, SchedulerJobState.YieldRequested
                })
                {
                    progressWindow.UpdateManualOperationWithContext(
                        waitingScan with { State = state },
                        "Ordner aktualisieren", preservedScanRoot, 14975);
                    var phase = ((TextBlock)progressWindow.FindName("PhaseText")).Text;
                    var count = ((TextBlock)progressWindow.FindName("CountText")).Text;
                    var current = ((TextBlock)progressWindow.FindName("CurrentFileText")).Text;
                    var parent = ((TextBlock)progressWindow.FindName("FolderPathText")).Text;
                    if (!phase.Contains(UiLanguage.T("wartet"),
                            StringComparison.OrdinalIgnoreCase) ||
                        phase.Contains(UiLanguage.T("pausiert"),
                            StringComparison.OrdinalIgnoreCase) ||
                        count != UiLanguage.Format("{0:N0} geprüft", 14975) ||
                        current != Path.GetFileName(preservedScanRoot) ||
                        parent != Path.GetDirectoryName(preservedScanRoot) ||
                        ((System.Windows.Controls.Image)progressWindow.FindName(
                            "CurrentThumbnail")).Source is not null)
                        throw new Exception("Restored waiting scan contradicts taskmanager state or confirmed progress.");
                }
                progressWindow.UpdateManualOperationWithContext(
                    waitingScan with {
                        State = SchedulerJobState.Running,
                        CurrentPath = preservedScanRoot + @"\photo.jpg",
                        Processed = 14976
                    }, "Ordner aktualisieren", preservedScanRoot, 14975);
                if (((TextBlock)progressWindow.FindName("CountText")).Text !=
                        UiLanguage.Format("{0:N0} geprüft", 14976))
                    throw new Exception("Running scan did not advance from the confirmed restart cursor.");
            }
            finally
            {
                progressWindow.EndManualOperation();
                progressWindow.Finish();
            }

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
            if (!scheduler.MoveTaskGlobally(
                "taskmanager:promoted", "taskmanager:scan-running",
                [
                    new SchedulerTaskOrderItem(
                        "taskmanager:scan-running", SchedulerPriority.ManualAction),
                    new SchedulerTaskOrderItem(
                        "taskmanager:promoted", SchedulerPriority.ManualAction)
                ]))
                throw new Exception("Global scheduler could not move a P0 mutation ahead of a running manual scan.");
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
                try
                {
                    var cleanup = Task.Run(async () => await scheduler.DisposeAsync());
                    if (!cleanup.Wait(TimeSpan.FromSeconds(5)))
                        Console.Error.WriteLine("DIAG: scheduler disposal still running after failure.");
                }
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
