using Microsoft.Data.Sqlite;
using PCasa.App.Models;

namespace PCasa.App.Data;

public sealed class BrowseCacheRepository
{
    private readonly LocalDatabase _database;

    public BrowseCacheRepository(LocalDatabase database)
    {
        _database = database;
    }

    public int CountEntries()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM thumbnail_entries;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CountMissing(BrowseThumbnailProfile profile)
    {
        using var connection = _database.OpenConnection();
        using var command = CreateMissingCandidatesCommand(connection, profile, countOnly: true);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<BrowseCacheCandidate> GetMissingCandidates(
        BrowseThumbnailProfile profile,
        long afterImageId,
        int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var connection = _database.OpenConnection();
        using var command = CreateMissingCandidatesCommand(connection, profile, countOnly: false);
        command.Parameters.AddWithValue("$afterImageId", afterImageId);
        command.Parameters.AddWithValue("$limit", limit);

        using var reader = command.ExecuteReader();
        var result = new List<BrowseCacheCandidate>();
        while (reader.Read())
        {
            var existingTiers = ParseTierSet(reader.IsDBNull(7) ? null : reader.GetString(7));
            result.Add(new BrowseCacheCandidate(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                existingTiers));
        }

        return result;
    }

    public IReadOnlyList<BrowseThumbnailIndexEntry> GetIndex(int tier)
        => ReadIndex(tier, imageId: null);

    // A completed file transfer needs ONE prepared image, not a reload of a
    // half-million-entry gallery. Uses the SAME index SQL and cache validity
    // rules as the normal full projection.
    public BrowseThumbnailIndexEntry? GetIndexEntry(int tier, long imageId)
        => ReadIndex(tier, imageId).FirstOrDefault();

    private IReadOnlyList<BrowseThumbnailIndexEntry> ReadIndex(
        int tier, long? imageId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                i.id,
                i.photo_folder_id,
                te.tier,
                te.pack_file,
                te.byte_offset,
                te.byte_length,
                te.pixel_width,
                te.pixel_height,
                i.file_name,
                CASE WHEN x.gps_latitude IS NOT NULL AND x.gps_longitude IS NOT NULL THEN 1 ELSE 0 END,
                CASE WHEN t.keywords IS NOT NULL AND LENGTH(TRIM(t.keywords)) > 0 THEN 1 ELSE 0 END,
                CASE WHEN LOWER(i.extension) IN ('.arw','.nef','.nrw','.cr2','.cr3','.dng',
                   '.raf','.rw2','.orf','.pef','.srw','.crw') THEN 1 ELSE 0 END,
                i.file_size,
                i.modified_utc_ticks,
                i.created_utc_ticks,
                x.date_time_original,
                i.width,
                i.height
            FROM images i
            INNER JOIN photo_folders pf ON pf.id = i.photo_folder_id
            INNER JOIN thumbnail_entries te ON te.image_id = i.id
            LEFT JOIN image_exif x ON x.image_id = i.id
                AND x.source_modified_utc_ticks = i.modified_utc_ticks
                AND x.source_file_size = i.file_size
            LEFT JOIN image_tags t ON t.image_id = i.id
                AND t.source_modified_utc_ticks = i.modified_utc_ticks
            WHERE te.tier = $tier
              AND te.source_modified_utc_ticks = i.modified_utc_ticks
            """ + (imageId is long ? " AND i.id = $imageId " : " ") + """
            ORDER BY
                pf.canonical_path COLLATE NOCASE,
                i.file_name COLLATE NOCASE,
                i.canonical_path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$tier", tier);
        if (imageId is long requestedId)
            command.Parameters.AddWithValue("$imageId", requestedId);

        using var reader = command.ExecuteReader();
        var result = new List<BrowseThumbnailIndexEntry>();
        while (reader.Read())
        {
            result.Add(new BrowseThumbnailIndexEntry(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetInt32(9) != 0,
                reader.GetInt32(10) != 0,
                reader.GetInt32(11) != 0,
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14),
                reader.IsDBNull(15)
                    ? null : ParseCaptureTime(reader.GetString(15)),
                reader.GetInt32(16),
                reader.GetInt32(17)));
        }

        return result;
    }

    // Reuse across large 500k-image index reads; do not allocate an array
    // of format strings for each catalog row.
    private static readonly string[] CaptureTimePatterns =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm:ss.FFF",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFF", "yyyy-MM-ddTHH:mm:ssK"
    ];

    private static long? ParseCaptureTime(string source)
    {
        // Camera EXIF uses YYYY:MM:DD HH:mm:ss. Importers also write ISO.
        // A missing/invalid source date is not an epoch date; the comparer
        // explicitly falls back to file creation then modification.
        return DateTime.TryParseExact(source.Trim(), CaptureTimePatterns,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces,
            out var timestamp)
            ? timestamp.Ticks : null;
    }

    public BrowseCacheWriteSession CreateWriteSession()
        => new(_database.OpenConnection());

    private static SqliteCommand CreateMissingCandidatesCommand(
        SqliteConnection connection,
        BrowseThumbnailProfile profile,
        bool countOnly)
    {
        var command = connection.CreateCommand();
        // Detect missing or outdated tiers even when an interrupted operation
        // left no pending job. This is the source of truth for cache completeness.
        var names = profile.Tiers.Select((_, index) => "$requiredTier" + index).ToArray();
        var missingTiers = string.Join(" OR ", names.Select(name =>
            "NOT EXISTS (SELECT 1 FROM thumbnail_entries te WHERE te.image_id = i.id " +
            "AND te.tier = " + name +
            " AND te.source_modified_utc_ticks = i.modified_utc_ticks)"));
        var predicate = "EXISTS (SELECT 1 FROM browse_cache_jobs j WHERE j.image_id = i.id)" +
            " OR (" + missingTiers + ")";

        command.CommandText = countOnly
            ? $"SELECT COUNT(*) FROM images i WHERE i.photo_folder_id IS NOT NULL AND {predicate};"
            : $"""
              SELECT
                  i.id,
                  i.photo_folder_id,
                  i.local_path,
                  i.thumbnail_path,
                  i.modified_utc_ticks,
                  i.width,
                  i.height,
                  COALESCE((
                      SELECT GROUP_CONCAT(te2.tier, ',')
                      FROM thumbnail_entries te2
                      WHERE te2.image_id = i.id
                        AND te2.source_modified_utc_ticks = i.modified_utc_ticks
                  ), '') AS existing_tiers
              FROM images i
              WHERE i.photo_folder_id IS NOT NULL
                AND i.id > $afterImageId
                AND {predicate}
              ORDER BY i.id
              LIMIT $limit;
              """;

        for (var index = 0; index < names.Length; index++)
            command.Parameters.AddWithValue(names[index], profile.Tiers[index]);
        return command;
    }

    private static IReadOnlySet<int> ParseTierSet(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return new HashSet<int>();
        }

        return csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, out var tier) ? tier : 0)
            .Where(tier => tier > 0)
            .ToHashSet();
    }
}

public sealed class BrowseCacheWriteSession : IDisposable
{
    private const int BatchSize = 500;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _upsert;
    private SqliteTransaction? _transaction;
    private int _pendingWrites;
    private bool _disposed;

    public BrowseCacheWriteSession(SqliteConnection connection)
    {
        _connection = connection;
        _upsert = connection.CreateCommand();
        _upsert.CommandText =
            """
            INSERT INTO thumbnail_entries (
                image_id,
                tier,
                pack_file,
                byte_offset,
                byte_length,
                pixel_width,
                pixel_height,
                source_modified_utc_ticks
            )
            SELECT $imageId, $tier, $packFile, $byteOffset, $byteLength,
                   $pixelWidth, $pixelHeight, $sourceModifiedUtcTicks
            FROM images
            WHERE id = $imageId AND modified_utc_ticks = $sourceModifiedUtcTicks
            ON CONFLICT(image_id, tier) DO UPDATE SET
                pack_file = excluded.pack_file,
                byte_offset = excluded.byte_offset,
                byte_length = excluded.byte_length,
                pixel_width = excluded.pixel_width,
                pixel_height = excluded.pixel_height,
                source_modified_utc_ticks = excluded.source_modified_utc_ticks;
            """;
        _upsert.Parameters.Add("$imageId", SqliteType.Integer);
        _upsert.Parameters.Add("$tier", SqliteType.Integer);
        _upsert.Parameters.Add("$packFile", SqliteType.Text);
        _upsert.Parameters.Add("$byteOffset", SqliteType.Integer);
        _upsert.Parameters.Add("$byteLength", SqliteType.Integer);
        _upsert.Parameters.Add("$pixelWidth", SqliteType.Integer);
        _upsert.Parameters.Add("$pixelHeight", SqliteType.Integer);
        _upsert.Parameters.Add("$sourceModifiedUtcTicks", SqliteType.Integer);
    }

    public void Save(
        long imageId,
        long sourceModifiedUtcTicks,
        PackedThumbnailLocation location)
    {
        ThrowIfDisposed();
        EnsureTransaction();

        _upsert.Parameters["$imageId"].Value = imageId;
        _upsert.Parameters["$tier"].Value = location.Tier;
        _upsert.Parameters["$packFile"].Value = location.PackFile;
        _upsert.Parameters["$byteOffset"].Value = location.ByteOffset;
        _upsert.Parameters["$byteLength"].Value = location.ByteLength;
        _upsert.Parameters["$pixelWidth"].Value = location.PixelWidth;
        _upsert.Parameters["$pixelHeight"].Value = location.PixelHeight;
        _upsert.Parameters["$sourceModifiedUtcTicks"].Value = sourceModifiedUtcTicks;
        _upsert.ExecuteNonQuery();

        _pendingWrites++;
        if (_pendingWrites >= BatchSize)
        {
            Commit();
        }
    }

    public void MarkComplete(long imageId, long sourceModifiedUtcTicks,
        BrowseThumbnailProfile profile)
    {
        ThrowIfDisposed();
        EnsureTransaction();
        using var check = _connection.CreateCommand();
        check.Transaction = _transaction;
        var names = profile.Tiers.Select((_, i) => "$tier" + i).ToArray();
        check.CommandText = $"""
            SELECT COUNT(*) FROM thumbnail_entries
            WHERE image_id = $id AND source_modified_utc_ticks = $ticks
              AND tier IN ({string.Join(",", names)});
            """;
        check.Parameters.AddWithValue("$id", imageId);
        check.Parameters.AddWithValue("$ticks", sourceModifiedUtcTicks);
        for (var i = 0; i < names.Length; i++)
            check.Parameters.AddWithValue(names[i], profile.Tiers[i]);
        if (Convert.ToInt32(check.ExecuteScalar()) != profile.Tiers.Count)
            return;

        using var complete = _connection.CreateCommand();
        complete.Transaction = _transaction;
        // The candidate may have become stale after its bytes were encoded.
        // Never clear a repair job created for a newer source timestamp.
        complete.CommandText = """
            DELETE FROM browse_cache_jobs
            WHERE image_id = $id
              AND EXISTS (
                  SELECT 1 FROM images
                  WHERE id = $id AND modified_utc_ticks = $ticks);
            """;
        complete.Parameters.AddWithValue("$id", imageId);
        complete.Parameters.AddWithValue("$ticks", sourceModifiedUtcTicks);
        complete.ExecuteNonQuery();
    }

    public void Commit()
    {
        if (_transaction is null)
        {
            return;
        }

        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        _upsert.Transaction = null;
        _pendingWrites = 0;
    }

    public void RollbackPending()
    {
        ThrowIfDisposed();
        if (_transaction is null) return;
        _transaction.Rollback();
        _transaction.Dispose();
        _transaction = null;
        _upsert.Transaction = null;
        _pendingWrites = 0;
    }

    private void EnsureTransaction()
    {
        if (_transaction is not null)
        {
            return;
        }

        _transaction = _connection.BeginTransaction();
        _upsert.Transaction = _transaction;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Commit();
        _upsert.Dispose();
        _connection.Dispose();
        _disposed = true;
    }
}
