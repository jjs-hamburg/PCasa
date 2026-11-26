using System.IO;
using PCasa.App.Data;
using PCasa.App.Services;

// Regression against real scheduler workers and a real SQLite journal.
// No mocks of priority, package cursors or process restart.
internal static class DurablePauseFlow
{
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "PCasa-DurablePause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ExercisePauseAndRestart(dir).GetAwaiter().GetResult();
            ExercisePersistentMultiWrite(dir).GetAwaiter().GetResult();
            ExerciseQueuedSessionWrites(dir).GetAwaiter().GetResult();
            ExerciseSessionControls(dir).GetAwaiter().GetResult();
            ExerciseAutomaticControlPersistence(dir).GetAwaiter().GetResult();
            ExerciseInitialImportStartupRelease(dir);
            ExerciseImageEditFailureLifecycle(dir).GetAwaiter().GetResult();
            ExerciseSessionFileJobLifetime(dir).GetAwaiter().GetResult();
            ExerciseRetirementBeforeBlockedRead(dir).GetAwaiter().GetResult();
            ExerciseOrdinaryCatalogShutdown(dir).GetAwaiter().GetResult();
            ExerciseYieldDuringShutdown(dir).GetAwaiter().GetResult();
            ExerciseSharedUserAcceptanceOrder(dir);
            ExerciseLegacyFirstImportOrder(dir);
            ExerciseNewImportPreemptsOlderManualScan(dir).GetAwaiter().GetResult();
            ExerciseAcceptedRecipeReservesRank(dir).GetAwaiter().GetResult();
            Console.WriteLine("DURABLE / AUTOMATIC PAUSE, RESUME, PRIORITY PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("DURABLE PAUSE/RESUME FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task ExerciseQueuedSessionWrites(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "alpha66-session-write.db"));
        database.Initialize();
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var first = scheduler.EnqueueSessionWriteAsync("session:first", async token =>
        {
            entered.TrySetResult();
            await release.Task;
            order.Enqueue(1); // current atomic file completes despite cancel
        }, "Bildbearbeitung speichern", "first.jpg", 1);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var second = scheduler.EnqueueSessionWriteAsync("session:second", _ =>
            {
                order.Enqueue(2);
                return Task.CompletedTask;
            }, "Bildbearbeitung speichern", "second.jpg", 1);
            var third = scheduler.EnqueueSessionWriteAsync("session:third", _ =>
            {
                order.Enqueue(3);
                return Task.CompletedTask;
            }, "Bildbearbeitung speichern", "third.jpg", 1);
            var queued = scheduler.Snapshot().Where(x => x.SessionWrite &&
                x.State == SchedulerJobState.Queued).OrderBy(x => x.QueueOrder).ToArray();
            if (queued.Length != 2 || queued[0].Key != "session:third" ||
                queued[1].Key != "session:second")
                throw new Exception("Accepted single-image writes did not share the LIFO scheduler queue.");
            var shutdown = scheduler.PrepareGracefulShutdownAsync();
            release.TrySetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(8));
            try { await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) { }
            var until = DateTime.UtcNow.AddSeconds(2);
            while (scheduler.HasSessionWrites && DateTime.UtcNow < until) await Task.Delay(10);
            if (!order.SequenceEqual(new[] { 1 }) || scheduler.HasSessionWrites ||
                !second.IsCanceled || !third.IsCanceled)
                throw new Exception("Closing dispatched an unstarted write or interrupted the current file.");
        }
        finally { release.TrySetResult(); }
        Console.WriteLine("PASS: Shutdown completes only the current atomic file and cancels queued session writes.");
    }

    private static async Task ExerciseSessionControls(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "alpha69-session-controls.db"));
        database.Initialize();
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var cursor = 0;
        var batch = scheduler.EnqueueSessionWriteAsync("session:rotation", async token =>
        {
            while (cursor < 3)
            {
                token.ThrowIfCancellationRequested();
                if (cursor == 0) { entered.TrySetResult(); await release.Task; }
                order.Enqueue(++cursor);
            }
        }, "Bildbearbeitung speichern", total: 3, resumable: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var canceled = scheduler.EnqueueSessionWriteAsync("session:cancel", _ =>
                throw new Exception("Deleted job executed."));
            if (!await scheduler.CancelActiveTaskAsync("session:cancel") || !canceled.IsCanceled)
                throw new Exception("Waiting session operation was not deleted.");
            if (!scheduler.SetSessionTaskPaused("session:rotation", true))
                throw new Exception("Running session operation was not pauseable.");
            var next = scheduler.EnqueueSessionWriteAsync("session:newer", _ =>
            {
                order.Enqueue(9);
                return Task.CompletedTask;
            });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!scheduler.Snapshot().Any(x => x.Key == "session:rotation" &&
                       x.State == SchedulerJobState.YieldRequested) && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            release.TrySetResult();
            await next.WaitAsync(TimeSpan.FromSeconds(8));
            if (batch.IsCompleted || !order.SequenceEqual(new[] { 1, 9 }))
                throw new Exception("Paused session job blocked higher work or repeated a file.");
            if (!scheduler.SetSessionTaskPaused("session:rotation", false))
                throw new Exception("Session resume failed.");
            await batch.WaitAsync(TimeSpan.FromSeconds(8));
            if (!order.SequenceEqual(new[] { 1, 9, 2, 3 }))
                throw new Exception("Session cursor was lost on resume.");
        }
        finally { release.TrySetResult(); }
        Console.WriteLine("PASS: Session jobs delete, pause, yield and resume at file boundaries.");
    }

    private static async Task ExercisePersistentMultiWrite(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "alpha64-multi-write.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new System.Collections.Concurrent.ConcurrentQueue<int>();
        const string key = "gps:multi-restart";
        SchedulerTaskPackage Build(string identity, string payload)
        {
            var package = new SchedulerTaskPackage(identity);
            for (var i = 1; i <= 3; i++)
            {
                var step = i;
                package.AddStep(async _ =>
                {
                    if (step == 1)
                    {
                        entered.TrySetResult();
                        await release.Task;
                    }
                    writes.Enqueue(step);
                });
            }
            return package;
        }
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            scheduler.RegisterDurablePackageType("gps-edit-v2", Build);
            var completion = scheduler.EnqueueDurableRecipeAsync(key,
                "gps-edit-v2", "{}", SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                if (journal.Pending().Single().Key != key)
                    throw new Exception("Multi-photo GPS did not own one durable identity.");
                var drain = scheduler.PrepareGracefulShutdownAsync();
                release.TrySetResult();
                await drain.WaitAsync(TimeSpan.FromSeconds(8));
                try { await completion.WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (OperationCanceledException) { }
                if (journal.Pending().Single().CompletedSteps != 1 ||
                    !writes.SequenceEqual(new[] { 1 }))
                    throw new Exception("Multi-write did not stop at the confirmed file boundary.");
            }
            finally { release.TrySetResult(); }
        }
        database.Initialize();
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            scheduler.RegisterDurablePackageType("gps-edit-v2", Build);
            if (scheduler.RecoverDurablePackages().Count != 0)
                throw new Exception("Multi-write recipe failed recovery.");
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (journal.Pending().Count != 0 && DateTime.UtcNow < deadline)
                await Task.Delay(30);
            if (journal.Pending().Count != 0 || !writes.SequenceEqual(new[] { 1, 2, 3 }))
                throw new Exception("Multi-write replayed confirmed files or lost unfinished files.");
        }
        Console.WriteLine("PASS: One multi-photo job resumes after shutdown without repeating confirmed steps.");
    }

    private static async Task ExerciseAcceptedRecipeReservesRank(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "preparing-recipe-rank.db"));
        database.Initialize();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var attempts = 0;
        SchedulerTaskPackage Build(string key, string payload)
        {
            if (key == "recipe:newer")
            {
                preparing.TrySetResult();
                releaseFactory.Task.GetAwaiter().GetResult();
            }
            var package = new SchedulerTaskPackage(key);
            package.AddStep(async token =>
            {
                if (key == "recipe:older" && Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    using var registration = token.Register(() => yielded.TrySetResult());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                events.Enqueue(key);
            });
            return package;
        }
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        scheduler.RegisterDurablePackageType("manual-folder-scan-v1", Build);
        var older = scheduler.EnqueueDurableRecipeAsync("recipe:older",
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = scheduler.EnqueueDurableRecipeAsync("recipe:newer",
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        try
        {
            await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await yielded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The older job must stay yielded while the newer recipe is
            // accepted but not yet executable. Independent UI reads still run.
            await scheduler.EnqueueAsync("recipe:gallery",
                SchedulerPriority.Interactive, _ => Task.CompletedTask,
                workKind: SchedulerWorkKind.BrowseRead).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(350);
            if (older.IsCompleted || Volatile.Read(ref attempts) != 1)
                throw new Exception("Older job overtook a newer accepted recipe.");
        }
        finally { releaseFactory.TrySetResult(); }
        await Task.WhenAll(older, newer).WaitAsync(TimeSpan.FromSeconds(8));
        if (!events.SequenceEqual(new[] { "recipe:newer", "recipe:older" }))
            throw new Exception("Recipe preparation changed the global accepted order.");
        Console.WriteLine("PASS: Accepted recipes reserve their rank before construction.");
    }

    private static async Task ExerciseNewImportPreemptsOlderManualScan(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "initial-import-preempts.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        var folders = new FolderRepository(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var attempts = 0;
        SchedulerTaskPackage Build(string key, string payload)
        {
            var package = new SchedulerTaskPackage(key);
            package.AddStep(async token =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                events.Enqueue("older-scan");
            });
            return package;
        }
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        scheduler.RegisterDurablePackageType("manual-folder-scan-v1", Build);
        var older = scheduler.EnqueueDurablePackageAsync(Build("older-scan", "{}"),
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var source = Path.Combine(dir, "new-priority-import");
        Directory.CreateDirectory(source);
        folders.Add(source);
        var accepted = folders.GetPendingFirstImportQueue().Single();
        if (accepted.QueueOrder >= journal.Pending().Single().QueueOrder)
            throw new Exception("New import has an older rank than the existing manual scan.");
        var import = scheduler.EnqueueAsync($"initial:{accepted.FolderId}",
            SchedulerPriority.ManualAction, _ =>
            {
                events.Enqueue("new-import");
                return Task.CompletedTask;
            }, workKind: SchedulerWorkKind.InitialImport, queueOrder: accepted.QueueOrder);
        await Task.WhenAll(import, older).WaitAsync(TimeSpan.FromSeconds(8));
        if (!events.SequenceEqual(new[] { "new-import", "older-scan" }) || attempts != 2)
            throw new Exception("New accepted initial import did not preempt and resume the older manual scan.");
        Console.WriteLine("PASS: Shared P0 admission dispatches newer first imports before older scans.");
    }

    private static void ExerciseLegacyFirstImportOrder(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "legacy-import-order.db"));
        database.Initialize();
        var folders = new FolderRepository(database);
        var paths = new[] { "legacy-import-a", "legacy-import-b", "legacy-import-c" }
            .Select(name => Path.Combine(dir, name)).ToArray();
        foreach (var path in paths)
        {
            Directory.CreateDirectory(path);
            folders.Add(path);
        }
        var roots = folders.GetAll().ToDictionary(root => root.LocalPath,
            StringComparer.OrdinalIgnoreCase);
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            // Deliberately use a legacy user-defined order different from
            // folder identity and insertion order.
            cmd.CommandText = """
                UPDATE monitored_folders
                SET initial_scan_queue_order=CASE id
                    WHEN $a THEN 30 WHEN $b THEN 10 WHEN $c THEN 20 END;
                """;
            cmd.Parameters.AddWithValue("$a", roots[paths[0]].Id);
            cmd.Parameters.AddWithValue("$b", roots[paths[1]].Id);
            cmd.Parameters.AddWithValue("$c", roots[paths[2]].Id);
            cmd.ExecuteNonQuery();
        }
        database.Initialize();
        var legacy = folders.GetPendingFirstImportQueue();
        var expected = new[] { roots[paths[1]].Id, roots[paths[2]].Id, roots[paths[0]].Id };
        if (!legacy.Select(row => row.FolderId).SequenceEqual(expected))
            throw new Exception("Upgrade reversed legacy first-import positions.");
        var added = Path.Combine(dir, "legacy-import-new");
        Directory.CreateDirectory(added);
        folders.Add(added);
        var newId = folders.GetAll().Single(root =>
            string.Equals(root.LocalPath, added, StringComparison.OrdinalIgnoreCase)).Id;
        database.Initialize();
        if (!folders.GetPendingFirstImportQueue().Select(row => row.FolderId)
                .SequenceEqual(new[] { newId }.Concat(expected)))
            throw new Exception("A new import did not precede the preserved legacy queue.");
        Console.WriteLine("PASS: Legacy import order survives upgrade and new LIFO admission.");
    }

    private static void ExerciseSharedUserAcceptanceOrder(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "shared-user-order.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        var folders = new FolderRepository(database);
        var first = journal.Put("before:import", "manual-scan-all-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        var source = Path.Combine(dir, "shared-order-source");
        Directory.CreateDirectory(source);
        folders.Add(source);
        var initial = folders.GetPendingFirstImportQueue().Single();
        var latest = journal.Put("after:import", "manual-folder-scan-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        if (!(latest.QueueOrder < initial.QueueOrder && initial.QueueOrder < first.QueueOrder))
            throw new Exception("Imports and durable user packages have different LIFO sequences.");
        database.Initialize();
        folders.Add(source); // selecting the existing root is not a new acceptance
        if (new FolderRepository(database).GetPendingFirstImportQueue().Single().QueueOrder
                != initial.QueueOrder ||
            new DurablePackageJournal(database).Pending().Single(row => row.Key == latest.Key)
                .QueueOrder != latest.QueueOrder)
            throw new Exception("Restart or duplicate acceptance changed a user job's rank.");
        var next = journal.Put("after:restart", "manual-scan-all-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        if (next.QueueOrder >= latest.QueueOrder)
            throw new Exception("Shared acceptance sequence was reset by migration.");
        Console.WriteLine("PASS: Initial imports and explicit user packages share durable LIFO admission.");
    }

    private static async Task ExerciseOrdinaryCatalogShutdown(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "shutdown-ordinary-catalog.db"));
        database.Initialize();
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinary = scheduler.EnqueueAsync("test:ordinary-catalog",
            SchedulerPriority.Background, async _ =>
            {
                entered.TrySetResult();
                await released.Task;
            }, workKind: SchedulerWorkKind.Catalog);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await scheduler.PrepareGracefulShutdownAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (ordinary.IsCompleted)
                throw new Exception("Ordinary catalog fixture was not still blocked.");
        }
        finally { released.TrySetResult(); }
        await ordinary.WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine("PASS: Ordinary catalog work does not block the safe write drain.");
    }

    private static async Task ExerciseYieldDuringShutdown(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "shutdown-yield.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var yielded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string key = "shutdown:write";
        SchedulerTaskPackage Build(string identity, string payload)
        {
            var package = new SchedulerTaskPackage(identity);
            package.AddStep(async token =>
            {
                started.TrySetResult();
                using var registration = token.Register(() => yielded.TrySetResult());
                await finishBoundary.Task;
                token.ThrowIfCancellationRequested();
            });
            return package;
        }
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        scheduler.RegisterDurablePackageType("gallery-delete-v1", Build);
        var writing = scheduler.EnqueueDurableRecipeAsync(key,
            "gallery-delete-v1", "{}", SchedulerPriority.Background,
            SchedulerWorkKind.Catalog);
        Task? higher = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
            higher = scheduler.EnqueueAsync("shutdown:higher",
                SchedulerPriority.ManualAction, _ => Task.CompletedTask);
            await yielded.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var drain = scheduler.PrepareGracefulShutdownAsync();
            finishBoundary.TrySetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(8));
            try { await writing.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) { }
            if (!journal.Pending().Any(row => row.Key == key && row.CompletedSteps == 0) ||
                scheduler.HasLiveExecution(key))
                throw new Exception("Yielded file job reentered the frozen shutdown queue.");
            if (higher.IsCompleted)
                throw new Exception("Unrelated waiting work dispatched during shutdown.");
        }
        finally { finishBoundary.TrySetResult(); }
        Console.WriteLine("PASS: Graceful shutdown does not requeue an interrupted file owner.");
    }

    private static async Task ExerciseSessionFileJobLifetime(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "session-file-jobs.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        journal.Put("old:transfer", "gallery-transfer-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
        journal.SetState("old:transfer", "running", 2);
        journal.Put("old:scan", "manual-folder-scan-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        journal.SetState("old:scan", "running", 1);
        journal.Put("old:retry", "import-error-retry-v1", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            var pending = journal.Pending();
            if (pending.Count != 3 ||
                pending.Single(row => row.Key == "old:scan").CompletedSteps != 1 ||
                pending.Single(row => row.Key == "old:transfer").CompletedSteps != 2 ||
                !pending.Any(row => row.Key == "old:retry"))
                throw new Exception("Startup lost a durable file or scan checkpoint.");
            journal.Put("new:delete", "gallery-delete-v1", "{}",
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            journal.Put("new:scan", "manual-scan-all-v1", "{}",
                SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        }
        var remaining = journal.Pending();
        if (remaining.Count != 5 ||
            remaining.Single(row => row.Key == "old:transfer").CompletedSteps != 2 ||
            !remaining.Any(row => row.Key == "new:delete"))
            throw new Exception("Shutdown discarded a durable file job.");
        Console.WriteLine("PASS: All durable jobs and checkpoints survive shutdown.");
    }

    private static async Task ExerciseRetirementBeforeBlockedRead(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "shutdown-blocked-read.db"));
        database.Initialize();
        var folders = new FolderRepository(database);
        var source = Path.Combine(dir, "shutdown-tag-source");
        Directory.CreateDirectory(source);
        folders.Add(source);
        var folderId = folders.GetAll().Single().Id;
        long imageId;
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO images(folder_id,local_path,canonical_path,file_name,
                    extension,file_size,modified_utc_ticks,width,height,
                    thumbnail_path,indexed_utc)
                VALUES($folder,$path,$path,'offline.jpg','.jpg',100,1,10,10,'','2026-01-01')
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$folder", folderId);
            cmd.Parameters.AddWithValue("$path", Path.Combine(source, "offline.jpg"));
            imageId = Convert.ToInt64(cmd.ExecuteScalar());
        }
        var journal = new DurablePackageJournal(database);
        await using var scheduler = new PCasaScheduler();
        scheduler.EnableDurablePackages(database);
        var tags = new ImageTagRepository(database);
        void Stage(string owner, string keyword, bool remove)
            => tags.QueueTagEdits(new[]
            {
                (ImageId: imageId, Edits: (IReadOnlyList<(string Tag, bool Remove)>)
                    new[] { (Tag: keyword, Remove: remove) })
            }, owner);
        Stage("shutdown:staged-tags", "unfinished", false);
        Stage("shutdown:older-tags", "shared", false);
        Stage("shutdown:newer-tags", "shared", true);
        database.Initialize();
        if (tags.PendingImageIdsOwnedBy("shutdown:older-tags").Count != 0 ||
            !tags.PendingImageIdsOwnedBy("shutdown:newer-tags").SequenceEqual(new[] { imageId }) ||
            !tags.PendingImageIdsOwnedBy("shutdown:staged-tags").SequenceEqual(new[] { imageId }))
            throw new Exception("Tag revision ownership did not survive overlapping edits and database reopen.");
        // No original exists at this path: cancellation must be a local
        // transaction, preserve other jobs, and restore a removed keyword.
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE image_tags SET keywords=keywords||char(10)||'existing' WHERE image_id=$id;";
            cmd.Parameters.AddWithValue("$id", imageId);
            cmd.ExecuteNonQuery();
        }
        const string cancelKey = "shutdown:cancel-tags";
        journal.Put(cancelKey, "tag-edit-v2", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.Catalog);
        journal.SetState(cancelKey, "running", 1);
        Stage(cancelKey, "temporary", false);
        Stage(cancelKey, "existing", true);
        if (!await scheduler.DeleteDurableTaskAsync(cancelKey).WaitAsync(TimeSpan.FromSeconds(5)) ||
            tags.PendingImageIdsOwnedBy(cancelKey).Count != 0 ||
            tags.PendingEditsForImage(imageId).Count != 2 ||
            !tags.PendingImageIdsOwnedBy("shutdown:newer-tags").Contains(imageId) ||
            !(tags.GetKeywords(imageId) ?? "").Split('\n').ToHashSet()
                .SetEquals(new[] { "unfinished", "existing" }) ||
            journal.Pending().Any(row => row.Key == cancelKey))
            throw new Exception("Targeted tag cancellation altered another job or lost the original projection.");
        const string uncertainKey = "shutdown:uncertain-tags";
        journal.Put(uncertainKey, "tag-edit-v2", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.Catalog);
        journal.SetState(uncertainKey, "running", 1);
        Stage(uncertainKey, "uncertain", false);
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE pending_tag_edits SET base_present=NULL WHERE owner_key=$key;";
            cmd.Parameters.AddWithValue("$key", uncertainKey);
            cmd.ExecuteNonQuery();
        }
        var refused = false;
        try { await scheduler.DeleteDurableTaskAsync(uncertainKey); }
        catch (InvalidOperationException) { refused = true; }
        if (!refused || tags.PendingImageIdsOwnedBy(uncertainKey).Count != 1 ||
            journal.Pending().Single(row => row.Key == uncertainKey).State != "paused")
            throw new Exception("An uncertain native write was silently reversed.");
        using (var db = database.OpenConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE pending_tag_edits SET base_present=0 WHERE owner_key=$key;";
            cmd.Parameters.AddWithValue("$key", uncertainKey);
            cmd.ExecuteNonQuery();
        }
        if (!await scheduler.DeleteDurableTaskAsync(uncertainKey))
            throw new Exception("Confirmed cancellation could not be retried.");
        Console.WriteLine("PASS: Owned tag cancellation preserves other jobs and refuses an uncertain native receipt.");
        journal.Put("shutdown:staged-tags", "tag-edit-v2", "{}",
            SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
        journal.SetState("shutdown:staged-tags", "running", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = scheduler.EnqueueAsync("shutdown:blocked-read",
            SchedulerPriority.Interactive, async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            }, workKind: SchedulerWorkKind.BrowseRead);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await scheduler.PrepareGracefulShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (read.IsCompleted || !journal.Pending().Any(row => row.Key == "shutdown:staged-tags" && row.CompletedSteps == 1) ||
                new OriginalWriteFailureRepository(database).IsParked(
                    OriginalWriteFailureRepository.Tags, imageId) ||
                tags.PendingEditsForImage(imageId).Count != 2)
                throw new Exception("Shutdown lost durable tag intents or waited for an unrelated read.");
        }
        finally { release.TrySetResult(); }
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS: Durable tag intents survive blocked background reads.");
    }

    private static async Task ExerciseImageEditFailureLifecycle(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "failed-image-save.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        var successfulSteps = 0;
        SchedulerTaskPackage BuildImageEdit(string key, string payload)
        {
            var package = new SchedulerTaskPackage(key);
            package.AddStep(_ =>
            {
                if (payload == "locked")
                    throw new IOException("Simulated Windows file sharing violation.");
                successfulSteps++;
                return Task.CompletedTask;
            });
            return package;
        }

        await using var scheduler = new PCasaScheduler();
        scheduler.RegisterDurablePackageType("image-edit-v1", BuildImageEdit);
        scheduler.EnableDurablePackages(database);

        try
        {
            await scheduler.EnqueueDurableRecipeAsync(
                "image:edit:first-locked", "image-edit-v1", "locked",
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            throw new Exception("Test image edit unexpectedly succeeded.");
        }
        catch (IOException) { }

        // Never leave a deferred first save which claims the image forever.
        if (journal.Pending().Any(record => record.Key == "image:edit:first-locked"))
            throw new Exception("Failed image save remained an active durable task.");
        using (var db = database.OpenConnection())
        using (var command = db.CreateCommand())
        {
            command.CommandText =
                "SELECT state FROM scheduler_packages WHERE package_key=$key;";
            command.Parameters.AddWithValue("$key", "image:edit:first-locked");
            if (!string.Equals(command.ExecuteScalar()?.ToString(),
                    "failed", StringComparison.Ordinal))
                throw new Exception("Failed image save lost its terminal audit record.");
        }

        // An explicit second Save is a NEW recipe. It must not be rejected
        // by the previous failed identity or replay that original recipe.
        await scheduler.EnqueueDurableRecipeAsync(
            "image:edit:manual-retry", "image-edit-v1", "success",
            SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
        if (successfulSteps != 1 ||
            journal.Pending().Any(record => record.Type == "image-edit-v1"))
            throw new Exception("Fresh manual save did not recover after first lock.");
        Console.WriteLine("PASS: Failed image save is terminal; next manual Save runs once.");
    }

    private static void ExerciseInitialImportStartupRelease(string dir)
    {
        var db = new LocalDatabase(Path.Combine(dir, "initial-pause.db"));
        db.Initialize();
        var folders = new FolderRepository(db);
        var path = Path.Combine(dir, "paused-import");
        Directory.CreateDirectory(path);
        folders.Add(path);
        var root = folders.GetAll().Single();
        if (!folders.SetPendingFirstImportPaused(root.Id, true))
            throw new Exception("Initial import refused pause");
        if (folders.GetPendingInitialScans().Any())
            throw new Exception("Paused initial import was executable in this session");
        var reopened = new FolderRepository(db);
        reopened.ReleaseStartupImportPauses();
        if (reopened.GetPendingInitialScans().Single().Id != root.Id)
            throw new Exception("Restart did not release original initial-import identity");
        if (!reopened.HasIncompleteImport(root.Id))
            throw new Exception("Releasing a pause falsely completed the import");
    }

    private static async Task ExerciseAutomaticControlPersistence(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "pcasa.db"));
        const string key = @"automatic:1:RescanRoot:G:\Camera Roll";
        const string ordinaryKey = @"automatic:1:ScanFile:G:\Camera Roll\photo.jpg";
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            if (!scheduler.SetExternalTaskPaused(key, true) ||
                !scheduler.IsExternalTaskPaused(key))
                throw new Exception("Automatic scan pause was not accepted");
            if (!scheduler.SetExternalTaskPaused(ordinaryKey, true))
                throw new Exception("Ordinary watcher pause was not accepted");
        }
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            if (scheduler.IsExternalTaskPaused(key) ||
                !scheduler.IsExternalTaskPaused(PCasaScheduler.ContinuousReviewKey))
                throw new Exception("Legacy hold did not migrate to permanent background review");
            if (scheduler.IsExternalTaskPaused(ordinaryKey) ||
                !scheduler.RecoverableExternalTaskKeys().Contains(ordinaryKey))
                throw new Exception("Restart did not release and recover the ordinary watcher request");
            scheduler.AcknowledgeRecoveredExternalTask(ordinaryKey);
            if (scheduler.RecoverableExternalTaskKeys().Contains(ordinaryKey))
                throw new Exception("Recovered watcher request was not acknowledged");
            if (!scheduler.SetExternalTaskPaused(PCasaScheduler.ContinuousReviewKey, false) ||
                scheduler.IsExternalTaskPaused(PCasaScheduler.ContinuousReviewKey))
                throw new Exception("Continuous review did not resume");
            if (scheduler.PromoteTaskNow(PCasaScheduler.ContinuousReviewKey) ||
                scheduler.MoveTaskGlobally(PCasaScheduler.ContinuousReviewKey, ordinaryKey,
                    new[] { new SchedulerTaskOrderItem(ordinaryKey, SchedulerPriority.Background),
                        new SchedulerTaskOrderItem(PCasaScheduler.ContinuousReviewKey, SchedulerPriority.Idle) }))
                throw new Exception("Permanent review accepted promotion or move");
            if (!scheduler.PromoteTaskNow(key))
                throw new Exception("Promote Now rejected the original key");
            var placement = scheduler.EffectiveTaskPlacement(key,
                SchedulerPriority.Idle, 15);
            if (placement.Priority != SchedulerPriority.ManualAction ||
                placement.QueueOrder != long.MinValue + 1)
                throw new Exception("Promote Now did not change scheduler rank");
            scheduler.CompleteTaskPromotion(key);
            placement = scheduler.EffectiveTaskPlacement(key,
                SchedulerPriority.Idle, 15);
            if (placement.Priority != SchedulerPriority.Idle ||
                placement.QueueOrder != 15)
                throw new Exception("Start Now left a permanent P0 override");
        }
    }

    private static async Task ExercisePauseAndRestart(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "pcasa.db"));
        database.Initialize();
        var journal = new DurablePackageJournal(database);
        const string key = "test:pause:tags-gps";
        var executed = new List<int>();
        var startedStepTwo = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStepAttempts = 0;
        var canFinish = false;
        SchedulerTaskPackage Make(string identity, string payload)
        {
            var pkg = new SchedulerTaskPackage(identity);
            pkg.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                executed.Add(1);
                return Task.CompletedTask;
            });
            pkg.AddStep(async token =>
            {
                secondStepAttempts++;
                startedStepTwo.TrySetResult();
                if (!canFinish)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                token.ThrowIfCancellationRequested();
                executed.Add(2);
            });
            pkg.AddStep(token =>
            {
                token.ThrowIfCancellationRequested();
                executed.Add(3);
                completed.TrySetResult();
                return Task.CompletedTask;
            });
            return pkg;
        }

        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.RegisterDurablePackageType("pause-test-v1", Make);
            scheduler.EnableDurablePackages(database);
            scheduler.RecoverDurablePackages();
            var pendingTask = scheduler.EnqueueDurableRecipeAsync(key,
                "pause-test-v1", "{}", SchedulerPriority.ManualAction,
                SchedulerWorkKind.Catalog);
            await startedStepTwo.Task.WaitAsync(TimeSpan.FromSeconds(12));
            if (!await scheduler.PauseDurableTaskAsync(key))
                throw new Exception("Running durable task refused pause");
            try { await pendingTask.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (OperationCanceledException) { }
            var paused = journal.Pending().Single(x => x.Key == key);
            if (paused.State != "paused" || paused.CompletedSteps != 1)
                throw new Exception("Pause lost original step cursor");
            if (!executed.SequenceEqual(new[] { 1 }))
                throw new Exception("Paused job executed an unconfirmed step");
        }

        // Alpha.63: normal holds expire on restart, but their progress does not.
        canFinish = true;
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.RegisterDurablePackageType("pause-test-v1", Make);
            scheduler.EnableDurablePackages(database);
            var recovered = journal.Pending().Single(x => x.Key == key);
            if (recovered.State != "queued" || recovered.CompletedSteps != 1)
                throw new Exception("Startup release changed the durable identity/cursor");
            scheduler.RecoverDurablePackages();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(12));
            // A test-step completion precedes the scheduler continuation's
            // final SQLite state commit. Wait for that authoritative receipt.
            for (var attempt = 0;
                 attempt < 100 && journal.Pending().Any(x => x.Key == key);
                 attempt++)
                await Task.Delay(30);
            if (!executed.SequenceEqual(new[] { 1, 2, 3 }))
                throw new Exception("Resume repeated a committed step");
            if (journal.Pending().Any(x => x.Key == key))
                throw new Exception("Completed package remained in active journal");
            if (secondStepAttempts != 2)
                throw new Exception("Resume did not restart exactly once");
        }
    }
}
