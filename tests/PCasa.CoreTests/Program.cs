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

    // These types must be delivered from the same WPF-free core assembly.
    var assembly = typeof(LocalDatabase).Assembly;
    if (assembly.GetName().Name != "PCasa.Core" ||
        typeof(SourceAvailabilityGate).Assembly != assembly ||
        typeof(SchedulerTaskPackage).Assembly != assembly ||
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
