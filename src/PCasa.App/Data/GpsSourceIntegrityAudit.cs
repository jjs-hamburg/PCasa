using System.IO;
using PCasa.App.Services;

namespace PCasa.App.Data;

/// <summary>
/// Read-only catalog provenance audit. The original-file correction is owned
/// exclusively by FolderScanner.ScanFile through the existing scheduler.
/// Never infer a coordinate from thumbnails, neighbours or another EXIF row.
/// </summary>
public sealed record GpsSourceAuditPage(
    long LastImageId, int Checked,
    IReadOnlyList<string> MismatchedPaths,
    int UnverifiedGpsRows = 0);

public sealed class GpsSourceIntegrityAudit(LocalDatabase database)
{
    public GpsSourceAuditPage ReadPage(
        long monitoredRootId, long afterImageId, int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.local_path, x.exif_json,
                   CASE WHEN json_valid(x.exif_json)=1
                     THEN json_extract(x.exif_json,'$.SourceFile')
                     ELSE NULL END,
                   x.gps_latitude, x.gps_longitude
              FROM images i JOIN image_exif x ON x.image_id=i.id
             WHERE i.folder_id=$root AND i.id>$after
             ORDER BY i.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$root", monitoredRootId);
        command.Parameters.AddWithValue("$after", afterImageId);
        command.Parameters.AddWithValue("$limit", pageSize);
        var last = afterImageId;
        var checkedCount = 0;
        var mismatched = new List<string>();
        var unverifiedGpsRows = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = reader.GetInt64(0);
            checkedCount++;
            var gpsInCatalog = !reader.IsDBNull(4) && !reader.IsDBNull(5);
            var expectedPath = reader.GetString(1);
            // A missing SourceFile does not PROVE that the coordinate belongs
            // to a different original. Never erase legacy/manual GPS from
            // the database solely because provenance was not stored yet.
            if (reader.IsDBNull(3))
            {
                if (gpsInCatalog) unverifiedGpsRows++;
                continue;
            }
            if (!ImageExifReader.IsSameSourcePath(reader.GetString(3), expectedPath))
            {
                mismatched.Add(expectedPath);
                continue;
            }
            // Even with correct provenance, older catalog code could have
            // materialized another photo's latitude/longitude columns.
            // The embedded native EXIF JSON is independent evidence: repair
            // via the existing scanner only when it gives a complete pair.
            if (reader.IsDBNull(2)) continue;
            try
            {
                var native = ImageExifReader.Parse(reader.GetString(2));
                if (native.GpsLatitude is not double lat ||
                    native.GpsLongitude is not double lon)
                {
                    if (gpsInCatalog) unverifiedGpsRows++;
                    continue;
                }
                if (reader.IsDBNull(4) || reader.IsDBNull(5) ||
                    Math.Abs(reader.GetDouble(4) - lat) > 0.000001 ||
                    Math.Abs(reader.GetDouble(5) - lon) > 0.000001)
                    mismatched.Add(expectedPath);
            }
            catch (System.Text.Json.JsonException)
            {
                if (gpsInCatalog) unverifiedGpsRows++;
            }
            catch (InvalidDataException)
            {
                if (gpsInCatalog) unverifiedGpsRows++;
            }
        }
        return new GpsSourceAuditPage(last, checkedCount, mismatched,
            unverifiedGpsRows);
    }
}
