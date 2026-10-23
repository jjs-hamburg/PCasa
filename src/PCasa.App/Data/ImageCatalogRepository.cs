using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Models;
using PCasa.App.Services;

namespace PCasa.App.Data;

public sealed class ImageCatalogRepository
{
    private readonly LocalDatabase _database;
    public LocalDatabase Database => _database;

    public ImageCatalogRepository(LocalDatabase database)
    {
        _database = database;
    }

    public CatalogScanSession CreateScanSession(string? sourcePath = null,
        long? folderId = null, long? generation = null,
        CancellationToken cancellationToken = default)
        => new(_database.OpenConnection(), sourcePath, folderId, generation, cancellationToken);

    // Relocating an unchanged image is NOT importing it a second time.
    // The same catalog scan session updates folder identity atomically,
    // retaining the image ID, EXIF, tags and every existing thumbnail tier.
    public bool RelocateImage(long imageId, string sourcePath,
        string destinationPath, MonitoredFolder targetRoot,
        CancellationToken cancellationToken = default,
        string? relocationIntentId = null)
    {
        using var session = CreateScanSession(cancellationToken: cancellationToken);
        return session.RelocateImage(imageId, sourcePath, destinationPath,
            targetRoot, relocationIntentId);
    }

    public void BeginRelocationIntent(string operationId, long imageId,
        string sourcePath, string destinationPath, long targetRootId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO file_transfer_intents
                (operation_id, image_id, source_path, destination_path,
                 target_root_id, created_utc)
            VALUES ($id, $image, $source, $destination, $root, $now);
            """;
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$destination", destinationPath);
        command.Parameters.AddWithValue("$root", targetRootId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    // Reconciliation is conservative: an unreachable NAS or both names
    // existing is NEVER treated as permission to delete an original.
    // Only a confirmed, completed move is adopted into the same catalog.
    public (int Recovered, int Unresolved) RecoverRelocations(
        IReadOnlyList<MonitoredFolder> monitored,
        CancellationToken cancellationToken = default)
    {
        var intents = new List<(string Id, long ImageId, string Source,
            string Destination, long RootId)>();
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT operation_id, image_id, source_path, destination_path,
                       target_root_id FROM file_transfer_intents
                ORDER BY created_utc;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                intents.Add((reader.GetString(0), reader.GetInt64(1),
                    reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }

        var recovered = 0;
        var unresolved = 0;
        foreach (var intent in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = monitored.FirstOrDefault(item =>
                item.Id == intent.RootId &&
                FolderExclusionRules.IsAtOrBelow(
                    intent.Destination, item.LocalPath));
            if (root is null)
            {
                unresolved++;
                continue;
            }
            try
            {
                lock (SourceFileLocks.ForPath(intent.Source))
                {
                    var sourceParent = Path.GetDirectoryName(intent.Source);
                    var destParent = Path.GetDirectoryName(intent.Destination);
                    if (sourceParent is null || destParent is null ||
                        !Directory.Exists(sourceParent) ||
                        !Directory.Exists(destParent))
                    {
                        unresolved++; // offline != removed
                        continue;
                    }

                    var sourceExists = File.Exists(intent.Source);
                    var destinationExists = File.Exists(intent.Destination);
                    if (!sourceExists && destinationExists)
                    {
                        if (RelocateImage(intent.ImageId, intent.Source,
                            intent.Destination, root, cancellationToken,
                            intent.Id) ||
                            ConfirmRelocationAlreadyApplied(intent.Id,
                                intent.ImageId, intent.Destination))
                            recovered++;
                        else unresolved++;
                    }
                    else if (sourceExists && !destinationExists)
                    {
                        // The operation never reached its physical move.
                        ClearRelocationIntent(intent.Id);
                    }
                    else unresolved++;
                }
            }
            catch (Exception ex) when (ex is IOException or
                UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                unresolved++;
            }
        }
        return (recovered, unresolved);
    }

    private bool ConfirmRelocationAlreadyApplied(string operationId,
        long imageId, string destinationPath)
    {
        using var connection = _database.OpenConnection();
        using var query = connection.CreateCommand();
        query.CommandText =
            "SELECT 1 FROM images WHERE id=$id AND local_path=$path COLLATE NOCASE";
        query.Parameters.AddWithValue("$id", imageId);
        query.Parameters.AddWithValue("$path", destinationPath);
        if (query.ExecuteScalar() is null) return false;
        ClearRelocationIntent(operationId);
        return true;
    }

    private void ClearRelocationIntent(string operationId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM file_transfer_intents WHERE operation_id=$id;";
        command.Parameters.AddWithValue("$id", operationId);
        command.ExecuteNonQuery();
    }

    public int CountAll()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM images;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Validate an existing selection against the catalog, not the thumbnail
    /// cache: a missing thumbnail tier must never deselect a valid source photo.
    /// A bounded IN query avoids one database round trip per selected image.
    /// </summary>
    public HashSet<long> GetExistingImageIds(IReadOnlyCollection<long> imageIds)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        var found = new HashSet<long>();
        if (imageIds.Count == 0) return found;

        using var connection = _database.OpenConnection();
        foreach (var batch in imageIds.Distinct().Chunk(400))
        {
            using var command = connection.CreateCommand();
            var parameters = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var name = "$id" + i;
                parameters.Add(name);
                command.Parameters.AddWithValue(name, batch[i]);
            }
            command.CommandText = "SELECT id FROM images WHERE id IN (" +
                string.Join(", ", parameters) + ");";
            using var reader = command.ExecuteReader();
            while (reader.Read()) found.Add(reader.GetInt64(0));
        }
        return found;
    }

    public IReadOnlyList<PhotoFolderSummary> GetPhotoFolders()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                pf.id,
                pf.monitored_folder_id,
                pf.local_path,
                pf.canonical_path,
                pf.display_name,
                COUNT(i.id) AS image_count
            FROM photo_folders pf
            LEFT JOIN images i ON i.photo_folder_id = pf.id
            GROUP BY
                pf.id,
                pf.monitored_folder_id,
                pf.local_path,
                pf.canonical_path,
                pf.display_name
            HAVING image_count > 0
            ORDER BY pf.canonical_path COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        var result = new List<PhotoFolderSummary>();

        while (reader.Read())
        {
            result.Add(new PhotoFolderSummary(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)));
        }

        return result;
    }

    public IReadOnlyList<GalleryImage> GetImagesPage(
        long photoFolderId,
        int offset,
        int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                photo_folder_id,
                local_path,
                canonical_path,
                file_name,
                width,
                height,
                thumbnail_path
            FROM images
            WHERE photo_folder_id = $photoFolderId
            ORDER BY file_name COLLATE NOCASE, canonical_path COLLATE NOCASE
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$photoFolderId", photoFolderId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        using var reader = command.ExecuteReader();
        var result = new List<GalleryImage>();

        while (reader.Read())
        {
            result.Add(new GalleryImage(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7)));
        }

        return result;
    }
}

public sealed class CatalogScanSession : IDisposable
{
    private const int BatchSize = 250;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _selectState;
    private readonly SqliteCommand _selectPhotoFolder;
    private readonly SqliteCommand _upsertPhotoFolder;
    private readonly SqliteCommand _updateLocation;
    private readonly SqliteCommand _upsert;
    private SqliteTransaction? _transaction;
    private int _pendingWrites;
    private bool _disposed;
    private readonly string? _sourcePath;
    private readonly long? _folderId;
    private readonly long? _generation;
    private readonly CancellationToken _cancellationToken;

    public CatalogScanSession(SqliteConnection connection,
        string? sourcePath = null, long? folderId = null, long? generation = null,
        CancellationToken cancellationToken = default)
    {
        if ((sourcePath is null) != (folderId is null) ||
            (sourcePath is null) != (generation is null))
            throw new ArgumentException("Source fence requires source, folder ID and generation.");
        _connection = connection;
        _sourcePath = sourcePath;
        _folderId = folderId;
        _generation = generation;
        _cancellationToken = cancellationToken;

        _selectState = connection.CreateCommand();
        _selectState.CommandText =
            """
            SELECT i.file_size, i.modified_utc_ticks, i.thumbnail_path,
                   i.photo_folder_id, i.local_path,
                   EXISTS(SELECT 1 FROM image_exif x WHERE x.image_id = i.id
                       AND x.source_modified_utc_ticks = i.modified_utc_ticks
                       AND x.source_file_size = i.file_size)
            FROM images i WHERE i.canonical_path = $canonicalPath;
            """;
        _selectState.Parameters.Add("$canonicalPath", SqliteType.Text);

        _selectPhotoFolder = connection.CreateCommand();
        _selectPhotoFolder.CommandText = """
            SELECT id, monitored_folder_id, local_path, display_name
            FROM photo_folders WHERE canonical_path = $canonicalPath;
            """;
        _selectPhotoFolder.Parameters.Add("$canonicalPath", SqliteType.Text);

        _upsertPhotoFolder = connection.CreateCommand();
        _upsertPhotoFolder.CommandText =
            """
            INSERT INTO photo_folders (
                monitored_folder_id,
                local_path,
                canonical_path,
                display_name,
                indexed_utc
            )
            VALUES (
                $monitoredFolderId,
                $localPath,
                $canonicalPath,
                $displayName,
                $indexedUtc
            )
            ON CONFLICT(canonical_path) DO UPDATE SET
                monitored_folder_id = excluded.monitored_folder_id,
                local_path = excluded.local_path,
                display_name = excluded.display_name,
                indexed_utc = excluded.indexed_utc
            RETURNING id;
            """;
        _upsertPhotoFolder.Parameters.Add("$monitoredFolderId", SqliteType.Integer);
        _upsertPhotoFolder.Parameters.Add("$localPath", SqliteType.Text);
        _upsertPhotoFolder.Parameters.Add("$canonicalPath", SqliteType.Text);
        _upsertPhotoFolder.Parameters.Add("$displayName", SqliteType.Text);
        _upsertPhotoFolder.Parameters.Add("$indexedUtc", SqliteType.Text);

        _updateLocation = connection.CreateCommand();
        _updateLocation.CommandText =
            """
            UPDATE images
            SET
                folder_id = $folderId,
                photo_folder_id = $photoFolderId,
                local_path = $localPath
            WHERE canonical_path = $canonicalPath;
            """;
        _updateLocation.Parameters.Add("$folderId", SqliteType.Integer);
        _updateLocation.Parameters.Add("$photoFolderId", SqliteType.Integer);
        _updateLocation.Parameters.Add("$localPath", SqliteType.Text);
        _updateLocation.Parameters.Add("$canonicalPath", SqliteType.Text);

        _upsert = connection.CreateCommand();
        _upsert.CommandText =
            """
            INSERT INTO images (
                folder_id,
                photo_folder_id,
                local_path,
                canonical_path,
                file_name,
                extension,
                file_size,
                modified_utc_ticks,
                width,
                height,
                thumbnail_path,
                indexed_utc
            )
            VALUES (
                $folderId,
                $photoFolderId,
                $localPath,
                $canonicalPath,
                $fileName,
                $extension,
                $fileSize,
                $modifiedUtcTicks,
                $width,
                $height,
                $thumbnailPath,
                $indexedUtc
            )
            ON CONFLICT(canonical_path) DO UPDATE SET
                folder_id = excluded.folder_id,
                photo_folder_id = excluded.photo_folder_id,
                local_path = excluded.local_path,
                file_name = excluded.file_name,
                extension = excluded.extension,
                file_size = excluded.file_size,
                modified_utc_ticks = excluded.modified_utc_ticks,
                width = excluded.width,
                height = excluded.height,
                thumbnail_path = excluded.thumbnail_path,
                indexed_utc = excluded.indexed_utc;
            """;

        _upsert.Parameters.Add("$folderId", SqliteType.Integer);
        _upsert.Parameters.Add("$photoFolderId", SqliteType.Integer);
        _upsert.Parameters.Add("$localPath", SqliteType.Text);
        _upsert.Parameters.Add("$canonicalPath", SqliteType.Text);
        _upsert.Parameters.Add("$fileName", SqliteType.Text);
        _upsert.Parameters.Add("$extension", SqliteType.Text);
        _upsert.Parameters.Add("$fileSize", SqliteType.Integer);
        _upsert.Parameters.Add("$modifiedUtcTicks", SqliteType.Integer);
        _upsert.Parameters.Add("$width", SqliteType.Integer);
        _upsert.Parameters.Add("$height", SqliteType.Integer);
        _upsert.Parameters.Add("$thumbnailPath", SqliteType.Text);
        _upsert.Parameters.Add("$indexedUtc", SqliteType.Text);
    }

    public CatalogImageState? GetState(string canonicalPath)
    {
        ThrowIfDisposed();

        _selectState.Parameters["$canonicalPath"].Value = canonicalPath;
        using var reader = _selectState.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CatalogImageState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.GetString(4), reader.GetInt32(5) != 0);
    }

    public long GetOrCreatePhotoFolder(
        long monitoredFolderId,
        string localPath,
        string canonicalPath,
        string displayName)
    {
        ThrowIfDisposed();
        _selectPhotoFolder.Parameters["$canonicalPath"].Value = canonicalPath;
        using (var existing = _selectPhotoFolder.ExecuteReader())
        {
            if (existing.Read() &&
                existing.GetInt64(1) == monitoredFolderId &&
                string.Equals(existing.GetString(2), localPath, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.GetString(3), displayName, StringComparison.Ordinal))
                return existing.GetInt64(0);
        }

        // An unchanged folder is a read-only lookup. Do not update indexed_utc
        // or create a SQLite write transaction for every directory on a rescan.
        EnsureTransaction();
        _upsertPhotoFolder.Parameters["$monitoredFolderId"].Value = monitoredFolderId;
        _upsertPhotoFolder.Parameters["$localPath"].Value = localPath;
        _upsertPhotoFolder.Parameters["$canonicalPath"].Value = canonicalPath;
        _upsertPhotoFolder.Parameters["$displayName"].Value = displayName;
        _upsertPhotoFolder.Parameters["$indexedUtc"].Value = DateTime.UtcNow.ToString("O");

        var id = Convert.ToInt64(_upsertPhotoFolder.ExecuteScalar());
        RegisterWrite();
        return id;
    }

    // A physical rename/move does not change picture bytes. Reassign only
    // the path/folder identity and leave thumbnail_entries/image_exif/
    // image_tags attached to the stable image ID. Runs inside ONE SQLite
    // transaction, including a replaced target's stale catalog row.
    public bool RelocateImage(long imageId, string sourcePath,
        string destinationPath, MonitoredFolder targetRoot,
        string? relocationIntentId = null)
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("Destination has no parent directory.",
                nameof(destinationPath));
        var canonicalDirectory =
            FolderScanner.ResolveCanonicalFullPath(targetRoot, directory);
        var canonicalDestination =
            FolderScanner.ResolveCanonicalFullPath(targetRoot, destinationPath);
        // Check the source before creating any target directory catalog row.
        long? oldPhotoFolderId;
        using (var source = _connection.CreateCommand())
        {
            source.CommandText = """
                SELECT photo_folder_id FROM images
                WHERE id = $imageId AND local_path = $source COLLATE NOCASE;
                """;
            source.Parameters.AddWithValue("$imageId", imageId);
            source.Parameters.AddWithValue("$source", sourcePath);
            var sourceFolder = source.ExecuteScalar();
            if (sourceFolder is null) return false;
            oldPhotoFolderId = sourceFolder is DBNull
                ? null : Convert.ToInt64(sourceFolder);
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (string.IsNullOrEmpty(name)) name = directory;
        var targetFolderId = GetOrCreatePhotoFolder(
            targetRoot.Id, directory, canonicalDirectory, name);
        EnsureTransaction();

        // Physical replacement and old catalog identity must never leave
        // two image rows claiming the same destination. Foreign-key cleanup
        // removes only the replaced image's metadata/cache references.
        var replaced = 0;
        using (var conflict = _connection.CreateCommand())
        {
            conflict.Transaction = _transaction;
            conflict.CommandText = """
                DELETE FROM images WHERE id <> $imageId
                  AND (canonical_path = $canonical COLLATE NOCASE
                    OR local_path = $destination COLLATE NOCASE);
                """;
            conflict.Parameters.AddWithValue("$imageId", imageId);
            conflict.Parameters.AddWithValue("$canonical", canonicalDestination);
            conflict.Parameters.AddWithValue("$destination", destinationPath);
            replaced = conflict.ExecuteNonQuery();
        }

        using (var relocate = _connection.CreateCommand())
        {
            relocate.Transaction = _transaction;
            relocate.CommandText = """
                UPDATE images
                SET folder_id = $rootId,
                    photo_folder_id = $photoFolderId,
                    canonical_path = $canonical,
                    local_path = $destination,
                    file_name = $name,
                    indexed_utc = $now
                WHERE id = $imageId AND local_path = $source COLLATE NOCASE;
                """;
            relocate.Parameters.AddWithValue("$rootId", targetRoot.Id);
            relocate.Parameters.AddWithValue("$photoFolderId", targetFolderId);
            relocate.Parameters.AddWithValue("$canonical", canonicalDestination);
            relocate.Parameters.AddWithValue("$destination", destinationPath);
            relocate.Parameters.AddWithValue("$name", Path.GetFileName(destinationPath));
            relocate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            relocate.Parameters.AddWithValue("$imageId", imageId);
            relocate.Parameters.AddWithValue("$source", sourcePath);
            if (relocate.ExecuteNonQuery() != 1)
            {
                RollbackPending();
                return false;
            }
        }

        using (var empty = _connection.CreateCommand())
        {
            empty.Transaction = _transaction;
            empty.CommandText = """
                DELETE FROM photo_folders
                WHERE id = $oldFolder AND id <> $keep
                  AND NOT EXISTS (SELECT 1 FROM images
                                  WHERE photo_folder_id = photo_folders.id);
                """;
            empty.Parameters.AddWithValue("$oldFolder",
                (object?)oldPhotoFolderId ?? DBNull.Value);
            empty.Parameters.AddWithValue("$keep", targetFolderId);
            empty.ExecuteNonQuery();
        }

        if (replaced > 0)
        {
            // Replaced target thumbnail packs are collected by the existing
            // idle maintenance, never while the user is moving files.
            using var maintenance = _connection.CreateCommand();
            maintenance.Transaction = _transaction;
            maintenance.CommandText = """
                UPDATE cache_maintenance
                SET cleanup_pending = 1,
                    request_generation = request_generation + 1
                WHERE id = 1;
                """;
            maintenance.ExecuteNonQuery();
        }
        if (relocationIntentId is not null)
        {
            using var completedIntent = _connection.CreateCommand();
            completedIntent.Transaction = _transaction;
            completedIntent.CommandText =
                "DELETE FROM file_transfer_intents WHERE operation_id=$id AND image_id=$imageId;";
            completedIntent.Parameters.AddWithValue("$id", relocationIntentId);
            completedIntent.Parameters.AddWithValue("$imageId", imageId);
            if (completedIntent.ExecuteNonQuery() != 1)
            {
                RollbackPending();
                throw new InvalidOperationException(
                    "Relocation intent is missing or has changed.");
            }
        }
        FlushPending();
        return true;
    }

    public void UpdateLocation(
        string canonicalPath,
        long folderId,
        long photoFolderId,
        string localPath)
    {
        ThrowIfDisposed();
        EnsureTransaction();

        _updateLocation.Parameters["$folderId"].Value = folderId;
        _updateLocation.Parameters["$photoFolderId"].Value = photoFolderId;
        _updateLocation.Parameters["$localPath"].Value = localPath;
        _updateLocation.Parameters["$canonicalPath"].Value = canonicalPath;
        _updateLocation.ExecuteNonQuery();
        RegisterWrite();
    }

    public void Upsert(CatalogImageData image)
    {
        ThrowIfDisposed();
        EnsureTransaction();

        _upsert.Parameters["$folderId"].Value = image.FolderId;
        _upsert.Parameters["$photoFolderId"].Value = image.PhotoFolderId;
        _upsert.Parameters["$localPath"].Value = image.LocalPath;
        _upsert.Parameters["$canonicalPath"].Value = image.CanonicalPath;
        _upsert.Parameters["$fileName"].Value = image.FileName;
        _upsert.Parameters["$extension"].Value = image.Extension;
        _upsert.Parameters["$fileSize"].Value = image.FileSize;
        _upsert.Parameters["$modifiedUtcTicks"].Value = image.ModifiedUtcTicks;
        _upsert.Parameters["$width"].Value = image.Width;
        _upsert.Parameters["$height"].Value = image.Height;
        _upsert.Parameters["$thumbnailPath"].Value = image.ThumbnailPath;
        _upsert.Parameters["$indexedUtc"].Value = DateTime.UtcNow.ToString("O");
        _upsert.ExecuteNonQuery();
        RegisterWrite();
    }

    public void SaveExif(string canonicalPath, long ticks, long size, ImageExifData exif)
    {
        ThrowIfDisposed();
        EnsureTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = """
            INSERT INTO image_exif(image_id, source_modified_utc_ticks, source_file_size,
                exif_json, camera_make, camera_model, date_time_original,
                gps_latitude, gps_longitude, gps_altitude)
            SELECT id, $ticks, $size, $json, $make, $model, $date,
                $lat, $lon, $alt FROM images
            WHERE canonical_path = $path AND modified_utc_ticks = $ticks
                  AND file_size = $size
            ON CONFLICT(image_id) DO UPDATE SET
                source_modified_utc_ticks = excluded.source_modified_utc_ticks,
                source_file_size = excluded.source_file_size,
                exif_json = excluded.exif_json,
                camera_make = excluded.camera_make,
                camera_model = excluded.camera_model,
                date_time_original = excluded.date_time_original,
                gps_latitude = excluded.gps_latitude,
                gps_longitude = excluded.gps_longitude,
                gps_altitude = excluded.gps_altitude;
            """;
        cmd.Parameters.AddWithValue("$path", canonicalPath);
        cmd.Parameters.AddWithValue("$ticks", ticks);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$json", exif.Json);
        cmd.Parameters.AddWithValue("$make", (object?)exif.CameraMake ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)exif.CameraModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$date", (object?)exif.DateTimeOriginal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lat", (object?)exif.GpsLatitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lon", (object?)exif.GpsLongitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$alt", (object?)exif.GpsAltitude ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        RegisterWrite();
    }

    public void SaveTags(string canonicalPath, long modifiedUtcTicks, string keywords)
    {
        ThrowIfDisposed();
        EnsureTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            INSERT INTO image_tags(image_id, keywords, source_modified_utc_ticks)
            SELECT id, $keywords, $ticks FROM images
            WHERE canonical_path = $path AND modified_utc_ticks = $ticks
            ON CONFLICT(image_id) DO UPDATE SET
                keywords = excluded.keywords,
                source_modified_utc_ticks = excluded.source_modified_utc_ticks;
            """;
        command.Parameters.AddWithValue("$path", canonicalPath);
        command.Parameters.AddWithValue("$keywords", keywords);
        command.Parameters.AddWithValue("$ticks", modifiedUtcTicks);
        command.ExecuteNonQuery();
        RegisterWrite();
    }

    public void SaveBrowseLocations(
        string canonicalPath,
        long modifiedUtcTicks,
        IReadOnlyList<PackedThumbnailLocation> locations,
        int requiredTierCount)
    {
        ThrowIfDisposed();
        EnsureTransaction();
        using var image = _connection.CreateCommand();
        image.Transaction = _transaction;
        image.CommandText = "SELECT id FROM images WHERE canonical_path = $path;";
        image.Parameters.AddWithValue("$path", canonicalPath);
        var imageId = Convert.ToInt64(image.ExecuteScalar()
            ?? throw new InvalidOperationException("Bild fehlt im Katalog."));
        using var insert = _connection.CreateCommand();
        insert.Transaction = _transaction;
        insert.CommandText = @"INSERT INTO thumbnail_entries
            (image_id, tier, pack_file, byte_offset, byte_length, pixel_width,
             pixel_height, source_modified_utc_ticks)
            VALUES ($id, $tier, $pack, $offset, $length, $width, $height, $ticks)
            ON CONFLICT(image_id, tier) DO UPDATE SET
                pack_file = excluded.pack_file,
                byte_offset = excluded.byte_offset,
                byte_length = excluded.byte_length,
                pixel_width = excluded.pixel_width,
                pixel_height = excluded.pixel_height,
                source_modified_utc_ticks = excluded.source_modified_utc_ticks;";
        foreach (var location in locations)
        {
            insert.Parameters.Clear();
            insert.Parameters.AddWithValue("$id", imageId);
            insert.Parameters.AddWithValue("$tier", location.Tier);
            insert.Parameters.AddWithValue("$pack", location.PackFile);
            insert.Parameters.AddWithValue("$offset", location.ByteOffset);
            insert.Parameters.AddWithValue("$length", location.ByteLength);
            insert.Parameters.AddWithValue("$width", location.PixelWidth);
            insert.Parameters.AddWithValue("$height", location.PixelHeight);
            insert.Parameters.AddWithValue("$ticks", modifiedUtcTicks);
            insert.ExecuteNonQuery();
        }
        // Scanner creates every configured tier for imported/modified images.
        // Clear the persistent job within the same transaction as the entries.
        if (locations.Select(location => location.Tier).Distinct().Count() == requiredTierCount)
        {
            using var complete = _connection.CreateCommand();
            complete.Transaction = _transaction;
            complete.CommandText = "DELETE FROM browse_cache_jobs WHERE image_id = $id;";
            complete.Parameters.AddWithValue("$id", imageId);
            complete.ExecuteNonQuery();
        }
        RegisterWrite();
    }

    public void RollbackPending()
    {
        ThrowIfDisposed();

        if (_transaction is null)
        {
            return;
        }

        _transaction.Rollback();
        _transaction.Dispose();
        ClearTransactionState();
    }

    private void EnsureTransaction()
    {
        if (_transaction is not null)
        {
            return;
        }

        _transaction = _connection.BeginTransaction();
        _selectState.Transaction = _transaction;
        _selectPhotoFolder.Transaction = _transaction;
        _upsertPhotoFolder.Transaction = _transaction;
        _updateLocation.Transaction = _transaction;
        _upsert.Transaction = _transaction;
        _pendingWrites = 0;
    }

    private void RegisterWrite()
    {
        _pendingWrites++;
        if (_pendingWrites >= BatchSize)
        {
            CommitBatch();
        }
    }

    // Never keep an earlier write transaction open across slow image/SMB decoding.
    public void FlushPending()
    {
        ThrowIfDisposed();
        CommitBatch();
    }

    private void CommitBatch()
    {
        if (_transaction is null) return;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_sourcePath is not null)
            {
                // Check in the SAME SQLite transaction as catalog mutations.
                using var guard = _connection.CreateCommand();
                guard.Transaction = _transaction;
                guard.CommandText = """
                    SELECT EXISTS(
                        SELECT 1 FROM scheduler_sources s
                        JOIN monitored_folders m ON m.canonical_path = s.canonical_path
                        WHERE s.canonical_path = $path AND s.generation = $generation
                          AND s.desired_present = 1 AND m.id = $folderId);
                    """;
                guard.Parameters.AddWithValue("$path", _sourcePath);
                guard.Parameters.AddWithValue("$generation", _generation!.Value);
                guard.Parameters.AddWithValue("$folderId", _folderId!.Value);
                if (Convert.ToInt32(guard.ExecuteScalar()) != 1)
                    throw new OperationCanceledException("Scan source generation is obsolete.");
            }
            _cancellationToken.ThrowIfCancellationRequested();
            _transaction.Commit();
        }
        catch
        {
            try { _transaction.Rollback(); } catch { /* Already completed. */ }
            throw;
        }
        finally
        {
            _transaction.Dispose();
            ClearTransactionState();
        }
    }

    private void ClearTransactionState()
    {
        _transaction = null;
        _selectState.Transaction = null;
        _selectPhotoFolder.Transaction = null;
        _upsertPhotoFolder.Transaction = null;
        _updateLocation.Transaction = null;
        _upsert.Transaction = null;
        _pendingWrites = 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CommitBatch();
        _upsert.Dispose();
        _updateLocation.Dispose();
        _upsertPhotoFolder.Dispose();
        _selectPhotoFolder.Dispose();
        _selectState.Dispose();
        _connection.Dispose();
        _disposed = true;
    }
}
