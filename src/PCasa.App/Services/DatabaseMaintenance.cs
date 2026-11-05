using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;

namespace PCasa.App.Services;

public sealed record DatabaseBackupProgress(
    string Phase, long ProcessedBytes, long TotalBytes,
    string? CurrentFile = null, int ProcessedFiles = 0, int TotalFiles = 0);

/// <summary>
/// Offline owner of PCasa's local catalog. Restores and resets are deliberately
/// executed by a separate PCasa process AFTER the UI/scheduler has exited.
/// Never touch user originals. The original catalog and caches are moved aside
/// before publication so a failure can restore them.
/// </summary>
public sealed class DatabaseMaintenance
{
    public sealed record FolderChoice(long Id, string OriginalPath,
        string OriginalCanonicalPath, string TargetPath, bool Include);

    public sealed record Request(string Operation, string? Archive,
        FolderChoice[] Folders, int ParentProcessId);

    public sealed record FolderInfo(long Id, string LocalPath, string CanonicalPath);
    public sealed record Summary(string Path, long Images, long Roots,
        long OpenWrites, long OpenTasks, long DatabaseBytes, long CacheBytes);

    private const string CatalogName = "pcasa.db";
    private const string ArchiveManifest = "manifest.json";
    private readonly string _directory;
    private readonly string _databasePath;

    public DatabaseMaintenance(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa");
        _databasePath = Path.Combine(_directory, CatalogName);
    }

    public Summary ReadSummary()
    {
        var db = new LocalDatabase(_databasePath);
        using var connection = db.OpenConnection();
        long Count(string table) => HasTable(connection, table)
            ? Scalar(connection, "SELECT COUNT(*) FROM " + table) : 0;
        var imageCount = Count("images");
        var roots = Count("monitored_folders");
        var pending = Count("pending_tag_edits");
        var tasks = HasTable(connection, "scheduler_packages")
            ? Scalar(connection,
                "SELECT COUNT(*) FROM scheduler_packages WHERE state!='completed'") : 0;
        var size = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
        var wal = _databasePath + "-wal";
        if (File.Exists(wal)) size += new FileInfo(wal).Length;
        return new Summary(_databasePath, imageCount, roots, pending, tasks,
            size, FolderBytes(Path.Combine(_directory, "thumbnails")) +
                  FolderBytes(Path.Combine(_directory, "browse-cache")));
    }

    public IReadOnlyList<FolderInfo> InspectBackup(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        VerifyArchive(zip);
        var temp = CreateWorkDir();
        try
        {
            var dbPath = Path.Combine(temp, CatalogName);
            zip.GetEntry(CatalogName)!.ExtractToFile(dbPath);
            using var db = OpenReadOnly(dbPath);
            VerifyIntegrity(db);
            using var command = db.CreateCommand();
            command.CommandText =
                "SELECT id, local_path, canonical_path FROM monitored_folders ORDER BY local_path COLLATE NOCASE";
            using var reader = command.ExecuteReader();
            var folders = new List<FolderInfo>();
            while (reader.Read())
                folders.Add(new FolderInfo(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2)));
            return folders;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteWorkDir(temp);
        }
    }

    public static string SaveRequest(Request request)
    {
        var file = Path.Combine(Path.GetTempPath(),
            "pcasa-maintenance-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, JsonSerializer.Serialize(request));
        return file;
    }

    public static string RunRequest(string requestPath, int parentProcessId)
    {
        if (parentProcessId <= 0 || !File.Exists(requestPath))
            throw new InvalidOperationException("Ungültiger Wartungsauftrag.");
        Request request;
        try
        {
            request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath))
                ?? throw new InvalidDataException("Wartungsauftrag ist leer.");
        }
        finally
        {
            // A failed request is never retried silently at the next startup.
            File.Delete(requestPath);
        }
        if (request.ParentProcessId != parentProcessId)
            throw new InvalidDataException("Wartungsauftrag gehört zu einem anderen Prozess.");
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            // No SQLite file replacement while even a stalled old process
            // could still hold a background worker or native file writer.
            if (!parent.WaitForExit(120000))
                throw new TimeoutException(
                    "PCasa konnte nicht vollständig beendet werden. Die Datenbank wurde nicht verändert.");
        }
        catch (ArgumentException) { /* Parent has already exited. */ }

        var maintenance = new DatabaseMaintenance();
        switch (request.Operation)
        {
            case "backup":
                if (string.IsNullOrWhiteSpace(request.Archive))
                    throw new InvalidDataException("Sicherungsziel fehlt.");
                maintenance.Backup(request.Archive);
                return "Die PCasa-Sicherung wurde erfolgreich erstellt.";
            case "restore":
                if (string.IsNullOrWhiteSpace(request.Archive))
                    throw new InvalidDataException("Sicherung fehlt.");
                maintenance.Restore(request.Archive, request.Folders);
                return "Die PCasa-Datenbank wurde wiederhergestellt. Neue Bestandsprüfungen können beginnen.";
            case "optimize":
                maintenance.Optimize();
                return "Die PCasa-Datenbank wurde optimiert.";
            case "reset":
                maintenance.Reset();
                return "Die lokale PCasa-Datenbank wurde vollständig zurückgesetzt.";
            default:
                throw new InvalidDataException("Unbekannte Datenbankoperation.");
        }
    }

    public void Backup(string destination,
        IProgress<DatabaseBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullDestination = Path.GetFullPath(destination);
        if (PathIsUnder(fullDestination, _directory))
            throw new InvalidOperationException(
                "Eine Sicherung darf nicht im aktiven PCasa-Datenverzeichnis liegen.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var work = CreateWorkDir();
        var temporaryArchive = fullDestination + ".part-" + Guid.NewGuid().ToString("N");
        long bytesProcessed = 0, bytesTotal = 0;
        int filesProcessed = 0, filesTotal = 0;
        long lastProgressTick = 0;
        void Report(string phase, string? current = null, bool force = false)
        {
            var tick = Environment.TickCount64;
            if (!force && tick - lastProgressTick < 170) return;
            lastProgressTick = tick;
            progress?.Report(new DatabaseBackupProgress(phase,
                Math.Min(bytesProcessed, bytesTotal), bytesTotal, current,
                filesProcessed, filesTotal));
        }
        void Transfer(Stream input, Stream output, string name, string phase)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    bytesProcessed += read;
                    Report(phase, name);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            filesProcessed++;
            Report(phase, name, force: true);
        }
        void Archive(ZipArchive zip, string source, string entryName,
            string phase)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
            using var input = new FileStream(source, FileMode.Open,
                FileAccess.Read, FileShare.Read, 1024 * 1024,
                FileOptions.SequentialScan);
            using var output = entry.Open();
            Transfer(input, output, entryName, phase);
        }

        try
        {
            Report("SQLite-Sicherung erstellen", force: true);
            var snapshot = Path.Combine(work, CatalogName);
            (string Source, string Snapshot, string Entry, long Bytes)[] cacheFiles;
            // Protect pack/index identity ONLY while creating the frozen
            // local snapshot. Time-consuming ZIP compression runs after
            // releasing the cache lock, so manual P0 writes are not blocked
            // for the full archive duration.
            lock (CatalogRemovalService.CacheLock)
            {
                using (var source = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly
                    }.ToString()))
                using (var target = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = snapshot, Mode = SqliteOpenMode.ReadWriteCreate
                    }.ToString()))
                {
                    source.Open();
                    target.Open();
                    source.BackupDatabase(target);
                }
                using (var verified = OpenReadOnly(snapshot)) VerifyIntegrity(verified);
                SqliteConnection.ClearAllPools();
                cancellationToken.ThrowIfCancellationRequested();

                cacheFiles = new[] { "thumbnails", "browse-cache" }
                    .Where(name => Directory.Exists(Path.Combine(_directory, name)))
                    .SelectMany(name => Directory.EnumerateFiles(
                        Path.Combine(_directory, name), "*",
                        SearchOption.AllDirectories).Select(file =>
                        (
                            Source: file,
                            Snapshot: Path.Combine(work, name,
                                Path.GetRelativePath(Path.Combine(_directory, name), file)),
                            Entry: name + "/" + Path.GetRelativePath(
                                Path.Combine(_directory, name), file).Replace('\\', '/'),
                            Bytes: new FileInfo(file).Length
                        )))
                    .ToArray();
                var cacheBytes = cacheFiles.Sum(file => file.Bytes);
                bytesTotal = new FileInfo(snapshot).Length + cacheBytes * 2;
                filesTotal = 1 + cacheFiles.Length * 2;
                // Fail BEFORE committing a backup when the staging volume
                // cannot accommodate an uncompressed cache snapshot.
                var localRoot = Path.GetPathRoot(work);
                if (localRoot is not null &&
                    new DriveInfo(localRoot).AvailableFreeSpace < cacheBytes + 64L * 1024 * 1024)
                    throw new IOException(
                        "Zu wenig lokaler Speicherplatz für die konsistente Cache-Sicherung.");

                Report("Vorschaudaten sichern", force: true);
                foreach (var file in cacheFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Snapshot)!);
                    using var input = new FileStream(file.Source, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 1024 * 1024,
                        FileOptions.SequentialScan);
                    using var output = new FileStream(file.Snapshot, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 1024 * 1024);
                    Transfer(input, output, file.Entry,
                        "Vorschaudaten sichern");
                }
            }

            // All ZIP input now resides in a frozen local snapshot. The
            // original PCasa cache is no longer locked while compressing.
            Report("Datenbank archivieren", force: true);
            using (var zip = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                Archive(zip, snapshot, CatalogName, "Datenbank archivieren");
                var info = zip.CreateEntry(ArchiveManifest, CompressionLevel.Fastest);
                using (var writer = new StreamWriter(info.Open()))
                    writer.Write(JsonSerializer.Serialize(new {
                        Format = "PCasa-local-backup", Version = 1,
                        CreatedUtc = DateTimeOffset.UtcNow
                    }));
                foreach (var file in cacheFiles)
                    Archive(zip, file.Snapshot, file.Entry,
                        "Vorschaudaten archivieren");
            }
            cancellationToken.ThrowIfCancellationRequested();
            Report("Sicherung abschließen", force: true);
            File.Move(temporaryArchive, fullDestination, overwrite: true);
            bytesProcessed = bytesTotal;
            Report("Sicherung abgeschlossen", force: true);
        }
        finally
        {
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
            SqliteConnection.ClearAllPools();
            DeleteWorkDir(work);
        }
    }

    public void Restore(string archivePath, IReadOnlyList<FolderChoice> choices)
    {
        var work = CreateWorkDir();
        try
        {
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                VerifyArchive(zip);
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name == ArchiveManifest || name.EndsWith("/", StringComparison.Ordinal))
                        continue;
                    if (name != CatalogName &&
                        !name.StartsWith("thumbnails/", StringComparison.Ordinal) &&
                        !name.StartsWith("browse-cache/", StringComparison.Ordinal))
                        throw new InvalidDataException("Fremde Datei in PCasa-Sicherung: " + name);
                    var target = Path.GetFullPath(Path.Combine(work,
                        name.Replace('/', Path.DirectorySeparatorChar)));
                    if (!PathIsUnder(target, work))
                        throw new InvalidDataException("Ungültiger Sicherungspfad.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                }
            }
            var restored = Path.Combine(work, CatalogName);
            using (var check = OpenReadOnly(restored)) VerifyIntegrity(check);
            var db = new LocalDatabase(restored);
            db.Initialize(); // additive migrations on the staged DB, not the live catalog
            PrepareRestoredDatabase(restored, choices);
            SqliteConnection.ClearAllPools();
            Publish(work, replaceCache: true);
        }
        finally { DeleteWorkDir(work); }
    }

    public void Optimize()
    {
        using var db = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = _databasePath, Mode = SqliteOpenMode.ReadWrite
            }.ToString());
        db.Open();
        VerifyIntegrity(db);
        using (var command = db.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA optimize;";
            command.ExecuteNonQuery();
        }
        VerifyIntegrity(db);
    }

    public void Reset()
    {
        var work = CreateWorkDir();
        try
        {
            var fresh = Path.Combine(work, CatalogName);
            new LocalDatabase(fresh).Initialize();
            Checkpoint(fresh);
            SqliteConnection.ClearAllPools();
            // No original photo paths are enumerated or deleted.
            Publish(work, replaceCache: true);
        }
        finally { DeleteWorkDir(work); }
    }

    private void PrepareRestoredDatabase(string path, IReadOnlyList<FolderChoice> choices)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite
            }.ToString());
        connection.Open();
        Sql(connection, "PRAGMA foreign_keys=ON;");
        var original = new List<FolderInfo>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id,local_path,canonical_path FROM monitored_folders;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                original.Add(new FolderInfo(reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2)));
        }
        var requested = choices.ToDictionary(c => c.Id);
        if (requested.Count != choices.Count || requested.Count != original.Count ||
            original.Any(root => !requested.TryGetValue(root.Id, out var c) ||
                !string.Equals(c.OriginalPath, root.LocalPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(c.OriginalCanonicalPath, root.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "Die Verzeichnisliste gehört nicht zu dieser Sicherung.");
        var included = original.Where(f => requested[f.Id].Include)
            .Select(f =>
            {
                var choice = requested[f.Id];
                var target = Path.GetFullPath(choice.TargetPath).TrimEnd('\\');
                if (target.Length == 2 && target[1] == ':') target += "\\";
                if (!Path.IsPathFullyQualified(target))
                    throw new InvalidDataException("Ungültiger Zielpfad: " + target);
                // Preserve canonical UNC identity for unchanged, possibly
                // offline network roots. Reclassify only remapped roots.
                var canonical = string.Equals(target, f.LocalPath,
                    StringComparison.OrdinalIgnoreCase)
                    ? f.CanonicalPath
                    : PathClassifier.Classify(target).CanonicalPath;
                var network = target.StartsWith(@"\\", StringComparison.Ordinal) ||
                    (Path.GetPathRoot(target) is string root &&
                     new DriveInfo(root).DriveType == DriveType.Network);
                return new Mapping(f.Id, f.LocalPath, f.CanonicalPath,
                    target, canonical, network);
            }).ToArray();
        if (included.Length == 0)
            throw new InvalidDataException(
                "Mindestens ein Verzeichnis muss übernommen werden.");
        for (var i = 0; i < included.Length; i++)
            for (var j = i + 1; j < included.Length; j++)
                if (PathIsUnder(included[i].TargetLocal, included[j].TargetLocal) ||
                    PathIsUnder(included[j].TargetLocal, included[i].TargetLocal) ||
                    PathIsUnder(included[i].TargetCanonical, included[j].TargetCanonical) ||
                    PathIsUnder(included[j].TargetCanonical, included[i].TargetCanonical))
                    throw new InvalidDataException(
                        "Zielverzeichnisse dürfen sich nicht überlappen: " +
                        included[i].TargetLocal + " / " + included[j].TargetLocal);

        using var tx = connection.BeginTransaction();
        // Mark ONLY images with unresolved original-file edits stale before
        // deleting their write intentions. The next normal scan re-reads
        // those originals; successfully indexed unchanged photos must NOT
        // be decoded again just because a database was restored.
        if (HasTable(connection, "pending_tag_edits", tx) &&
            HasTable(connection, "original_write_failures", tx))
            Sql(connection, """
                UPDATE images SET modified_utc_ticks=0
                WHERE id IN (
                    SELECT image_id FROM pending_tag_edits
                    UNION
                    SELECT image_id FROM original_write_failures
                );
                """, tx);
        // Purge all recovered work BEFORE any path remapping; never replay a
        // pre-restore mutation against a different physical photo.
        foreach (var table in new[] {
            "scheduler_task_order", "scheduler_packages", "file_transfer_intents",
            "original_write_failures", "pending_tag_edits",
            "scan_resume_directories", "scan_resume_jobs",
            "initial_scan_directories", "initial_scan_file_cursors",
            "scan_directory_first_pass", "scan_directory_verified",
            "scan_root_review", "browse_cache_jobs"
        })
            if (HasTable(connection, table, tx))
                Sql(connection, "DELETE FROM " + table + ";", tx);
        Sql(connection,
            "UPDATE cache_maintenance SET cleanup_pending=0 WHERE id=1;", tx);
        Sql(connection,
            "UPDATE scan_review_cycle SET current_cycle=1, started_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=1;", tx);

        // Local tag projections may have reflected P0 intentions that never
        // reached original files. Force a source refresh for those photos on
        // the next scan, before dropping the pending edit table: a restored
        // catalog must never advertise uncommitted metadata as verified.
        // This is currently handled by the full-root initial recheck below.
        foreach (var f in original.Where(f => !requested[f.Id].Include))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM monitored_folders WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", f.Id);
            cmd.ExecuteNonQuery();
        }

        // Two-phase mapping avoids violating UNIQUE path constraints when
        // users swap two drive letters or roots. Only paths inside a chosen
        // monitored root are rewritten; local cache paths are independent.
        var stagingPrefix = "PCASA_RESTORE_" + Guid.NewGuid().ToString("N") + ":";
        connection.CreateFunction<string, string>("pcasa_local", value =>
            stagingPrefix + Map(value, included, canonical: false));
        connection.CreateFunction<string, string>("pcasa_canonical", value =>
            stagingPrefix + Map(value, included, canonical: true));
        foreach (var (table, column, canonical) in new[] {
            ("monitored_folders","local_path",false),
            ("monitored_folders","canonical_path",true),
            ("photo_folders","local_path",false),
            ("photo_folders","canonical_path",true),
            ("images","local_path",false),
            ("images","canonical_path",true),
            ("excluded_folders","local_path",false),
            ("created_photo_directories","local_path",false)
        })
        {
            if (!HasTable(connection, table, tx)) continue;
            Sql(connection, $"UPDATE {table} SET {column}=pcasa_{(canonical ? "canonical" : "local")}({column}) WHERE {column} IS NOT NULL;", tx);
        }
        foreach (var (table, column) in new[] {
            ("monitored_folders","local_path"),
            ("monitored_folders","canonical_path"),
            ("photo_folders","local_path"),
            ("photo_folders","canonical_path"),
            ("images","local_path"),
            ("images","canonical_path"),
            ("excluded_folders","local_path"),
            ("created_photo_directories","local_path")
        })
        {
            if (!HasTable(connection, table, tx)) continue;
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                $"UPDATE {table} SET {column}=substr({column},$offset) WHERE {column} IS NOT NULL;";
            cmd.Parameters.AddWithValue("$offset", stagingPrefix.Length + 1);
            cmd.ExecuteNonQuery();
        }
        foreach (var map in included)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE monitored_folders SET is_network=$network, initial_scan_pending=1, initial_scan_paused=0, initial_scan_deleted=0, initial_scan_queue_order=id WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", map.Id);
            cmd.Parameters.AddWithValue("$network", map.IsNetwork ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        // The old source/generation/epoch is unrelated to the newly selected
        // machine and paths. Start one clean scan generation, reusing indexed
        // image metadata and thumbnails where still valid.
        Sql(connection, "DELETE FROM scheduler_sources;", tx);
        Sql(connection, """
            INSERT INTO scheduler_sources(canonical_path,generation,scan_epoch,desired_present,updated_utc)
            SELECT canonical_path,1,0,1,strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM monitored_folders;
            """, tx);
        // Thumbnails are part of the archive, but their absolute paths might
        // refer to another Windows user's profile. Rehome by file name.
        var localCache = Path.Combine(_directory, "thumbnails");
        connection.CreateFunction<string, string>("pcasa_thumb", value =>
            Path.Combine(localCache, Path.GetFileName(value)));
        Sql(connection, "UPDATE images SET thumbnail_path=pcasa_thumb(thumbnail_path);", tx);
        // The next fresh root scan checks every file, but unchanged images
        // retain their existing thumbnails and EXIF. Unfinished metadata
        // edits were selectively invalidated above instead.
        tx.Commit();
        VerifyIntegrity(connection);
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
    }

    private sealed record Mapping(long Id, string OldLocal, string OldCanonical,
        string TargetLocal, string TargetCanonical, bool IsNetwork);

    private static string Map(string path, IReadOnlyList<Mapping> maps, bool canonical)
    {
        var match = maps
            .Where(m => PathIsUnder(path, canonical ? m.OldCanonical : m.OldLocal))
            .OrderByDescending(m => (canonical ? m.OldCanonical : m.OldLocal).Length)
            .FirstOrDefault();
        if (match is null) return path;
        var prefix = canonical ? match.OldCanonical : match.OldLocal;
        var target = canonical ? match.TargetCanonical : match.TargetLocal;
        var tail = path.Substring(prefix.TrimEnd('\\').Length);
        return target.TrimEnd('\\') + tail;
    }

    private static void Checkpoint(string file)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = file, Mode = SqliteOpenMode.ReadWrite
            }.ToString());
        connection.Open();
        Sql(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        VerifyIntegrity(connection);
    }

    private void Publish(string preparedDir, bool replaceCache)
    {
        Directory.CreateDirectory(_directory);
        var rollback = CreateWorkDir();
        var names = replaceCache
            ? new[] { CatalogName, CatalogName + "-wal", CatalogName + "-shm",
                "thumbnails", "browse-cache" }
            : new[] { CatalogName, CatalogName + "-wal", CatalogName + "-shm" };
        var installed = new List<string>();
        var moved = new List<string>();
        var complete = false;
        try
        {
            foreach (var name in names)
            {
                var original = Path.Combine(_directory, name);
                if (!File.Exists(original) && !Directory.Exists(original)) continue;
                var safe = Path.Combine(rollback, name);
                Move(original, safe);
                moved.Add(name);
            }
            foreach (var name in names.Where(x => !x.EndsWith("-wal", StringComparison.Ordinal) &&
                                                   !x.EndsWith("-shm", StringComparison.Ordinal)))
            {
                var prepared = Path.Combine(preparedDir, name);
                if (!File.Exists(prepared) && !Directory.Exists(prepared)) continue;
                Move(prepared, Path.Combine(_directory, name));
                installed.Add(name);
            }
            complete = true;
        }
        catch
        {
            foreach (var name in installed.AsEnumerable().Reverse())
            {
                var target = Path.Combine(_directory, name);
                if (File.Exists(target)) File.Delete(target);
                if (Directory.Exists(target)) Directory.Delete(target, true);
            }
            foreach (var name in moved.AsEnumerable().Reverse())
                Move(Path.Combine(rollback, name), Path.Combine(_directory, name));
            throw;
        }
        finally
        {
            // On a failed rollback retain the moved-aside original directory
            // instead of ever deleting the last surviving copy.
            if (complete || moved.All(name =>
                !File.Exists(Path.Combine(rollback, name)) &&
                !Directory.Exists(Path.Combine(rollback, name))))
                DeleteWorkDir(rollback);
        }
    }

    private static void Move(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to);
        else if (Directory.Exists(from)) Directory.Move(from, to);
    }

    private void AddFolder(ZipArchive archive, string name)
    {
        var path = Path.Combine(_directory, name);
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*",
                     SearchOption.AllDirectories))
        {
            var entry = name + "/" + Path.GetRelativePath(path, file)
                .Replace('\\', '/');
            archive.CreateEntryFromFile(file, entry, CompressionLevel.Fastest);
        }
    }

    private static void VerifyArchive(ZipArchive zip)
    {
        var manifest = zip.GetEntry(ArchiveManifest)
            ?? throw new InvalidDataException("Dies ist keine PCasa-Datenbanksicherung.");
        using var reader = new StreamReader(manifest.Open());
        using var json = JsonDocument.Parse(reader.ReadToEnd());
        if (!json.RootElement.TryGetProperty("Format", out var format) ||
            format.GetString() != "PCasa-local-backup" ||
            !json.RootElement.TryGetProperty("Version", out var version) ||
            version.GetInt32() != 1 ||
            zip.GetEntry(CatalogName) is null)
            throw new InvalidDataException("Nicht unterstütztes PCasa-Sicherungsformat.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!names.Add(name))
                throw new InvalidDataException("Doppelte Datei in der Sicherung.");
            if (name.StartsWith("/", StringComparison.Ordinal) ||
                name.Contains("../", StringComparison.Ordinal) ||
                name.Contains(':', StringComparison.Ordinal) ||
                (name != CatalogName && name != ArchiveManifest &&
                 !name.StartsWith("thumbnails/", StringComparison.Ordinal) &&
                 !name.StartsWith("browse-cache/", StringComparison.Ordinal)))
                throw new InvalidDataException("Ungültiger Sicherungseintrag: " + name);
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly
            }.ToString());
        connection.Open();
        return connection;
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var result = cmd.ExecuteScalar()?.ToString();
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite-Integritätsprüfung fehlgeschlagen: " + result);
    }

    private static bool HasTable(SqliteConnection db, string name,
        SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name)";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    private static long Scalar(SqliteConnection db, string query)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = query;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Sql(SqliteConnection connection, string query,
        SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = query;
        cmd.ExecuteNonQuery();
    }

    private static long FolderBytes(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*",
                     SearchOption.AllDirectories))
            try { bytes += new FileInfo(file).Length; }
            catch (IOException) { }
        return bytes;
    }

    private static bool PathIsUnder(string path, string root)
    {
        var normalizedRoot = root.TrimEnd('\\', '/');
        var normalizedPath = path.TrimEnd('\\', '/');
        return string.Equals(normalizedPath, normalizedRoot,
            StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + "\\",
                StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedRoot + "/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateWorkDir()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa-maintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteWorkDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { /* Restart can reclaim abandoned temp dirs. */ }
        catch (UnauthorizedAccessException) { }
    }
}
