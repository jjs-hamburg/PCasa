using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Services;

internal static class FolderRemovalFlow
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? caption);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam);
    private const uint WmCommand = 0x0111;
    private const int IdYes = 6;

    public static int Run()
    {
        // Program.Main refuses to run any WPF smoke test outside GitHub Actions.
        var testRoot = Path.Combine(Path.GetTempPath(),
            "PCasa-FolderContextFlow-" + Guid.NewGuid().ToString("N"));
        var aPath = Path.Combine(testRoot, "A");
        var bPath = Path.Combine(testRoot, "B");
        Directory.CreateDirectory(aPath);
        Directory.CreateDirectory(bPath);

        PCasa.App.MainWindow? window = null;
        try
        {
            for (var i = 0; i < 12; i++)
            {
                MakeJpeg(Path.Combine(aPath, $"a-{i:00}.jpg"));
                MakeJpeg(Path.Combine(bPath, $"b-{i:00}.jpg"));
            }
            var db = new LocalDatabase();
            db.Initialize();
            var roots = new FolderRepository(db);
            roots.Add(testRoot);
            var root = roots.GetAll().Single(f =>
                string.Equals(f.LocalPath, testRoot, StringComparison.OrdinalIgnoreCase));
            var scanner = new FolderScanner(new ImageCatalogRepository(db),
                new ThumbnailCache(), roots, new BrowseThumbnailProfileRepository(db));
            scanner.Scan(root);
            new BackgroundScanSettings { Enabled = false }.Save();
            var catalog = new ImageCatalogRepository(db);
            var groups = catalog.GetPhotoFolders();
            var aId = groups.Single(g => string.Equals(g.LocalPath, aPath,
                StringComparison.OrdinalIgnoreCase)).Id;
            var bId = groups.Single(g => string.Equals(g.LocalPath, bPath,
                StringComparison.OrdinalIgnoreCase)).Id;

            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow { Width = 1040, Height = 670 };
            window.Show();

            var surface = (BrowseGallerySurface)window.FindName("BrowseSurface");
            var tree = (TreeView)window.FindName("FoldersTree");
            var status = (TextBlock)window.FindName("StatusText");
            Until(window, () => surface.GetFolderOffset(aId) is not null &&
                surface.GetFolderOffset(bId) is not null, "Initial image groups missing.");

            var aItem = Walk(tree).Single(i => string.Equals(PathOf(i), aPath,
                StringComparison.OrdinalIgnoreCase));
            aItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, aItem),
                "Album A was not selected.");

            // Simulate an invisible but long-running catalog/cache worker:
            // the user's click must hide its thumbnails BEFORE the shared
            // lock becomes free. A test that only removes an idle folder would
            // miss the original defect.
            var cacheLock = typeof(CatalogRemovalService)
                .GetField("CacheLock", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)?.GetValue(null)
                ?? throw new Exception("Expected shared catalog lock was not found.");
            using var lockAcquired = new ManualResetEventSlim();
            using var releaseLock = new ManualResetEventSlim();
            var blocker = Task.Run(() =>
            {
                lock (cacheLock)
                {
                    lockAcquired.Set();
                    if (!releaseLock.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("Test failed to release the simulated import.");
                }
            });
            if (!lockAcquired.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Simulated import did not acquire the catalog lock.");
            try
            {
                // Invoke the SAME menu item the user clicked. Native WPF
                // confirmation is accepted by an isolated test runner.
                ClickRemoveMenu(window, tree);
                if (surface.GetFolderOffset(aId) is not null ||
                    Walk(tree).Any(i => string.Equals(PathOf(i), aPath,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Context menu kept folder or thumbnails visible while catalog was busy.");
                if (catalog.CountAll() != 24)
                    throw new Exception("Test no longer holds the catalog cleanup behind the lock.");
            }
            finally
            {
                releaseLock.Set();
                blocker.GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS: Actual context menu immediately hides album under simulated in-progress import.");

            Until(window, () => catalog.CountAll() == 12 &&
                status.Text.Contains("Bildeinträge aus PCasa entfernt", StringComparison.OrdinalIgnoreCase) &&
                surface.GetFolderOffset(aId) is null &&
                surface.GetFolderOffset(bId) is not null &&
                !Walk(tree).Any(i => string.Equals(PathOf(i), aPath,
                    StringComparison.OrdinalIgnoreCase)),
                "Context menu removal did not converge database, tree and gallery.");
            if (!roots.GetExcludedPaths(root.Id).Any(x =>
                string.Equals(x, aPath, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Context menu did not save folder exclusion.");
            if (Directory.GetFiles(aPath, "*.jpg").Length != 12)
                throw new Exception("Context removal deleted source photographs.");
            Console.WriteLine("PASS: Context menu persists subtree exclusion, retains unrelated images and original files.");

            // Removing the monitored root through the SAME menu must remove it
            // from monitoring (not leave a disabled monitored root in the tree).
            var rootItem = Walk(tree).Single(i =>
                string.Equals(PathOf(i), testRoot, StringComparison.OrdinalIgnoreCase));
            rootItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, rootItem),
                "Monitored root could not be selected.");
            ClickRemoveMenu(window, tree);
            Until(window, () => catalog.CountAll() == 0 &&
                roots.GetAll().All(r => r.Id != root.Id) &&
                surface.GetFolderOffset(bId) is null &&
                !Walk(tree).Any(i => string.Equals(PathOf(i), testRoot,
                    StringComparison.OrdinalIgnoreCase)),
                "Removing root via context menu left a monitored folder or thumbnails.");
            if (Directory.GetFiles(bPath, "*.jpg").Length != 12)
                throw new Exception("Removing monitored root deleted source files.");
            Console.WriteLine("PASS: Context menu removes monitored root and its thumbnails without touching original files.");

            window.Close();
            window = null;
            Console.WriteLine("RESULT: Real folder-tree context removal smoke checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: Real context menu removal: " + error);
            return 1;
        }
        finally
        {
            try { window?.Close(); } catch { }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(testRoot, recursive: true); } catch { }
        }
    }

    private static void ClickRemoveMenu(Window window, TreeView tree)
    {
        var menu = tree.ContextMenu ?? throw new Exception("Folder menu is missing.");
        var command = menu.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Aus PCasa entfernen"));

        var accepted = false;
        var promptTimer = new DispatcherTimer(DispatcherPriority.Send,
            window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        promptTimer.Tick += (_, _) =>
        {
            var dialog = FindWindow("#32770", "Aus PCasa entfernen");
            if (dialog == IntPtr.Zero) return;
            accepted = true;
            promptTimer.Stop();
            SendMessage(dialog, WmCommand, (IntPtr)IdYes, IntPtr.Zero);
        };
        promptTimer.Start();
        try
        {
            command.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, command));
        }
        finally
        {
            promptTimer.Stop();
        }
        if (!accepted)
            throw new Exception("The actual WPF context menu confirmation did not open.");
    }

    private static IEnumerable<TreeViewItem> Walk(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var child in Walk(item)) yield return child;
        }
    }

    private static string? PathOf(TreeViewItem item) =>
        item.Tag?.GetType().GetProperty("FullPath")?.GetValue(item.Tag) as string;

    private static void Until(Window window, Func<bool> ready, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (ready()) return;
            Thread.Sleep(40);
        }
        throw new TimeoutException(message);
    }

    private static void MakeJpeg(string path)
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20; pixels[i + 1] = 105;
            pixels[i + 2] = 185; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(64, 64, 96, 96,
            PixelFormats.Bgr32, null, pixels, 64 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
