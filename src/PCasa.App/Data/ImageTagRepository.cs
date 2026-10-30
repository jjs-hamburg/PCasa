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

    // Suggestions are derived from the existing SQLite keyword index; no
    // original photo, EXIF, thumbnail or NAS path is opened. FTS narrows
    // candidate rows before we split stored full tag phrases in managed code.
    public IReadOnlyList<string> SuggestExistingTags(string prefix,
        int maximum = 8, CancellationToken cancellationToken = default)
    {
        prefix = prefix.Trim();
        if (prefix.Length == 0) return Array.Empty<string>();
        maximum = Math.Clamp(maximum, 1, 16);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string? tag, int weight = 1)
        {
            if (string.IsNullOrWhiteSpace(tag)) return;
            tag = tag.Trim();
            if (tag.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                counts[tag] = counts.GetValueOrDefault(tag) + weight;
        }

        using var db = _database.OpenConnection();
        // Recent and queued words are already real tag vocabulary.
        using (var recents = db.CreateCommand())
        {
            recents.CommandText = "SELECT tag FROM recent_tags ORDER BY last_used_utc DESC LIMIT 9;";
            using var reader = recents.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(reader.GetString(0), 20);
            }
        }
        using (var pending = db.CreateCommand())
        {
            pending.CommandText = """
                SELECT tag FROM pending_tag_edits
                WHERE instr(lower(tag),lower($fragment)) > 0
                GROUP BY tag LIMIT 100;
                """;
            pending.Parameters.AddWithValue("$fragment", prefix);
            using var reader = pending.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Add(reader.GetString(0), 2);
            }
        }
        // FTS uses token prefixes, not SQL '%' wildcard scans over 500k rows.
        // Use the last typed word so multi-word tags also match after spaces.
        var words = Regex.Matches(prefix, @"[\p{L}\p{N}_]+")
            .Select(m => m.Value).ToArray();
        if (words.Length > 0)
        {
            var term = words[^1].Replace("\"", "\"\"");
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT t.keywords FROM image_tags_fts f
                JOIN image_tags t ON t.image_id=f.rowid
                WHERE image_tags_fts MATCH $term
                LIMIT 1000;
                """;
            cmd.Parameters.AddWithValue("$term", "\"" + term + "\"*");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) continue;
                foreach (var tag in reader.GetString(0)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries))
                    Add(tag);
            }
        }
        return counts.OrderByDescending(kv =>
                kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(maximum).Select(kv => kv.Key).ToArray();
    }

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
        long indexedSize;
        long indexedTicks;
        using (var pathCommand = db.CreateCommand())
        {
            pathCommand.CommandText =
                "SELECT local_path, file_size, modified_utc_ticks FROM images WHERE id = $id";
            pathCommand.Parameters.AddWithValue("$id", imageId);
            using var reader = pathCommand.ExecuteReader();
            if (!reader.Read())
                throw new IOException("Bild ist nicht mehr im Katalog.");
            path = reader.GetString(0);
            indexedSize = reader.GetInt64(1);
            indexedTicks = reader.GetInt64(2);
        }
        // A scheduler yield or source transfer must not race a metadata write.
        // The lock covers write + verification; never hold a global cache
        // lock across slow SMB I/O.
        using (SourceFileLocks.Enter(path, cancellationToken))
        {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff" or ".arw"))
            throw new NotSupportedException($"Schreiben von Tags in {ext} wird nicht unterstützt.");
        // A tag edit must not invalidate pixels, EXIF or packed thumbnails.
        // Retain their cache identity only when the original file matched
        // the catalog BEFORE the edit and ExifTool preserved its mtime.
        var before = new FileInfo(path);
        var oldSize = before.Length;
        var oldTicks = before.LastWriteTimeUtc.Ticks;
        // Read the authoritative tags from the shared source using the SAME
        // ExifTool session that performs the write. SQLite may be stale if
        // another computer edited the image since our last catalog scan.
        using var ownedWriter = writer is null ? new ExifToolKeywordWriter.Session() : null;
        var session = writer ?? ownedWriter!;
        var original = session.Read(path, cancellationToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = remove
            ? original.RemoveAll(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0
            : !original.Contains(tag, StringComparer.OrdinalIgnoreCase);
        if (!changed) return;
        if (!remove) original.Add(tag);
        cancellationToken.ThrowIfCancellationRequested();
        session.Write(path, original, cancellationToken);
        // Even ExifTool -P may round a subsecond file timestamp on Windows.
        // Restore the EXACT source timestamp under the per-image write lock.
        // Otherwise the existing file scanner needlessly decodes the same
        // pixels again and rebuilds all thumbnail tiers after a tag edit.
        if (File.GetLastWriteTimeUtc(path).Ticks != oldTicks)
            File.SetLastWriteTimeUtc(path, new DateTime(oldTicks, DateTimeKind.Utc));
        // A successful ExifTool acknowledgment confirms the write. Do not
        // perform a second NAS read merely to verify the same metadata.
        var confirmed = string.Join("\n", original);

        var after = new FileInfo(path);
        var newSize = after.Length;
        var newTicks = after.LastWriteTimeUtc.Ticks;
        var metadataOnly = oldSize == indexedSize && oldTicks == indexedTicks &&
                           newTicks == oldTicks;
        // One SQLite transaction publishes the verified keywords and, only
        // for a known metadata-only edit, the changed byte size. This keeps
        // the EXISTING EXIF and thumbnail cache valid without starting a
        // redundant NAS decode/pack rebuild on a watcher notification.
        using var transaction = db.BeginTransaction();
        if (metadataOnly && newSize != oldSize)
        {
            using var size = db.CreateCommand();
            size.Transaction = transaction;
            size.CommandText = """
                UPDATE images SET file_size = $newSize
                WHERE id = $id AND file_size = $oldSize
                  AND modified_utc_ticks = $ticks;
                """;
            size.Parameters.AddWithValue("$id", imageId);
            size.Parameters.AddWithValue("$newSize", newSize);
            size.Parameters.AddWithValue("$oldSize", oldSize);
            size.Parameters.AddWithValue("$ticks", oldTicks);
            if (size.ExecuteNonQuery() == 1)
            {
                using var exif = db.CreateCommand();
                exif.Transaction = transaction;
                exif.CommandText = """
                    UPDATE image_exif SET source_file_size = $newSize
                    WHERE image_id = $id AND source_file_size = $oldSize
                      AND source_modified_utc_ticks = $ticks;
                    """;
                exif.Parameters.AddWithValue("$id", imageId);
                exif.Parameters.AddWithValue("$newSize", newSize);
                exif.Parameters.AddWithValue("$oldSize", oldSize);
                exif.Parameters.AddWithValue("$ticks", oldTicks);
                exif.ExecuteNonQuery();
            }
        }
        using (var update = db.CreateCommand())
        {
            update.Transaction = transaction;
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
        transaction.Commit();
        }
    }

    // Immediate, durable user intent. No remote file I/O and no separate work queue.
    // Existing index and pending intent are committed atomically.
    public int QueueTagEdit(IReadOnlyList<long> imageIds, string tag, bool remove)
        => QueueTagEdits(imageIds, new[] { (Tag: tag, Remove: remove) });

    public int QueueTagEdits(IReadOnlyList<long> imageIds,
        IReadOnlyList<(string Tag, bool Remove)> edits)
        => QueueTagEdits(imageIds.Select(id =>
            (ImageId: id, Edits: edits)).ToArray());

    // All per-image edits of one OK action share ONE SQLite transaction.
    // This is the existing authoritative pending_tag_edits write path,
    // not another job queue or an additional metadata writer.
    public int QueueTagEdits(IReadOnlyList<(
        long ImageId, IReadOnlyList<(string Tag, bool Remove)> Edits)> editsByImage)
    {
        using var db = _database.OpenConnection();
        using var tx = db.BeginTransaction();
        var count = 0;
        var recent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, edits) in editsByImage)
        {
            foreach (var (originalTag, remove) in edits)
            {
            var tag = originalTag.Trim();
            if (tag.Length == 0) throw new ArgumentException("Tag fehlt.");
            using var read = db.CreateCommand();
            read.Transaction = tx;
            read.CommandText = """
                SELECT i.modified_utc_ticks, COALESCE(t.keywords, '')
                FROM images i LEFT JOIN image_tags t ON t.image_id=i.id
                WHERE i.id=$id;
                """;
            read.Parameters.AddWithValue("$id", id);
            long ticks;
            string words;
            using (var reader = read.ExecuteReader())
            {
                if (!reader.Read()) continue;
                ticks = reader.GetInt64(0);
                words = reader.GetString(1);
            }
            var tags = words.Split(new[] { '\n', '\r' },
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var changed = remove
                ? tags.RemoveAll(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)) > 0
                : !tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            if (!changed) continue;
            if (!remove) tags.Add(tag);
            using var upsert = db.CreateCommand();
            upsert.Transaction = tx;
            upsert.CommandText = """
                INSERT INTO image_tags(image_id,keywords,source_modified_utc_ticks)
                VALUES($id,$words,$ticks)
                ON CONFLICT(image_id) DO UPDATE SET keywords=excluded.keywords,
                    source_modified_utc_ticks=excluded.source_modified_utc_ticks;
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$words", string.Join("\n", tags));
            upsert.Parameters.AddWithValue("$ticks", ticks);
            upsert.ExecuteNonQuery();
            using var pending = db.CreateCommand();
            pending.Transaction = tx;
            pending.CommandText = """
                INSERT INTO pending_tag_edits(image_id,tag,desired_present,updated_utc)
                VALUES($id,$tag,$desired,$now)
                ON CONFLICT(image_id,tag) DO UPDATE SET
                    desired_present=excluded.desired_present,
                    revision=pending_tag_edits.revision+1,
                    updated_utc=excluded.updated_utc;
                """;
            pending.Parameters.AddWithValue("$id", id);
            pending.Parameters.AddWithValue("$tag", tag);
            pending.Parameters.AddWithValue("$desired", remove ? 0 : 1);
            pending.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            pending.ExecuteNonQuery();
            count++;
            if (!remove) recent.Add(tag);
            }
        }
        tx.Commit();
        foreach (var tag in recent) RememberTag(tag);
        return count;
    }

    // The persisted intent table is the only source for pending-write dots.
    public IReadOnlyList<string> PendingTagPaths()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT i.local_path FROM pending_tag_edits p
            JOIN images i ON i.id=p.image_id;
            """;
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public IReadOnlyList<(long Id, long ImageId, string Tag, bool Present, long Revision)>
        PendingEdits(int maximum = 512)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT rowid,image_id,tag,desired_present,revision
            FROM pending_tag_edits ORDER BY updated_utc LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", maximum);
        using var reader = cmd.ExecuteReader();
        var result = new List<(long,long,string,bool,long)>();
        while (reader.Read())
            result.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),
                reader.GetInt64(3)!=0,reader.GetInt64(4)));
        return result;
    }

    // The single SQLite intent table determines recovery, not a stale
    // immutable list from an earlier software version. Include source paths
    // so one offline volume can be skipped for this attempt while reachable
    // images on other volumes continue to be written.
    public IReadOnlyList<(long ImageId, string Path)> PendingImagePaths()
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT p.image_id,i.local_path
            FROM pending_tag_edits p
            JOIN images i ON i.id=p.image_id
            GROUP BY p.image_id
            ORDER BY MIN(p.updated_utc),p.image_id;
            """;
        using var reader = cmd.ExecuteReader();
        var results = new List<(long, string)>();
        while (reader.Read())
            results.Add((reader.GetInt64(0), reader.GetString(1)));
        return results;
    }

    public IReadOnlyList<(long Id, long ImageId, string Tag, bool Present, long Revision)>
        PendingEditsForImage(long imageId)
    {
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT rowid,image_id,tag,desired_present,revision
              FROM pending_tag_edits WHERE image_id=$image
             ORDER BY updated_utc;
            """;
        cmd.Parameters.AddWithValue("$image", imageId);
        using var reader = cmd.ExecuteReader();
        var result = new List<(long,long,string,bool,long)>();
        while (reader.Read())
            result.Add((reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),
                reader.GetInt64(3)!=0,reader.GetInt64(4)));
        return result;
    }

    // Execute one source read and at most one write per image, regardless of
    // the number of pending tag changes. The SQLite intent table remains the
    // sole durable backlog; it is never copied into another persistent queue.
    public void ApplyPendingImageEdits(long imageId,
        IReadOnlyList<(long Id, long ImageId, string Tag, bool Present, long Revision)> edits,
        ExifToolKeywordWriter.Session writer, CancellationToken token,
        Action<string>? onFileStarting = null)
    {
        if (edits.Count == 0) return;
        token.ThrowIfCancellationRequested();
        using var db = _database.OpenConnection();
        using var pathCommand = db.CreateCommand();
        pathCommand.CommandText =
            "SELECT local_path,file_size,modified_utc_ticks FROM images WHERE id=$id";
        pathCommand.Parameters.AddWithValue("$id", imageId);
        string path;
        long indexedSize, indexedTicks;
        using (var reader = pathCommand.ExecuteReader())
        {
            if (!reader.Read()) return; // Deleted images cascade their pending work.
            path = reader.GetString(0);
            indexedSize = reader.GetInt64(1);
            indexedTicks = reader.GetInt64(2);
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".tif" or ".tiff" or ".arw"))
            throw new NotSupportedException($"Schreiben von Tags in {ext} wird nicht unterstützt.");
        // This reports the REAL file path before a possibly slow NAS/source
        // lock. The scheduler still owns all progress and job state.
        onFileStarting?.Invoke(path);
        using (SourceFileLocks.Enter(path, token))
        {
            // Refresh latest revisions under this image's source lock.
            // An older package's snapshot can be stale after a newer manual
            // tag edit. Silent return used to advance its durable cursor
            // while pending_tag_edits (and the folder dot) remained.
            // Coalesce all current tags for this image into ONE native write.
            var currentEdits =
                new List<(long Id, long ImageId, string Tag, bool Present, long Revision)>();
            using (var check = db.CreateCommand())
            {
                check.CommandText = """
                    SELECT rowid,image_id,tag,desired_present,revision
                    FROM pending_tag_edits WHERE image_id=$id
                    ORDER BY updated_utc;
                    """;
                check.Parameters.AddWithValue("$id", imageId);
                using var row = check.ExecuteReader();
                while (row.Read())
                    currentEdits.Add((row.GetInt64(0), row.GetInt64(1),
                        row.GetString(2), row.GetInt64(3) != 0, row.GetInt64(4)));
            }
            if (currentEdits.Count == 0) return;
            token.ThrowIfCancellationRequested();
            var before = new FileInfo(path);
            var oldSize = before.Length;
            var oldTicks = before.LastWriteTimeUtc.Ticks;
            var sourceKeywords = writer.ReadSnapshot(path, token);
            var tags = sourceKeywords.Combined.ToList();
            var changed = false;
            foreach (var edit in currentEdits)
            {
                if (edit.Present)
                {
                    if (!tags.Contains(edit.Tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(edit.Tag);
                        changed = true;
                    }
                }
                else if (tags.RemoveAll(t =>
                    t.Equals(edit.Tag, StringComparison.OrdinalIgnoreCase)) > 0)
                    changed = true;
            }
            // Also repair a previously divergent IPTC/XMP pair, even when
            // the requested keyword was already present in one of them.
            var target = tags.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var xmp = sourceKeywords.Xmp.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var iptc = sourceKeywords.Iptc.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fieldsDiffer = !xmp.SetEquals(target) ||
                (ext != ".arw" && !iptc.SetEquals(target));
            if (changed || fieldsDiffer)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(path, tags, token);
                if (File.GetLastWriteTimeUtc(path).Ticks != oldTicks)
                    File.SetLastWriteTimeUtc(path,
                        new DateTime(oldTicks, DateTimeKind.Utc));
            }
            // The physical ExifTool write is now complete. Even if a
            // higher-priority task requested a yield during those bytes,
            // checkpoint this result in SQLite before surrendering the
            // scheduler step. Otherwise the intent would be needlessly
            // replayed while an orphaned native temp might block progress.
            var after = new FileInfo(path);
            var newSize = after.Length;
            var newTicks = after.LastWriteTimeUtc.Ticks;
            using var tx = db.BeginTransaction();
            // Preserve the optimistic projection: newer edits may have landed
            // while the source write was in progress.
            using (var done = db.CreateCommand())
            {
                done.Transaction = tx;
                done.CommandText = """
                    DELETE FROM pending_tag_edits
                    WHERE image_id=$id AND tag=$tag AND revision=$revision;
                    """;
                done.Parameters.AddWithValue("$id", imageId);
                var tagParam = done.Parameters.Add("$tag", SqliteType.Text);
                var revisionParam = done.Parameters.Add("$revision", SqliteType.Integer);
                foreach (var edit in currentEdits)
                {
                    tagParam.Value = edit.Tag;
                    revisionParam.Value = edit.Revision;
                    done.ExecuteNonQuery();
                }
            }
            // A keyword-only edit preserves the original timestamp. Update
            // the cached byte length so scans do not rebuild pixel caches.
            if (oldSize == indexedSize && oldTicks == indexedTicks &&
                newTicks == oldTicks && newSize != oldSize)
            {
                using var size = db.CreateCommand();
                size.Transaction = tx;
                size.CommandText = """
                    UPDATE images SET file_size=$newSize
                    WHERE id=$id AND file_size=$oldSize
                      AND modified_utc_ticks=$ticks;
                    """;
                size.Parameters.AddWithValue("$newSize", newSize);
                size.Parameters.AddWithValue("$id", imageId);
                size.Parameters.AddWithValue("$oldSize", oldSize);
                size.Parameters.AddWithValue("$ticks", oldTicks);
                if (size.ExecuteNonQuery() == 1)
                {
                    using var exif = db.CreateCommand();
                    exif.Transaction = tx;
                    exif.CommandText = """
                        UPDATE image_exif SET source_file_size=$newSize
                        WHERE image_id=$id AND source_file_size=$oldSize
                          AND source_modified_utc_ticks=$ticks;
                        """;
                    exif.Parameters.AddWithValue("$newSize", newSize);
                    exif.Parameters.AddWithValue("$id", imageId);
                    exif.Parameters.AddWithValue("$oldSize", oldSize);
                    exif.Parameters.AddWithValue("$ticks", oldTicks);
                    exif.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Explicitly abandon pending tag writes. Because tag drafts already
    /// update the gallery projection, restore it from the authoritative
    /// ORIGINAL (read-only) before deleting any pending revisions. If the
    /// source cannot be read or a newer revision appears, keep both intents
    /// and error visible in the review dialog.
    /// </summary>
    public bool DiscardPendingImageEdits(long imageId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = _database.OpenConnection();
        string path;
        long indexedTicks;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT local_path,modified_utc_ticks FROM images WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", imageId);
            using var row = cmd.ExecuteReader();
            if (!row.Read()) return false;
            path = row.GetString(0);
            indexedTicks = row.GetInt64(1);
        }
        using (SourceFileLocks.Enter(path, token))
        {
            var prior = PendingEditsForImage(imageId);
            if (prior.Count == 0) return true;
            token.ThrowIfCancellationRequested();
            using var writer = new ExifToolKeywordWriter.Session();
            var snapshot = writer.ReadSnapshot(path, token);
            var originalKeywords = string.Join("\n",
                snapshot.Combined.Distinct(StringComparer.OrdinalIgnoreCase));
            using var tx = db.BeginTransaction();
            var current = new List<(string Tag,long Revision)>();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "SELECT tag,revision FROM pending_tag_edits WHERE image_id=$id ORDER BY updated_utc;";
                cmd.Parameters.AddWithValue("$id", imageId);
                using var rows = cmd.ExecuteReader();
                while (rows.Read()) current.Add((rows.GetString(0),rows.GetInt64(1)));
            }
            if (current.Count != prior.Count ||
                prior.Where((edit, i) =>
                    edit.Tag != current[i].Tag || edit.Revision != current[i].Revision).Any())
                throw new IOException("Neuere Tag-Änderungen sind eingetroffen; Verwerfen erneut bestätigen.");
            using (var update = db.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    INSERT INTO image_tags(image_id,keywords,source_modified_utc_ticks)
                    VALUES ($id,$tags,$ticks)
                    ON CONFLICT(image_id) DO UPDATE SET
                        keywords=excluded.keywords,
                        source_modified_utc_ticks=excluded.source_modified_utc_ticks;
                    """;
                update.Parameters.AddWithValue("$id", imageId);
                update.Parameters.AddWithValue("$tags", originalKeywords);
                update.Parameters.AddWithValue("$ticks", indexedTicks);
                update.ExecuteNonQuery();
            }
            using (var delete = db.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM pending_tag_edits WHERE image_id=$id;";
                delete.Parameters.AddWithValue("$id", imageId);
                delete.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return true;
    }

    // Commas/semicolons separate distinct Tag requirements. Matching is
    // AND across all supplied terms; no file/ExifTool/NAS reads are involved.
    // A single unseparated query retains its established FTS-prefix behavior.
    public HashSet<long> SearchImageIds(string text)
    {
        var tagParts = text.Split([',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tagParts.Length == 0) return new HashSet<long>();
        var groupedWords = tagParts.Select(part =>
                Regex.Matches(part, @"[\p{L}\p{N}_]+")
                    .Select(m => m.Value).ToArray())
            .ToArray();
        if (groupedWords.Any(group => group.Length == 0))
            return new HashSet<long>();
        // Quoted words neutralize FTS syntax, while the existing prefix match
        // narrows candidate rows efficiently even in very large libraries.
        var expression = string.Join(" AND ", groupedWords.Select(group =>
            "(" + string.Join(" AND ", group.Select(word =>
                "\"" + word.Replace("\"", "\"\"") + "\"*")) + ")"));
        using var db = _database.OpenConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = tagParts.Length == 1
            ? "SELECT rowid FROM image_tags_fts WHERE image_tags_fts MATCH $terms;"
            : """
                SELECT f.rowid,t.keywords
                FROM image_tags_fts f
                JOIN image_tags t ON t.image_id=f.rowid
                WHERE image_tags_fts MATCH $terms;
                """;
        cmd.Parameters.AddWithValue("$terms", expression);
        using var reader = cmd.ExecuteReader();
        var matches = new HashSet<long>();
        while (reader.Read())
        {
            if (tagParts.Length == 1)
            {
                matches.Add(reader.GetInt64(0));
                continue;
            }
            // FTS narrows the rows, but AND between words alone could match
            // two different words in an unrelated long Tag. Require every
            // input term to match an individual stored Tag (prefix of word
            // sequence), without consuming one Tag twice.
            var stored = reader.IsDBNull(1) ? Array.Empty<string>()
                : reader.GetString(1).Split('\n',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);
            var tokensByTag = stored.Select(tag => Regex.Matches(tag,
                    @"[\p{L}\p{N}_]+").Select(m => m.Value).ToArray())
                .ToArray();
            var assigned = new bool[stored.Length];
            bool MatchesEvery(int termIndex)
            {
                if (termIndex == groupedWords.Length) return true;
                var words = groupedWords[termIndex];
                for (var tagIndex = 0; tagIndex < tokensByTag.Length; tagIndex++)
                {
                    if (assigned[tagIndex]) continue;
                    var tokens = tokensByTag[tagIndex];
                    var matchesPhrase = false;
                    for (var start = 0; start + words.Length <= tokens.Length; start++)
                    {
                        var all = true;
                        for (var word = 0; word < words.Length; word++)
                            if (!tokens[start + word].StartsWith(words[word],
                                StringComparison.OrdinalIgnoreCase))
                            {
                                all = false;
                                break;
                            }
                        if (all) { matchesPhrase = true; break; }
                    }
                    if (!matchesPhrase) continue;
                    assigned[tagIndex] = true;
                    if (MatchesEvery(termIndex + 1)) return true;
                    assigned[tagIndex] = false;
                }
                return false;
            }
            if (MatchesEvery(0)) matches.Add(reader.GetInt64(0));
        }
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
            if (Path.GetExtension(path).Equals(".arw", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new ExifToolKeywordWriter.Session();
                return string.Join("\n", reader.Read(path));
            }
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
                      AND NOT EXISTS (SELECT 1 FROM pending_tag_edits p WHERE p.image_id=i.id)
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
                      AND NOT EXISTS (SELECT 1 FROM pending_tag_edits p WHERE p.image_id=images.id)
                    ON CONFLICT(image_id) DO UPDATE SET
                        keywords = excluded.keywords,
                        source_modified_utc_ticks = excluded.source_modified_utc_ticks
                    WHERE NOT EXISTS (SELECT 1 FROM pending_tag_edits p WHERE p.image_id=image_tags.image_id);
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
