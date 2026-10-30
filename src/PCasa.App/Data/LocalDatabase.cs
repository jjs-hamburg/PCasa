using System.IO;
using Microsoft.Data.Sqlite;

namespace PCasa.App.Data;

public sealed class LocalDatabase
{
    public string DatabasePath { get; }

    public LocalDatabase(string? databasePath = null)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa", "pcasa.db");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Private");
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public void Initialize()
    {
        using var connection = OpenConnection();

        using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL;";
            journalMode.ExecuteScalar();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS monitored_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    local_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    canonical_path TEXT NOT NULL,
                    is_network INTEGER NOT NULL CHECK (is_network IN (0, 1)),
                    server_sync_enabled INTEGER NOT NULL DEFAULT 0 CHECK (server_sync_enabled IN (0, 1)),
                    created_utc TEXT NOT NULL
                );

                -- A directory explicitly created by PCasa remains selectable
                -- even before its first image is indexed. The filesystem
                -- remains authoritative; this is a durable tree projection.
                CREATE TABLE IF NOT EXISTS created_photo_directories (
                    monitored_folder_id INTEGER NOT NULL,
                    local_path TEXT NOT NULL COLLATE NOCASE,
                    created_utc TEXT NOT NULL,
                    PRIMARY KEY (monitored_folder_id, local_path),
                    FOREIGN KEY (monitored_folder_id)
                        REFERENCES monitored_folders(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS photo_folders (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    monitored_folder_id INTEGER NOT NULL,
                    local_path TEXT NOT NULL,
                    canonical_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    display_name TEXT NOT NULL,
                    indexed_utc TEXT NOT NULL,
                    FOREIGN KEY (monitored_folder_id) REFERENCES monitored_folders(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS images (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    folder_id INTEGER NOT NULL,
                    photo_folder_id INTEGER NULL,
                    local_path TEXT NOT NULL,
                    canonical_path TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    file_name TEXT NOT NULL,
                    extension TEXT NOT NULL,
                    file_size INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    created_utc_ticks INTEGER NULL,
                    width INTEGER NOT NULL,
                    height INTEGER NOT NULL,
                    thumbnail_path TEXT NOT NULL,
                    indexed_utc TEXT NOT NULL,
                    FOREIGN KEY (folder_id) REFERENCES monitored_folders(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS excluded_folders (
                    monitored_folder_id INTEGER NOT NULL,
                    local_path TEXT NOT NULL COLLATE NOCASE,
                    created_utc TEXT NOT NULL,
                    PRIMARY KEY (monitored_folder_id, local_path),
                    FOREIGN KEY (monitored_folder_id)
                        REFERENCES monitored_folders(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS thumbnail_entries (
                    image_id INTEGER NOT NULL,
                    tier INTEGER NOT NULL,
                    pack_file TEXT NOT NULL,
                    byte_offset INTEGER NOT NULL,
                    byte_length INTEGER NOT NULL,
                    pixel_width INTEGER NOT NULL,
                    pixel_height INTEGER NOT NULL,
                    source_modified_utc_ticks INTEGER NOT NULL,
                    PRIMARY KEY (image_id, tier),
                    FOREIGN KEY (image_id) REFERENCES images(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS browse_cache_settings (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    tiers_csv TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );

                INSERT INTO browse_cache_settings (id, tiers_csv, updated_utc)
                VALUES (1, '96,192,384', strftime('%Y-%m-%dT%H:%M:%fZ','now'))
                ON CONFLICT(id) DO NOTHING;
                """;
            command.ExecuteNonQuery();
        }

        // Additive migration: preserve old catalogs and all packed thumbnails.
        // Existing images are enriched on their next ordinary scan. Never
        // pretend that indexed_utc is the source file's creation timestamp.
        EnsureColumn(connection, "images", "created_utc_ticks", "INTEGER NULL");

        // One metadata row per photo, deleted by the existing image FK.
        // The complete EXIF JSON is kept alongside indexed camera/GPS fields.
        using (var exif = connection.CreateCommand())
        {
            exif.CommandText = """
                CREATE TABLE IF NOT EXISTS image_exif (
                    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                    source_modified_utc_ticks INTEGER NOT NULL,
                    source_file_size INTEGER NOT NULL,
                    exif_json TEXT NOT NULL,
                    camera_make TEXT,
                    camera_model TEXT,
                    date_time_original TEXT,
                    gps_latitude REAL,
                    gps_longitude REAL,
                    gps_altitude REAL
                );
                CREATE INDEX IF NOT EXISTS idx_image_exif_gps
                    ON image_exif(gps_latitude, gps_longitude)
                    WHERE gps_latitude IS NOT NULL AND gps_longitude IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_image_exif_camera
                    ON image_exif(camera_make, camera_model);
                """;
            exif.ExecuteNonQuery();
        }

        // One durable metadata index shared by scanner, sidebar and tag search.
        // FTS rows are synchronized by SQLite triggers; no second in-memory tag list.
        using (var tags = connection.CreateCommand())
        {
            tags.CommandText = """
                CREATE TABLE IF NOT EXISTS image_tags (
                    image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                    keywords TEXT NOT NULL DEFAULT '',
                    source_modified_utc_ticks INTEGER NOT NULL
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS image_tags_fts USING fts5(
                    keywords, content='image_tags', content_rowid='image_id',
                    tokenize='unicode61 remove_diacritics 2'
                );
                CREATE TRIGGER IF NOT EXISTS image_tags_insert AFTER INSERT ON image_tags BEGIN
                    INSERT INTO image_tags_fts(rowid, keywords)
                    VALUES (new.image_id, new.keywords);
                END;
                CREATE TRIGGER IF NOT EXISTS image_tags_delete AFTER DELETE ON image_tags BEGIN
                    INSERT INTO image_tags_fts(image_tags_fts, rowid, keywords)
                    VALUES ('delete', old.image_id, old.keywords);
                END;
                CREATE TRIGGER IF NOT EXISTS image_tags_update AFTER UPDATE ON image_tags BEGIN
                    INSERT INTO image_tags_fts(image_tags_fts, rowid, keywords)
                    VALUES ('delete', old.image_id, old.keywords);
                    INSERT INTO image_tags_fts(rowid, keywords)
                    VALUES (new.image_id, new.keywords);
                END;
                """;
            tags.ExecuteNonQuery();
        }

        // A root can be removed from the catalog immediately. Orphaned
        // thumbnails and packed-cache bytes are reclaimed later at idle.
        // This durable marker makes interrupted cleanup resume after restart.
        using (var maintenance = connection.CreateCommand())
        {
            maintenance.CommandText = """
                CREATE TABLE IF NOT EXISTS cache_maintenance (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    cleanup_pending INTEGER NOT NULL DEFAULT 0
                        CHECK (cleanup_pending IN (0, 1)),
                    request_generation INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO cache_maintenance(id, cleanup_pending)
                VALUES (1, 0) ON CONFLICT(id) DO NOTHING;
                """;
            maintenance.ExecuteNonQuery();
        }
        // Additive migration for catalogs created before maintenance requests
        // carried a generation. No existing thumbnail/catalog rows are touched.
        EnsureColumn(connection, "cache_maintenance", "request_generation",
            "INTEGER NOT NULL DEFAULT 0");

        // Durable tag intentions. SQLite is the immediate UI projection;
        // this table is the restart-safe record of edits not yet in the file.
        using (var pendingTags = connection.CreateCommand())
        {
            pendingTags.CommandText = """
                CREATE TABLE IF NOT EXISTS pending_tag_edits (
                    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                    tag TEXT NOT NULL COLLATE NOCASE,
                    desired_present INTEGER NOT NULL CHECK(desired_present IN (0,1)),
                    revision INTEGER NOT NULL DEFAULT 1,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY (image_id, tag)
                );
                """;
            pendingTags.ExecuteNonQuery();
        }

        // The last used tags are shared by every sidebar opening in this local
        // catalogue, rather than being lost with the window's in-memory state.
        using (var recent = connection.CreateCommand())
        {
            recent.CommandText = """
                CREATE TABLE IF NOT EXISTS recent_tags (
                    tag TEXT PRIMARY KEY COLLATE NOCASE,
                    last_used_utc TEXT NOT NULL
                );
                """;
            recent.ExecuteNonQuery();
        }

        // Scheduler source identity survives removal of a monitored_folders row.
        // This additive migration does not change or delete existing catalog data.
        using (var sources = connection.CreateCommand())
        {
            sources.CommandText = """
                CREATE TABLE IF NOT EXISTS scheduler_sources (
                    canonical_path TEXT PRIMARY KEY COLLATE NOCASE,
                    generation INTEGER NOT NULL DEFAULT 1 CHECK (generation > 0),
                    scan_epoch INTEGER NOT NULL DEFAULT 0 CHECK (scan_epoch >= 0),
                    desired_present INTEGER NOT NULL CHECK (desired_present IN (0, 1)),
                    updated_utc TEXT NOT NULL
                );
                INSERT OR IGNORE INTO scheduler_sources
                    (canonical_path, generation, desired_present, updated_utc)
                SELECT canonical_path, 1, 1, strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM monitored_folders;
                """;
            sources.ExecuteNonQuery();
        }
        // Manual scan epochs are independent from source membership/exclusion
        // generations. Older installations need this additive column.
        EnsureColumn(connection, "scheduler_sources", "scan_epoch",
            "INTEGER NOT NULL DEFAULT 0");

        // Durable directory checkpoints belong only to an unfinished first import.
        // Existing images and monitored folders are never modified by this migration.
        using (var checkpoints = connection.CreateCommand())
        {
            checkpoints.CommandText = """
                CREATE TABLE IF NOT EXISTS initial_scan_directories (
                    monitored_folder_id INTEGER NOT NULL,
                    local_path TEXT NOT NULL COLLATE NOCASE,
                    source_generation INTEGER NOT NULL,
                    scan_epoch INTEGER NOT NULL,
                    completed_utc TEXT NOT NULL,
                    PRIMARY KEY (monitored_folder_id, local_path),
                    FOREIGN KEY (monitored_folder_id)
                        REFERENCES monitored_folders(id) ON DELETE CASCADE
                );
                """;
            checkpoints.ExecuteNonQuery();
        }

        // Last successfully committed file in the currently unfinished directory.
        // A stable filename ordering permits restart without re-decoding prior
        // images. This is a progress hint, never authorization for pruning.
        using (var fileCursor = connection.CreateCommand())
        {
            fileCursor.CommandText = """
                CREATE TABLE IF NOT EXISTS initial_scan_file_cursors (
                    monitored_folder_id INTEGER NOT NULL,
                    local_directory TEXT NOT NULL COLLATE NOCASE,
                    last_file_name TEXT NOT NULL,
                    source_generation INTEGER NOT NULL,
                    scan_epoch INTEGER NOT NULL,
                    updated_utc TEXT NOT NULL,
                    PRIMARY KEY(monitored_folder_id, local_directory),
                    FOREIGN KEY(monitored_folder_id)
                        REFERENCES monitored_folders(id) ON DELETE CASCADE
                );
                """;
            fileCursor.ExecuteNonQuery();
        }

        // Same restart-safe receipt format for manual scans and automatic
        // reconciliation; first imports keep their existing durable receipts.
        // A row describes ONE inventory, never a permanent "already scanned"
        // flag. Success removes the entire receipt; the next fresh scan
        // starts at the first file again.
        using (var regularScan = connection.CreateCommand())
        {
            regularScan.CommandText = """
                CREATE TABLE IF NOT EXISTS scan_resume_jobs (
                    scan_key TEXT PRIMARY KEY,
                    monitored_folder_id INTEGER NOT NULL,
                    scan_root TEXT NOT NULL COLLATE NOCASE,
                    source_generation INTEGER NOT NULL,
                    scan_epoch INTEGER NOT NULL,
                    checked_count INTEGER NOT NULL DEFAULT 0,
                    started_utc TEXT NOT NULL,
                    FOREIGN KEY(monitored_folder_id)
                        REFERENCES monitored_folders(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS scan_resume_directories (
                    scan_key TEXT NOT NULL,
                    local_directory TEXT NOT NULL COLLATE NOCASE,
                    last_file_name TEXT,
                    completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
                    PRIMARY KEY(scan_key,local_directory),
                    FOREIGN KEY(scan_key)
                        REFERENCES scan_resume_jobs(scan_key) ON DELETE CASCADE
                );
                """;
            regularScan.ExecuteNonQuery();
        }

        // Original write failures belong to one shared durable ledger, not
        // separate GPS and tag queues. Create the table during DB setup so
        // direct repository callers (tests/CLI) also support supersession.
        using (var writeFailures = connection.CreateCommand())
        {
            writeFailures.CommandText = """
                CREATE TABLE IF NOT EXISTS original_write_failures (
                    kind TEXT NOT NULL,
                    image_id INTEGER NOT NULL,
                    package_key TEXT NOT NULL,
                    file_path TEXT NOT NULL,
                    attempts INTEGER NOT NULL CHECK(attempts >= 0),
                    last_error TEXT NOT NULL,
                    last_attempt_utc TEXT NOT NULL,
                    resolved INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(kind,image_id)
                );
                CREATE INDEX IF NOT EXISTS idx_original_write_failures_open
                    ON original_write_failures(resolved,kind,attempts);
                """;
            writeFailures.ExecuteNonQuery();
        }

        // One-time upgrade for folders added by older builds that never
        // initiated their first scan: do not strand an empty monitored root.
        // Completed (including intentionally empty) scans are not repeatedly
        // requeued on every startup: this runs ONLY when the column is new.
        if (!ColumnExists(connection, "monitored_folders", "initial_scan_pending"))
        {
            EnsureColumn(connection, "monitored_folders", "initial_scan_pending",
                "INTEGER NOT NULL DEFAULT 0");
            using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                UPDATE monitored_folders SET initial_scan_pending = 1
                WHERE NOT EXISTS (
                    SELECT 1 FROM images i WHERE i.folder_id = monitored_folders.id);
                """;
            migrate.ExecuteNonQuery();
        }
        EnsureColumn(connection, "images", "photo_folder_id", "INTEGER NULL");
        RemoveLegacyRelativePath(connection, "photo_folders");
        RemoveLegacyRelativePath(connection, "images");

        using var indexes = connection.CreateCommand();
        indexes.CommandText =
            """
            CREATE INDEX IF NOT EXISTS idx_photo_folders_path
                ON photo_folders(canonical_path COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_images_photo_folder_name
                ON images(photo_folder_id, file_name COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_images_file_name
                ON images(file_name COLLATE NOCASE);

            -- Manual file transfers and reconciliation locate an image by
            -- its full local source path. Without this index, 500K images
            -- would require a full catalog scan per moved file.
            CREATE INDEX IF NOT EXISTS idx_images_local_path
                ON images(local_path COLLATE NOCASE);

            CREATE INDEX IF NOT EXISTS idx_thumbnail_entries_tier_image
                ON thumbnail_entries(tier, image_id);

            -- Compaction finds all entries belonging to one pack. Without this
            -- index it scans the entire thumbnail table once per pack file.
            CREATE INDEX IF NOT EXISTS idx_thumbnail_entries_pack_offset
                ON thumbnail_entries(pack_file, byte_offset);

            -- Folder reconciliation limits image candidates to one monitored root.
            CREATE INDEX IF NOT EXISTS idx_images_root_local
                ON images(folder_id, local_path COLLATE NOCASE);
            """;
        indexes.ExecuteNonQuery();
        // Cache jobs are persistent: interrupted writes remain pending after restart.
        // Existing image and pack data are never reset by this migration.
        using (var jobs = connection.CreateCommand())
        {
            jobs.CommandText = """
                CREATE TABLE IF NOT EXISTS browse_cache_jobs (
                    image_id INTEGER PRIMARY KEY,
                    state TEXT NOT NULL DEFAULT 'PENDING',
                    updated_utc TEXT NOT NULL,
                    FOREIGN KEY(image_id) REFERENCES images(id) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS browse_cache_tracking (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    tiers_csv TEXT NOT NULL
                );
                CREATE TRIGGER IF NOT EXISTS pcasa_cache_image_added
                AFTER INSERT ON images BEGIN
                    INSERT OR REPLACE INTO browse_cache_jobs(image_id, state, updated_utc)
                    VALUES (NEW.id, 'PENDING', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                END;
                CREATE TRIGGER IF NOT EXISTS pcasa_cache_image_changed
                AFTER UPDATE OF modified_utc_ticks ON images
                WHEN OLD.modified_utc_ticks <> NEW.modified_utc_ticks BEGIN
                    INSERT OR REPLACE INTO browse_cache_jobs(image_id, state, updated_utc)
                    VALUES (NEW.id, 'PENDING', strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                END;
                """;
            jobs.ExecuteNonQuery();
        }

        // The filesystem and SQLite cannot share an atomic transaction.
        // Keep a durable intent BEFORE an original is physically moved.
        // The catalog relocation clears it in the SAME SQLite commit.
        // Unresolved intents must remain inspectable, never silently pruned.
        using (var intents = connection.CreateCommand())
        {
            intents.CommandText = """
                CREATE TABLE IF NOT EXISTS file_transfer_intents (
                    operation_id TEXT PRIMARY KEY,
                    image_id INTEGER NOT NULL,
                    source_path TEXT NOT NULL,
                    destination_path TEXT NOT NULL,
                    target_root_id INTEGER NOT NULL,
                    created_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_transfer_intents_source
                    ON file_transfer_intents(source_path COLLATE NOCASE);
                """;
            intents.ExecuteNonQuery();
        }

        // One-time adoption of existing catalogs: only incomplete tier sets
        // are queued. A changed tier profile invalidates the old completion
        // assumption; it is not a reason to delete cached pack files.
        using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.CommandText =
                "SELECT tiers_csv FROM browse_cache_settings WHERE id = 1;";
            var csv = (string?)profileCommand.ExecuteScalar() ?? "96,192,384";
            using var previousCommand = connection.CreateCommand();
            previousCommand.CommandText =
                "SELECT tiers_csv FROM browse_cache_tracking WHERE id = 1;";
            var previous = (string?)previousCommand.ExecuteScalar();
            if (!string.Equals(previous, csv, StringComparison.Ordinal))
            {
                using var transaction = connection.BeginTransaction();
                using var enqueue = connection.CreateCommand();
                enqueue.Transaction = transaction;
                if (previous is null)
                {
                    var tiers = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse).ToArray();
                    var parameters = tiers.Select((_, i) => "$tier" + i).ToArray();
                    enqueue.CommandText = $"""
                        INSERT OR IGNORE INTO browse_cache_jobs(image_id, state, updated_utc)
                        SELECT i.id, 'PENDING', strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        FROM images i
                        WHERE (SELECT COUNT(*) FROM thumbnail_entries te
                               WHERE te.image_id = i.id
                                 AND te.source_modified_utc_ticks = i.modified_utc_ticks
                                 AND te.tier IN ({string.Join(",", parameters)})) < $required;
                        """;
                    for (var i = 0; i < tiers.Length; i++)
                        enqueue.Parameters.AddWithValue(parameters[i], tiers[i]);
                    enqueue.Parameters.AddWithValue("$required", tiers.Length);
                }
                else
                {
                    enqueue.CommandText = """
                        INSERT OR IGNORE INTO browse_cache_jobs(image_id, state, updated_utc)
                        SELECT id, 'PENDING', strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        FROM images;
                        """;
                }
                enqueue.ExecuteNonQuery();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    INSERT INTO browse_cache_tracking(id, tiers_csv) VALUES(1, $csv)
                    ON CONFLICT(id) DO UPDATE SET tiers_csv = excluded.tiers_csv;
                    """;
                update.Parameters.AddWithValue("$csv", csv);
                update.ExecuteNonQuery();
                transaction.Commit();
            }
        }
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (ColumnExists(connection, tableName, columnName))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private static void RemoveLegacyRelativePath(
        SqliteConnection connection,
        string tableName)
    {
        if (!ColumnExists(connection, tableName, "relative_path"))
        {
            return;
        }

        if (string.Equals(tableName, "images", StringComparison.OrdinalIgnoreCase))
        {
            using var dropIndex = connection.CreateCommand();
            dropIndex.CommandText = "DROP INDEX IF EXISTS idx_images_folder_relative;";
            dropIndex.ExecuteNonQuery();
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} DROP COLUMN relative_path;";
        alter.ExecuteNonQuery();
    }

    private static bool ColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(
                reader.GetString(1),
                columnName,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
