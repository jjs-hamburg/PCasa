using Microsoft.Data.Sqlite;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App.Data;

public sealed class FolderRepository
{
    private readonly LocalDatabase _database;

    public FolderRepository(LocalDatabase database)
    {
        _database = database;
    }

    public IReadOnlyList<MonitoredFolder> GetAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, local_path, canonical_path, is_network, server_sync_enabled
            FROM monitored_folders
            ORDER BY local_path COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var result = new List<MonitoredFolder>();

        while (reader.Read())
        {
            result.Add(new MonitoredFolder
            {
                Id = reader.GetInt64(0),
                LocalPath = reader.GetString(1),
                CanonicalPath = reader.GetString(2),
                IsNetwork = reader.GetBoolean(3),
                ServerSyncEnabled = reader.GetBoolean(4)
            });
        }

        return result;
    }

    public void Add(string path)
    {
        var classification = PathClassifier.Classify(path);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO monitored_folders
                (local_path, canonical_path, is_network, server_sync_enabled, created_utc)
            VALUES
                ($localPath, $canonicalPath, $isNetwork, 0, $createdUtc);
            """;
        command.Parameters.AddWithValue("$localPath", classification.LocalPath);
        command.Parameters.AddWithValue("$canonicalPath", classification.CanonicalPath);
        command.Parameters.AddWithValue("$isNetwork", classification.IsNetwork ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Remove(long id, IProgress<string>? progress = null)
    {
        // User-visible removal is a single transactional catalog change.
        // Expensive physical thumbnail cleanup is persisted and runs at idle,
        // not while the folder manager is waiting.
        progress?.Report("Ordner aus dem Katalog entfernen …");
        new CatalogRemovalService(_database).DetachMonitoredFolder(id);
        progress?.Report("Ordner entfernt · Cache-Bereinigung erfolgt im Leerlauf");
    }

    public IReadOnlyList<string> GetExcludedPaths(long monitoredFolderId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT local_path FROM excluded_folders
            WHERE monitored_folder_id = $id
            ORDER BY local_path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", monitoredFolderId);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void SetExcluded(long monitoredFolderId, string subfolderPath, bool excluded)
    {
        var monitored = GetAll().FirstOrDefault(item => item.Id == monitoredFolderId)
            ?? throw new InvalidOperationException("Der überwachte Ordner existiert nicht mehr.");
        var normalized = FolderExclusionRules.Normalize(subfolderPath);
        if (!FolderExclusionRules.IsAtOrBelow(normalized, monitored.LocalPath))
        {
            throw new InvalidOperationException(
                "Ordner liegt nicht innerhalb der überwachten Wurzel.");
        }

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = excluded
            ? """
              INSERT OR IGNORE INTO excluded_folders
                  (monitored_folder_id, local_path, created_utc)
              VALUES ($id, $path, $now);
              """
            : """
              DELETE FROM excluded_folders
              WHERE monitored_folder_id = $id AND local_path = $path COLLATE NOCASE;
              """;
        command.Parameters.AddWithValue("$id", monitoredFolderId);
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        if (excluded)
        {
            // Redundant exclusions nested under a newly excluded parent are
            // removed so re-including the parent behaves predictably.
            using var descendants = connection.CreateCommand();
            descendants.Transaction = transaction;
            descendants.CommandText = """
                SELECT local_path FROM excluded_folders
                WHERE monitored_folder_id = $id;
                """;
            descendants.Parameters.AddWithValue("$id", monitoredFolderId);
            using var reader = descendants.ExecuteReader();
            var redundant = new List<string>();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (!string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase) &&
                    FolderExclusionRules.IsAtOrBelow(path, normalized))
                {
                    redundant.Add(path);
                }
            }

            reader.Close();
            foreach (var path in redundant)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM excluded_folders
                    WHERE monitored_folder_id = $id AND local_path = $path;
                    """;
                delete.Parameters.AddWithValue("$id", monitoredFolderId);
                delete.Parameters.AddWithValue("$path", path);
                delete.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public void SetServerSync(long id, bool enabled)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE monitored_folders
            SET server_sync_enabled = CASE
                WHEN is_network = 1 AND substr(canonical_path, 1, 2) = '\\' THEN $enabled
                ELSE 0
            END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.ExecuteNonQuery();
    }
}
