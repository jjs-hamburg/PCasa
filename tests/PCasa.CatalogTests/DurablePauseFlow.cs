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
            ExerciseImageEditFailureLifecycle(dir).GetAwaiter().GetResult();
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

    private static async Task ExerciseAutomaticControlPersistence(string dir)
    {
        var database = new LocalDatabase(Path.Combine(dir, "pcasa.db"));
        const string key = @"automatic:1:RescanRoot:G:\Camera Roll";
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            if (!scheduler.SetExternalTaskPaused(key, true) ||
                !scheduler.IsExternalTaskPaused(key))
                throw new Exception("Automatic scan pause was not accepted");
        }
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.EnableDurablePackages(database);
            if (!scheduler.IsExternalTaskPaused(key))
                throw new Exception("Automatic scan pause was lost on restart");
            if (!scheduler.SetExternalTaskPaused(key, false) ||
                scheduler.IsExternalTaskPaused(key))
                throw new Exception("Automatic scan did not resume");
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

        // Closing/recreating the process must NOT auto-resume a user pause.
        await using (var scheduler = new PCasaScheduler())
        {
            scheduler.RegisterDurablePackageType("pause-test-v1", Make);
            scheduler.EnableDurablePackages(database);
            scheduler.RecoverDurablePackages();
            await Task.Delay(350);
            if (secondStepAttempts != 1 ||
                journal.Pending().Single(x => x.Key == key).State != "paused")
                throw new Exception("Recovered scheduler restarted a paused package");
            canFinish = true;
            if (!scheduler.ResumeDurableTask(key))
                throw new Exception("Resume did not activate original identity");
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
