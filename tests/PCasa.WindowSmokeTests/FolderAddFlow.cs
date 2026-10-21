using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class FolderAddFlow
{
    public static int Run()
    {
        var rootPath = Path.Combine(Path.GetTempPath(),
            "PCasa-PriorityAdd-" + Guid.NewGuid().ToString("N"));
        var initialRoot = Path.Combine(rootPath, "Added");
        var afterRestart = Path.Combine(rootPath, "AfterRestart");
        Directory.CreateDirectory(initialRoot);
        Directory.CreateDirectory(afterRestart);
        WriteJpeg(Path.Combine(initialRoot, "first.jpg"));
        WriteJpeg(Path.Combine(afterRestart, "second.jpg"));

        PCasa.App.MainWindow? window = null;
        using var lockTaken = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        Task? blocked = null;
        try
        {
            var db = new LocalDatabase();
            db.Initialize();
            var roots = new FolderRepository(db);
            new BackgroundScanSettings { Enabled = false }.Save();

            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            Until(window, () => window.FindName("FoldersTree") is TreeView,
                "MainWindow never displayed its folder tree.");

            var tree = (TreeView)window.FindName("FoldersTree");
            var surface = (BrowseGallerySurface)window.FindName("BrowseSurface");
            var status = (TextBlock)window.FindName("StatusText");

            // The current scan has intentionally not started yet. A simulated
            // long running pack writer holds its short index lock, proving
            // the user's Add decision and tree insertion do not wait for it.
            var cacheLock = typeof(CatalogRemovalService)
                .GetField("CacheLock", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) ?? throw new Exception("Cache coordination lock missing.");
            blocked = Task.Run(() =>
            {
                lock (cacheLock)
                {
                    lockTaken.Set();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(25)))
                        throw new TimeoutException("The simulated writer was not released.");
                }
            });
            if (!lockTaken.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Blocking cache writer never started.");

            var addMethod = typeof(PCasa.App.MainWindow).GetMethod(
                "AddFolderFromManagerAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("Folder manager no longer shares the add coordinator.");
            var addOperation = addMethod.Invoke(window, new object[] { initialRoot }) as Task
                ?? throw new Exception("Shared folder add coordinator did not return a task.");

            Until(window, () => addOperation.IsCompleted,
                "Folder Add waited behind cache preparation.");
            addOperation.GetAwaiter().GetResult();

            if (!Walk(tree).Any(item => SamePath(PathOf(item), initialRoot)))
                throw new Exception("Newly monitored root was not immediately visible.");
            if (roots.GetAll().All(root => !SamePath(root.LocalPath, initialRoot)) ||
                roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, initialRoot)))
                throw new Exception("Added root/first scan was not persisted before import.");
            if (new ImageCatalogRepository(db).CountAll() != 0)
                throw new Exception("The test failed to hold the first image scan pending.");
            if (!status.Text.Contains("Einlesen", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Status did not acknowledge the user-requested import.");
            Console.WriteLine("PASS: Actual manager add coordinator exposes folder immediately while scanner is blocked.");

            releaseLock.Set();
            blocked.GetAwaiter().GetResult();
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 1 &&
                roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, initialRoot)),
                "Priority initial scan did not finish or clear its durable request.");
            var firstFolder = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, initialRoot));
            Until(window, () => surface.GetFolderOffset(firstFolder.Id).HasValue,
                "Newly imported picture did not appear in gallery.");
            Console.WriteLine("PASS: Priority scanner imports the photo and updates the gallery even with monitoring off.");

            window.Close();
            window = null;

            // Re-open PCasa with a second persisted, unfinished user selection.
            // No watcher event or in-memory task survives. Startup must
            // independently find and execute the one durable queued request.
            roots.Add(afterRestart);
            if (roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, afterRestart)))
                throw new Exception("Second root has no pending initial import.");

            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            tree = (TreeView)window.FindName("FoldersTree");
            Until(window, () =>
                Walk(tree).Any(item => SamePath(PathOf(item), afterRestart)),
                "Pending root was missing from tree after restart.");
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 2 &&
                roots.GetPendingInitialScans().Count == 0,
                "Restart failed to execute the durable first-import request.");
            Console.WriteLine("PASS: Restart resumes persisted user import without background monitoring.");
            window.Close();
            window = null;
            Console.WriteLine("RESULT: Real WPF folder-add, import and restart checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: WPF folder add / scheduler integration: " + error);
            return 1;
        }
        finally
        {
            releaseLock.Set();
            try { blocked?.GetAwaiter().GetResult(); } catch { }
            try { window?.Close(); } catch { }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(rootPath, recursive: true); } catch { }
        }
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static string? PathOf(TreeViewItem item) =>
        item.Tag?.GetType().GetProperty("FullPath")?.GetValue(item.Tag) as string;

    private static IEnumerable<TreeViewItem> Walk(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in Walk(item)) yield return child;
        }
    }

    private static void Until(Window window, Func<bool> ready, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (ready()) return;
            Thread.Sleep(35);
        }
        throw new TimeoutException(message);
    }

    private static void WriteJpeg(string path)
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 25;
            pixels[i + 1] = 116;
            pixels[i + 2] = 192;
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(64, 64, 96, 96,
            PixelFormats.Bgr32, null, pixels, 64 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
