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
        var laterFolder = Path.Combine(initialRoot, "Later");
        Directory.CreateDirectory(laterFolder);
        var delayedPhoto = Path.Combine(laterFolder, "later.jpg");
        WriteJpeg(delayedPhoto);
        WriteJpeg(Path.Combine(afterRestart, "second.jpg"));

        PCasa.App.MainWindow? window = null;
        using var gateReady = new ManualResetEventSlim();
        using var releaseDelayedPhoto = new ManualResetEventSlim();
        Task? blockedFile = null;
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

            // Slow second image simulates a very large/NAS import. The first
            // JPEG must already appear in the gallery before this scan can
            // complete. The synchronization gate is confined to the second
            // image; PCasa's per-file lock remains its single source owner.
            blockedFile = Task.Run(() =>
            {
                lock (SourceFileLocks.ForPath(delayedPhoto))
                {
                    gateReady.Set();
                    if (!releaseDelayedPhoto.Wait(TimeSpan.FromSeconds(25)))
                        throw new TimeoutException("The delayed source was never released.");
                }
            });
            if (!gateReady.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Slow second image did not acquire its file gate.");

            // Clicking Abbruch must discard every staged path and must not
            // touch the durable catalog or start a scanner.
            ShowManagerWithStagedRoot(window, initialRoot, commit: false, roots);
            if (roots.GetAll().Any(root => SamePath(root.LocalPath, initialRoot)) ||
                new ImageCatalogRepository(db).CountAll() != 0)
                throw new Exception("Cancel committed an unconfirmed folder selection.");
            Console.WriteLine("PASS: Real manager Abbruch discards staged root and starts no import.");

            // Exactly the same dialog with OK must commit the entire
            // selection, close, expose its root and THEN launch a user scan.
            ShowManagerWithStagedRoot(window, initialRoot, commit: true, roots);
            Until(window, () => Walk(tree).Any(item => SamePath(PathOf(item), initialRoot)),
                "Confirmed root did not become visible in folder tree.");
            if (roots.GetAll().All(root => !SamePath(root.LocalPath, initialRoot)))
                throw new Exception("Confirmed folder selection was not persisted.");
            Console.WriteLine("PASS: Actual WPF manager OK commits and immediately displays the new root.");

            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 1,
                "The initial JPEG was not committed before the later image.");
            var firstFolder = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, initialRoot));
            Until(window, () =>
                    surface.GetFolderOffset(firstFolder.Id).HasValue &&
                    roots.GetPendingInitialScans().Any(root => SamePath(root.LocalPath, initialRoot)),
                "Gallery stayed empty while first JPEG was committed and the scan remained active.");
            Console.WriteLine("PASS: First image becomes visible while next file blocks the active scan.");

            releaseDelayedPhoto.Set();
            blockedFile.GetAwaiter().GetResult();
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 2 &&
                roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, initialRoot)),
                "Priority initial scan did not finish or clear its durable request.");
            var later = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, laterFolder));
            Until(window, () => surface.GetFolderOffset(later.Id).HasValue,
                "Completed scan did not expose later JPEG in gallery.");
            Console.WriteLine("PASS: Full catalog/gallery completes without background monitoring.");

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
                new ImageCatalogRepository(db).CountAll() == 3 &&
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
            releaseDelayedPhoto.Set();
            try { blockedFile?.GetAwaiter().GetResult(); } catch { }
            try { window?.Close(); } catch { }
            // The Windows workflow executes several independent WPF processes
            // against the runner's local test catalog. Never leave synthetic
            // monitored roots/images for the following tag-search test.
            try
            {
                var cleanup = new FolderRepository(new LocalDatabase());
                foreach (var folder in cleanup.GetAll().Where(folder =>
                    folder.LocalPath.StartsWith(rootPath + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                    cleanup.Remove(folder.Id);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Synthetic folder cleanup: " + error);
            }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(rootPath, recursive: true); } catch { }
        }
    }

    private static void ShowManagerWithStagedRoot(PCasa.App.MainWindow window,
        string path, bool commit, FolderRepository repository)
    {
        var handler = typeof(PCasa.App.MainWindow).GetMethod("BrowseFolderManager_Click",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Main menu has no shared folder manager handler.");
        var staging = typeof(PCasa.App.FolderManagerWindow).GetMethod("StageNewRoot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("The manager is not staging changes.");

        Exception? dispatchError = null;
        var completed = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(() =>
            {
                try { handler.Invoke(window, new object[] { window, new RoutedEventArgs() }); }
                catch (Exception error) { dispatchError = error; completed = true; }
            }));
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() =>
            {
                try
                {
                    var dialog = Application.Current.Windows.OfType<PCasa.App.FolderManagerWindow>()
                        .SingleOrDefault(w => w.IsVisible)
                        ?? throw new Exception("Real folder manager did not open.");
                    if (dialog.FindName("OkButton") is not Button ||
                        dialog.FindName("CancelButton") is not Button)
                        throw new Exception("Folder manager has no OK/Abbruch buttons.");
                    staging.Invoke(dialog, new object[] { path });
                    if (repository.GetAll().Any(root => SamePath(root.LocalPath, path)))
                        throw new Exception("Folder was written to SQLite before OK.");
                    var control = (Button)dialog.FindName(commit ? "OkButton" : "CancelButton");
                    control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, control));
                    completed = true;
                }
                catch (Exception error)
                {
                    dispatchError = error;
                    completed = true;
                    foreach (var manager in Application.Current.Windows
                                 .OfType<PCasa.App.FolderManagerWindow>().Where(w => w.IsVisible))
                        manager.Close();
                }
            }));

        Until(window, () => completed, "Could not complete manager dialog.");
        if (dispatchError is not null)
            throw new Exception("Real WPF manager staging failed.", dispatchError);
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
