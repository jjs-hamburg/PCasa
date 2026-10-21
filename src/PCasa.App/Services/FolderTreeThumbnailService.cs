using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Windows.Media.Imaging;
using PCasa.App.Data;

namespace PCasa.App.Services;

public static class FolderTreeThumbnailService
{
    private static readonly ConcurrentDictionary<long, Task<BitmapSource?>> Cache = new();
    private static readonly SemaphoreSlim LoadGate = new(4, 4);

    public static Task<BitmapSource?> GetAsync(long photoFolderId)
        => Cache.GetOrAdd(photoFolderId, LoadAsync);

    private static async Task<BitmapSource?> LoadAsync(long photoFolderId)
    {
        await LoadGate.WaitAsync();
        try
        {
            return await Task.Run(() => Load(photoFolderId));
        }
        finally
        {
            LoadGate.Release();
        }
    }

    private static BitmapSource? Load(long photoFolderId)
    {
        try
        {
            var database = new LocalDatabase();
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT thumbnail_path
                FROM images
                WHERE photo_folder_id = $photoFolderId
                ORDER BY file_name COLLATE NOCASE, canonical_path COLLATE NOCASE
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$photoFolderId", photoFolderId);

            var path = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            var thumbnailPath = path!;
            return new ThumbnailImageLoader().Load(
                thumbnailPath,
                decodePixelWidth: 36,
                CancellationToken.None);
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
