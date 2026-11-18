using System.IO;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class RenameFlow
{
    public static void Run()
    {
        var sandbox = Path.Combine(Path.GetTempPath(),
            "PCasa-Rename-" + Guid.NewGuid().ToString("N"));
        var pictures = Path.Combine(sandbox, "Pictures");
        var albums = Path.Combine(pictures, "Albums");
        var child = Path.Combine(albums, "2018");
        Directory.CreateDirectory(child);
        var photo = Path.Combine(child, "old.jpg");
        File.WriteAllBytes(photo, [1,2,3,4]);
        try
        {
            var database = new LocalDatabase(Path.Combine(sandbox, "test.db"));
            database.Initialize();
            var roots = new FolderRepository(database);
            roots.Add(pictures);
            var root = roots.GetAll().Single();
            var catalog = new ImageCatalogRepository(database);
            var thumbnail = Path.Combine(sandbox, "cached-preview.jpg");
            File.WriteAllBytes(thumbnail, [7,8,9]);
            var pack = "packed-preview-" + Guid.NewGuid().ToString("N") + ".pack";
            using (var db = database.OpenConnection())
            using (var command = db.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO photo_folders (monitored_folder_id,local_path,
                        canonical_path,display_name,indexed_utc)
                    VALUES ($root,$dir,$dir,'2018',$now);
                    INSERT INTO images (folder_id,photo_folder_id,local_path,canonical_path,
                        file_name,extension,file_size,modified_utc_ticks,width,height,
                        thumbnail_path,indexed_utc)
                    VALUES ($root,
                        (SELECT id FROM photo_folders WHERE canonical_path=$dir),
                        $image,$image,'old.jpg','.jpg',4,13579,32,24,$thumbnail,$now);
                    INSERT INTO thumbnail_entries (image_id,tier,pack_file,byte_offset,
                        byte_length,pixel_width,pixel_height,source_modified_utc_ticks)
                    VALUES ((SELECT id FROM images WHERE canonical_path=$image),
                        192,$pack,0,9,32,24,13579);
                    INSERT INTO image_tags(image_id,keywords,source_modified_utc_ticks)
                    VALUES ((SELECT id FROM images WHERE canonical_path=$image),'retain-me',13579);
                    INSERT INTO created_photo_directories(monitored_folder_id,local_path,created_utc)
                    VALUES($root,$albums,$now);
                    """;
                command.Parameters.AddWithValue("$root", root.Id);
                command.Parameters.AddWithValue("$dir", child);
                command.Parameters.AddWithValue("$image", photo);
                command.Parameters.AddWithValue("$thumbnail", thumbnail);
                command.Parameters.AddWithValue("$albums", albums);
                command.Parameters.AddWithValue("$pack", pack);
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
            long id;
            using (var db = database.OpenConnection())
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM images WHERE local_path=$path";
                cmd.Parameters.AddWithValue("$path", photo);
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            // The physical directory rename and its DB rebase are independent
            // steps; every descendant path change is one SQLite transaction.
            var renamedAlbums = Path.Combine(pictures, "Renamed");
            var rename = new CatalogRenameService(database);
            rename.InvalidateScans(albums);
            Directory.Move(albums, renamedAlbums);
            rename.RebaseDirectory(albums, renamedAlbums, albums, renamedAlbums);
            var renamedImage = Path.Combine(renamedAlbums, "2018", "old.jpg");
            VerifySameImage(database, id, renamedImage, thumbnail, pack);
            using (var db = database.OpenConnection())
            using (var q = db.CreateCommand())
            {
                q.CommandText = "SELECT COUNT(*) FROM created_photo_directories WHERE local_path=$path";
                q.Parameters.AddWithValue("$path", renamedAlbums);
                if (Convert.ToInt32(q.ExecuteScalar()) != 1)
                    throw new Exception("Created subdirectory path lost on rename.");
            }

            // Rename the MONITORED ROOT too, preserving its root ID and
            // every cached thumbnail without re-indexing.
            var rootRenamed = Path.Combine(sandbox, "MyPictures");
            rename.InvalidateScans(pictures);
            Directory.Move(pictures, rootRenamed);
            rename.RebaseDirectory(pictures, rootRenamed, pictures, rootRenamed);
            if (roots.GetAll().Single().Id != root.Id ||
                !string.Equals(roots.GetAll().Single().LocalPath, rootRenamed,
                    StringComparison.OrdinalIgnoreCase))
                throw new Exception("Monitored root identity did not survive rename.");
            renamedImage = Path.Combine(rootRenamed, "Renamed", "2018", "old.jpg");
            VerifySameImage(database, id, renamedImage, thumbnail, pack);

            // A file rename reuses the production relocation journal and
            // catalog scan session; the same image ID and cache survive it.
            var destination = Path.Combine(Path.GetDirectoryName(renamedImage)!, "new.jpg");
            var liveRoot = roots.GetAll().Single();
            var intent = Guid.NewGuid().ToString("N");
            catalog.BeginRelocationIntent(intent, id, renamedImage, destination, liveRoot.Id);
            File.Move(renamedImage, destination);
            if (!catalog.RelocateImage(id, renamedImage, destination, liveRoot,
                    relocationIntentId: intent))
                throw new Exception("The catalog rejected a same-folder file rename.");
            VerifySameImage(database, id, destination, thumbnail, pack);
            if (catalog.PendingRelocationIds().Count != 0)
                throw new Exception("Completed file rename left an unresolved move intent.");

            Console.WriteLine(
                "PASS: Image, subtree and monitored-root Rename preserve image IDs, tags, photo folders and packed thumbnail tiers.");
        }
        finally
        {
            try { Directory.Delete(sandbox, true); }
            catch (IOException) { }
        }
    }

    private static void VerifySameImage(LocalDatabase database, long id,
        string expectedPath, string thumb, string pack)
    {
        using var db = database.OpenConnection();
        using var c = db.CreateCommand();
        c.CommandText = """
            SELECT i.id, i.local_path, i.thumbnail_path, i.file_size, i.modified_utc_ticks,
                   t.pack_file, tg.keywords
            FROM images i
            JOIN thumbnail_entries t ON t.image_id=i.id AND t.tier=192
            JOIN image_tags tg ON tg.image_id=i.id
            WHERE i.id=$id;
            """;
        c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader();
        if (!r.Read() || r.GetInt64(0) != id ||
            !string.Equals(r.GetString(1), expectedPath,
                StringComparison.OrdinalIgnoreCase) ||
            r.GetString(2) != thumb || r.GetInt64(3) != 4 ||
            r.GetInt64(4) != 13579 || r.GetString(5) != pack ||
            r.GetString(6) != "retain-me" || !File.Exists(expectedPath))
            throw new Exception("Rename replaced the image identity or its cached content.");
    }
}
