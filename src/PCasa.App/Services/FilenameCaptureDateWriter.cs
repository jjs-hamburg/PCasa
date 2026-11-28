using System.IO;
using System.Text.Json;
using PCasa.App.Data;

namespace PCasa.App.Services;

/// <summary>Metadata-only write. Existing thumbnail packs and tag source revisions
/// remain valid because the pixel data and original file timestamps are unchanged.
/// A replay after native write but before catalog commit reconciles verified EXIF.</summary>
public sealed class FilenameCaptureDateWriter(LocalDatabase database, UndoOptions? undoOptions = null)
{
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".png" or ".mp4";

    public static bool IsMp4(string path) => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    private static string? ExplicitVideoCaptureDate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var obj = doc.RootElement;
        return obj.TryGetProperty("XMP-exif:DateTimeOriginal", out var tag) &&
            tag.ValueKind == JsonValueKind.String ? tag.GetString() : null;
    }

    public static bool HasCaptureDate(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var obj = doc.RootElement;
            if (obj.ValueKind == JsonValueKind.Array && obj.GetArrayLength() == 1) obj = obj[0];
            if (obj.ValueKind != JsonValueKind.Object) return false;
            return obj.EnumerateObject().Any(p =>
                (p.Name == "DateTimeOriginal" || p.Name.EndsWith(":DateTimeOriginal", StringComparison.Ordinal) ||
                 p.Name.EndsWith("]DateTimeOriginal", StringComparison.Ordinal)) &&
                p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()));
        }
        catch (JsonException) { return false; }
    }

    public void Apply(long imageId, string requested, bool missingOnly, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = ExifDateMetadata.ParseEditableDate(requested)
            ?? throw new InvalidDataException("Ungültiges Aufnahmedatum.");
        using var db = database.OpenConnection();
        using var select = db.CreateCommand();
        select.CommandText = "SELECT local_path,file_size,modified_utc_ticks FROM images WHERE id=$id;";
        select.Parameters.AddWithValue("$id", imageId);
        string path; long indexedSize, indexedTicks;
        using (var row = select.ExecuteReader())
        {
            if (!row.Read()) return;
            path = row.GetString(0); indexedSize = row.GetInt64(1); indexedTicks = row.GetInt64(2);
        }
        if (!Supports(path))
            throw new NotSupportedException("EXIF-Datumsbearbeitung für diese Datei nicht unterstützt.");
        // Never write a date derived from a formerly named, now renamed file.
        if (PhotoDateFromFilename.Recognize(Path.GetFileName(path))?.AsExifDateTime != target)
            throw new IOException("Dateiname wurde seit der Auswahl geändert: " + path);
        using var lease = SourceFileLocks.Enter(path, token);
        var before = new FileInfo(path);
        if (!before.Exists) throw new FileNotFoundException("Originaldatei nicht verfügbar.", path);
        var originalCreated = before.CreationTimeUtc;
        var originalModified = before.LastWriteTimeUtc;
        using var readerLease = MetadataWriteScope.RentReader();
        var reader = readerLease.Value;
        var source = reader.Read(path, token);
        var video = IsMp4(path);
        var current = ExifDateMetadata.FieldsFromJson(source.Json);
        var applied = ExifDateMetadata.SameProtectedTag("EXIF:DateTimeOriginal", target,
            video ? ExplicitVideoCaptureDate(source.Json) : current.DateTimeOriginal);
        if (missingOnly && !applied && (video ? !string.IsNullOrWhiteSpace(source.DateTimeOriginal) : HasCaptureDate(source.Json))) return;
        if (originalModified.Ticks != indexedTicks || (before.Length != indexedSize && !applied))
            throw new IOException("Datei wurde außerhalb von PCasa verändert: " + path);
        using var receipt = OriginalFileMutationReceipts.Begin(path);
        ImageExifData? stagedExif = null;
        if (!applied)
        {
            token.ThrowIfCancellationRequested();
            using var writerLease = MetadataWriteScope.RentWriter();
            var writer = writerLease.Value;
            if (video) writer.WriteMp4CaptureDate(path, target, token);
            else writer.WriteExactExifDates(path, new Dictionary<string, string?>
            {
                ["EXIF:DateTimeOriginal"] = target
            }, token);
            stagedExif = writer.LastVerifiedExif;
            // Once a native mutation has completed, finish verification and
            // catalog publication even if a newer P0 requested a yield.
            File.SetCreationTimeUtc(path, originalCreated);
            File.SetLastWriteTimeUtc(path, originalModified);
        }
        var after = new FileInfo(path);
        var verified = stagedExif ?? reader.Read(path, CancellationToken.None);
        var dates = ExifDateMetadata.FieldsFromJson(verified.Json);
        if (!ExifDateMetadata.SameProtectedTag("EXIF:DateTimeOriginal", target,
                video ? ExplicitVideoCaptureDate(verified.Json) : dates.DateTimeOriginal) ||
            (video && !ExifDateMetadata.SameProtectedTag("EXIF:DateTimeOriginal", target, verified.DateTimeOriginal)) ||
            dates.CreateDate != current.CreateDate || dates.ModifyDate != current.ModifyDate ||
            after.LastWriteTimeUtc.Ticks != indexedTicks)
            throw new IOException("Gespeichertes Aufnahmedatum konnte nicht bestätigt werden: " + path);
        using var tx = db.BeginTransaction();
        using (var update = db.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = """
                UPDATE images SET file_size=$size,created_utc_ticks=$created
                WHERE id=$id AND local_path=$path COLLATE NOCASE
                    AND file_size=$old AND modified_utc_ticks=$ticks;
                """;
            update.Parameters.AddWithValue("$id", imageId);
            update.Parameters.AddWithValue("$path", path);
            update.Parameters.AddWithValue("$size", after.Length);
            update.Parameters.AddWithValue("$created", after.CreationTimeUtc.Ticks);
            update.Parameters.AddWithValue("$old", indexedSize);
            update.Parameters.AddWithValue("$ticks", indexedTicks);
            if (update.ExecuteNonQuery() != 1) throw new IOException("Bildkatalog wurde gleichzeitig verändert.");
        }
        using (var exif = db.CreateCommand())
        {
            exif.Transaction = tx;
            exif.CommandText = """
                INSERT INTO image_exif(image_id,source_modified_utc_ticks,source_file_size,
                    exif_json,camera_make,camera_model,date_time_original,gps_latitude,gps_longitude,gps_altitude)
                VALUES($id,$ticks,$size,$json,$make,$model,$date,$lat,$lon,$alt)
                ON CONFLICT(image_id) DO UPDATE SET source_modified_utc_ticks=excluded.source_modified_utc_ticks,
                    source_file_size=excluded.source_file_size,exif_json=excluded.exif_json,
                    camera_make=excluded.camera_make,camera_model=excluded.camera_model,
                    date_time_original=excluded.date_time_original,gps_latitude=excluded.gps_latitude,
                    gps_longitude=excluded.gps_longitude,gps_altitude=excluded.gps_altitude;
                """;
            exif.Parameters.AddWithValue("$id", imageId);
            exif.Parameters.AddWithValue("$ticks", indexedTicks);
            exif.Parameters.AddWithValue("$size", after.Length);
            exif.Parameters.AddWithValue("$json", verified.Json);
            exif.Parameters.AddWithValue("$make", (object?)verified.CameraMake ?? DBNull.Value);
            exif.Parameters.AddWithValue("$model", (object?)verified.CameraModel ?? DBNull.Value);
            exif.Parameters.AddWithValue("$date", target);
            exif.Parameters.AddWithValue("$lat", (object?)verified.GpsLatitude ?? DBNull.Value);
            exif.Parameters.AddWithValue("$lon", (object?)verified.GpsLongitude ?? DBNull.Value);
            exif.Parameters.AddWithValue("$alt", (object?)verified.GpsAltitude ?? DBNull.Value);
            exif.ExecuteNonQuery();
        }
        OriginalFileMutationReceipts.RecordConfirmed(db, tx, imageId, after);
        tx.Commit();
        receipt.Confirm(after.Length, after.LastWriteTimeUtc.Ticks);
    }
}
