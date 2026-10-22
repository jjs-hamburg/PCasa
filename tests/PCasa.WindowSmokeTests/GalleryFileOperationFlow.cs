using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class GalleryFileOperationFlow
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "PCasa-Explorer-Gallery-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "Source");
        var second = Path.Combine(root, "Destination");
        var third = Path.Combine(root, "Moved");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Directory.CreateDirectory(third);
        var original = Path.Combine(first, "test.jpg");
        var copied = Path.Combine(second, "test.jpg");
        var moved = Path.Combine(third, "test.jpg");
        var duplicate = Path.Combine(first, "test - Kopie.jpg");
        WriteJpeg(original);
        PCasa.App.MainWindow? window = null;
        try
        {
            new BackgroundScanSettings { Enabled = false }.Save();
            var db = new LocalDatabase();
            db.Initialize();
            var folderRepo = new FolderRepository(db);
            folderRepo.Add(root);
            // Establish a known, completed catalog before exercising file
            // transfers. Startup scanning has its own separate real-WPF test.
            var sourceRoot = folderRepo.GetAll().Single(item =>
                string.Equals(item.LocalPath, root,
                    StringComparison.OrdinalIgnoreCase));
            var initial = new FolderScanner(new ImageCatalogRepository(db),
                new ThumbnailCache(), folderRepo).Scan(sourceRoot);
            if (initial.Failed != 0 || CountInRoot(db, root) != 1)
                throw new Exception("Synthetic JPEG catalog seed unexpected: " +
                    $"Seen={initial.ImageFilesSeen}, Updated={initial.Updated}, " +
                    $"Unchanged={initial.Unchanged}, Failed={initial.Failed}, " +
                    $"Removed={initial.Removed}, CountAll={new ImageCatalogRepository(db).CountAll()}, " +
                    $"Pending={folderRepo.GetPendingInitialScans().Count}, " +
                    $"Root={root}, Files={string.Join(",", Directory.GetFiles(root, "*", SearchOption.AllDirectories))}");
            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            Until(window, () => CountInRoot(db, root) == 1 &&
                folderRepo.GetPendingInitialScans().Count == 0,
                "Initial source image was not indexed.");

            Transfer(window, [original], second, move: false);
            Until(window, () => File.Exists(copied) &&
                CountInRoot(db, root) == 2,
                "Copy did not materialize/index destination.");
            if (!File.Exists(original))
                throw new Exception("Copy deleted original source.");
            Console.WriteLine("PASS: Copy preserved original and indexed second photo.");

            Transfer(window, [copied], third, move: true);
            Until(window, () => !File.Exists(copied) && File.Exists(moved) &&
                CountInRoot(db, root) == 2,
                "Move did not detach old catalog image and index new path.");
            Console.WriteLine("PASS: Move removed source, kept original and updated catalog.");

            Transfer(window, [original], first, move: false);
            Until(window, () => File.Exists(duplicate) &&
                CountInRoot(db, root) == 3,
                "Same-directory copy did not automatically create distinct name.");
            Console.WriteLine("PASS: Same-folder copy never silently overwrites source.");

            window.Close();
            window = null;
            Console.WriteLine("RESULT: Real WPF gallery copy/move/catalog tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: Explorer gallery file operations: " + ex);
            return 1;
        }
        finally
        {
            try { window?.Close(); } catch { }
            try
            {
                var repository = new FolderRepository(new LocalDatabase());
                foreach (var monitored in repository.GetAll().Where(item =>
                    string.Equals(item.LocalPath, root,
                        StringComparison.OrdinalIgnoreCase)))
                    repository.Remove(monitored.Id);
            }
            catch { }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static int CountInRoot(LocalDatabase db, string path)
        => new ImageCatalogRepository(db).GetPhotoFolders()
            .Where(folder => FolderExclusionRules.IsAtOrBelow(folder.LocalPath, path))
            .Sum(folder => folder.ImageCount);

    private static void Transfer(PCasa.App.MainWindow window,
        string[] paths, string target, bool move)
    {
        var method = typeof(PCasa.App.MainWindow).GetMethod(
            "TransferGalleryFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("Shared gallery file-operation path was not found.");
        var task = (Task?)method.Invoke(window, [paths, target, move])
            ?? throw new Exception("Transfer method did not return completion task.");
        Until(window, () => task.IsCompleted, "File transfer never completed.");
        task.GetAwaiter().GetResult();
    }

    private static void Until(Window window, Func<bool> ready, string message)
    {
        var limit = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < limit)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (ready()) return;
            Thread.Sleep(20);
        }
        throw new TimeoutException(message);
    }

    private static void WriteJpeg(string path)
    {
        const int width = 48, height = 32;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 110;
            pixels[i + 2] = 220;
            pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
