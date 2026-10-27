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
        Task? blockedRestart = null;
        using var restartGateReady = new ManualResetEventSlim();
        using var releaseRestartPhoto = new ManualResetEventSlim();
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
            VerifyRotatedViewerGeometry(window);
            Console.WriteLine("PASS: WPF viewer uses portrait canvas for EXIF-rotated JPEG, without squeezing.");

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
            var initialTreeRoot = Walk(tree).Single(item => SamePath(PathOf(item), initialRoot));

            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 1,
                "The initial JPEG was not committed before the later image.");
            var firstFolder = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, initialRoot));
            Until(window, () =>
                    surface.GetFolderOffset(firstFolder.Id).HasValue &&
                    roots.GetPendingInitialScans().Any(root => SamePath(root.LocalPath, initialRoot)),
                "Gallery stayed empty while first JPEG was committed and the scan remained active.");
            Until(window, () =>
                    (initialTreeRoot.Header as string)?.Contains("(1)") == true,
                "The folder counter did not increment while the second image was blocked.");
            if (!ReferenceEquals(initialTreeRoot,
                Walk(tree).Single(item => SamePath(PathOf(item), initialRoot))))
                throw new Exception("Gallery catalog refresh destroyed the independent folder tree.");
            Console.WriteLine("PASS: First image and folder counter visible during blocked ongoing scan; tree identity preserved.");

            releaseDelayedPhoto.Set();
            blockedFile.GetAwaiter().GetResult();
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 2 &&
                roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, initialRoot)),
                "Priority initial scan did not finish or clear its durable request.");
            var later = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, laterFolder));
            Until(window, () => surface.GetFolderOffset(later.Id).HasValue &&
                    (initialTreeRoot.Header as string)?.Contains("(2)") == true,
                "Completed scan did not expose its second JPEG and updated folder count.");
            Console.WriteLine("PASS: Full catalog/gallery and parent count complete without background monitoring.");

            // A manual folder creation borrows the NAS scan's existing progress
            // window. Its 1/1 final caption must NEVER remain frozen when the
            // overlay ends, even if the next SMB callback is delayed.
            var sharedProgress = new PCasa.App.BrowseCacheProgressWindow { Owner = window };
            sharedProgress.Show();
            try
            {
                sharedProgress.UpdateScan(new ScanProgress(
                    2, 1, 1, 0, "scan-background-first.jpg", Added: 1));
                sharedProgress.BeginManualOperation("Ordner anlegen", 1);
                sharedProgress.UpdateManualOperation(new SchedulerJobSnapshot(
                    10, "folder:create:test", SchedulerPriority.ManualAction,
                    SchedulerJobState.Running, DateTimeOffset.UtcNow, null)
                {
                    Phase = "Ordner angelegt",
                    Processed = 1, Total = 1,
                    CurrentPath = "ORG"
                });
                sharedProgress.UpdateScan(new ScanProgress(
                    4, 2, 2, 0, "scan-background-latest.jpg", Added: 2));
                sharedProgress.EndManualOperation();
                var phaseText = (TextBlock?)sharedProgress.FindName("PhaseText");
                var currentFileText = (TextBlock?)sharedProgress.FindName("CurrentFileText");
                var countText = (TextBlock?)sharedProgress.FindName("CountText");
                if (phaseText?.Text != "Bilder werden eingelesen" ||
                    currentFileText?.Text != "scan-background-latest.jpg" ||
                    countText?.Text != "Neu: 2 · geprüft: 4")
                    throw new Exception(
                        "After manual 1/1, borrowed background progress remained frozen.");

                sharedProgress.BeginManualOperation("Ordner anlegen", 1);
                sharedProgress.Finish(); // Real background owner completed.
                sharedProgress.EndManualOperation();
                if (sharedProgress.IsVisible)
                    throw new Exception(
                        "Background progress window failed to close after deferred Finish.");
            }
            finally
            {
                sharedProgress.Finish();
            }
            Console.WriteLine("PASS: Manual completion restores background scheduler presentation immediately.");

            // A manually created folder is published on the WPF tree before
            // the actual I/O can start. Occupy the one Manual SourceProbe
            // worker to simulate a slow network creation request.
            var createScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            var releaseCreateProbe = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var newFolderPath = Path.Combine(initialRoot, "FreshFolder");
            var pendingProbe = createScheduler.EnqueueAsync(
                "test:slow-manual-source-probe",
                SchedulerPriority.ManualAction,
                async token => await releaseCreateProbe.Task.WaitAsync(token),
                workKind: SchedulerWorkKind.SourceProbe);
            try
            {
                Until(window, () => createScheduler.Snapshot().Any(job =>
                        job.Key == "test:slow-manual-source-probe" &&
                        job.State == SchedulerJobState.Running),
                    "Manual source-probe test lane did not start.");
                initialTreeRoot.IsSelected = true;
                var createHandler = typeof(PCasa.App.MainWindow).GetMethod(
                    "FolderContext_CreateDirectory_Click",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new Exception("New-folder context-menu handler missing.");
                var submitted = false;
                Exception? creationDialogError = null;
                var dialogTimer = new DispatcherTimer(DispatcherPriority.Background,
                    window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(35) };
                var dialogDeadline = DateTime.UtcNow.AddSeconds(10);
                dialogTimer.Tick += (_, _) =>
                {
                    if (submitted) { dialogTimer.Stop(); return; }
                    var dialog = Application.Current.Windows
                        .OfType<Window>()
                        .FirstOrDefault(w => w.IsVisible &&
                            w.Title == "Neuen Unterordner anlegen");
                    if (dialog is null)
                    {
                        if (DateTime.UtcNow < dialogDeadline) return;
                        creationDialogError = new TimeoutException(
                            "Folder creation dialog did not open.");
                        submitted = true;
                        dialogTimer.Stop();
                        return;
                    }
                    dialogTimer.Stop();
                    try
                    {
                        var panel = dialog.Content as StackPanel ??
                            throw new Exception("New folder dialog has no input panel.");
                        var nameInput = panel.Children.OfType<TextBox>().Single();
                        if (nameInput.Text.Length != 0)
                            throw new Exception("New folder name must start empty.");
                        nameInput.Text = "FreshFolder";
                        var button = panel.Children.OfType<Button>().Single();
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                        submitted = true;
                    }
                    catch (Exception ex)
                    {
                        creationDialogError = ex;
                        submitted = true;
                        dialog.Close();
                    }
                };
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => createHandler.Invoke(
                        window, [window, new RoutedEventArgs()])));
                dialogTimer.Start();
                try
                {
                    Until(window, () => submitted, "New-folder dialog was not handled.");
                }
                finally { dialogTimer.Stop(); }
                if (creationDialogError is not null)
                    throw new Exception("Could not submit new-folder dialog.",
                        creationDialogError);

                Until(window, () =>
                    Walk(tree).Any(item => SamePath(PathOf(item), newFolderPath)),
                    "New folder did not appear before scheduled disk I/O.");
                if (Directory.Exists(newFolderPath))
                    throw new Exception("New folder creation bypassed blocked manual scheduler.");
                Until(window, () => createScheduler.Snapshot().Any(job =>
                    job.Key.StartsWith("folder:create:") &&
                    job.State == SchedulerJobState.Queued),
                    "New folder creation was not scheduled immediately.");
                releaseCreateProbe.TrySetResult();
                Until(window, () => Directory.Exists(newFolderPath) &&
                    roots.GetCreatedDirectories().Any(path => SamePath(path, newFolderPath)),
                    "New folder was not durably registered with its physical creation.");
                Console.WriteLine(
                    "PASS: Empty folder name, immediate tree entry, manual priority and durable directory creation.");
            }
            finally
            {
                releaseCreateProbe.TrySetResult();
                Until(window, () => pendingProbe.IsCompleted,
                    "Held manual source-probe never finished.");
            }

            window.Close();
            window = null;

            // Re-open PCasa with a second persisted, unfinished user selection.
            // No watcher event or in-memory task survives. Startup must
            // independently find and execute the one durable queued request.
            roots.Add(afterRestart);
            if (roots.GetPendingInitialScans().All(root => !SamePath(root.LocalPath, afterRestart)))
                throw new Exception("Second root has no pending initial import.");

            blockedRestart = Task.Run(() =>
            {
                lock (SourceFileLocks.ForPath(Path.Combine(afterRestart, "second.jpg")))
                {
                    restartGateReady.Set();
                    if (!releaseRestartPhoto.Wait(TimeSpan.FromSeconds(25)))
                        throw new TimeoutException("Restart source lock not released.");
                }
            });
            if (!restartGateReady.Wait(TimeSpan.FromSeconds(5)))
                throw new Exception("Restart source lock not acquired.");
            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            tree = (TreeView)window.FindName("FoldersTree");
            surface = (BrowseGallerySurface)window.FindName("BrowseSurface");
            Until(window, () =>
                Walk(tree).Any(item => SamePath(PathOf(item), newFolderPath)),
                "Empty folder created in PCasa vanished from the tree after restart.");
            Console.WriteLine("PASS: Empty NAS directory survives PCasa restart without any images.");
            Until(window, () =>
                Walk(tree).Any(item => SamePath(PathOf(item), afterRestart)),
                "Pending root was missing from tree after restart.");
            Until(window, () => surface.GetFolderOffset(firstFolder.Id).HasValue &&
                roots.GetPendingInitialScans().Any(root => SamePath(root.LocalPath, afterRestart)),
                "Existing thumbnails vanished while another folder's scan was blocked.");
            Console.WriteLine("PASS: Existing thumbnails stay accessible while a separate pending root is blocked.");

            // Regression: a pending first import on one root MUST NOT prevent
            // automatic discovery on a fully imported, independent root.
            // This reproduces the real Pictures-pending / S:-completed case.
            var missedPhoto = Path.Combine(initialRoot, "previously-unseen.jpg");
            WriteJpeg(missedPhoto);
            new BackgroundScanSettings { Enabled = true }.Save();
            Until(window, () =>
                    new ImageCatalogRepository(db).CountAll() == 3 &&
                    roots.GetPendingInitialScans().Any(root =>
                        SamePath(root.LocalPath, afterRestart)),
                "A separate pending first import disabled the other root's automatic inventory.");
            Console.WriteLine(
                "PASS: Unindexed images on a completed root are discovered while another root's first import is blocked.");

            // Verify an actual NEW file notification, not just startup/root
            // reconciliation. Rename a complete JPEG into place to ensure
            // the watcher never reads a partially written file.
            var incomingTemp = Path.Combine(initialRoot, "incoming-temporary.bin");
            var incomingPhoto = Path.Combine(initialRoot, "incoming-during-import.jpg");
            WriteJpeg(incomingTemp);
            File.Move(incomingTemp, incomingPhoto);
            Until(window, () =>
                    new ImageCatalogRepository(db).CountAll() == 4 &&
                    roots.GetPendingInitialScans().Any(root =>
                        SamePath(root.LocalPath, afterRestart)),
                "Live file-system notification was blocked by a different pending first import.");
            Console.WriteLine(
                "PASS: New file notifications are indexed while a separate first import is blocked.");

            releaseRestartPhoto.Set();
            blockedRestart.GetAwaiter().GetResult();
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 5 &&
                roots.GetPendingInitialScans().Count == 0,
                "Restarted durable first import failed to resume after independent automatic scans.");
            Console.WriteLine("PASS: Background image discovery and durable first-import resume coexist.");

            // Explorer mode is a temporary lazy filesystem projection:
            // show non-indexed directories, including hidden folders,
            // without rebuilding the gallery's existing tree or rescanning
            // a whole monitored drive. Manual scan uses the SAME P0 entrypoint.
            var neverScanned = Path.Combine(initialRoot, "ExplorerNotIndexed");
            var hiddenExplorer = Path.Combine(initialRoot, "ExplorerHidden");
            Directory.CreateDirectory(neverScanned);
            Directory.CreateDirectory(hiddenExplorer);
            File.SetAttributes(hiddenExplorer,
                File.GetAttributes(hiddenExplorer) | FileAttributes.Hidden);
            WriteJpeg(Path.Combine(neverScanned, "now-visible.jpg"));

            // Disable automatic monitoring for this manual-scan assertion.
            new BackgroundScanSettings { Enabled = false }.Save();
            var selectedRoot = Walk(tree).Single(item =>
                SamePath(PathOf(item), initialRoot));
            selectedRoot.IsSelected = true;
            var treeBefore = selectedRoot;
            var browserButton = (Button)window.FindName("ShowAllFilesystemFoldersButton");
            var explorer = (TreeView)window.FindName("ExplorerTree");
            browserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, browserButton));
            Until(window, () => explorer.Visibility == Visibility.Visible &&
                tree.Visibility == Visibility.Collapsed &&
                Walk(explorer).Any(item => SamePath(PathOf(item), neverScanned)) &&
                Walk(explorer).Any(item => SamePath(PathOf(item), hiddenExplorer)),
                "Explorer did not display all child directories on first expansion.");
            if (!ReferenceEquals(treeBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), initialRoot))) ||
                explorer.Items.Count != 1 ||
                browserButton.Tag as string != "ActiveGold")
                throw new Exception("Explorer mode rebuilt the catalog tree or navigated away.");

            var newFolderItem = Walk(explorer).Single(item =>
                SamePath(PathOf(item), neverScanned));
            if (newFolderItem.Tag?.GetType().GetProperty("PhotoFolderId")?
                    .GetValue(newFolderItem.Tag) is not null)
                throw new Exception("Unscanned folder unexpectedly has a catalog ID.");
            newFolderItem.IsSelected = true;
            var scan = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseScanCurrentFolder_Click",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            scan.Invoke(window, [explorer, new RoutedEventArgs()]);
            var scheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            Until(window, () => scheduler.Snapshot().Any(job =>
                    job.Key.StartsWith("catalog:complete-scan:", StringComparison.Ordinal) &&
                    job.Key.Contains("ExplorerNotIndexed", StringComparison.Ordinal) &&
                    job.Priority == SchedulerPriority.ManualAction),
                "Explorer context scan did not enter the shared scheduler at P0.");
            Until(window, () => new ImageCatalogRepository(db).GetPhotoFolders()
                .Any(folder => SamePath(folder.LocalPath, neverScanned)) &&
                newFolderItem.Tag?.GetType().GetProperty("PhotoFolderId")?
                    .GetValue(newFolderItem.Tag) is long &&
                surface.GetFolderOffset(
                    new ImageCatalogRepository(db).GetPhotoFolders()
                        .Single(folder => SamePath(folder.LocalPath, neverScanned)).Id)
                    is not null,
                "Manual scan of Explorer-only folder did not publish its image.");

            browserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, browserButton));
            if (explorer.Visibility != Visibility.Collapsed ||
                tree.Visibility != Visibility.Visible ||
                browserButton.Tag is not null)
                throw new Exception("Explorer toggle failed to restore catalog tree.");

            Console.WriteLine(
                "PASS: Lazy Explorer shows hidden/unindexed folders, P0 scan and restores original tree.");
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
            releaseRestartPhoto.Set();
            try { blockedFile?.GetAwaiter().GetResult(); } catch { }
            try { blockedRestart?.GetAwaiter().GetResult(); } catch { }
            try { window?.Close(); } catch { }
            // This smoke test temporarily enables automatic discovery.
            // Restore the existing isolated test expectation for later suites.
            try { new BackgroundScanSettings { Enabled = false }.Save(); } catch { }
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
        // The dialog now loads its authoritative configuration on a
        // scheduler catalog-read worker BEFORE ShowDialog. A one-shot
        // Background callback is racy: it may execute before the dialog
        // exists, then an unanswered modal window hangs the entire CI job.
        // Poll only the visible WPF dialog; this has no product-side
        // scheduling or state of its own.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var dialogTimer = new DispatcherTimer(DispatcherPriority.Background,
            window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        dialogTimer.Tick += (_, _) =>
        {
            if (completed) { dialogTimer.Stop(); return; }
            try
            {
                var dialog = Application.Current.Windows
                    .OfType<PCasa.App.FolderManagerWindow>()
                    .SingleOrDefault(w => w.IsVisible);
                if (dialog is null)
                {
                    if (DateTime.UtcNow < deadline) return;
                    throw new TimeoutException(
                        "The scheduled folder-manager read did not open its dialog.");
                }
                dialogTimer.Stop();
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
                dialogTimer.Stop();
                dispatchError = error;
                completed = true;
                foreach (var manager in Application.Current.Windows
                             .OfType<PCasa.App.FolderManagerWindow>().Where(w => w.IsVisible))
                    manager.Close();
            }
        };
        dialogTimer.Start();

        try { Until(window, () => completed, "Could not complete manager dialog."); }
        finally { dialogTimer.Stop(); }
        if (dispatchError is not null)
            throw new Exception("Real WPF manager staging failed.", dispatchError);
    }

    private static void VerifyRotatedViewerGeometry(PCasa.App.MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewerOpen = window.GetType().GetField("_viewerOpen", flags)
            ?? throw new Exception("WPF viewer state not found.");
        var info = window.GetType().GetField("_viewerImageInfo", flags)
            ?? throw new Exception("Viewer image dimensions not found.");
        var set = window.GetType().GetMethod("SetViewerBitmap", flags)
            ?? throw new Exception("Viewer bitmap setter not found.");
        var image = (System.Windows.Controls.Image)window.FindName("PhotoViewerImage");

        const int decodedWidth = 48, decodedHeight = 64;
        var pixels = new byte[decodedWidth * decodedHeight * 4];
        var orientedBitmap = BitmapSource.Create(decodedWidth, decodedHeight,
            96, 96, PixelFormats.Bgr32, null, pixels, decodedWidth * 4);
        orientedBitmap.Freeze();
        viewerOpen.SetValue(window, true);
        info.SetValue(window,
            new PhotoViewerImageInfo(42, "original-exif6.jpg",
                "original-exif6.jpg", 4032, 3024));
        try
        {
            set.Invoke(window, new object?[] { orientedBitmap });
            var adjusted = (PhotoViewerImageInfo?)info.GetValue(window);
            if (adjusted is not { Width: 3024, Height: 4032 })
                throw new Exception("EXIF6 viewer retained unrotated catalog dimensions.");
            var displayedRatio = image.Width / image.Height;
            if (double.IsNaN(displayedRatio) ||
                Math.Abs(displayedRatio - 3024d / 4032) > 0.01)
                throw new Exception(
                    $"EXIF portrait was squeezed into a {displayedRatio:0.###} viewer canvas.");
        }
        finally
        {
            viewerOpen.SetValue(window, false);
            info.SetValue(window, null);
            image.Source = null;
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
