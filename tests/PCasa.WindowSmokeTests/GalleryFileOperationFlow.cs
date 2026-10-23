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

            // Keep a background catalog worker occupied. The copy must
            // START and finish in the dedicated manual lane without waiting.
            var scheduler = (PCasaScheduler?)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window) ?? throw new Exception("Shared scheduler missing.");
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var background = scheduler.EnqueueAsync(
                "test:background-blocker", SchedulerPriority.Background,
                async token =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            try
            {
                Until(window, () => entered.Task.IsCompleted,
                    "Background scheduler worker never started.");
                Transfer(window, [original], second, move: false);
                var job = scheduler.Snapshot().LastOrDefault(item =>
                    item.Key.StartsWith("gallery:file-transfer:", StringComparison.Ordinal));
                if (job is null || job.Priority != SchedulerPriority.ManualAction ||
                    job.StartedAt is null || background.IsCompleted)
                    throw new Exception(
                        "Manual copy waited for background job or bypassed highest scheduler priority.");
            }
            finally
            {
                release.TrySetResult();
                Until(window, () => background.IsCompleted,
                    "Background worker did not recover after manual copy.");
                background.GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS: Manual file transfer preempts occupied background scheduler.");
            Until(window, () => File.Exists(copied) &&
                CountInRoot(db, root) == 2,
                "Copy did not materialize/index destination.");
            if (!File.Exists(original))
                throw new Exception("Copy deleted original source.");
            Console.WriteLine("PASS: Copy preserved original and indexed second photo.");

            // A move must not re-import or re-encode an already indexed photo.
            // Preserve identity, EXIF, tags, all cache offsets and the cached
            // thumbnail path; the WPF gallery must show the target immediately.
            var preservedId = CatalogIdAt(db, copied);
            var beforeCache = ThumbnailIdentities(db, preservedId);
            if (beforeCache.Length == 0)
                throw new Exception("Cannot test move: no prepared thumbnail tiers.");
            var previousMetadata = MetadataIdentity(db, preservedId);
            var originalIndexCount = new BrowseCacheRepository(db).CountEntries();

            // This lock blocks scanner reconstruction of the destination.
            // A true catalog relocation must finish without ever needing it.
            lock (SourceFileLocks.ForPath(moved))
            {
                Transfer(window, [copied], third, move: true);
                if (!File.Exists(moved) || File.Exists(copied))
                    throw new Exception("Physical move failed.");
                if (CatalogIdAt(db, moved) != preservedId)
                    throw new Exception("Move replaced the original image ID.");
                if (!beforeCache.SequenceEqual(ThumbnailIdentities(db, preservedId)))
                    throw new Exception("Move re-created or discarded thumbnail cache tiers.");
                if (MetadataIdentity(db, preservedId) != previousMetadata)
                    throw new Exception("Move lost original EXIF or tag references.");
                if (new BrowseCacheRepository(db).CountEntries() != originalIndexCount)
                    throw new Exception("Move scheduled a duplicate thumbnail import.");
                var cachedTarget = new BrowseCacheRepository(db).GetIndex(96)
                    .FirstOrDefault(entry => entry.ImageId == preservedId);
                if (cachedTarget is null ||
                    !string.Equals(cachedTarget.FileName, "test.jpg",
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Immediate browse index still shows old location.");
            }
            Until(window, () => !File.Exists(copied) && File.Exists(moved) &&
                CountInRoot(db, root) == 2,
                "Move did not commit target catalog location.");
            Console.WriteLine("PASS: Cached move preserves image ID, EXIF, tags and thumb tiers with no destination scan.");

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
            if (tree.SelectedItem is not TreeViewItem)
                throw new Exception("Initial gallery has no selected folder tree item.");
            // A drop target is highlighted independently from the tree's
            // selected source item, then cleared without changing selection.
            if (nested.Header is not string folderName ||
                folderName.Contains("System.Windows.Controls", StringComparison.Ordinal))
                throw new Exception("Tree item header must remain a real folder name.");
            var oldSelection = tree.SelectedItem;
            var highlight = typeof(PCasa.App.MainWindow).GetMethod(
                "HighlightDropFolder", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Tree target highlighter not installed.");
            nested.ApplyTemplate();
            var templateBorder = nested.Template.FindName("HeaderBorder", nested) as Border
                ?? throw new Exception("Existing tree template is missing its header border.");
            highlight.Invoke(window, [nested]);
            window.UpdateLayout();
            if (templateBorder.BorderBrush is not SolidColorBrush activeOutline ||
                activeOutline.Color.B == 0 || templateBorder.BorderThickness.Left < 0.5)
                throw new Exception("The actual WPF drop target outline was not displayed.");
            highlight.Invoke(window, [null]);
            window.UpdateLayout();
            if (templateBorder.BorderThickness.Left > 0 ||
                !ReferenceEquals(oldSelection, tree.SelectedItem))
                throw new Exception("Target outline changed folder selection or survived drag.");
            Console.WriteLine("PASS: Tree target highlights during drop without losing source selection.");

            // An explicit user scan must work even if a prior background or
            // startup import has marked the global scanning state as busy.
            var old = Path.Combine(batch, "test.jpg");
            var renamed = Path.Combine(batch, "renamed-manually.jpg");
            File.Move(old, renamed);
            var batchNode = FindFolderItems(tree).FirstOrDefault(item =>
                item.Tag?.GetType().GetProperty("FullPath")?.GetValue(item.Tag) is string path &&
                string.Equals(path, batch, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Renamed photo's folder missing in WPF tree.");
            FolderTreeNavigation.ExpandAncestors(batchNode);
            batchNode.BringIntoView();
            window.UpdateLayout();
            batchNode.IsSelected = true;
            if (!ReferenceEquals(tree.SelectedItem, batchNode))
                throw new Exception("WPF tree did not select the requested Batch folder.");
            var scanningFlag = typeof(PCasa.App.MainWindow).GetField("_isScanning",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Scan state field missing.");
            scanningFlag.SetValue(window, true);
            var manual = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseScanCurrentFolder_Click",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Manual scan context menu handler is missing.");
            manual.Invoke(window, [tree, new RoutedEventArgs()]);
            try
            {
                Until(window, () => !CatalogContains(db, old) && CatalogContains(db, renamed),
                    "Manual user scan did not replace renamed catalog row.");
                Until(window, () => CountInRoot(db, root) == 3 &&
                    new BrowseCacheRepository(db).GetIndex(96).Any(entry =>
                        string.Equals(entry.FileName, "renamed-manually.jpg", StringComparison.OrdinalIgnoreCase)),
                    "Manual scan did not reload renamed image in thumbnail index.");
            }
            catch (Exception ex)
            {
                var status = (window.FindName("StatusText") as TextBlock)?.Text;
                var state = typeof(PCasa.App.MainWindow).GetField("_scheduler",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                    as PCasaScheduler;
                var jobs = state?.Snapshot()
                    .Where(job => job.Key.Contains("complete-scan", StringComparison.Ordinal))
                    .Select(job => $"{job.State}/{job.Error}").ToArray();
                throw new Exception($"Manual scan diagnostic: selected={(tree.SelectedItem is TreeViewItem selected ? selected.Header : null)}; status={status}; catalogOld={CatalogContains(db, old)}; catalogNew={CatalogContains(db, renamed)}; index={new BrowseCacheRepository(db).GetIndex(96).Count}; jobs={string.Join(", ", jobs ?? [])}", ex);
            }
            Console.WriteLine("PASS: Context-menu manual scan bypasses busy background state, reconciles renamed file and reloads browse index.");

            // A thumbnail tier is allowed to be missing: its path fallback
            // must use the shared catalog-read worker, not block the UI.
            var getSelectedPaths = typeof(PCasa.App.MainWindow).GetMethod(
                "SelectedGalleryPathsAsync", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Scheduled selected-image path reader is missing.");
            var heldReaders = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseReaders = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var readers = Enumerable.Range(0, 2).Select(i => scheduler.EnqueueAsync(
                $"test:held-catalog-read:{i}", SchedulerPriority.Interactive,
                async token =>
                {
                    heldReaders.TrySetResult();
                    await releaseReaders.Task.WaitAsync(token);
                }, workKind: SchedulerWorkKind.CatalogRead)).ToArray();
            try
            {
                Until(window, () => scheduler.Snapshot().Count(job =>
                    job.Key.StartsWith("test:held-catalog-read:") &&
                    job.State == SchedulerJobState.Running) == 2,
                    "Catalog-read worker lanes were not occupied.");
                var fetchPaths = (Task<string[]>)getSelectedPaths.Invoke(
                    window, [new long[] { CatalogIdAt(db, Path.Combine(batch,
                        "renamed-manually.jpg")), long.MaxValue }])!;
                if (fetchPaths.IsCompleted)
                    throw new Exception("Uncached selected paths bypassed catalog scheduler.");
                var dispatcherStillResponsive = false;
                window.Dispatcher.BeginInvoke(() => dispatcherStillResponsive = true);
                Until(window, () => dispatcherStillResponsive,
                    "Uncached selected-path read blocked WPF dispatcher.");
                releaseReaders.TrySetResult();
                Until(window, () => fetchPaths.IsCompleted,
                    "Uncached selected-path read did not resume.");
                var resolved = fetchPaths.GetAwaiter().GetResult();
                if (!resolved.Contains(Path.Combine(batch, "renamed-manually.jpg"),
                        StringComparer.OrdinalIgnoreCase))
                    throw new Exception("Scheduled fallback lost a catalogued selection.");
            }
            finally
            {
                releaseReaders.TrySetResult();
                Until(window, () => readers.All(job => job.IsCompleted),
                    "Held catalog readers did not finish.");
            }
            Console.WriteLine("PASS: Uncached selected paths never query SQLite on the WPF dispatcher.");

            // The previous priority test only occupied a worker with Task.
            // Hold the actual FolderScanner in a source-image lock to prove
            // a MANUAL file move starts before the in-flight image scan ends.
            var blockingPhoto = Path.Combine(first, "priority-scan-photo.jpg");
            var prioritySource = Path.Combine(first, "priority-move-photo.jpg");
            var priorityTarget = Path.Combine(second, "priority-move-photo.jpg");
            WriteJpeg(blockingPhoto);
            WriteJpeg(prioritySource);
            Task actualBackgroundScan;
            lock (SourceFileLocks.ForPath(blockingPhoto))
            {
                actualBackgroundScan = scheduler.EnqueueAsync(
                    "test:running-image-scanner", SchedulerPriority.Background,
                    token =>
                    {
                        new FolderScanner(new ImageCatalogRepository(db),
                            new ThumbnailCache(), folderRepo).ScanFile(
                                sourceRoot, blockingPhoto, cancellationToken: token);
                        return Task.CompletedTask;
                    });
                Until(window, () => scheduler.Snapshot().Any(job =>
                        job.Key == "test:running-image-scanner" &&
                        job.State == SchedulerJobState.Running),
                    "Real image scanner did not start.");

                Transfer(window, [prioritySource], second, move: true);
                if (!File.Exists(priorityTarget) || File.Exists(prioritySource) ||
                    actualBackgroundScan.IsCompleted)
                    throw new Exception(
                        "Manual move waited for a running real image scanner.");
            }
            Until(window, () => actualBackgroundScan.IsCompleted,
                "Real image scanner did not recover after yielding manual priority.");
            actualBackgroundScan.GetAwaiter().GetResult();
            Console.WriteLine("PASS: Manual move completes ahead of real in-flight image scanning.");

            // Next block just the destination's EXIF/thumbnail source lock.
            // The actual move must already be committed while its indexing
            // continuation is blocked, proving physical work runs FIRST.
            var uncachedSource = Path.Combine(first, "physical-first.jpg");
            var uncachedDestination = Path.Combine(third, "physical-first.jpg");
            WriteJpeg(uncachedSource);
            Task deferredIndexing;
            lock (SourceFileLocks.ForPath(uncachedDestination))
            {
                deferredIndexing = StartTransfer(window, [uncachedSource], third, move: true);
                Until(window, () => !File.Exists(uncachedSource) &&
                    File.Exists(uncachedDestination),
                    "Physical move waited for the destination image decoder.");
                // File movement AND its gallery publication must not wait
                // for the new file's EXIF/thumbnail import. That is another
                // scheduler job using the existing scanner after completion.
                Until(window, () => deferredIndexing.IsCompleted,
                    "Manual move still waited for destination image indexing.");
            }
            Until(window, () => deferredIndexing.IsCompleted,
                "Deferred image indexing did not complete.");
            deferredIndexing.GetAwaiter().GetResult();
            Console.WriteLine("PASS: Uncatalogued move completes before destination EXIF/thumbnail import.");

            // Progress is read from the scheduler's live operation, not a
            // second UI timer. Block one file lock to observe Running before
            // the job finishes, and verify the shared progress window.
            var progressSource = Path.Combine(first, "progress-operation.jpg");
            WriteJpeg(progressSource);
            Task progressTask;
            lock (SourceFileLocks.ForPath(progressSource))
            {
                progressTask = StartTransfer(window, [progressSource], third, move: true);
                Until(window, () => scheduler.Snapshot().Any(x =>
                        x.Key.StartsWith("gallery:file-transfer:") &&
                        x.State == SchedulerJobState.Running &&
                        x.Phase == "Bilder verschieben"),
                    "Manual move was not visible as a running scheduler job.");
                var presenter = typeof(PCasa.App.MainWindow).GetField(
                    "_visibleTransferWindow",
                    BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(window) as PCasa.App.BrowseCacheProgressWindow;
                if (presenter is null || !presenter.IsVisible)
                    throw new Exception("Manual move has no visible progress presenter.");
                var title = (TextBlock?)presenter.FindName("PhaseText");
                if (title is null || !title.Text.Contains("verschieben",
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Progress window does not show manual move.");
                Until(window, () =>
                    (presenter.FindName("CurrentFileText") as TextBlock)?.Text ==
                        Path.GetFileName(progressSource),
                    "First file was not presented before waiting on the source lock.");
                presenter.UpdateScan(new ScanProgress(3, 1, 2, 0, progressSource));
                if (!title.Text.Contains("verschieben",
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Background scanner overwrote manual progress.");
            }
            Until(window, () => progressTask.IsCompleted,
                "Foreground operation did not finish.");
            progressTask.GetAwaiter().GetResult();
            var mostRecent = scheduler.Snapshot().Last(item =>
                item.Key.StartsWith("gallery:file-transfer:"));
            if (mostRecent.State != SchedulerJobState.Completed ||
                mostRecent.Phase != "Galerie aktualisiert")
                throw new Exception("Scheduler reports Completed before gallery publication.");
            Console.WriteLine("PASS: Progress and completion use the same scheduler job, ahead of scan status.");

            // Simulate a crash AFTER File.Move and BEFORE SQLite relocation.
            // Startup recovery must retain image identity, cache and metadata
            // without a second decoder, and clear the durable intent.
            var recoveryFrom = Path.Combine(first, "recovery-test.jpg");
            var recoveryTo = Path.Combine(second, "recovery-test.jpg");
            WriteJpeg(recoveryFrom);
            new FolderScanner(new ImageCatalogRepository(db), new ThumbnailCache(),
                folderRepo).ScanFile(sourceRoot, recoveryFrom);
            var recoveryId = CatalogIdAt(db, recoveryFrom);
            var recoveryCache = ThumbnailIdentities(db, recoveryId);
            var recoveryIntent = Guid.NewGuid().ToString("N");
            var repository = new ImageCatalogRepository(db);
            repository.BeginRelocationIntent(recoveryIntent, recoveryId,
                recoveryFrom, recoveryTo, sourceRoot.Id);
            File.Move(recoveryFrom, recoveryTo);
            var recovered = repository.RecoverRelocations(folderRepo.GetAll());
            if (recovered.Recovered != 1 || recovered.Unresolved != 0 ||
                CatalogIdAt(db, recoveryTo) != recoveryId ||
                !recoveryCache.SequenceEqual(ThumbnailIdentities(db, recoveryId)))
                throw new Exception("Interrupted move was not safely recovered.");
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM file_transfer_intents;";
                if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                    throw new Exception("Completed recovery left an uncommitted intent.");
            }
            Console.WriteLine("PASS: Crash after physical move is recovered without thumbnail rebuild.");

            // The progress window is now screen-anchored, intentionally
            // independent of the PCasa window's size on that monitor.
            var progressWindow = new PCasa.App.BrowseCacheProgressWindow { Owner = window };
            progressWindow.Show();
            try
            {
                var right = SystemParameters.WorkArea.Right;
                Until(window, () => Math.Abs(
                        right - (progressWindow.Left + progressWindow.ActualWidth) - 12) < 3,
                    "Progress window did not align to monitor work-area right edge.");
                var anchoredLeft = progressWindow.Left;
                window.Width += 110;
                Until(window, () => Math.Abs(progressWindow.Left - anchoredLeft) < 3,
                    "Progress window shifted when the owner was resized on one monitor.");
            }
            finally
            {
                progressWindow.Finish();
            }
            Console.WriteLine("PASS: Progress dialog stays screen-anchored when PCasa resizes.");

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

    private static long CatalogIdAt(LocalDatabase db, string path)
    {
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM images WHERE local_path = $path COLLATE NOCASE;";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(command.ExecuteScalar()
            ?? throw new Exception("Catalog image missing: " + path));
    }

    private static string[] ThumbnailIdentities(LocalDatabase db, long imageId)
    {
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tier, pack_file, byte_offset, byte_length,
                   source_modified_utc_ticks
            FROM thumbnail_entries WHERE image_id = $id ORDER BY tier;
            """;
        command.Parameters.AddWithValue("$id", imageId);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(string.Join("|", Enumerable.Range(0, 5)
                .Select(index => reader.GetValue(index).ToString())));
        return values.ToArray();
    }

    private static string MetadataIdentity(LocalDatabase db, long imageId)
    {
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT exif_json FROM image_exif WHERE image_id = $id),
              (SELECT keywords FROM image_tags WHERE image_id = $id),
              (SELECT thumbnail_path FROM images WHERE id = $id)
            """;
        command.Parameters.AddWithValue("$id", imageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new Exception("Missing EXIF/cache metadata.");
        return string.Join("\u001f", Enumerable.Range(0, 3)
            .Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetString(i)));
    }

    private static bool CatalogContains(LocalDatabase db, string path)
    {
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM images WHERE local_path = $path COLLATE NOCASE)";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static int CountInRoot(LocalDatabase db, string path)
        => new ImageCatalogRepository(db).GetPhotoFolders()
            .Where(folder => FolderExclusionRules.IsAtOrBelow(folder.LocalPath, path))
            .Sum(folder => folder.ImageCount);

    private static Task StartTransfer(PCasa.App.MainWindow window,
        string[] paths, string target, bool move)
    {
        var method = typeof(PCasa.App.MainWindow).GetMethod(
            "TransferGalleryFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Exception("Shared gallery file-operation path was not found.");
        return (Task?)method.Invoke(window, [paths, target, move])
            ?? throw new Exception("Transfer method did not return completion task.");
    }

    private static void Transfer(PCasa.App.MainWindow window,
        string[] paths, string target, bool move)
    {
        var task = StartTransfer(window, paths, target, move);
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
