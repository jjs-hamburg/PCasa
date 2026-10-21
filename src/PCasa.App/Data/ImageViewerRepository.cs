using Microsoft.Data.Sqlite;
using PCasa.App.Models;

namespace PCasa.App.Data;

public sealed class ImageViewerRepository
{
    private readonly LocalDatabase _database;

    public ImageViewerRepository(LocalDatabase database)
    {
        _database = database;
    }

    public GalleryImage? Get(long imageId)
    {
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
            WHERE id = $imageId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$imageId", imageId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new GalleryImage(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7));
    }
}
