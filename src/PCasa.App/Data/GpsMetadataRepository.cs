using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using PCasa.App.Services;

namespace PCasa.App.Data;

/// <summary>
/// One scheduled GPS edit step owns one original file. The source file is the
/// authority: re-read and verify native metadata before changing SQLite.
/// Idempotent on journal replay after a write succeeded but checkpoint failed.
/// </summary>
public sealed class GpsMetadataRepository(LocalDatabase database)
{
    public enum EditMode { MissingOnly, Replace, Remove }

    public void Apply(long imageId, double? latitude, double? longitude,
        EditMode mode, CancellationToken token, Action<string>? starting = null,
        string? packageKey = null)
    {
        token.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        long readMilliseconds = 0, writeMilliseconds = 0, verifyMilliseconds = 0;
        using var db = database.OpenConnection();
        using var select = db.CreateCommand();
        select.CommandText = """
            SELECT local_path, file_size, modified_utc_ticks
              FROM images WHERE id=$id;
            """;
        select.Parameters.AddWithValue("$id", imageId);
        string path;
        long indexedSize, indexedTicks;
        using (var row = select.ExecuteReader())
        {
            if (!row.Read()) return; // Deleted image: no longer a valid target.
            path = row.GetString(0);
            indexedSize = row.GetInt64(1);
            indexedTicks = row.GetInt64(2);
        }
        starting?.Invoke(path);
        using (SourceFileLocks.Enter(path, token))
        {
            token.ThrowIfCancellationRequested();
            if (packageKey is not null)
            {
                // The durable journal sequence is the ONE P0 ordering
                // authority. Completed newer edits still supersede older
                // packages that resume after NAS disconnection or yield.
                using var superseded = db.CreateCommand();
                superseded.CommandText = """
                    SELECT EXISTS(
                      SELECT 1 FROM scheduler_packages newer
                      JOIN scheduler_packages original
                        ON original.package_key=$key
                     WHERE newer.package_type='gps-edit-v1'
                       AND newer.sequence > original.sequence
                       AND newer.state NOT IN ('canceled','failed')
                       AND EXISTS (
                         SELECT 1 FROM json_each(newer.payload_json,'$.ImageIds')
                          WHERE CAST(value AS INTEGER)=$id
                       )
                    );
                    """;
                superseded.Parameters.AddWithValue("$key", packageKey);
                superseded.Parameters.AddWithValue("$id", imageId);
                if (Convert.ToInt32(superseded.ExecuteScalar()) != 0)
                    return;
            }
            // Never edit a source which another process changed after indexing.
            var before = new FileInfo(path);
            if (!before.Exists)
                throw new FileNotFoundException("GPS-Originaldatei nicht verfügbar.", path);

            using var reader = new ImageExifReader();
            var source = reader.Read(path, token);
            readMilliseconds = elapsed.ElapsedMilliseconds;
            var alreadyHasGps = source.GpsLatitude.HasValue &&
                source.GpsLongitude.HasValue;
            // A journal replay may resume after ExifTool replaced the file
            // but before SQLite recorded its new byte length. In that exact
            // case the VERIFIED requested coordinates permit reconciliation.
            var alreadyApplied = mode switch
            {
                EditMode.MissingOnly => alreadyHasGps &&
                    Same(source.GpsLatitude, latitude) &&
                    Same(source.GpsLongitude, longitude),
                EditMode.Replace => Same(source.GpsLatitude, latitude) &&
                    Same(source.GpsLongitude, longitude),
                EditMode.Remove => !alreadyHasGps,
                _ => false
            };
            if (before.LastWriteTimeUtc.Ticks != indexedTicks ||
                (before.Length != indexedSize && !alreadyApplied))
                throw new IOException("Datei wurde außerhalb von PCasa verändert: " + path);
            var shouldWrite = mode switch
            {
                EditMode.MissingOnly => !alreadyHasGps,
                EditMode.Replace => !Same(source.GpsLatitude, latitude) ||
                    !Same(source.GpsLongitude, longitude),
                EditMode.Remove => alreadyHasGps,
                _ => throw new InvalidDataException("Unbekannter GPS-Auftrag.")
            };
            if (shouldWrite)
            {
                token.ThrowIfCancellationRequested();
                using var writer = new ExifToolKeywordWriter.Session();
                writer.WriteGps(path,
                    mode == EditMode.Remove ? null : latitude,
                    mode == EditMode.Remove ? null : longitude, token);
                writeMilliseconds = elapsed.ElapsedMilliseconds - readMilliseconds;
                // Never cancel between the physical write and catalog commit.
                if (File.GetLastWriteTimeUtc(path).Ticks != indexedTicks)
                    File.SetLastWriteTimeUtc(path,
                        new DateTime(indexedTicks, DateTimeKind.Utc));
            }
            var after = new FileInfo(path);
            var verified = reader.Read(path, CancellationToken.None);
            verifyMilliseconds = elapsed.ElapsedMilliseconds -
                readMilliseconds - writeMilliseconds;
            if (shouldWrite && (mode == EditMode.Remove
                ? verified.GpsLatitude.HasValue || verified.GpsLongitude.HasValue
                : !Same(verified.GpsLatitude, latitude) ||
                  !Same(verified.GpsLongitude, longitude)))
                throw new IOException("GPS-Daten konnten nicht in der Originaldatei bestätigt werden: " + path);
            using var tx = db.BeginTransaction();
            using (var update = db.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE images SET file_size=$size
                     WHERE id=$id AND local_path=$path COLLATE NOCASE
                       AND file_size=$oldSize AND modified_utc_ticks=$ticks;
                    """;
                update.Parameters.AddWithValue("$size", after.Length);
                update.Parameters.AddWithValue("$id", imageId);
                update.Parameters.AddWithValue("$path", path);
                update.Parameters.AddWithValue("$oldSize", indexedSize);
                update.Parameters.AddWithValue("$ticks", indexedTicks);
                if (update.ExecuteNonQuery() != 1)
                    throw new IOException("GPS-Katalog wurde gleichzeitig verändert; Wiederholung nötig.");
            }
            using (var exif = db.CreateCommand())
            {
                exif.Transaction = tx;
                exif.CommandText = """
                    INSERT INTO image_exif(image_id,source_modified_utc_ticks,
                        source_file_size,exif_json,camera_make,camera_model,
                        date_time_original,gps_latitude,gps_longitude,gps_altitude)
                    VALUES($id,$ticks,$size,$json,$make,$model,$date,$lat,$lon,$alt)
                    ON CONFLICT(image_id) DO UPDATE SET
                        source_modified_utc_ticks=excluded.source_modified_utc_ticks,
                        source_file_size=excluded.source_file_size,
                        exif_json=excluded.exif_json,
                        camera_make=excluded.camera_make,
                        camera_model=excluded.camera_model,
                        date_time_original=excluded.date_time_original,
                        gps_latitude=excluded.gps_latitude,
                        gps_longitude=excluded.gps_longitude,
                        gps_altitude=excluded.gps_altitude;
                    """;
                exif.Parameters.AddWithValue("$id", imageId);
                exif.Parameters.AddWithValue("$ticks", after.LastWriteTimeUtc.Ticks);
                exif.Parameters.AddWithValue("$size", after.Length);
                exif.Parameters.AddWithValue("$json", verified.Json);
                exif.Parameters.AddWithValue("$make", (object?)verified.CameraMake ?? DBNull.Value);
                exif.Parameters.AddWithValue("$model", (object?)verified.CameraModel ?? DBNull.Value);
                exif.Parameters.AddWithValue("$date", (object?)verified.DateTimeOriginal ?? DBNull.Value);
                exif.Parameters.AddWithValue("$lat", (object?)verified.GpsLatitude ?? DBNull.Value);
                exif.Parameters.AddWithValue("$lon", (object?)verified.GpsLongitude ?? DBNull.Value);
                exif.Parameters.AddWithValue("$alt", (object?)verified.GpsAltitude ?? DBNull.Value);
                exif.ExecuteNonQuery();
            }
            tx.Commit();
            PCasaDiagnostics.WriteMessage("GPS.Edit.Timing",
                $"ImageId={imageId}; Changed={shouldWrite}; Bytes={after.Length}; " +
                $"ReadMs={readMilliseconds}; NativeWriteMs={writeMilliseconds}; " +
                $"VerifyMs={verifyMilliseconds}; TotalMs={elapsed.ElapsedMilliseconds}");
        }
    }

    private static bool Same(double? a, double? b)
        => a is null && b is null ||
           a is double x && b is double y && Math.Abs(x - y) < 0.000001;
}
