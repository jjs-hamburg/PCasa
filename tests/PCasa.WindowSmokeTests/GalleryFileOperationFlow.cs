using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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

            // Two monitored source folders contain 'test.jpg'. Moving both
            // into one empty destination must keep BOTH photos. The second
            // planned name must not offer to overwrite the first planned file.
            var batch = Path.Combine(root, "Batch");
            Directory.CreateDirectory(batch);
            Transfer(window, [original, moved], batch, move: true);
            Until(window, () =>
                !File.Exists(original) && !File.Exists(moved) &&
                File.Exists(Path.Combine(batch, "test.jpg")) &&
                File.Exists(Path.Combine(batch, "test - Kopie.jpg")) &&
                CountInRoot(db, root) == 3,
                "Two same-name sources did not survive a single batch move.");
            Console.WriteLine("PASS: Batch move preserves both same-name source photos.");

            var probe = typeof(PCasa.App.MainWindow).GetMethod(
                "ConfirmedMissingExternalMove",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("External move reachability guard is missing.");
            bool Missing(string path) => (bool)(probe.Invoke(null, [path])
                ?? throw new Exception("Missing-file probe returned no result."));
            if (Missing(Path.Combine(batch, "test.jpg")) ||
                !Missing(Path.Combine(batch, "missing.jpg")) ||
                Missing(Path.Combine(root, "UnreachableParent", "missing.jpg")))
                throw new Exception("External move treats unavailable parents as deleted photos.");
            Console.WriteLine("PASS: External move reconciles only confirmed missing files.");

            // Verify that an ordinary empty-gallery click clears Ctrl+A's
            // selection through the actual MainWindow selection handler.
            var surface = (PCasa.App.Controls.BrowseGallerySurface)window.FindName("BrowseSurface");
            var selectedField = typeof(PCasa.App.MainWindow).GetField(
                "_selectedImageIds", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Gallery selection state missing.");
            var selectedIds = (HashSet<long>)selectedField.GetValue(window)!;
            selectedIds.Add(987654321L);
            var clear = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseSurface_EmptyAreaClicked", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Empty gallery click handler missing.");
            clear.Invoke(window, [surface, EventArgs.Empty]);
            if (selectedIds.Count != 0)
                throw new Exception("Clicking empty gallery failed to clear selection.");
            Console.WriteLine("PASS: Clicking gallery background clears multi-selection.");

            // The actual tree has nested WPF TreeViewItem containers. An
            // eligible folder must resolve as the destination itself, not as
            // its top-level drive or monitoring ancestor.
            var tree = (TreeView)window.FindName("FoldersTree");
            var getDropTarget = typeof(PCasa.App.MainWindow).GetMethod(
                "TreeDropTargetFromElement", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Folder-tree hit-test resolver missing.");
            var nested = FindFolderItems(tree)
                .FirstOrDefault(item => string.Equals(
                    getDropTarget.Invoke(window, [item]) as string,
                    first, StringComparison.OrdinalIgnoreCase));
            if (nested is null)
                throw new Exception("Nested folder is not an eligible drop target.");
            if (!nested.AllowDrop)
                throw new Exception("Nested folder does not accept WPF drops.");
            Console.WriteLine("PASS: Nested folder resolves to its own drop destination.");

            // Window resizing must re-anchor the progress window after
            // layout rather than leave its coordinates at the old size.
            var progressWindow = new PCasa.App.BrowseCacheProgressWindow { Owner = window };
            progressWindow.Show();
            Until(window, () => Math.Abs(progressWindow.Left -
                    (window.Left + window.ActualWidth - progressWindow.Width - 12)) < 3,
                "Progress window did not align to initial owner right edge.");
            window.Width += 110;
            Until(window, () => Math.Abs(progressWindow.Left -
                    (window.Left + window.ActualWidth - progressWindow.Width - 12)) < 3,
                "Progress window did not follow resized owner.");
            progressWindow.Finish();
            Console.WriteLine("PASS: Progress dialog follows owner window resizing.");

            if (typeof(PCasa.App.MainWindow).GetMethod("SHObjectProperties",
                    BindingFlags.NonPublic | BindingFlags.Static) is null)
                throw new Exception("Native Windows properties invocation is missing.");
            Console.WriteLine("PASS: File properties use native Windows shell API.");

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

    private static IEnumerable<TreeViewItem> FindFolderItems(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var nested in FindFolderItems(item))
                yield return nested;
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
