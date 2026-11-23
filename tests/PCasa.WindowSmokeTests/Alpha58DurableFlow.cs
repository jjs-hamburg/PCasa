using System.IO;
using PCasa.App.Data;
using PCasa.App.Services;

// Regression for Alpha.58: exit must stop at the next committed file step,
// never repeat that original on next startup, and never discard queued work.
internal static class Alpha58DurableFlow
{
    // The console test runs on an STA with WPF's DispatcherSynchronizationContext.
    // Blocking DisposeAsync on that STA deadlocks its continuations. Perform
    // teardown on a pool thread, exactly like the app's shutdown owner.
    private static void DisposeWithTimeout(PCasaScheduler scheduler)
        => Task.Run(async () =>
            await scheduler.DisposeAsync().ConfigureAwait(false))
            .WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "PCasa-Alpha58-Durable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var beganFirstOriginal = new ManualResetEventSlim(false);
        using var finishFirstOriginal = new ManualResetEventSlim(false);
        PCasaScheduler? scheduler = null;
        try
        {
            var database = new LocalDatabase(Path.Combine(root, "pcasa.db"));
            database.Initialize();
            var firstWritten = 0;
            var secondWritten = 0;
            SchedulerTaskPackage Recipe(string key, string payload)
            {
                if (payload != "{}") throw new InvalidDataException("Invalid test recipe");
                var package = new SchedulerTaskPackage(key);
                package.AddStep(_ =>
                {
                    if (Interlocked.Increment(ref firstWritten) != 1)
                        throw new Exception("First original was written twice.");
                    beganFirstOriginal.Set();
                    if (!finishFirstOriginal.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("First original remained blocked.");
                    return Task.CompletedTask;
                });
                package.AddStep(_ =>
                {
                    if (Interlocked.Increment(ref secondWritten) != 1)
                        throw new Exception("Second original was written twice.");
                    return Task.CompletedTask;
                });
                return package;
            }

            scheduler = new PCasaScheduler();
            scheduler.EnableDurablePackages(database);
            scheduler.RegisterDurablePackageType("alpha58-file-restart", Recipe);
            var running = scheduler.EnqueueDurableRecipeAsync(
                "alpha58:originals", "alpha58-file-restart", "{}",
                SchedulerPriority.ManualAction);
            if (!beganFirstOriginal.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("First original never started.");

            Console.WriteLine("Alpha.58: first original entered; freeze new dispatch.");
            scheduler.BeginGracefulShutdown();
            // Exiting must wait for THIS write, but never start the second.
            finishFirstOriginal.Set();
            var drain = scheduler.PrepareGracefulShutdownAsync();
            if (!drain.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("Graceful exit did not checkpoint the original.");

            Console.WriteLine("Alpha.58: first original checkpointed; disposing prior scheduler.");
            DisposeWithTimeout(scheduler);
            scheduler = null;
            var journal = new DurablePackageJournal(database);
            var pending = journal.Pending().SingleOrDefault(
                entry => entry.Key == "alpha58:originals");
            if (pending is null || pending.State != "queued" ||
                pending.CompletedSteps != 1 || firstWritten != 1 || secondWritten != 0)
                throw new Exception("Exit lost cursor, restarted work, or executed the next original.");

            // A fresh scheduler, reading ONLY SQLite, must resume step two
            // with the identical identity and not touch the confirmed first.
            scheduler = new PCasaScheduler();
            scheduler.EnableDurablePackages(database);
            scheduler.RegisterDurablePackageType("alpha58-file-restart", Recipe);
            Console.WriteLine("Alpha.58: restart from SQLite journal.");
            var unresolved = scheduler.RecoverDurablePackages();
            if (unresolved.Count != 0)
                throw new Exception("Recovery unexpectedly rejected the saved recipe.");
            if (!SpinWait.SpinUntil(() =>
                journal.RecentCompleted().Any(entry =>
                    entry.Key == "alpha58:originals" &&
                    entry.CompletedSteps == 2), TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The second original did not resume.");
            if (firstWritten != 1 || secondWritten != 1)
                throw new Exception("Original writes were repeated or skipped on restart.");

            DisposeWithTimeout(scheduler);
            scheduler = null;
            Console.WriteLine("PASS: Exit completes the current file, persists cursor, and resumes without duplicate writes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL Alpha.58 durable writes: " + ex);
            return 1;
        }
        finally
        {
            finishFirstOriginal.Set();
            if (scheduler is not null)
                try { DisposeWithTimeout(scheduler); }
                catch { /* Preserve the original test exception. */ }
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
