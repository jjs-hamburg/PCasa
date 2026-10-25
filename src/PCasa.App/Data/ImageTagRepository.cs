using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using PCasa.App.Services;
using Microsoft.Data.Sqlite;

namespace PCasa.App.Data;

/// <summary>
/// Single SQLite owner of searchable image keywords. Image files are read only
/// when newly imported, changed, or not yet indexed; never during a search.
/// </summary>
public sealed class ImageTagRepository
{
    private readonly LocalDatabase _database;

    public ImageTagRepository(LocalDatabase database) => _database = database;

    public IReadOnlyList<string> GetRecentTags()
    {
        using var db = _database.OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText =
            "SELECT tag FROM recent_tags ORDER BY last_used_utc DESC, tag DESC LIMIT 9;";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void RememberTag(string tag)
    {
        tag = tag.Trim();
        if (tag.Length == 0) return;
        using var db = _database.OpenConnection();
        using var transaction = db.BeginTransaction();
        using (var upsert = db.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO recent_tags(tag, last_used_utc) VALUES ($tag, $now)
                ON CONFLICT(tag) DO UPDATE SET
                    tag=excluded.tag, last_used_utc=excluded.last_used_utc;
                """;
            upsert.Parameters.AddWithValue("$tag", tag);
            upsert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            upsert.ExecuteNonQuery();
        }
        using (var trim = db.CreateCommand())
        {
            trim.Transaction = transaction;
            trim.CommandText = """
                DELETE FROM recent_tags WHERE tag NOT IN (
                    SELECT tag FROM recent_tags
                    ORDER BY last_used_utc DESC, tag DESC LIMIT 9
                );
                """;
            trim.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string? GetKeywords(long imageId)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT keywords FROM image_tags WHERE image_id = $id;";
        cmd.Parameters.AddWithValue("$id", imageId);
        return cmd.ExecuteScalar() as string;
    }


    /// <summary>
    /// Update original XMP through the single ExifTool writer. A selection may
    /// pass one reusable process session; single-image callers use the same
    /// writer path. The original file remains authoritative.
    /// </summary>
    public void ChangeKeywords(long imageId, string tag, bool remove,
        ExifToolKeywordWriter.Session? writer = null,
        CancellationToken cancellationToken = default)
    {
        ChangeKeywordsLocked(imageId, tag, remove, writer, cancellationToken);
    }

    private void ChangeKeywordsLocked(long imageId, string tag, bool remove,
        ExifToolKeywordWriter.Session? writer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Tag fehlt.");
        cancellationToken.ThrowIfCancellationRequested();
        using var db = _database.OpenConnection();
        string path;
        string? cached;
        long modifiedTicks;
        long? indexedTicks;
        using (var pathCommand = db.CreateCommand())
        {
            pathCommand.CommandText = """
                SELECT i.local_path, i.modified_utc_ticks, t.keywords,
                       t.source_modified_utc_ticks
                FROM images i LEFT JOIN image_tags t ON t.image_id = i.id
                WHERE i.id = $id;
                """;
            pathCommand.Parameters.AddWithValue("$id", imageId);
            using var reader = pathCommand.ExecuteReader();
            if (!reader.Read())
                throw new IOException("Bild ist nicht mehr im Katalog.");
            path = reader.GetString(0);
            modifiedTicks = reader.GetInt64(1);
            cached = reader.IsDBNull(2) ? null : reader.GetString(2);
            indexedTicks = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        }
        // A scheduler yield or source transfer must not race a metadata write.
        // The lock covers the complete write + physical verification, but no
        // global cache/index lock is held across slow network I/O.
        using (SourceFileLocks.Enter(path, cancellationToken))
        {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff"))
            throw new NotSupportedException($"Schreiben von Tags in {ext} wird nicht unterstützt.");
        // The scanner already imported these keywords. Reuse the indexed
        // value ONLY if the actual source mtime still matches the indexed
        // version. Otherwise reread the original and preserve external tags.
        var current = cached is not null && indexedTicks == modifiedTicks &&
            File.GetLastWriteTimeUtc(path).Ticks == modifiedTicks
                ? cached : ReadKeywords(path);
        if (current is null)
            throw new IOException("Metadaten können nicht gelesen werden.");
        var original = current.Split(new[] { (char)10, (char)13 },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = remove
            ? original.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0
            : !original.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (!changed) return;
        if (!remove) original.Add(tag);
        cancellationToken.ThrowIfCancellationRequested();
        if (writer is null) ExifToolKeywordWriter.Write(path, original);
        else writer.Write(path, original, cancellationToken);
        // The real JPEG/TIFF metadata must match the requested edit before
        // the SQLite search index or UI can report it as saved.
        var confirmed = ReadKeywords(path)
            ?? throw new IOException("Gespeicherte Tags konnten nicht verifiziert werden.");
        var verified = confirmed.Split(new[] { (char)10, (char)13 },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (remove ? verified.Contains(tag, StringComparer.OrdinalIgnoreCase)
                   : !verified.Contains(tag, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Die Tag-Änderung konnte nicht verifiziert werden.");

        // File metadata is the authority. Cache the reread result only after
        // the in-place write and verification succeeded.
        using var update = db.CreateCommand();
        update.CommandText = """
            INSERT INTO image_tags (image_id, keywords, source_modified_utc_ticks)
            SELECT id, $words, modified_utc_ticks FROM images WHERE id = $id
            ON CONFLICT(image_id) DO UPDATE SET
                keywords = excluded.keywords,
                source_modified_utc_ticks = excluded.source_modified_utc_ticks;
            """;
        update.Parameters.AddWithValue("$id", imageId);
        update.Parameters.AddWithValue("$words", confirmed);
        update.ExecuteNonQuery();
        }
    }

    public HashSet<long> SearchImageIds(string text)
    {
        var tokens = Regex.Matches(text, @"[\p{L}\p{N}_]+")
            .Select(m => m.Value)
            .Where(v => v.Length > 0)
            .ToArray();
        if (tokens.Length == 0) return new HashSet<long>();
        // Quoted terms prevent FTS syntax injection; '*' enables fast prefixes.
        var expression = string.Join(" AND ",
            tokens.Select(token => "\"" + token.Replace("\"", "\"\"") + "\"*"));
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT rowid FROM image_tags_fts WHERE image_tags_fts MATCH $terms;";
        cmd.Parameters.AddWithValue("$terms", expression);
        using var reader = cmd.ExecuteReader();
        var matches = new HashSet<long>();
        while (reader.Read()) matches.Add(reader.GetInt64(0));
        return matches;
    }

    // Null means the source was unavailable or could not be decoded.
    // Empty string means it was inspected successfully and has no keywords.
    public static string? ReadKeywords(string path)
    {
        lock (SourceFileLocks.ForPath(path))
            return ReadKeywordsLocked(path);
    }

    private static string? ReadKeywordsLocked(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var keywords = (decoder.Frames[0].Metadata as BitmapMetadata)?.Keywords;
            return keywords is { Count: > 0 }
                ? string.Join("\n", keywords.Where(word => !string.IsNullOrWhiteSpace(word))
                    .Select(word => word.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                : string.Empty;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception) { return string.Empty; } // Unsupported image metadata.
    }

    /// <summary>
    /// Backfill existing images, resumable by the database itself. The scanner
    /// updates the same rows whenever the source timestamp changes.
    /// No thumbnail or browse pack is rebuilt.
    /// </summary>
    public void IndexMissing(CancellationToken cancellationToken,
        Action<int>? onProgress = null)
    {
        long afterId = 0;
        var processed = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<(long Id, string Path, long Ticks)>();
            using (var db = _database.OpenConnection())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT i.id, i.local_path, i.modified_utc_ticks
                    FROM images i
                    LEFT JOIN image_tags t ON t.image_id = i.id
                    WHERE i.id > $after
                      AND (t.image_id IS NULL OR
                           t.source_modified_utc_ticks != i.modified_utc_ticks)
                    ORDER BY i.id LIMIT 32;
                    """;
                cmd.Parameters.AddWithValue("$after", afterId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    batch.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
            }
            if (batch.Count == 0) return;
            afterId = batch[^1].Id;

            var found = new List<(long Id, string Path, string Keywords, long Ticks)>();
            foreach (var image in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var words = ReadKeywords(image.Path);
                if (words is not null)
                    found.Add((image.Id, image.Path, words, image.Ticks));
            }

            // Any network file verification belongs outside the mutation
            // lock: an unavailable share must not block catalog operations.
            var verified = new List<(long Id, string Path, string Keywords, long Ticks)>();
            foreach (var image in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(image.Path) &&
                        File.GetLastWriteTimeUtc(image.Path).Ticks == image.Ticks)
                        verified.Add(image);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (verified.Count > 0)
            {
                // Always acquire the catalogue mutation lock BEFORE starting
                // a SQLite write transaction. A tag write may have replaced
                // an image since the earlier metadata read.
                lock (CatalogRemovalService.CacheLock)
                {
                using var db = _database.OpenConnection();
                using var tx = db.BeginTransaction();
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO image_tags(image_id, keywords, source_modified_utc_ticks)
                    SELECT id, $words, $ticks FROM images
                    WHERE id = $id AND modified_utc_ticks = $ticks
                    ON CONFLICT(image_id) DO UPDATE SET
                        keywords = excluded.keywords,
                        source_modified_utc_ticks = excluded.source_modified_utc_ticks;
                    """;
                cmd.Parameters.Add("$id", SqliteType.Integer);
                cmd.Parameters.Add("$words", SqliteType.Text);
                cmd.Parameters.Add("$ticks", SqliteType.Integer);
                foreach (var image in verified)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    cmd.Parameters["$id"].Value = image.Id;
                    cmd.Parameters["$words"].Value = image.Keywords;
                    cmd.Parameters["$ticks"].Value = image.Ticks;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                }
            }
            processed += batch.Count;
            onProgress?.Invoke(processed);
            // Priority preemption is handled by PCasaScheduler: no artificial
            // per-batch sleep delays for a large photo library.
        }
    }
}
