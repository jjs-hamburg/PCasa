using PCasa.App.Services;

namespace PCasa.App.Data;

/// <summary>
/// Read-only catalog provenance audit. The original-file correction is owned
/// exclusively by FolderScanner.ScanFile through the existing scheduler.
/// Never infer a coordinate from thumbnails, neighbours or another EXIF row.
/// </summary>
public sealed record GpsSourceAuditPage(
    long LastImageId, int Checked,
    IReadOnlyList<string> MismatchedPaths);

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
            SELECT i.id, i.local_path,
                   CASE WHEN json_valid(x.exif_json)=1
                     THEN json_extract(x.exif_json,'$.SourceFile')
                     ELSE NULL END
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
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = reader.GetInt64(0);
            checkedCount++;
            if (reader.IsDBNull(2)) continue; // Legacy provenance is unknown.
            var expectedPath = reader.GetString(1);
            var source = reader.GetString(2);
            if (!ImageExifReader.IsSameSourcePath(source, expectedPath))
                mismatched.Add(expectedPath);
        }
        return new GpsSourceAuditPage(last, checkedCount, mismatched);
    }
}
