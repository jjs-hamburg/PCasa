using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCasa.App.Controls;
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

            // A transfer takes BOTH source and destination locks so it cannot
            // race with a concurrent scanner reading the target. Never hold a
            // destination lock on the WPF test thread while waiting for the
            // transfer itself: that would deadlock the test, not the app.
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

            // Regression for a REAL native mouse gesture: async void returns
            // from MouseMove while the held button may be released; later
            // DoDragDrop then cannot deliver a physical move.
            var beginDrag = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseSurface_ImagesDragRequested",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Native gallery drag callback is missing.");
            if (beginDrag.GetCustomAttributes(
                    typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute),
                    inherit: false).Length != 0)
                throw new Exception(
                    "Native mouse drag crosses an async state-machine boundary.");
            var prepareDrag = typeof(PCasa.App.MainWindow).GetMethod(
                "PreparedDragFilesOrQueueLookup",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Synchronous drag-file preparation is missing.");

            // With catalog read lanes held, the mouse gesture MUST return
            // without blocking and without attempting a late native drag.
            var dragReadersRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dragReaders = Enumerable.Range(0, 2).Select(i =>
                scheduler.EnqueueAsync(
                    $"test:drag-held-catalog-read:{i}",
                    SchedulerPriority.Interactive,
                    async token => await dragReadersRelease.Task.WaitAsync(token),
                    workKind: SchedulerWorkKind.CatalogRead)).ToArray();
            var dragFallbackId = CatalogIdAt(db, Path.Combine(batch,
                "renamed-manually.jpg"));
            try
            {
                Until(window, () => scheduler.Snapshot().Count(job =>
                    job.Key.StartsWith("test:drag-held-catalog-read:") &&
                    job.State == SchedulerJobState.Running) == 2,
                    "Drag path lookup lanes did not block.");
                var prepared = (string[]?)prepareDrag.Invoke(window,
                    [new long[] { dragFallbackId, long.MaxValue }]);
                if (prepared is not null)
                    throw new Exception("Uncached native drag tried to start before path lookup.");
                var status = (TextBlock)window.FindName("StatusText");
                if (!status.Text.Contains("vorbereitet", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Deferred drag was not reported to the user.");
            }
            finally
            {
                dragReadersRelease.TrySetResult();
                Until(window, () => dragReaders.All(task => task.IsCompleted),
                    "Deferred drag catalog readers remained blocked.");
            }
            Console.WriteLine("PASS: Mouse gesture never awaits a catalog read; missing drag paths are prepared explicitly.");

            // In-app dragging means MOVE even between different drive roots,
            // unless Ctrl requests COPY. Explorer-origin drags still use
            // Windows same-/cross-volume behavior.
            var preferredEffect = typeof(PCasa.App.MainWindow).GetMethod(
                "PreferredDropEffect", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("Shared drag effect rule missing.");
            static DragDropEffects Effect(System.Reflection.MethodInfo method,
                bool own, bool ctrl, bool shift, bool sameRoot)
                => (DragDropEffects)method.Invoke(null,
                    [own, ctrl, shift, sameRoot,
                     DragDropEffects.Copy | DragDropEffects.Move])!;
            if (Effect(preferredEffect, true, false, false, false) != DragDropEffects.Move ||
                Effect(preferredEffect, true, true, false, true) != DragDropEffects.Copy ||
                Effect(preferredEffect, false, false, false, false) != DragDropEffects.Copy ||
                Effect(preferredEffect, false, false, false, true) != DragDropEffects.Move)
                throw new Exception("Internal move and Explorer drag intent diverged.");
            Console.WriteLine("PASS: Internal drag MOVE and Ctrl COPY respect Explorer external conventions.");

            // Right CLICK and right DRAG are different gestures. The menu
            // can only be requested after moving beyond Windows' native drag
            // distance; the drag-origin data explicitly tags the right mouse
            // button rather than guessing from keyboard modifiers.
            var threshold = typeof(BrowseGallerySurface).GetMethod(
                "PassedDragThreshold", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("Missing right-click versus right-drag threshold.");
            bool IsDrag(Point from, Point to) =>
                (bool)threshold.Invoke(null, [from, to])!;
            var dx = SystemParameters.MinimumHorizontalDragDistance;
            var dy = SystemParameters.MinimumVerticalDragDistance;
            if (IsDrag(new Point(0, 0), new Point(dx / 2, dy / 2)) ||
                !IsDrag(new Point(0, 0), new Point(dx, 0)) ||
                !IsDrag(new Point(0, 0), new Point(0, dy)))
                throw new Exception("Right-button gesture cannot distinguish menu click from drag.");
            var choice = typeof(PCasa.App.MainWindow).GetMethod(
                "RightDragNeedsChoice", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("Right-drop choice decision is missing.");
            var ownRightDrag = new DataObject(DataFormats.FileDrop, new[] { original });
            ownRightDrag.SetData("PCasa.InternalGalleryDrag", true);
            ownRightDrag.SetData("PCasa.InternalGalleryRightDrag", true);
            bool WantsChoice(DataObject data, bool accepted) =>
                (bool)choice.Invoke(null, [data, accepted])!;
            if (!WantsChoice(ownRightDrag, true) ||
                WantsChoice(ownRightDrag, false) ||
                WantsChoice(new DataObject(DataFormats.FileDrop, new[] { original }), true))
                throw new Exception("The right-drop menu can appear on a left/external drag.");
            var leftOrigin = new BrowseImageClickedEventArgs(1, ModifierKeys.None);
            var rightOrigin = new BrowseImageClickedEventArgs(1, ModifierKeys.None,
                System.Windows.Input.MouseButton.Right);
            if (leftOrigin.DragButton != System.Windows.Input.MouseButton.Left ||
                rightOrigin.DragButton != System.Windows.Input.MouseButton.Right)
                throw new Exception("Mouse-button origin was lost in gallery drag event.");
            Console.WriteLine("PASS: Right-click threshold and right-drop ownership are independent of left/native drag.");

            // The footer design is an actual hairline slider, not the old
            // rectangular 11x16 capsule visually scaled down.
            var thumbTemplate = window.FindResource(
                "PcasaCompactSliderThumbTemplate") as ControlTemplate
                ?? throw new Exception("Footer grip template unavailable.");
            var gripRoot = thumbTemplate.LoadContent() as Grid;
            var grip = gripRoot?.Children.OfType<
                System.Windows.Shapes.Ellipse>().SingleOrDefault();
            var sizeSlider = (Slider)window.FindName("ThumbnailSizeSlider");
            var spacingSlider = (Slider)window.FindName("ThumbnailSpacingSlider");
            sizeSlider.ApplyTemplate();
            spacingSlider.ApplyTemplate();
            foreach (var slider in new[] { sizeSlider, spacingSlider })
            {
                var track = slider.Template.FindName("PART_Track", slider)
                    as System.Windows.Controls.Primitives.Track;
                if (track?.Thumb is not { Width: 20, Height: 24 } ||
                    slider.Template.LoadContent() is not Grid sliderRoot ||
                    !sliderRoot.Children.OfType<Border>().Any(rail =>
                        rail.Height == 2))
                    throw new Exception("Footer sliders reverted to the oversized WPF design.");
            }
            if (grip is null || grip.Width != 12 || grip.Height != 12)
                throw new Exception("Footer still uses a bulky rectangular slider knob.");
            Console.WriteLine("PASS: Fine hairline footer sliders have 12px flat grips with accessible drag targets.");

            // A completed in-app MOVE already owns its catalog mutation.
            // The native OLE Move result must NOT enqueue a second manual
            // external-reconciliation job and preempt its own transfer.
            var needsExternal = typeof(PCasa.App.MainWindow).GetMethod(
                "NeedsExternalMoveReconciliation",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new Exception("Missing internal/external drop ownership distinction.");
            static bool NeedsExternal(System.Reflection.MethodInfo method,
                DragDropEffects result, bool acceptedInternal)
                => (bool)method.Invoke(null, [result, acceptedInternal])!;
            if (NeedsExternal(needsExternal, DragDropEffects.Move, acceptedInternal: true) ||
                !NeedsExternal(needsExternal, DragDropEffects.Move, acceptedInternal: false) ||
                NeedsExternal(needsExternal, DragDropEffects.Copy, acceptedInternal: false))
                throw new Exception(
                    "Accepted in-app gallery drop still schedules external MOVE reconciliation.");
            Console.WriteLine("PASS: Internal MOVE does not self-preempt via external reconciliation.");


            // A partly completed multi-photo move may temporarily change
            // the catalog's source/target identities. It must NEVER publish
            // a filtered/torn gallery containing only those two folders.
            // Keep the second source locked while its first sibling moves.
            var midFlightTarget = Path.Combine(root, "MidFlight");
            Directory.CreateDirectory(midFlightTarget);
            var firstMidFlight = renamed;
            var secondMidFlight = Path.Combine(batch, "test - Kopie.jpg");
            var firstMovingImageId = CatalogIdAt(db, firstMidFlight);
            var sourceMovingFolderId = new ImageCatalogRepository(db)
                .GetPhotoFolders().Single(folder =>
                    string.Equals(folder.LocalPath, batch,
                        StringComparison.OrdinalIgnoreCase)).Id;
            var unrelatedId = CatalogIdAt(db, duplicate);
            var indexField = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry> Displayed()
                => (IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    indexField.GetValue(window)!;
            Until(window, () => Displayed().Any(image => image.ImageId == unrelatedId),
                "Unrelated folder was already absent before starting manual transfer.");
            Task midFlightTransfer;
            lock (SourceFileLocks.ForPath(secondMidFlight))
            {
                midFlightTransfer = StartTransfer(window,
                    [firstMidFlight, secondMidFlight], midFlightTarget, move: true);
                Until(window, () =>
                    File.Exists(Path.Combine(midFlightTarget,
                        Path.GetFileName(firstMidFlight))) &&
                    File.Exists(secondMidFlight) && !midFlightTransfer.IsCompleted,
                    "First image did not move independently before the second locked source.");

                // A completed file step must publish its two affected folders
                // immediately, without waiting for the next blocked step or
                // running a wholesale NAS/gallery catalog refresh.
                Until(window, () =>
                {
                    var targetFolder = new ImageCatalogRepository(db)
                        .GetPhotoFolders().FirstOrDefault(folder =>
                            string.Equals(folder.LocalPath, midFlightTarget,
                                StringComparison.OrdinalIgnoreCase));
                    return targetFolder is not null &&
                        Displayed().Any(image =>
                            image.ImageId == firstMovingImageId &&
                            image.PhotoFolderId == targetFolder.Id) &&
                        !Displayed().Any(image =>
                            image.ImageId == firstMovingImageId &&
                            image.PhotoFolderId == sourceMovingFolderId) &&
                        surface.GetFolderOffset(targetFolder.Id) is not null;
                }, "Source and target remained frozen until manual package completion.");
                Console.WriteLine(
                    "PASS: Source loses and destination gains each committed image while move remains active.");

                // Pump WPF beyond the publication-debounce interval while the
                // manual package remains incomplete and source image B blocked.
                var observeUntil = DateTime.UtcNow.AddMilliseconds(1400);
                Until(window, () => DateTime.UtcNow >= observeUntil,
                    "WPF dispatcher stopped responding during mid-flight move.");
                if (!Displayed().Any(image => image.ImageId == unrelatedId))
                    throw new Exception(
                        "A partial move temporarily emptied an unrelated gallery folder.");
            }
            Until(window, () => midFlightTransfer.IsCompleted,
                "Partially completed manual package did not resume after lock release.");
            midFlightTransfer.GetAwaiter().GetResult();
            if (!Displayed().Any(image => image.ImageId == unrelatedId))
                throw new Exception(
                    "Completed manual move lost the unaffected gallery projection.");
            Console.WriteLine(
                "PASS: Unrelated gallery stays populated throughout an interrupted 2-image move.");

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

            // Unknown destinations require import AFTER physical relocation,
            // but neither step may race a decoder holding the destination lock.
            // Verify that the transfer yields at the shared source gate and
            // completes ingestion under the SAME manual package once released.
            var uncachedSource = Path.Combine(first, "physical-first.jpg");
            var uncachedDestination = Path.Combine(third, "physical-first.jpg");
            WriteJpeg(uncachedSource);
            Task deferredIndexing;
            lock (SourceFileLocks.ForPath(uncachedDestination))
            {
                deferredIndexing = StartTransfer(window, [uncachedSource], third, move: true);
                Until(window, () => scheduler.Snapshot().Any(job =>
                        job.Key.StartsWith("gallery:file-transfer:") &&
                        job.State == SchedulerJobState.Running &&
                        string.Equals(job.CurrentPath, uncachedSource,
                            StringComparison.OrdinalIgnoreCase)),
                    "Uncatalogued move never reached destination lock.");
                // A scanner cannot read a partially moved/replaced destination.
                if (deferredIndexing.IsCompleted ||
                    !File.Exists(uncachedSource) || File.Exists(uncachedDestination))
                    throw new Exception(
                        "Transfer ignored the destination-image lock.");
            }
            Until(window, () => deferredIndexing.IsCompleted,
                "Deferred physical move and image indexing did not complete.");
            deferredIndexing.GetAwaiter().GetResult();
            if (File.Exists(uncachedSource) || !File.Exists(uncachedDestination) ||
                !CatalogContains(db, uncachedDestination))
                throw new Exception("Package finished without physical move and indexed destination image.");
            Console.WriteLine("PASS: Uncatalogued move honors destination lock and finishes same-package EXIF, cache and catalog import.");

            // An earlier manual file move may be stuck in a source lock.
            // The user's next manual drop must become a QUEUED scheduler job,
            // not vanish behind a separate _galleryFileOperationRunning gate.
            var firstQueuedSource = Path.Combine(first, "two-transfers-a.jpg");
            var secondQueuedSource = Path.Combine(first, "two-transfers-b.jpg");
            var firstQueuedTarget = Path.Combine(third, "two-transfers-a.jpg");
            var secondQueuedTarget = Path.Combine(third, "two-transfers-b.jpg");
            WriteJpeg(firstQueuedSource);
            WriteJpeg(secondQueuedSource);
            Task firstQueuedTransfer;
            Task secondQueuedTransfer;
            lock (SourceFileLocks.ForPath(firstQueuedSource))
            {
                firstQueuedTransfer = StartTransfer(window,
                    [firstQueuedSource], third, move: true);
                Until(window, () => scheduler.Snapshot().Any(job =>
                        job.Key.StartsWith("gallery:file-transfer:") &&
                        job.State == SchedulerJobState.Running &&
                        string.Equals(job.CurrentPath, firstQueuedSource,
                            StringComparison.OrdinalIgnoreCase)),
                    "The first manual move never reached its blocked source.");
                secondQueuedTransfer = StartTransfer(window,
                    [secondQueuedSource], third, move: true);
                Until(window, () => scheduler.Snapshot().Any(job =>
                        job.Key.StartsWith("gallery:file-transfer:") &&
                        job.State == SchedulerJobState.Queued),
                    "The second manual move was silently discarded before the scheduler.");
            }
            try
            {
                Until(window, () => firstQueuedTransfer.IsCompleted &&
                    secondQueuedTransfer.IsCompleted,
                    "Two manual moves did not drain the scheduler queue.");
            }
            catch (TimeoutException ex)
            {
                var outstanding = string.Join(" | ", scheduler.Snapshot()
                    .Where(job => job.Key.StartsWith("gallery:file-transfer:",
                        StringComparison.Ordinal))
                    .TakeLast(6)
                    .Select(job => $"{job.Key}: {job.State} / {job.Phase} / " +
                        $"{job.Error} / {job.Processed}/{job.Total}"));
                var heldGate = SourceFileLocks.ForPath(firstQueuedSource);
                var sourceGateAvailable = Monitor.TryEnter(heldGate);
                if (sourceGateAvailable) Monitor.Exit(heldGate);
                throw new TimeoutException("Manual LIFO stalled. " +
                    $"SourceGateAvailable={sourceGateAvailable}, " +
                    $"First={firstQueuedTransfer.Status}, " +
                    $"Second={secondQueuedTransfer.Status}; " +
                    $"A={File.Exists(firstQueuedSource)}->{File.Exists(firstQueuedTarget)}, " +
                    $"B={File.Exists(secondQueuedSource)}->{File.Exists(secondQueuedTarget)}; " +
                    outstanding, ex);
            }
            firstQueuedTransfer.GetAwaiter().GetResult();
            secondQueuedTransfer.GetAwaiter().GetResult();
            if (File.Exists(firstQueuedSource) || File.Exists(secondQueuedSource) ||
                !File.Exists(firstQueuedTarget) || !File.Exists(secondQueuedTarget))
                throw new Exception(
                    "Queued manual moves did not physically relocate both source files. " +
                    $"A source={File.Exists(firstQueuedSource)}, target={File.Exists(firstQueuedTarget)}; " +
                    $"B source={File.Exists(secondQueuedSource)}, target={File.Exists(secondQueuedTarget)}; " +
                    $"Task A={firstQueuedTransfer.Status}, B={secondQueuedTransfer.Status}; " +
                    $"UI={(window.FindName("StatusText") as TextBlock)?.Text}; " +
                    "A-locations=" + string.Join(",", Directory.GetFiles(root,
                        "*two-transfers-a*", SearchOption.AllDirectories)) + "; " +
                    "B-locations=" + string.Join(",", Directory.GetFiles(root,
                        "*two-transfers-b*", SearchOption.AllDirectories)) + "; " +
                    "Jobs=" + string.Join(" | ", scheduler.Snapshot()
                        .Where(job => job.Key.StartsWith("gallery:file-transfer:",
                            StringComparison.Ordinal)).TakeLast(5)
                        .Select(job => $"{job.Key}:{job.State}/{job.Phase}/{job.Error}")));
            Console.WriteLine(
                "PASS: Overlapping manual drops are queued, executed and visible without losing files.");

            // The same-volume path, including replacement, must use a
            // native rename; it must never stage a full image over the VPN.
            var fastSource = Path.Combine(first, "fast-rename-photo.jpg");
            var fastTarget = Path.Combine(third, "fast-rename-photo.jpg");
            var fastOriginal = new byte[96 * 1024];
            Random.Shared.NextBytes(fastOriginal);
            File.WriteAllBytes(fastSource, fastOriginal);
            var fastMode = ServerSideFileMover.Move(fastSource, fastTarget);
            if (fastMode != ServerSideFileMover.MoveMethod.Rename ||
                File.Exists(fastSource) ||
                !File.ReadAllBytes(fastTarget).SequenceEqual(fastOriginal))
                throw new Exception("Same-volume rename copied data or lost image content.");

            var replaceSource = Path.Combine(first, "fast-replace.jpg");
            var replaceTarget = Path.Combine(third, "fast-replace.jpg");
            File.WriteAllBytes(replaceSource, fastOriginal);
            File.WriteAllText(replaceTarget, "old-data");
            var replaceMode = ServerSideFileMover.Move(
                replaceSource, replaceTarget, replace: true);
            if (replaceMode != ServerSideFileMover.MoveMethod.Rename ||
                File.Exists(replaceSource) ||
                !File.ReadAllBytes(replaceTarget).SequenceEqual(fastOriginal))
                throw new Exception("Replace move was not a server-side rename.");
            Console.WriteLine("PASS: Rename and overwrite use OS metadata moves, not byte copies.");

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
                    "_visibleManualWindow",
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

            // Reconstruct an ACTUAL durable gallery package at the exact
            // power-loss boundary: physical rename done, per-file receipt
            // still says 'executing', and the scheduler cursor is at step A.
            // This exercises the real registered factory and catalog identity,
            // not only the legacy standalone relocation-intent reconciler.
            var durableSource = Path.Combine(first, "durable-restart.jpg");
            var durableTarget = Path.Combine(second, "durable-restart.jpg");
            WriteJpeg(durableSource);
            new FolderScanner(new ImageCatalogRepository(db), new ThumbnailCache(),
                folderRepo).ScanFile(sourceRoot, durableSource);
            var durableImageId = CatalogIdAt(db, durableSource);
            var durableImageCache = ThumbnailIdentities(db, durableImageId);
            var durableKey = "gallery:file-transfer:" + Guid.NewGuid().ToString("N");
            var durablePayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                Sources = new[] { durableSource },
                Destination = second,
                Move = true
            });
            var durableLedger = new DurablePackageJournal(db);
            durableLedger.Put(durableKey, "gallery-transfer-v1", durablePayload,
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            durableLedger.SetState(durableKey, "running", 1);
            var fileReceipts = new DurableGalleryTransferJournal(db);
            var durableReceipt = new GalleryTransferReceipt(
                durableSource, durableTarget, true, false, sourceRoot.Id,
                durableImageId, null, "gallery-transfer:" + durableKey + ":0",
                "planned");
            fileReceipts.Prepare(durableKey, 0, durableReceipt);
            fileReceipts.EnsureRelocationIntent(durableReceipt);
            fileReceipts.Advance(durableKey, 0, "planned", "executing");
            File.Move(durableSource, durableTarget);

            var packageFactory = typeof(PCasa.App.MainWindow)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Single(method => method.Name == "BuildDurableGalleryTransferPackage" &&
                    method.GetParameters().Length == 2);
            var reconstructed = (SchedulerTaskPackage?)packageFactory.Invoke(
                window, [durableKey, durablePayload])
                ?? throw new Exception("Durable gallery package factory missing.");
            var replay = scheduler.EnqueueDurablePackageAsync(reconstructed,
                "gallery-transfer-v1", durablePayload, SchedulerPriority.ManualAction);
            Until(window, () => replay.IsCompleted,
                "Restarted durable package did not complete.");
            replay.GetAwaiter().GetResult();
            if (File.Exists(durableSource) || !File.Exists(durableTarget) ||
                CatalogIdAt(db, durableTarget) != durableImageId ||
                !durableImageCache.SequenceEqual(ThumbnailIdentities(db, durableImageId)) ||
                new DurablePackageJournal(db).Pending().Any(item =>
                    item.Key == durableKey))
                throw new Exception(
                    "Restarted durable gallery package duplicated a move or lost image identity.");
            Console.WriteLine(
                "PASS: Restarted gallery package reconciles physical move exactly once.");


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

            // Two user operations may overlap in separate scheduler lanes.
            // Finishing an older operation must not close the current
            // foreground presentation or replace its status.
            var beginManual = typeof(PCasa.App.MainWindow).GetMethod(
                "BeginVisibleManualOperation",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("Shared manual scheduler presenter missing.");
            var endManual = typeof(PCasa.App.MainWindow).GetMethod(
                "EndVisibleManualOperation",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("Shared manual presenter end missing.");
            beginManual.Invoke(window, ["test:older-manual", "Älterer Auftrag", 1]);
            beginManual.Invoke(window, ["test:newer-manual", "Neuerer Auftrag", 1]);
            endManual.Invoke(window, ["test:older-manual"]);
            var currentProgress = Application.Current.Windows
                .OfType<PCasa.App.BrowseCacheProgressWindow>()
                .FirstOrDefault(p => p.IsVisible);
            if (currentProgress is null ||
                (currentProgress.FindName("PhaseText") as TextBlock)?.Text
                    is not string currentPhase ||
                !currentPhase.Contains("Neuerer Auftrag", StringComparison.Ordinal))
                throw new Exception(
                    "Older manual completion replaced or closed a newer progress presenter.");
            endManual.Invoke(window, ["test:newer-manual"]);
            Console.WriteLine(
                "PASS: Older manual completion cannot hide newer scheduler progress.");

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
