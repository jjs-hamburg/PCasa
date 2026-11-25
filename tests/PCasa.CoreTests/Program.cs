using PCasa.App.Data;
using PCasa.App.Services;

var directory = Path.Combine(Path.GetTempPath(), "PCasa.CoreTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var database = new LocalDatabase(Path.Combine(directory, "catalog.db"));
    database.Initialize();
    database.Initialize(); // migrations must be idempotent on every host
    using (var connection = database.OpenConnection())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('pending_tag_edits')
            WHERE name IN ('owner_key','base_present');
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 2)
            throw new Exception("Tag migration did not preserve both cancellation fields.");
    }

    var reachable = false;
    var probes = 0;
    var clock = DateTimeOffset.UtcNow;
    var gate = new SourceAvailabilityGate(_ => { probes++; return reachable; },
        () => clock, offlineInterval: TimeSpan.FromSeconds(10));
    if (gate.CanAccess(directory) || gate.CanAccess(directory) || probes != 1)
        throw new Exception("Offline source probe was not coalesced.");
    reachable = true;
    clock = clock.AddSeconds(11);
    if (!gate.CanAccess(directory) || probes != 2)
        throw new Exception("Source did not recover after its retry interval.");

    // Exercise the actual production scheduler and durable journal on
    // both non-Windows hosts, including preemption and saved step order.
    var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
    await using (var scheduler = new PCasaScheduler())
    {
        scheduler.EnableDurablePackages(database);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var older = new SchedulerTaskPackage("portable:older");
        older.AddStep(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            order.Enqueue("older");
        });
        var first = scheduler.EnqueueDurablePackageAsync(older,
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newer = new SchedulerTaskPackage("portable:newer");
        newer.AddStep(_ => { order.Enqueue("newer"); return Task.CompletedTask; });
        var second = scheduler.EnqueueDurablePackageAsync(newer,
            "manual-folder-scan-v1", "{}", SchedulerPriority.ManualAction,
            SchedulerWorkKind.CatalogScan);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        if (!order.SequenceEqual(new[] { "newer", "older" }) ||
            attempts != 2 || new DurablePackageJournal(database).Pending().Count != 0)
            throw new Exception("Portable scheduler did not preserve LIFO and durable completion.");
    }

    // These types must be delivered from the same WPF-free core assembly.
    var assembly = typeof(LocalDatabase).Assembly;
    if (assembly.GetName().Name != "PCasa.Core" ||
        typeof(SourceAvailabilityGate).Assembly != assembly ||
        typeof(SchedulerTaskPackage).Assembly != assembly ||
        typeof(PCasaScheduler).Assembly != assembly ||
        typeof(DurablePackageJournal).Assembly != assembly ||
        typeof(SchedulerTaskOrderStore).Assembly != assembly ||
        typeof(DurableGalleryTransferJournal).Assembly != assembly ||
        typeof(OriginalWriteFailureRepository).Assembly != assembly ||
        assembly.GetReferencedAssemblies().Any(reference =>
            reference.Name is "PresentationFramework" or "PresentationCore" or
                "WindowsBase" or "PCasa.App"))
        throw new Exception("The portable core depends on the Windows presentation layer.");
    Console.WriteLine("PASS: portable SQLite migrations, source recovery and assembly boundary.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
