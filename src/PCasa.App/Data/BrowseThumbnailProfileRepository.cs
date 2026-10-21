using PCasa.App.Models;

namespace PCasa.App.Data;

public sealed class BrowseThumbnailProfileRepository
{
    private readonly LocalDatabase _database;

    public BrowseThumbnailProfileRepository(LocalDatabase database)
    {
        _database = database;
    }

    public BrowseThumbnailProfile Get()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT tiers_csv FROM browse_cache_settings WHERE id = 1;";

        var value = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return BrowseThumbnailProfile.Default;
        }

        var tiers = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out var tier) ? tier : 0);

        return BrowseThumbnailProfile.Normalize(tiers);
    }

    public void Save(IEnumerable<int> tiers)
    {
        var profile = BrowseThumbnailProfile.Normalize(tiers);
        var csv = string.Join(',', profile.Tiers);

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = "SELECT tiers_csv FROM browse_cache_settings WHERE id = 1;";
        var previousCsv = previous.ExecuteScalar() as string;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO browse_cache_settings (id, tiers_csv, updated_utc)
            VALUES (1, $tiersCsv, $updatedUtc)
            ON CONFLICT(id) DO UPDATE SET
                tiers_csv = excluded.tiers_csv,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$tiersCsv", csv);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        if (!string.Equals(previousCsv, csv, StringComparison.Ordinal))
        {
            using var enqueue = connection.CreateCommand();
            enqueue.Transaction = transaction;
            enqueue.CommandText = """
                INSERT OR IGNORE INTO browse_cache_jobs(image_id, state, updated_utc)
                SELECT id, 'PENDING', strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM images;
                """;
            enqueue.ExecuteNonQuery();
            using var tracking = connection.CreateCommand();
            tracking.Transaction = transaction;
            tracking.CommandText = """
                INSERT INTO browse_cache_tracking(id, tiers_csv) VALUES(1, $csv)
                ON CONFLICT(id) DO UPDATE SET tiers_csv=excluded.tiers_csv;
                """;
            tracking.Parameters.AddWithValue("$csv", csv);
            tracking.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
