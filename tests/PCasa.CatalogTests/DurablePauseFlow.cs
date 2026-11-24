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
            ExerciseAutomaticControlPersistence(dir).GetAwaiter().GetResult();
            ExerciseInitialImportStartupRelease(dir);
            ExerciseImageEditFailureLifecycle(dir).GetAwaiter().GetResult();
            ExerciseSessionFileJobLifetime(dir).GetAwaiter().GetResult();
            ExerciseYieldDuringShutdown(dir).GetAwaiter().GetResult();
            ExerciseSharedUserAcceptanceOrder(dir);
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
        scheduler.EnableDurablePackages(database, retireFileJobsOnSessionEnd: true);
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
            if (journal.Pending().Any(row => row.Key == key) ||
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
            scheduler.EnableDurablePackages(database, retireFileJobsOnSessionEnd: true);
            var pending = journal.Pending();
            if (pending.Count != 2 ||
                pending.Single(row => row.Key == "old:scan").CompletedSteps != 1 ||
                !pending.Any(row => row.Key == "old:retry"))
                throw new Exception("Startup replayed a file job or discarded a scan checkpoint.");
            journal.Put("new:delete", "gallery-delete-v1", "{}",
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            journal.Put("new:scan", "manual-scan-all-v1", "{}",
                SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
        }
        var remaining = journal.Pending();
        if (remaining.Count != 3 || remaining.Any(row =>
                !DurablePackageJournal.SurvivesSession(row.Type)))
            throw new Exception("Shutdown retained a file job or lost a scan.");
        using var db = database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM scheduler_packages
             WHERE state='canceled'
               AND ((package_key='old:transfer' AND completed_steps=2)
                    OR package_key='new:delete');
            """;
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 2)
            throw new Exception("Session retirement lost the terminal audit records.");
        Console.WriteLine("PASS: File jobs end with the session; scan receipts survive.");
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
