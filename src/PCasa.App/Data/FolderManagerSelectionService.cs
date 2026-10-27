using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Services;

namespace PCasa.App.Data;

/// <summary>
/// Sole durable commit boundary for an entire folder-manager OK decision.
/// No dialog checkbox writes to the database or starts a scanner. Root,
/// exclusion and source-generation changes are atomic; foreign-key cascades
/// remove obsolete catalog rows in this same transaction.
/// </summary>
public sealed class FolderManagerSelectionService
{
    private readonly LocalDatabase _database;

    public FolderManagerSelectionService(LocalDatabase database) => _database = database;

    public readonly record struct ExplorerRegistration(bool Changed, bool CreatedRoot);

    // The manager's one transaction is also the only Explorer registration
    // path. An excluded direct child belongs to its existing monitored root:
    // reactivate its checkbox, never create a duplicate independent root.
    public ExplorerRegistration EnsureMonitoredFolder(string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = FolderExclusionRules.Normalize(path);
        var repository = new FolderRepository(_database);
        var roots = repository.GetAll();
        var covering = FolderExclusionRules.FindOwner(normalized, roots);

        var choices = roots.Select(root => new FolderManagerRootChoice(
            root.LocalPath, repository.GetExcludedPaths(root.Id).ToArray(),
            root.ServerSyncEnabled)).ToList();
        if (covering is not null)
        {
            var exclusions = repository.GetExcludedPaths(covering.Id);
            if (!FolderExclusionRules.IsExcluded(normalized, exclusions))
                return new ExplorerRegistration(false, false);

            // The current exclusion schema represents disjoint excluded
            // subtrees, not include-overrides. Never silently enable an
            // ancestor and all its other children merely to select one
            // descendant. The manager must resolve that configuration.
            var blockedByAncestor = exclusions.FirstOrDefault(excluded =>
                FolderExclusionRules.IsAtOrBelow(normalized, excluded) &&
                !string.Equals(normalized,
                    FolderExclusionRules.Normalize(excluded),
                    StringComparison.OrdinalIgnoreCase));
            if (blockedByAncestor is not null)
                throw new InvalidOperationException(
                    $"Der übergeordnete Ordner '{blockedByAncestor}' ist abgewählt. " +
                    "Bitte zuerst diesen Ordner im Ordnermanager aktivieren.");

            choices = choices.Select(choice =>
                string.Equals(FolderExclusionRules.Normalize(choice.LocalPath),
                    FolderExclusionRules.Normalize(covering.LocalPath),
                    StringComparison.OrdinalIgnoreCase)
                    ? choice with
                    {
                        ExcludedPaths = choice.ExcludedPaths
                            .Where(excluded => !string.Equals(
                                FolderExclusionRules.Normalize(excluded), normalized,
                                StringComparison.OrdinalIgnoreCase))
                            .ToArray()
                    }
                    : choice).ToList();
            return new ExplorerRegistration(Apply(new FolderManagerSelection(
                choices, BackgroundScanSettings.Load().Enabled),
                cancellationToken), false);
        }

        // No monitored ancestor exists: only now is this a new root.
        choices.Add(new FolderManagerRootChoice(normalized, [], false));
        return new ExplorerRegistration(Apply(new FolderManagerSelection(
            choices, BackgroundScanSettings.Load().Enabled),
            cancellationToken), true);
    }

    public bool Apply(FolderManagerSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);

        // UNC classification may consult a network provider: do it BEFORE
        // holding the catalog/cache commit lock.
        var desired = selection.Roots.Select(choice => (
                Choice: choice,
                Classified: PathClassifier.Classify(choice.LocalPath)))
            .ToArray();
        if (desired.Select(x => x.Classified.LocalPath).Distinct(
                StringComparer.OrdinalIgnoreCase).Count() != desired.Length)
            throw new ArgumentException("Ein Überwachungsordner wurde mehrfach ausgewählt.");

        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = new Dictionary<string, (long Id, string Canonical)>(
                StringComparer.OrdinalIgnoreCase);

            using (var all = Command(connection, transaction, """
                SELECT id, local_path, canonical_path FROM monitored_folders;
                """))
            using (var reader = all.ExecuteReader())
            {
                while (reader.Read())
                    existing.Add(reader.GetString(1), (reader.GetInt64(0), reader.GetString(2)));
            }

            var selectedPaths = desired.Select(x => x.Classified.LocalPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var modified = false;
            var reclaim = false;
            var timestamp = DateTime.UtcNow.ToString("O");

            foreach (var (path, old) in existing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selectedPaths.Contains(path)) continue;

                using (var stale = Command(connection, transaction, """
                    UPDATE scheduler_sources
                    SET generation = generation + 1, desired_present = 0,
                        updated_utc = $now
                    WHERE canonical_path = $path;
                    """))
                {
                    stale.Parameters.AddWithValue("$path", old.Canonical);
                    stale.Parameters.AddWithValue("$now", timestamp);
                    stale.ExecuteNonQuery();
                }
                using (var delete = Command(connection, transaction,
                    "DELETE FROM monitored_folders WHERE id = $id;"))
                {
                    delete.Parameters.AddWithValue("$id", old.Id);
                    delete.ExecuteNonQuery();
                }
                reclaim = modified = true;
            }

            foreach (var (choice, classified) in desired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id;
                if (existing.TryGetValue(classified.LocalPath, out var current))
                    id = current.Id;
                else
                {
                    using (var insert = Command(connection, transaction, """
                        INSERT INTO monitored_folders (
                            local_path, canonical_path, is_network,
                            server_sync_enabled, created_utc, initial_scan_pending)
                        VALUES ($local, $canonical, $network, $sync, $now, 1)
                        RETURNING id;
                        """))
                    {
                        insert.Parameters.AddWithValue("$local", classified.LocalPath);
                        insert.Parameters.AddWithValue("$canonical", classified.CanonicalPath);
                        insert.Parameters.AddWithValue("$network", classified.IsNetwork ? 1 : 0);
                        insert.Parameters.AddWithValue("$sync",
                            classified.IsNetwork && classified.CanonicalPath.StartsWith(@"\\",
                                StringComparison.Ordinal) && choice.ServerSyncEnabled ? 1 : 0);
                        insert.Parameters.AddWithValue("$now", timestamp);
                        id = Convert.ToInt64(insert.ExecuteScalar());
                    }

                    using (var source = Command(connection, transaction, """
                        INSERT INTO scheduler_sources (
                            canonical_path, generation, desired_present, updated_utc)
                        VALUES ($path, 1, 1, $now)
                        ON CONFLICT(canonical_path) DO UPDATE SET
                            generation = generation + 1,
                            desired_present = 1,
                            updated_utc = excluded.updated_utc;
                        """))
                    {
                        source.Parameters.AddWithValue("$path", classified.CanonicalPath);
                        source.Parameters.AddWithValue("$now", timestamp);
                        source.ExecuteNonQuery();
                    }
                    modified = true;
                }

                var wanted = choice.ExcludedPaths.Select(FolderExclusionRules.Normalize)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var excluded in wanted)
                    if (!FolderExclusionRules.IsAtOrBelow(excluded, classified.LocalPath))
                        throw new ArgumentException($"Ausschluss liegt außerhalb der Wurzel: {excluded}");

                // Folder manager and context-menu exclusions share this one
                // transactional transition: desired rules, source generation,
                // catalog rows and deferred cache-reclaim intent.
                var transition = ApplyExclusionSet(connection, transaction, id,
                    classified.LocalPath, classified.CanonicalPath, wanted,
                    cancellationToken);
                if (transition.Changed)
                {
                    modified = true;
                    reclaim |= transition.Removed > 0;
                }

                using (var sync = Command(connection, transaction, """
                    UPDATE monitored_folders SET server_sync_enabled = $enabled
                    WHERE id = $id AND server_sync_enabled != $enabled;
                    """))
                {
                    sync.Parameters.AddWithValue("$id", id);
                    sync.Parameters.AddWithValue("$enabled",
                        classified.IsNetwork &&
                        classified.CanonicalPath.StartsWith(@"\\", StringComparison.Ordinal) &&
                        choice.ServerSyncEnabled ? 1 : 0);
                    if (sync.ExecuteNonQuery() > 0) modified = true;
                }
            }

            if (reclaim)
            {
                using var maintenance = Command(connection, transaction, """
                    UPDATE cache_maintenance
                    SET cleanup_pending = 1, request_generation = request_generation + 1
                    WHERE id = 1;
                    """);
                maintenance.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return modified;
        }
    }


    public readonly record struct ExclusionTransition(bool Changed, int Removed);

    // Explicit single-subtree decision. This is a complete durable operation,
    // not a metadata update followed by a second cleanup request. The root
    // decision in the folder manager uses the SAME ApplyExclusionSet method.
    public ExclusionTransition ChangeExclusion(long folderId, string path,
        bool excluded, CancellationToken cancellationToken = default)
    {
        var normalized = FolderExclusionRules.Normalize(path);
        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            string rootPath;
            string canonicalPath;
            using (var root = Command(connection, transaction,
                "SELECT local_path, canonical_path FROM monitored_folders WHERE id=$id;"))
            {
                root.Parameters.AddWithValue("$id", folderId);
                using var reader = root.ExecuteReader();
                if (!reader.Read())
                    throw new OperationCanceledException(
                        "Der überwachte Ordner ist inzwischen entfernt.");
                rootPath = reader.GetString(0);
                canonicalPath = reader.GetString(1);
            }
            if (!FolderExclusionRules.IsAtOrBelow(normalized, rootPath) ||
                string.Equals(normalized,
                    FolderExclusionRules.Normalize(rootPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Nur Unterordner innerhalb einer überwachten Wurzel dürfen ausgeschlossen werden.");

            var wanted = ReadExcludedPaths(connection, transaction, folderId);
            if (excluded)
            {
                // Excluding an ancestor supersedes all nested exclusions.
                // A child of an already excluded ancestor adds no new state.
                if (!FolderExclusionRules.IsExcluded(normalized, wanted))
                {
                    wanted.RemoveWhere(current =>
                        FolderExclusionRules.IsAtOrBelow(current, normalized));
                    wanted.Add(normalized);
                }
            }
            else wanted.Remove(normalized);

            var transition = ApplyExclusionSet(connection, transaction,
                folderId, rootPath, canonicalPath, wanted, cancellationToken);
            if (transition.Removed > 0)
            {
                using var cleanup = Command(connection, transaction, """
                    UPDATE cache_maintenance
                    SET cleanup_pending=1, request_generation=request_generation+1
                    WHERE id=1;
                    """);
                cleanup.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return transition;
        }
    }


    // Startup repair for durable exclusions written by older, split-phase
    // context operations. The desired rules are already in SQLite; only
    // catalog rows that violate them can be removed. No network probe or
    // original-file mutation occurs here.
    public int ReconcilePersistedExclusions(
        CancellationToken cancellationToken = default)
    {
        lock (CatalogRemovalService.CacheLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var roots = new List<(long Id, string Local, string Canonical)>();
            using (var query = Command(connection, transaction, """
                SELECT id, local_path, canonical_path FROM monitored_folders;
                """))
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                    roots.Add((reader.GetInt64(0), reader.GetString(1),
                        reader.GetString(2)));
            }

            var removed = 0;
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var persisted = ReadExcludedPaths(connection, transaction, root.Id);
                if (persisted.Count == 0) continue;
                var outcome = ApplyExclusionSet(connection, transaction,
                    root.Id, root.Local, root.Canonical, persisted,
                    cancellationToken);
                removed += outcome.Removed;
            }
            if (removed > 0)
            {
                using var cleanup = Command(connection, transaction, """
                    UPDATE cache_maintenance
                    SET cleanup_pending=1, request_generation=request_generation+1
                    WHERE id=1;
                    """);
                cleanup.ExecuteNonQuery();
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return removed;
        }
    }

    private static HashSet<string> ReadExcludedPaths(
        SqliteConnection connection, SqliteTransaction transaction, long folderId)
    {
        using var read = Command(connection, transaction, """
            SELECT local_path FROM excluded_folders WHERE monitored_folder_id=$id;
            """);
        read.Parameters.AddWithValue("$id", folderId);
        using var reader = read.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    // One SQLite transaction is the complete domain commit. A crash leaves
    // EITHER the old rules and catalog, OR the new rules and pruned catalog.
    // The cache compactor only receives a durable maintenance request.
    private static ExclusionTransition ApplyExclusionSet(
        SqliteConnection connection, SqliteTransaction transaction,
        long folderId, string rootPath, string canonicalPath,
        IEnumerable<string> targetPaths, CancellationToken cancellationToken)
    {
        var wanted = targetPaths.Select(FolderExclusionRules.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in wanted)
            if (!FolderExclusionRules.IsAtOrBelow(path, rootPath))
                throw new ArgumentException(
                    $"Ausschluss liegt außerhalb der Überwachungswurzel: {path}");

        var previous = ReadExcludedPaths(connection, transaction, folderId);
        var rulesChanged = !previous.SetEquals(wanted);
        if (!rulesChanged)
        {
            // Repair catalogs left half-finished by pre-atomic versions.
            // This is catalog-only; never infer file deletion from NAS reachability.
            var staleCatalogRows = false;
            foreach (var path in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var check = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM images WHERE folder_id=$id AND
                        (local_path=$path COLLATE NOCASE OR
                         substr(local_path,1,length($prefix))=$prefix COLLATE NOCASE));
                    """);
                check.Parameters.AddWithValue("$id", folderId);
                check.Parameters.AddWithValue("$path", path);
                check.Parameters.AddWithValue("$prefix",
                    Path.TrimEndingDirectorySeparator(path) + "\\");
                if (Convert.ToInt32(check.ExecuteScalar()) == 1)
                {
                    staleCatalogRows = true;
                    break;
                }
            }
            if (!staleCatalogRows) return new(false, 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rulesChanged)
        {
            using (var remove = Command(connection, transaction, """
                DELETE FROM excluded_folders WHERE monitored_folder_id=$id;
                """))
            {
                remove.Parameters.AddWithValue("$id", folderId);
                remove.ExecuteNonQuery();
            }
            foreach (var path in wanted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var add = Command(connection, transaction, """
                    INSERT INTO excluded_folders(monitored_folder_id, local_path, created_utc)
                    VALUES($id,$path,$now);
                    """);
                add.Parameters.AddWithValue("$id", folderId);
                add.Parameters.AddWithValue("$path", path);
                add.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                add.ExecuteNonQuery();
            }
        }

        // This generation increment is in the SAME commit as the exclusion
        // and catalog deletion, closing the old scan's commit boundary.
        using (var advance = Command(connection, transaction, """
            UPDATE scheduler_sources
            SET generation=generation+1, updated_utc=$now
            WHERE canonical_path=$canonical AND desired_present=1;
            """))
        {
            advance.Parameters.AddWithValue("$canonical", canonicalPath);
            advance.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            if (advance.ExecuteNonQuery() != 1)
                throw new OperationCanceledException(
                    "Der Überwachungsordner wurde inzwischen entfernt.");
        }

        if (rulesChanged &&
            previous.Except(wanted, StringComparer.OrdinalIgnoreCase).Any())
        {
            // Persist the reimport obligation. It survives restart and is not
            // dependent on whether the background watcher is enabled.
            using var pending = Command(connection, transaction, """
                UPDATE monitored_folders SET initial_scan_pending=1 WHERE id=$id;
                """);
            pending.Parameters.AddWithValue("$id", folderId);
            pending.ExecuteNonQuery();
        }

        var removedImages = 0;
        foreach (var path in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var prune = Command(connection, transaction, """
                DELETE FROM images WHERE folder_id=$id AND
                    (local_path=$path COLLATE NOCASE OR
                     substr(local_path,1,length($prefix))=$prefix COLLATE NOCASE);
                """);
            prune.Parameters.AddWithValue("$id", folderId);
            prune.Parameters.AddWithValue("$path", path);
            prune.Parameters.AddWithValue("$prefix",
                Path.TrimEndingDirectorySeparator(path) + "\\");
            removedImages += prune.ExecuteNonQuery();
        }
        using (var empty = Command(connection, transaction, """
            DELETE FROM photo_folders
            WHERE monitored_folder_id=$id
              AND NOT EXISTS (SELECT 1 FROM images WHERE photo_folder_id=photo_folders.id);
            """))
        {
            empty.Parameters.AddWithValue("$id", folderId);
            empty.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(rulesChanged || removedImages > 0, removedImages);
    }

    private static SqliteCommand Command(SqliteConnection connection,
        SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
