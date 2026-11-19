using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class PsdPreviewCacheRegression
{
    public static void Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(),
            "PCasa-PsdOrientation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var photos = Path.Combine(sandbox, "photos");
            Directory.CreateDirectory(photos);
            var psd = Path.Combine(photos, "portrait.psd");
            var jpeg = Path.Combine(photos, "landscape.jpg");
            var legacyPsdJpeg = Path.Combine(sandbox, "wrong-embedded-preview.jpg");
            var oldPack = "old-preview.pack";
            CreateMergedRgbPsd(psd, width: 24, height: 48);
            SaveJpeg(jpeg, 48, 24);
            // A real old PSD catalog can point at a landscape, cropped
            // embedded thumbnail while the Photoshop merged image is portrait.
            SaveJpeg(legacyPsdJpeg, 48, 24);

            var db = new LocalDatabase(Path.Combine(sandbox, "test.db"));
            db.Initialize();
            var roots = new FolderRepository(db);
            roots.Add(photos);
            var root = roots.GetAll().Single();
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO photo_folders(monitored_folder_id,local_path,
                        canonical_path,display_name,indexed_utc)
                    VALUES ($root,$folder,$folder,'photos',$now);
                    INSERT INTO images(folder_id,photo_folder_id,local_path,
                        canonical_path,file_name,extension,file_size,
                        modified_utc_ticks,width,height,thumbnail_path,indexed_utc)
                    VALUES ($root,(SELECT id FROM photo_folders WHERE local_path=$folder),
                        $psd,$psd,'portrait.psd','.psd',100,777,24,48,$wrong,$now);
                    INSERT INTO images(folder_id,photo_folder_id,local_path,
                        canonical_path,file_name,extension,file_size,
                        modified_utc_ticks,width,height,thumbnail_path,indexed_utc)
                    VALUES ($root,(SELECT id FROM photo_folders WHERE local_path=$folder),
                        $jpeg,$jpeg,'landscape.jpg','.jpg',100,777,48,24,$jpeg,$now);
                    INSERT INTO thumbnail_entries(image_id,tier,pack_file,byte_offset,
                        byte_length,pixel_width,pixel_height,source_modified_utc_ticks)
                    SELECT id,192,$pack,0,9,
                           CASE extension WHEN '.psd' THEN 48 ELSE 48 END,
                           CASE extension WHEN '.psd' THEN 24 ELSE 24 END,777
                    FROM images;
                    DELETE FROM preview_cache_migrations
                    WHERE name='psd-merged-thumbnail-v2';
                    """;
                command.Parameters.AddWithValue("$root", root.Id);
                command.Parameters.AddWithValue("$folder", photos);
                command.Parameters.AddWithValue("$psd", psd);
                command.Parameters.AddWithValue("$jpeg", jpeg);
                command.Parameters.AddWithValue("$wrong", legacyPsdJpeg);
                command.Parameters.AddWithValue("$pack", oldPack);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }

            // Upgrade an old database: only the PSD tier is invalidated.
            db.Initialize();
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT i.extension, COUNT(te.image_id) AS tiers,
                           COUNT(j.image_id) AS queued
                    FROM images i
                    LEFT JOIN thumbnail_entries te ON te.image_id=i.id
                    LEFT JOIN browse_cache_jobs j ON j.image_id=i.id
                    GROUP BY i.id ORDER BY i.extension;
                    """;
                using var reader = command.ExecuteReader();
                var foundPsd = false;
                var foundJpeg = false;
                while (reader.Read())
                {
                    var ext = reader.GetString(0);
                    if (ext == ".psd")
                    {
                        foundPsd = true;
                        if (reader.GetInt64(1) != 0 || reader.GetInt64(2) != 1)
                            throw new Exception("PSD old pack not invalidated/queued.");
                    }
                    else if (ext == ".jpg")
                    {
                        foundJpeg = true;
                        if (reader.GetInt64(1) != 1)
                            throw new Exception("Unrelated JPEG packed tier was invalidated.");
                    }
                }
                if (!foundPsd || !foundJpeg)
                    throw new Exception("Synthetic catalog entries missing.");
            }
            db.Initialize(); // Restart must not repeat migration.

            // Until rebuilt, never display the embedded wrong JPEG from the
            // legacy source. The gallery is allowed a placeholder instead.
            var index = new BrowseCacheRepository(db).GetGalleryIndex(192);
            var missingPsd = index.Single(entry => entry.FileName == "portrait.psd");
            if (missingPsd.LegacyThumbnailPath is not null)
                throw new Exception("Gallery still exposes the wrong PSD JPEG fallback.");

            // Rebuild via the ORDINARY packed-thumbnail path, not a separate
            // PSD cache. The resulting tier must match the native portrait
            // composite orientation, regardless of the legacy JPEG.
            var result = new BrowseCacheBuilder(new BrowseCacheRepository(db),
                new BrowseThumbnailProfileRepository(db)).BuildMissing();
            if (result.FailedImages != 0)
                throw new Exception("PSD composite preview rebuild failed.");
            var rebuilt = new BrowseCacheRepository(db).GetIndex(192)
                .Single(entry => entry.FileName == "portrait.psd");
            if (rebuilt.PixelWidth >= rebuilt.PixelHeight)
                throw new Exception("New PSD thumbnail retained the landscape legacy orientation.");
            using var decoded = File.OpenRead(PsdCompositePreview.Resolve(psd));
            var frame = BitmapFrame.Create(decoded,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth >= frame.PixelHeight)
                throw new Exception("PSD full merged composite is not portrait.");
            Console.WriteLine(
                "PASS: PSD cache migration touches only PSD, never displays stale JPEG and rebuilds portrait merged-composite tiers.");
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CreateMergedRgbPsd(string path, int width, int height)
    {
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write("8BPS"u8);
        WriteU16(writer, 1); // PSD version
        writer.Write(new byte[6]);
        WriteU16(writer, 3); // RGB channels
        WriteU32(writer, (uint)height);
        WriteU32(writer, (uint)width);
        WriteU16(writer, 8);
        WriteU16(writer, 3); // RGB colour mode
        WriteU32(writer, 0); // Color-mode section length
        WriteU32(writer, 0); // Image-resources section length
        WriteU32(writer, 0); // Layers/masks section length
        WriteU16(writer, 0); // Raw merged composite
        for (var channel = 0; channel < 3; channel++)
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    writer.Write((byte)(channel == 0 ? x * 5 :
                        channel == 1 ? y * 4 : 60));
    }

    private static void WriteU16(BinaryWriter writer, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void WriteU32(BinaryWriter writer, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        writer.Write(buffer);
    }

    private static void SaveJpeg(string path, int width, int height)
    {
        var buffer = new byte[checked(width * height * 4)];
        for (var i = 0; i < buffer.Length; i += 4)
        { buffer[i] = 120; buffer[i + 1] = 100; buffer[i + 2] = 30; buffer[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,
            null,buffer,width*4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
