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
        ManualResetEventSlim? explorerWatchdogStop = null;
        DispatcherTimer? explorerModalProbe = null;
        string? explorerModalError = null;
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

            var app = new PCasa.App.App
            {
                // Two real window lifecycles in one WPF process.
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
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

            // Previously the Taskmanager was EMPTY while this exact running
            // initial import already displayed "Checking file" and had
            // committed its first JPEG. The watcher is intentionally OFF in
            // this test, so projecting only watcher events cannot pass.
            var openTaskManager = typeof(PCasa.App.MainWindow).GetMethod(
                "TaskManagerMenu_Click",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new Exception("Taskmanager command is missing.");
            openTaskManager.Invoke(window, new object[] { window, new RoutedEventArgs() });
            var taskManager = Application.Current.Windows
                .OfType<PCasa.App.TaskManagerWindow>().Single();
            try
            {
                var taskGrid = (DataGrid)typeof(PCasa.App.TaskManagerWindow)
                    .GetField("_list", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(taskManager)!;
                Until(window, () => taskGrid.Items.Cast<object>().Any(row =>
                    row.GetType().GetProperty("Title")?.GetValue(row) is string title &&
                    title.Contains(UiLanguage.T("Erstimport"), StringComparison.Ordinal) &&
                    row.GetType().GetProperty("Status")?.GetValue(row) is "Läuft" &&
                    row.GetType().GetProperty("Directory")?.GetValue(row) is string path &&
                    (SamePath(path, initialRoot) ||
                     SamePath(path, laterFolder))),
                    "Taskmanager stayed EMPTY while the first import was actually running.");
                var runningImport = taskGrid.Items.Cast<object>().First(row =>
                    row.GetType().GetProperty("Title")?.GetValue(row) is string title &&
                    title.Contains(UiLanguage.T("Erstimport"), StringComparison.Ordinal));
                if (runningImport.GetType().GetProperty("Deletable")
                        ?.GetValue(runningImport) is not true)
                    throw new Exception("Running automatic import has no safe Stop action.");
                Console.WriteLine("PASS: Taskmanager sees live initial import without watcher, with real directory and safe Stop.");
            }
            finally { taskManager.Close(); }

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

            // The previous Edit-menu command is now an icon-only toggle
            // immediately left of the property Filter. Both edges must call
            // the SAME folder inversion and undo on the second click.
            initialTreeRoot.IsSelected = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var invertSelection = (System.Windows.Controls.Primitives.ToggleButton?)window.FindName("GalleryInvertSelectionToggle")
                ?? throw new Exception("Folder selection inversion toggle missing.");
            var selectedImages = (HashSet<long>)typeof(PCasa.App.MainWindow)
                .GetField("_selectedImageIds", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            if (selectedImages.Count != 0)
                throw new Exception("New gallery folder should start unselected.");
            invertSelection.IsChecked = true;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (selectedImages.Count != 1)
                throw new Exception("Inversion toggle selected anything outside the current folder.");
            invertSelection.IsChecked = false;
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (selectedImages.Count != 0)
                throw new Exception("Second inversion toggle did not restore previous selection.");
            Console.WriteLine("PASS: Folder selection inversion toggle affects only the selected folder.");

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
                    4, 2, 2, 0, "scan-background-latest.jpg", Added: 2, ReadingNew: true));
                sharedProgress.EndManualOperation();
                var phaseText = (TextBlock?)sharedProgress.FindName("PhaseText");
                var currentFileText = (TextBlock?)sharedProgress.FindName("CurrentFileText");
                var countText = (TextBlock?)sharedProgress.FindName("CountText");
                if (phaseText?.Text != "Bild einlesen" ||
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

            var firstWindow = window;
            firstWindow.Close();
            Until(firstWindow, () => !firstWindow.IsVisible,
                "The first window's asynchronous scheduler/ExifTool shutdown did not finish.");
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
            // Simulate a durable P0 NAS job parked across restart. Its
            // presence must never leave an already indexed gallery blank.
            // Unlike an ordinary transient blocker, this exercises the
            // journal-level P0 admission fence that formerly starved P1 reads.
            const string parkedStartupKey = "test:startup-deferred-manual-scan";
            var startupJournal = new DurablePackageJournal(db);
            startupJournal.Put(parkedStartupKey, "manual-folder-scan-v1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Directory = afterRestart,
                    MonitoredRootId = roots.GetAll().Single(root =>
                        SamePath(root.LocalPath, afterRestart)).Id
                }), SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
            startupJournal.Defer(parkedStartupKey, 0,
                "Test fixture: parked manual source",
                TimeSpan.FromMinutes(30));
            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            tree = (TreeView)window.FindName("FoldersTree");
            surface = (BrowseGallerySurface)window.FindName("BrowseSurface");
            try
            {
                Until(window, () =>
                    Walk(tree).Any(item => SamePath(PathOf(item), newFolderPath)),
                    "Persisted folder tree stayed blank behind deferred P0 at startup.");
                Until(window, () => surface.GetFolderOffset(firstFolder.Id).HasValue,
                    "Existing photo gallery stayed blank behind deferred P0 at startup.");
                Console.WriteLine(
                    "PASS: Startup folder tree and committed images render with deferred durable P0.");
            }
            finally
            {
                if (!startupJournal.Cancel(parkedStartupKey))
                    throw new Exception("Could not retire startup P0 test fixture.");
            }
            Console.WriteLine("PASS: Empty NAS directory survives PCasa restart without any images.");
            Until(window, () =>
                Walk(tree).Any(item => SamePath(PathOf(item), afterRestart)),
                "Pending root was missing from tree after restart.");
            Until(window, () => surface.GetFolderOffset(firstFolder.Id).HasValue &&
                roots.GetPendingInitialScans().Any(root => SamePath(root.LocalPath, afterRestart)),
                "Existing thumbnails vanished while another folder's scan was blocked.");
            Console.WriteLine("PASS: Existing thumbnails stay accessible while a separate pending root is blocked.");

            // A previously unseen file created while monitoring is disabled
            // requires root reconciliation, but NOT ahead of an unfinished
            // durable first import on another root. The old startup sweep
            // scanned thousands of already indexed images first.
            var missedPhoto = Path.Combine(initialRoot, "previously-unseen.jpg");
            WriteJpeg(missedPhoto);
            new BackgroundScanSettings { Enabled = true }.Save();
            try
            {
                var monitorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                var knownField = typeof(PCasa.App.MainWindow).GetField(
                    "_backgroundKnownFolderIds", monitorFlags)!;
                Until(window, () =>
                        knownField.GetValue(window) is HashSet<long> known &&
                        known.Contains(roots.GetAll().Single(root =>
                            SamePath(root.LocalPath, initialRoot)).Id) &&
                        roots.GetPendingInitialScans().Any(root =>
                            SamePath(root.LocalPath, afterRestart)),
                    "Monitor never attached to completed root.");
                if (new ImageCatalogRepository(db).CountAll() != 2)
                    throw new Exception(
                        "Full root reconciliation ran before the durable first import.");
            }
            catch (TimeoutException ex)
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object? Field(string name) =>
                    typeof(PCasa.App.MainWindow).GetField(name, flags)?.GetValue(window);
                var pending = Field("_backgroundPending") as System.Collections.IDictionary;
                var known = Field("_backgroundKnownFolderIds") as HashSet<long>;
                var configured = Field("_backgroundScanSettings")
                    as BackgroundScanSettings;
                var offline = Field("_backgroundOfflineRootPaths") as string[];
                var lifetime = Field("_backgroundLifetime")
                    as CancellationTokenSource;
                var debounce = Field("_backgroundDebounceTimer") as DispatcherTimer;
                var jobs = ((PCasaScheduler)Field("_scheduler")!).Snapshot()
                    .Where(job => job.State is SchedulerJobState.Running or
                        SchedulerJobState.Queued or SchedulerJobState.YieldRequested)
                    .Select(job => job.Key + ":" + job.State + ":" + job.Priority)
                    .Take(16);
                throw new TimeoutException(
                    ex.Message + " | count=" + new ImageCatalogRepository(db).CountAll() +
                    " pendingRoots=" + string.Join(",", roots.GetPendingInitialScans()
                        .Select(root => root.LocalPath)) +
                    " | bgEnabled=" + BackgroundScanSettings.Load().Enabled +
                    " monitor=" + Field("_backgroundMonitorRefreshRunning") +
                    " monitorRequested=" + Field("_backgroundMonitorRefreshRequested") +
                    " initialized=" + Field("_backgroundScanInitialized") +
                    " configuredEnabled=" + configured?.Enabled +
                    " monitoringClosing=" + Field("_backgroundMonitorClosing") +
                    " monitorNull=" + (Field("_backgroundFolderMonitor") is null) +
                    " lifetimeCanceled=" + lifetime?.IsCancellationRequested +
                    " offline=" + string.Join(",", offline ?? []) +
                    " known=" + string.Join(",", known ?? []) +
                    " roots=" + string.Join(",", roots.GetAll().Select(root =>
                        root.Id + ":" + root.LocalPath)) +
                    " pending=" + (pending?.Count.ToString() ?? "?") +
                    " bgWorker=" + Field("_backgroundWorkerRunning") +
                    " browseCache=" + Field("_browseCacheBuilding") +
                    " manualScan=" + Field("_manualScanRunning") +
                    " isScanning=" + Field("_isScanning") +
                    " debounceEnabled=" + debounce?.IsEnabled +
                    " | scheduler=" + string.Join("; ", jobs), ex);
            }
            Console.WriteLine(
                "PASS: Startup root reconciliation is deferred while a durable first import is unfinished.");

            // Verify an actual NEW file notification, not just startup/root
            // reconciliation. Rename a complete JPEG into place to ensure
            // the watcher never reads a partially written file.
            var incomingTemp = Path.Combine(initialRoot, "incoming-temporary.bin");
            var incomingPhoto = Path.Combine(initialRoot, "incoming-during-import.jpg");
            WriteJpeg(incomingTemp);
            File.Move(incomingTemp, incomingPhoto);
            Until(window, () =>
                    new ImageCatalogRepository(db).CountAll() == 3 &&
                    roots.GetPendingInitialScans().Any(root =>
                        SamePath(root.LocalPath, afterRestart)),
                "Live file-system notification was blocked by a different pending first import.");
            Console.WriteLine(
                "PASS: New file notifications are indexed while a separate first import is blocked.");

            releaseRestartPhoto.Set();
            blockedRestart.GetAwaiter().GetResult();
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() >= 4 &&
                roots.GetPendingInitialScans().Count == 0,
                "Restarted durable first import did not resume after independent file notifications.");
            Until(window, () =>
                new ImageCatalogRepository(db).CountAll() == 5,
                "Deferred root reconciliation lost the file created while monitoring was disabled.");
            Console.WriteLine("PASS: Live discovery runs before durable first-import completion; deferred root reconciliation catches offline changes afterwards.");

            // Explorer Ansicht is an in-place expansion of FoldersTree,
            // not a replacement control. Even hidden and unindexed folders
            // appear while existing WPF nodes, selection and expansion survive.
            var neverScanned = Path.Combine(initialRoot, "ExplorerNotIndexed");
            var hiddenExplorer = Path.Combine(initialRoot, "ExplorerHidden");
            var inactiveExplorer = Path.Combine(initialRoot, "ExplorerInactive");
            var firstExplorer = Path.Combine(initialRoot, "AaaaExplorerFirst");
            var lastExplorer = Path.Combine(initialRoot, "ZzzzExplorerLast");
            var otherBranchOnly = Path.Combine(afterRestart, "OutsideSelectedBranch");
            Directory.CreateDirectory(neverScanned);
            Directory.CreateDirectory(hiddenExplorer);
            Directory.CreateDirectory(inactiveExplorer);
            Directory.CreateDirectory(firstExplorer);
            Directory.CreateDirectory(lastExplorer);
            Directory.CreateDirectory(otherBranchOnly);
            File.SetAttributes(hiddenExplorer,
                File.GetAttributes(hiddenExplorer) | FileAttributes.Hidden);
            WriteJpeg(Path.Combine(inactiveExplorer, "registered.jpg"));

            // Exclude two siblings under the SAME monitored root. Explorer
            // must reactivate only the requested child, not register a second
            // root or silently re-enable the other excluded sibling.
            var presentRoots = roots.GetAll();
            var choices = presentRoots.Select(root =>
                new FolderManagerRootChoice(root.LocalPath,
                    SamePath(root.LocalPath, initialRoot)
                        ? new[] { inactiveExplorer, lastExplorer }
                        : roots.GetExcludedPaths(root.Id).ToArray(),
                    root.ServerSyncEnabled)).ToArray();
            new FolderManagerSelectionService(db).Apply(
                new FolderManagerSelection(choices, BackgroundScanEnabled: false));
            // This synthetic setup changes SQLite outside the real WPF
            // Ordnermanager. Publish the resulting committed selection to
            // the same UI snapshot that the manager's OK handler updates.
            // Otherwise the following rejected Scan command incorrectly
            // uses the OLD cached inclusion state and starts a delayed
            // validation that can fail while Explorer Add is running.
            var committedProjection = roots.GetAll().Select(root =>
                (Folder: root, ExcludedPaths:
                    (IReadOnlyList<string>)roots.GetExcludedPaths(root.Id))).ToArray();
            var projectionField = typeof(PCasa.App.MainWindow).GetField(
                "_dropTargetProjection",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new Exception("Folder projection missing.");
            projectionField.SetValue(window, committedProjection);
            if (!committedProjection.Any(root =>
                    SamePath(root.Folder.LocalPath, initialRoot) &&
                    root.ExcludedPaths.Any(path => SamePath(path, inactiveExplorer))))
                throw new Exception("Excluded Explorer child was not published in WPF setup.");

            new BackgroundScanSettings { Enabled = false }.Save();
            var selectedRoot = Walk(tree).Single(item =>
                SamePath(PathOf(item), initialRoot));
            var unrelatedRoot = Walk(tree).Single(item =>
                SamePath(PathOf(item), afterRestart));
            unrelatedRoot.IsExpanded = true;
            var unrelatedRootBefore = unrelatedRoot;
            var unrelatedChildrenBefore = unrelatedRoot.Items
                .OfType<TreeViewItem>().ToArray();
            selectedRoot.IsSelected = true;
            // A collapsed selected folder must also enumerate physical children.
            selectedRoot.IsExpanded = false;
            var treeBefore = selectedRoot;
            var expandedBefore = treeBefore.IsExpanded;
            var originalRoots = presentRoots.Select(r => r.LocalPath).ToArray();
            var browserButton = (Button)window.FindName("ShowAllFilesystemFoldersButton");
            // Test the REAL WPF toggle while the app's scheduler has an
            // occupied P0 writer and another P0 is waiting. Earlier tests
            // used an idle scheduler and missed the user's actual blocker.
            var explorerScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            var foregroundP0Started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseForegroundP0 = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var blockedForegroundP0 = explorerScheduler.EnqueueAsync(
                "test:viewer-explorer-running-P0", SchedulerPriority.ManualAction,
                async _ =>
                {
                    foregroundP0Started.TrySetResult();
                    await releaseForegroundP0.Task;
                }, workKind: SchedulerWorkKind.CatalogScan);
            try
            {
                Until(window, () => foregroundP0Started.Task.IsCompleted,
                    "Test could not start a blocked durable-priority catalog task.");
                browserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, browserButton));
                Until(window, () =>
                        Walk(tree).Any(item => SamePath(PathOf(item), neverScanned)) &&
                        Walk(tree).Any(item => SamePath(PathOf(item), hiddenExplorer)) &&
                        Walk(tree).Any(item => SamePath(PathOf(item), inactiveExplorer)) &&
                        Walk(tree).Any(item => SamePath(PathOf(item), firstExplorer)) &&
                        Walk(tree).Any(item => SamePath(PathOf(item), lastExplorer)),
                    "Explorer did not reveal non-catalogued folders while P0 was blocked.");
                if (blockedForegroundP0.IsCompleted)
                    throw new Exception(
                        "Explorer UI only completed because the P0 blocker already finished.");
            }
            finally { releaseForegroundP0.TrySetResult(); }
            Until(window, () => blockedForegroundP0.IsCompleted,
                "Blocked P0 fixture did not finish after release.");
            Console.WriteLine(
                "PASS: Actual WPF Explorer shows physical unindexed folders while P0 is blocked.");

            // The sibling root is expanded too, but Explorer mode must not
            // touch it: only the CURRENT selection and its descendants qualify.
            if (Walk(tree).Any(item => SamePath(PathOf(item), otherBranchOnly)) ||
                !ReferenceEquals(unrelatedRootBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), afterRestart))) ||
                !unrelatedRoot.IsExpanded ||
                !unrelatedChildrenBefore.SequenceEqual(
                    unrelatedRoot.Items.OfType<TreeViewItem>()))
                throw new Exception(
                    "Explorer Ansicht scanned or changed an unrelated expanded branch.");

            // Insert before, between and after EXISTING folders in the same
            // alphabetical ordering as the normal catalog tree, without
            // reconstructing any already opened WPF tree nodes.
            var directChildren = selectedRoot.Items.OfType<TreeViewItem>()
                .Where(item => PathOf(item) is not null).ToArray();
            var names = directChildren.Select(item =>
                Path.GetFileName(PathOf(item)!)).ToArray();
            if (!names.SequenceEqual(names.OrderBy(name => name,
                    StringComparer.CurrentCultureIgnoreCase),
                    StringComparer.CurrentCultureIgnoreCase) ||
                !ReferenceEquals(selectedRoot, treeBefore))
                throw new Exception(
                    "Explorer child folders are not alphabetically interleaved.");
            if (window.FindName("ExplorerTree") is not null ||
                !ReferenceEquals(treeBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), initialRoot))) ||
                expandedBefore || !treeBefore.IsExpanded ||
                browserButton.Tag as string != "ActiveGold")
                throw new Exception(
                    "Explorer Ansicht replaced or collapsed the original folder tree.");

            // Lazy Explorer children require a sentinel to show the
            // expander; it must never render "Ordner laden ..." as a
            // pseudo-folder, including during a blocked network probe.
            var firstExplorerItem = Walk(tree).Single(item =>
                SamePath(PathOf(item), firstExplorer));
            var sentinel = firstExplorerItem.Items.OfType<TreeViewItem>()
                .SingleOrDefault(child => child.Tag is null);
            if (sentinel is null || sentinel.Visibility != Visibility.Collapsed ||
                sentinel.IsEnabled || sentinel.Header is not null)
                throw new Exception(
                    "Explorer lazy expander displayed its internal loading placeholder.");
            if (Walk(tree).Any(item => item.Tag is null &&
                item.Visibility == Visibility.Visible))
                throw new Exception(
                    "Explorer exposed an internal placeholder as a folder.");
            Console.WriteLine(
                "PASS: Explorer lazy expansion does not show 'Ordner laden' rows.");

            var inactiveItem = Walk(tree).Single(item =>
                SamePath(PathOf(item), inactiveExplorer));
            if (inactiveItem.Tag?.GetType().GetProperty("PhotoFolderId")?
                    .GetValue(inactiveItem.Tag) is not null)
                throw new Exception("Excluded unindexed folder already has a catalog ID.");
            Console.WriteLine("TRACE Explorer: selecting excluded path");
            inactiveItem.IsSelected = true;
            Console.WriteLine("TRACE Explorer: selected excluded path");
            var scan = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseScanCurrentFolder_Click",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Console.WriteLine("TRACE Explorer: before excluded scan");
            scan.Invoke(window, [tree, new RoutedEventArgs()]);
            Console.WriteLine("TRACE Explorer: after excluded scan");
            window.UpdateLayout();
            Console.WriteLine("TRACE Explorer: after layout");
            if (!roots.GetExcludedPaths(initialTreeRoot.Tag is null
                    ? throw new Exception("Root tag missing.")
                    : roots.GetAll().Single(root =>
                        SamePath(root.LocalPath, initialRoot)).Id)
                .Any(path => SamePath(path, inactiveExplorer)))
                throw new Exception(
                    "Explorer scan unexpectedly activated an excluded folder.");
            var add = typeof(PCasa.App.MainWindow).GetMethod(
                "BrowseAddFolder_Click",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            // Distinguish actual modal error dialogs from the normal
            // non-modal BrowseCacheProgressWindow. Closing the latter is a
            // false positive and can interfere with regular cache work.
            var explorerWindow = window;
            var scheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(window)!;
            var lastExplorerTrace = DateTime.UtcNow;
            explorerModalProbe = new DispatcherTimer(DispatcherPriority.Background,
                explorerWindow.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            explorerModalProbe.Tick += (_, _) =>
            {
                if ((DateTime.UtcNow - lastExplorerTrace).TotalSeconds >= 2)
                {
                    lastExplorerTrace = DateTime.UtcNow;
                    Console.WriteLine("TRACE Explorer: dispatcher alive; open owned windows: " +
                        string.Join(", ", Application.Current.Windows.OfType<Window>()
                            .Where(candidate => candidate.IsVisible &&
                                ReferenceEquals(candidate.Owner, explorerWindow))
                            .Select(candidate => candidate.GetType().Name)));
                }
                foreach (var dialog in Application.Current.Windows.OfType<Window>()
                             .Where(candidate => candidate.IsVisible &&
                                 ReferenceEquals(candidate.Owner, explorerWindow) &&
                                 candidate is not PCasa.App.BrowseCacheProgressWindow &&
                                 // A real manager window contains both a DockPanel and
                                 // a StackPanel, just like the generic error-dialog
                                 // heuristic below. It must never be mistaken for
                                 // an Explorer registration error and closed!
                                 candidate is not PCasa.App.FolderManagerWindow))
                {
                    // Only the actual PcasaDialog confirmation layout is an
                    // error condition here, not ordinary owned progress UI.
                    if (dialog.Content is not Grid layout ||
                        !layout.Children.OfType<DockPanel>().Any() ||
                        !layout.Children.OfType<StackPanel>().Any()) continue;
                    var body = layout.Children.OfType<DockPanel>()
                        .SelectMany(panel => panel.Children.OfType<ScrollViewer>())
                        .Select(scroll => scroll.Content as TextBox)
                        .FirstOrDefault()?.Text;
                    explorerModalError = $"Unexpected Explorer Add error dialog: {dialog.Title}: {body}";
                    Console.Error.WriteLine("FAIL: " + explorerModalError);
                    dialog.Close();
                    explorerModalProbe.Stop();
                    break;
                }
            };
            explorerModalProbe.Start();
            explorerWatchdogStop = new ManualResetEventSlim();
            var watchStop = explorerWatchdogStop;
            var explorerWatchdog = new Thread(() =>
            {
                for (var elapsed = 10; elapsed <= 50; elapsed += 10)
                {
                    if (watchStop.Wait(TimeSpan.FromSeconds(10))) return;
                    try
                    {
                        var active = scheduler.Snapshot()
                            .Where(job => job.State is SchedulerJobState.Running or
                                SchedulerJobState.Queued or SchedulerJobState.YieldRequested)
                            .Take(12)
                            .Select(job => job.State + " / " + job.Key)
                            .ToArray();
                        Console.Error.WriteLine($"TRACE Explorer {elapsed}s scheduler: " +
                            string.Join(" | ", active));
                        explorerWindow.Dispatcher.BeginInvoke(
                            DispatcherPriority.Normal,
                            new Action(() => Console.Error.WriteLine(
                                $"TRACE Explorer {elapsed}s WPF heartbeat")));
                        Console.Error.Flush();
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(
                            $"TRACE Explorer {elapsed}s watchdog sampling failed: {error}");
                    }
                }
                Console.Error.WriteLine(
                    "FAIL: Explorer Add did not finish within 50 seconds. " +
                    "See dispatcher heartbeats and scheduler snapshot above.");
                Console.Error.Flush();
                Environment.Exit(36);
            }) { IsBackground = true, Name = "ExplorerAddSmokeWatchdog" };
            explorerWatchdog.Start();
            Console.WriteLine("TRACE Explorer: before explicit add");
            add.Invoke(window, [tree, new RoutedEventArgs()]);
            Console.WriteLine("TRACE Explorer: after explicit add");
            Until(window, () =>
            {
                if (explorerModalError is not null) return true;
                var currentRoots = roots.GetAll();
                var parent = currentRoots.FirstOrDefault(root =>
                    SamePath(root.LocalPath, initialRoot));
                return parent is not null &&
                    !roots.GetExcludedPaths(parent.Id)
                        .Any(path => SamePath(path, inactiveExplorer));
            }, "Explicit Add to PCasa did not reactivate the excluded child in Ordnermanager.");
            if (explorerModalError is not null)
                throw new Exception(explorerModalError);
            Console.WriteLine("TRACE Explorer: exclusion cleared");
            var afterActivation = roots.GetAll();
            if (afterActivation.Count != originalRoots.Length ||
                afterActivation.Any(root =>
                    SamePath(root.LocalPath, inactiveExplorer)) ||
                !originalRoots.All(path => afterActivation.Any(root =>
                    SamePath(root.LocalPath, path))) ||
                !roots.GetExcludedPaths(afterActivation.Single(root =>
                    SamePath(root.LocalPath, initialRoot)).Id)
                    .Any(path => SamePath(path, lastExplorer)))
                throw new Exception(
                    "Explorer created a duplicate root or lost a sibling exclusion.");

            Until(window, () => scheduler.Snapshot().Any(job =>
                    job.Key.StartsWith("catalog:complete-scan:", StringComparison.Ordinal) &&
                    job.Key.Contains("ExplorerInactive", StringComparison.Ordinal) &&
                    job.Priority == SchedulerPriority.ManualAction),
                "Explorer manual scan did not start at P0.");
            Until(window, () => new ImageCatalogRepository(db).GetPhotoFolders()
                    .Any(folder => SamePath(folder.LocalPath, inactiveExplorer)) &&
                inactiveItem.Tag?.GetType().GetProperty("PhotoFolderId")?
                    .GetValue(inactiveItem.Tag) is long &&
                surface.GetFolderOffset(
                    new ImageCatalogRepository(db).GetPhotoFolders()
                        .Single(folder => SamePath(folder.LocalPath, inactiveExplorer)).Id)
                    is not null,
                "Explorer scanned folder was not published without rebuilding the tree.");

            // Live gallery publication can now precede the durable scan's
            // final source-receipt acknowledgement. That is correct: the
            // photo committed first, the scan completes separately. Verify
            // each lifecycle without assuming they finish in one UI tick.
            Until(window, () => !roots.GetPendingInitialScans().Any(root =>
                    SamePath(root.LocalPath, initialRoot)),
                "Targeted reactivation did not eventually complete the pending first import.");
            if (!ReferenceEquals(treeBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), initialRoot))) ||
                !treeBefore.IsExpanded)
                throw new Exception("Explorer scan replaced the original open tree nodes.");

            // The user reported that an Explorer-registered folder makes the
            // manager permanently unable to open. Open the REAL modal dialog
            // immediately after registration while manual P0 is waiting.
            VerifyManagerAvailableAfterExplorerRegistration(
                window, initialRoot, inactiveExplorer, blockManualScheduler: true);
            Console.WriteLine(
                "PASS: Real folder manager opens, expands the registered Explorer " +
                "subfolder and closes while a P0 is blocked.");

            browserButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, browserButton));
            if (browserButton.Tag is not null ||
                !ReferenceEquals(treeBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), initialRoot))) ||
                treeBefore.IsExpanded ||
                !Walk(tree).Any(item => SamePath(PathOf(item), inactiveExplorer)) ||
                Walk(tree).Any(item => SamePath(PathOf(item), neverScanned)) ||
                Walk(tree).Any(item => SamePath(PathOf(item), hiddenExplorer)) ||
                Walk(tree).Any(item => SamePath(PathOf(item), firstExplorer)) ||
                Walk(tree).Any(item => SamePath(PathOf(item), lastExplorer)) ||
                Walk(tree).Any(item => SamePath(PathOf(item), otherBranchOnly)) ||
                !ReferenceEquals(unrelatedRootBefore,
                    Walk(tree).Single(item => SamePath(PathOf(item), afterRestart))))
                throw new Exception(
                    "Explorer off destroyed original nodes or lost a newly registered root.");

            explorerWatchdogStop.Set();
            explorerModalProbe.Stop();
            Console.WriteLine(
                "PASS: Explorer enables excluded subtree within original monitored root without duplicates or lost exclusions.");

            // Real THIRD application launch, with enabled monitoring and
            // fully indexed existing roots. Merely constructing watchers
            // must not schedule another recursive scan of those roots.
            // A new photo created while PCasa is running is covered above.
            // A real, running PCasa must persist the selected tree folder
            // and focused photo before asynchronous scheduler shutdown. The
            // third window must recover them after the gallery index loads.
            var savedTree = (TreeView)window.FindName("FoldersTree");
            var rememberedItem = Walk(savedTree).First(item =>
                SamePath(PathOf(item), initialRoot));
            rememberedItem.IsSelected = true;
            Until(window, () => ReferenceEquals(savedTree.SelectedItem, rememberedItem),
                "Could not set remembered tree focus before restart.");
            var sessionFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var appearanceObject = typeof(PCasa.App.MainWindow)
                .GetField("_appearance", sessionFlags)!.GetValue(window)!;
            appearanceObject.GetType().GetProperty("RestoreGalleryPosition")!
                .SetValue(appearanceObject, true);
            long rememberedImageId;
            using (var connection = db.OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM images WHERE local_path=$path COLLATE NOCASE;";
                cmd.Parameters.AddWithValue("$path", Path.Combine(initialRoot, "first.jpg"));
                rememberedImageId = Convert.ToInt64(cmd.ExecuteScalar());
            }
            var oldSelection = (GallerySelection)typeof(PCasa.App.MainWindow)
                .GetField("_gallerySelection", sessionFlags)!.GetValue(window)!;
            var oldIds = (HashSet<long>)typeof(PCasa.App.MainWindow)
                .GetField("_selectedImageIds", sessionFlags)!.GetValue(window)!;
            oldIds.Clear();
            oldIds.Add(rememberedImageId);
            oldSelection.SetFocus(rememberedImageId);
            ((BrowseGallerySurface)window.FindName("BrowseSurface")).SetSelection(oldIds);

            var previousWindow = window;
            previousWindow.Close();
            Until(previousWindow, () => !previousWindow.IsVisible,
                "Previous WPF session did not close before scan-startup regression.");
            window = null;
            if (roots.GetPendingInitialScans().Count != 0 ||
                roots.GetPendingAutomaticInventories().Count != 0)
                throw new Exception("Startup scan regression fixture still has unfinished inventory.");
            new BackgroundScanSettings { Enabled = true }.Save();
            window = new PCasa.App.MainWindow { Width = 1100, Height = 700 };
            window.Show();
            var scanFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var firstSnapshotField = typeof(PCasa.App.MainWindow).GetField(
                "_backgroundFirstMonitorSnapshot", scanFlags)
                ?? throw new Exception("Startup monitor state not available.");
            var scanScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", scanFlags)!.GetValue(window)!;
            Until(window, () =>
                firstSnapshotField.GetValue(window) is false,
                "Startup monitor did not finish first availability snapshot.");
            if (scanScheduler.Snapshot().Any(job =>
                    job.Key.StartsWith("background-scan:", StringComparison.Ordinal) &&
                    job.State is SchedulerJobState.Queued or SchedulerJobState.Running or
                        SchedulerJobState.YieldRequested) ||
                roots.GetPendingAutomaticInventories().Count != 0)
                throw new Exception(
                    "Startup manufactured a new root scan for already indexed monitored folders.");
            Console.WriteLine("PASS: Real WPF restart with enabled watchers does not invent whole-root inventory.");
            var restoredTree = (TreeView)window.FindName("FoldersTree");
            var restoredSelection = (GallerySelection)typeof(PCasa.App.MainWindow)
                .GetField("_gallerySelection", scanFlags)!.GetValue(window)!;
            var restoredIds = (HashSet<long>)typeof(PCasa.App.MainWindow)
                .GetField("_selectedImageIds", scanFlags)!.GetValue(window)!;
            Until(window, () =>
                typeof(PCasa.App.MainWindow).GetField("_galleryPositionRestored",
                    scanFlags)!.GetValue(window) is true,
                "Saved gallery position was never restored after startup.");
            if (!SamePath(PathOf(restoredTree.SelectedItem as TreeViewItem), initialRoot) ||
                restoredSelection.FocusedId != rememberedImageId ||
                !restoredIds.Contains(rememberedImageId))
                throw new Exception(
                    "Gallery/tree focus or single-image selection was lost after PCasa restart.");
            Console.WriteLine("PASS: Real restart restores tree focus and selected photo identity.");

            // A fresh PCasa window must still reopen the manager with the
            // Explorer registration persisted from the previous process view.
            VerifyManagerAvailableAfterExplorerRegistration(
                window, initialRoot, inactiveExplorer, blockManualScheduler: false);
            Console.WriteLine(
                "PASS: Folder manager reopens after restart with Explorer-added child.");

            // BackgroundScanSettings.Save always broadcasts Changed. Saving
            // the ALREADY ENABLED setting must not manufacture 7,000-file
            // root scans; no watcher was disconnected, so no reconcile is due.
            for (var i = 0; i < 2; i++)
            {
                new BackgroundScanSettings { Enabled = true }.Save();
                var settingsDispatched = false;
                window.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(() => settingsDispatched = true));
                Until(window, () => settingsDispatched,
                    "Unchanged settings notification was not dispatched.");
                if (scanScheduler.Snapshot().Any(job =>
                        job.Key.StartsWith("background-scan:", StringComparison.Ordinal) &&
                        job.State is SchedulerJobState.Queued or SchedulerJobState.Running or
                            SchedulerJobState.YieldRequested) ||
                    roots.GetPendingAutomaticInventories().Count != 0)
                    throw new Exception(
                        "Saving unchanged enabled settings manufactured another full scan.");
            }
            Console.WriteLine("PASS: Repeated unchanged Enabled=true settings saves do not restart a completed inventory.");

            // END-TO-END LIVE-SCAN TEST: a real committed JPEG must be
            // visible in the WPF gallery BEFORE scan completion. The old
            // checks only inspected a queued refresh timer, which can fire
            // without ever publishing the image to the viewport.
            new BackgroundScanSettings { Enabled = false }.Save();
            // Simulate the production state of an ongoing background scan:
            // OnCatalogImageCommitted is deliberately disabled then, so the
            // targeted indexed-image publication MUST work on its own.
            var runningScanField = typeof(PCasa.App.MainWindow).GetField(
                "_isScanning", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var previousRunningScan = (bool)runningScanField.GetValue(window)!;
            runningScanField.SetValue(window, true);
            var liveRoot = roots.GetAll().Single(root =>
                SamePath(root.LocalPath, initialRoot));
            var liveEndRoot = roots.GetAll().Single(root =>
                SamePath(root.LocalPath, afterRestart));
            var liveScanner = new FolderScanner(new ImageCatalogRepository(db),
                new ThumbnailCache(), roots, new BrowseThumbnailProfileRepository(db));
            var liveIndexField = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex", BindingFlags.Instance |
                BindingFlags.NonPublic)!;
            var liveScroller = (ScrollViewer)window.FindName("BrowseScrollViewer");
            var liveSurface = (BrowseGallerySurface)window.FindName("BrowseSurface");
            var activeFolderField = typeof(PCasa.App.MainWindow).GetField(
                "_activePhotoFolderId", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var previousActiveFolder = activeFolderField.GetValue(window);
            bool IsVisiblyRendered(string path)
            {
                var id = new ImageCatalogRepository(db).GetImageId(path);
                if (id is not long imageId ||
                    liveIndexField.GetValue(window) is not
                        IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry> index)
                    return false;
                var ordinal = -1;
                for (var i = 0; i < index.Count; i++)
                    if (index[i].ImageId == imageId) { ordinal = i; break; }
                liveSurface.SetViewport(liveScroller.VerticalOffset,
                    Math.Max(1, liveScroller.ViewportHeight));
                var visible = liveSurface.GetVisibleIndexRange();
                return ordinal >= 0 && visible is { } span &&
                    ordinal >= span.First && ordinal <= span.Last;
            }
            // The live edges must be calculated per FOLDER, not from the
            // scrollable height of the complete gallery. A middle folder's
            // beginning is deliberately NOT the top of the global viewport.
            window.Height = 410;
            window.Width = 900;
            Until(window, () => liveScroller.ViewportHeight > 100 &&
                liveSurface.GetFolderOffset(later.Id) is > 2,
                "WPF gallery did not establish a non-global folder boundary.");
            var moreLast = Enumerable.Range(0, 12).Select(i =>
                Path.Combine(afterRestart, $"last-{i:00}.jpg")).ToArray();
            foreach (var photo in moreLast) WriteJpeg(photo);
            var fillLast = Task.Run(() =>
            {
                foreach (var photo in moreLast)
                    liveScanner.ScanFile(liveEndRoot, photo);
            });
            Until(window, () => fillLast.IsCompleted, "Last-folder scan failed.");
            fillLast.GetAwaiter().GetResult();
            var navigation = typeof(PCasa.App.MainWindow).GetMethod(
                "RefreshBrowseGalleryAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            // Extend the last section BEFORE navigating to the middle one:
            // otherwise ScrollViewer clamps the requested middle-folder
            // beginning because there are too few images below it.
            var refresh = (Task)navigation!.Invoke(window,
                [true, false, false])!;
            Until(window, () => refresh.IsCompleted,
                "Explicit gallery refresh after real scan did not complete.");
            refresh.GetAwaiter().GetResult();
            var laterStart = liveSurface.GetFolderOffset(later.Id)!.Value;
            liveScroller.ScrollToVerticalOffset(laterStart);
            Until(window, () => liveSurface.GetLiveFolderEdge(
                    liveScroller.VerticalOffset, liveScroller.ViewportHeight)
                    is { FolderId: var folderId, AtStart: true } &&
                    folderId == later.Id,
                "Could not navigate to a non-global folder start.");
            if (liveScroller.VerticalOffset <= 2)
                throw new Exception("Folder-start test accidentally used the global gallery start.");
            activeFolderField.SetValue(window, (long?)later.Id);

            // The first photo row is visible BELOW the preceding folder's
            // last row. It is not at the viewport's exact first pixel.
            liveScroller.ScrollToVerticalOffset(laterStart - 60);
            Until(window, () => liveSurface.GetLiveFolderEdge(
                    liveScroller.VerticalOffset, liveScroller.ViewportHeight, later.Id)
                    is { AtStart: true },
                "Middle folder first row was not recognized with previous folder visible.");
            var beforeCommitOffset = liveScroller.VerticalOffset;
            var firstLive = Path.Combine(laterFolder, "000-live-begin.jpg");
            WriteJpeg(firstLive);
            var firstLiveScan = Task.Run(() => liveScanner.ScanFile(liveRoot, firstLive));
            Until(window, () => firstLiveScan.IsCompleted,
                "Real folder-start scanner could not commit a JPEG.");
            firstLiveScan.GetAwaiter().GetResult();
            Until(window, () => IsVisiblyRendered(firstLive),
                "Real committed JPEG did not appear at the start of its OWN folder.");
            if (Math.Abs(liveScroller.VerticalOffset - beforeCommitOffset) > 3)
                throw new Exception("Live scan moved the visible start boundary of its own folder.");
            Console.WriteLine("PASS: Real scan publishes new JPG at non-global folder start.");

            // Finish the previous scan's queued WPF layout/anchor restoration
            // before taking an independent baseline for a foreign-folder scan.
            Until(window, () => liveSurface.GetLiveFolderEdge(
                    liveScroller.VerticalOffset, liveScroller.ViewportHeight, later.Id)
                    is { AtStart: true },
                "First live JPEG did not leave its folder start visible.");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            // A committed photo in a DIFFERENT folder must not make this
            // folder move, even if the foreign photo sorts before/after it.
            var foreignPhoto = Path.Combine(afterRestart, "000-foreign-no-jump.jpg");
            var stableAnchor = liveSurface.CaptureImageAnchor(
                liveScroller.VerticalOffset);
            WriteJpeg(foreignPhoto);
            var foreignScan = Task.Run(() => liveScanner.ScanFile(liveEndRoot, foreignPhoto));
            Until(window, () => foreignScan.IsCompleted,
                "Real foreign-folder scanner could not commit a JPEG.");
            foreignScan.GetAwaiter().GetResult();
            // Pump the real WPF dispatcher rather than just testing the event
            // dispatch (a previous regression test made precisely that error).
            Until(window, () => new ImageCatalogRepository(db).GetImageId(
                    foreignPhoto) is long,
                "Foreign photo did not enter the catalog.");
            if (liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)
                    != stableAnchor ||
                Math.Abs(liveScroller.VerticalOffset - beforeCommitOffset) > 3)
                throw new Exception(
                    $"Foreign-folder scan moved the current gallery focus: " +
                    $"Before={stableAnchor}, After={liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)}, " +
                    $"Offset={liveScroller.VerticalOffset:F2}, Expected={beforeCommitOffset:F2}");
            Console.WriteLine("PASS: Foreign-folder scan preserves the current folder anchor.");

            // Grow the middle folder beyond a viewport to test the REAL
            // trailing boundary (not just a short section visible all at once).
            var moreMiddle = Enumerable.Range(0, 12).Select(i =>
                Path.Combine(laterFolder, $"middle-{i:00}.jpg")).ToArray();
            foreach (var photo in moreMiddle) WriteJpeg(photo);
            var fillMiddle = Task.Run(() =>
            {
                foreach (var photo in moreMiddle)
                    liveScanner.ScanFile(liveRoot, photo);
            });
            Until(window, () => fillMiddle.IsCompleted,
                "Middle-folder synthetic scan did not complete.");
            fillMiddle.GetAwaiter().GetResult();
            Until(window, () => new ImageCatalogRepository(db).GetImageId(
                    moreMiddle[^1]) is long && IsVisiblyRendered(firstLive),
                "Middle-folder start did not remain visible during scan.");
            var middleEndOffset = liveSurface.GetFolderTrailingOffset(
                later.Id, liveScroller.ViewportHeight)!.Value;
            liveScroller.ScrollToVerticalOffset(middleEndOffset);
            Until(window, () => liveSurface.GetLiveFolderEdge(
                    liveScroller.VerticalOffset, liveScroller.ViewportHeight)
                    is { FolderId: var folderId, AtEnd: true } &&
                    folderId == later.Id,
                "Could not navigate to the middle folder's trailing edge.");

            var secondLive = Path.Combine(laterFolder, "zzz-live-end.jpg");
            WriteJpeg(secondLive);
            var bottomScan = Task.Run(() => liveScanner.ScanFile(liveRoot, secondLive));
            Until(window, () => bottomScan.IsCompleted,
                "Real folder-end scanner could not commit a JPEG.");
            bottomScan.GetAwaiter().GetResult();
            Until(window, () => IsVisiblyRendered(secondLive),
                "Real committed JPEG did not appear at the end of its OWN folder.");
            if (Math.Abs(liveScroller.VerticalOffset -
                    liveSurface.GetFolderTrailingOffset(
                        later.Id, liveScroller.ViewportHeight)!.Value) > 3)
                throw new Exception("Live scan displaced the folder-end scroll anchor.");
            Console.WriteLine("PASS: Real scan publishes new JPG at non-global folder end.");

            static bool SamePhotoAnchor(
                (long ImageId, double RelativeY)? before,
                (long ImageId, double RelativeY)? after)
                => before is { } a && after is { } b &&
                   a.ImageId == b.ImageId &&
                   Math.Abs(a.RelativeY - b.RelativeY) < 0.1;

            // Re-enable a large upper folder section, after hiding it, and
            // confirm that the current last folder stays at the same image.
            // This reproduces the reported focus jump on folder expansion.
            // Switching to the last folder explicitly reconciles foreign
            // scan commits without changing the previously followed folder.
            var lastRefresh = (Task)navigation!.Invoke(window,
                [true, false, false])!;
            Until(window, () => lastRefresh.IsCompleted,
                "Last-folder explicit refresh did not complete.");
            lastRefresh.GetAwaiter().GetResult();
            var lastFolder = new ImageCatalogRepository(db).GetPhotoFolders()
                .Single(folder => SamePath(folder.LocalPath, afterRestart));
            var lastStart = liveSurface.GetFolderOffset(lastFolder.Id)!.Value;
            liveScroller.ScrollToVerticalOffset(lastStart);
            Until(window, () => liveSurface.GetLiveFolderEdge(
                    liveScroller.VerticalOffset, liveScroller.ViewportHeight)
                    is { FolderId: var folderId, AtStart: true } &&
                    folderId == lastFolder.Id,
                "Cannot position last folder before visibility test.");
            activeFolderField.SetValue(window, (long?)lastFolder.Id);
            var viewAnchor = liveSurface.CaptureImageAnchor(
                liveScroller.VerticalOffset);
            if (viewAnchor is null)
                throw new Exception("Last folder has no visible image anchor.");

            var settings = typeof(PCasa.App.MainWindow).GetField(
                "_appearance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            settings.GetType().GetProperty("HideCollapsedFolders")!
                .SetValue(settings, true);
            var upperRoot = Walk(restoredTree).Single(item =>
                SamePath(PathOf(item), initialRoot));
            upperRoot.IsExpanded = true;
            var updateVisibility = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateGalleryFolderVisibility",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            updateVisibility.Invoke(window, [true]);
            upperRoot.IsExpanded = false;
            Until(window, () => liveSurface.GetFolderOffset(later.Id) is null,
                "Collapsing upper folders did not hide the affected gallery section.");
            try
            {
                Until(window, () =>
                    SamePhotoAnchor(viewAnchor,
                    liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)) &&
                    liveSurface.GetLiveFolderEdge(
                        liveScroller.VerticalOffset, liveScroller.ViewportHeight,
                        lastFolder.Id) is { AtStart: true },
                    "Hiding upper folders moved the current folder START into its interior.");
            }
            catch (TimeoutException ex)
            {
                throw new Exception(
                    $"Visibility diagnostic: Before={viewAnchor}, " +
                    $"After={liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)}, " +
                    $"Offset={liveScroller.VerticalOffset:F2}, FolderTop={liveSurface.GetFolderOffset(lastFolder.Id):F2}, " +
                    $"WasTop={lastStart:F2}, Active={activeFolderField.GetValue(window)}, " +
                    $"Edge={liveSurface.GetLiveFolderEdge(liveScroller.VerticalOffset,liveScroller.ViewportHeight,lastFolder.Id)}",
                    ex);
            }
            upperRoot.IsExpanded = true;
            Until(window, () => liveSurface.CaptureImageAnchor(
                    liveScroller.VerticalOffset) == viewAnchor,
                "Re-expanding folders above the current folder moved the image focus.");
            if (activeFolderField.GetValue(window) is not long focusedFolder ||
                focusedFolder != lastFolder.Id)
                throw new Exception("Re-expanding upper folders changed the active folder.");
            Console.WriteLine("PASS: Hiding/revealing folders above the viewport preserves photo and folder focus.");

            // Regression from the real library: with NO selected picture,
            // hiding folders above an arbitrary position inside a very large
            // folder must retain the same folder-relative pixel coordinate.
            // The previous image-only/edge-only restore could jump to the
            // middle of the folder as the global ScrollViewer extent shrank.
            var selectionField = typeof(PCasa.App.MainWindow).GetField(
                "_selectedImageIds", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var selectedIds = (HashSet<long>)selectionField.GetValue(window)!;
            selectedIds.Clear();
            liveSurface.SetSelection(selectedIds);
            var folderHeight = liveSurface.GetFolderBottomOffset(lastFolder.Id)!.Value -
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value;
            var insideOffset = Math.Min(135d,
                Math.Max(38d, folderHeight - liveScroller.ViewportHeight - 24d));
            liveScroller.ScrollToVerticalOffset(
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value + insideOffset);
            Until(window, () =>
                liveScroller.VerticalOffset >=
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value + 30,
                "Cannot position an unselected arbitrary interior photo row.");
            var relativeBefore = liveScroller.VerticalOffset -
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value;
            var arbitraryAnchor = liveSurface.CaptureImageAnchor(
                liveScroller.VerticalOffset);
            upperRoot.IsExpanded = false;
            Until(window, () => liveSurface.GetFolderOffset(later.Id) is null,
                "Collapsing upper folders did not remove their photo section.");
            var relativeHidden = liveScroller.VerticalOffset -
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value;
            if (Math.Abs(relativeHidden - relativeBefore) > 3 ||
                !SamePhotoAnchor(arbitraryAnchor,
                    liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)) ||
                selectedIds.Count != 0)
                throw new Exception(
                    $"Unselected gallery position moved when upper folders were hidden: " +
                    $"Before={relativeBefore:F2}, After={relativeHidden:F2}");
            upperRoot.IsExpanded = true;
            Until(window, () => liveSurface.GetFolderOffset(later.Id) is not null,
                "Expanding upper folders did not restore their photo section.");
            var relativeRestored = liveScroller.VerticalOffset -
                liveSurface.GetFolderOffset(lastFolder.Id)!.Value;
            if (Math.Abs(relativeRestored - relativeBefore) > 3 ||
                !SamePhotoAnchor(arbitraryAnchor,
                    liveSurface.CaptureImageAnchor(liveScroller.VerticalOffset)) ||
                selectedIds.Count != 0)
                throw new Exception(
                    $"Unselected gallery position moved when upper folders were restored: " +
                    $"Before={relativeBefore:F2}, After={relativeRestored:F2}");
            Console.WriteLine(
                "PASS: Unselected arbitrary interior position survives hiding and restoring upper folders.");
            settings.GetType().GetProperty("HideCollapsedFolders")!
                .SetValue(settings, false);
            updateVisibility.Invoke(window, [true]);
            runningScanField.SetValue(window, previousRunningScan);
            activeFolderField.SetValue(window, previousActiveFolder);
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
            explorerWatchdogStop?.Set();
            explorerModalProbe?.Stop();
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

    private static void VerifyManagerAvailableAfterExplorerRegistration(
        PCasa.App.MainWindow window, string rootPath, string explorerAddedPath,
        bool blockManualScheduler)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
            .GetField("_scheduler", flags)!.GetValue(window)!;
        var managerCommand = typeof(PCasa.App.MainWindow).GetMethod(
            "BrowseFolderManager_Click", flags)
            ?? throw new Exception("Folder manager menu command is missing.");
        var blockerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? blocker = null;
        Task? pendingManual = null;
        DispatcherTimer? dialogTimer = null;
        Exception? dialogError = null;
        var completed = false;
        try
        {
            if (blockManualScheduler)
            {
                blocker = scheduler.EnqueueAsync(
                    "test:manager-running-p0", SchedulerPriority.ManualAction,
                    async _ =>
                    {
                        blockerStarted.TrySetResult();
                        // Deliberately don't observe scheduler yield until
                        // the fixture is released: a newer P0 remains queued.
                        await releaseBlocker.Task;
                    }, workKind: SchedulerWorkKind.CatalogScan);
                Until(window, () => blockerStarted.Task.IsCompleted,
                    "Could not start the scheduler P0 blocker.");
                pendingManual = scheduler.EnqueueAsync(
                    "test:manager-pending-p0", SchedulerPriority.ManualAction,
                    _ => Task.CompletedTask,
                    workKind: SchedulerWorkKind.CatalogScan);
                Until(window, () => scheduler.Snapshot().Any(job =>
                        job.Key == "test:manager-pending-p0" &&
                        job.State == SchedulerJobState.Queued),
                    "The manager regression did not arrange a waiting P0.");
            }

            var deadline = DateTime.UtcNow.AddSeconds(20);
            dialogTimer = new DispatcherTimer(DispatcherPriority.Background,
                window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
            dialogTimer.Tick += (_, _) =>
            {
                if (completed)
                {
                    dialogTimer.Stop();
                    return;
                }
                try
                {
                    var manager = Application.Current.Windows
                        .OfType<PCasa.App.FolderManagerWindow>()
                        .FirstOrDefault(w => w.IsVisible);
                    if (manager is null)
                    {
                        if (DateTime.UtcNow >= deadline)
                            throw new TimeoutException(
                                "Folder manager never opened after Explorer registration.");
                        return;
                    }

                    var tree = (TreeView)manager.FindName("FolderManagerTree");
                    var actualRoot = Walk(tree).FirstOrDefault(item =>
                        SamePath(ManagedPath(item), rootPath));
                    if (actualRoot is null)
                        throw new Exception(
                            "Folder manager opened without persisted Explorer parent.");
                    actualRoot.IsExpanded = true;

                    // The manager now shows ONLY configured monitored roots.
                    // An Explorer-registered physical child under an existing
                    // root must not appear as a duplicate managed root, and
                    // opening the manager must never wait on P0 or enumerate.
                    if (Walk(tree).Any(item =>
                        SamePath(ManagedPath(item), explorerAddedPath)))
                        throw new Exception(
                            "Manager listed an already covered physical child as a duplicate root.");
                    completed = true;
                    ((Button)manager.FindName("CancelButton")).RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex)
                {
                    dialogError = ex;
                    completed = true;
                    foreach (var manager in Application.Current.Windows
                                 .OfType<PCasa.App.FolderManagerWindow>()
                                 .Where(w => w.IsVisible))
                        manager.Close();
                }
            };
            dialogTimer.Start();
            window.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                new Action(() =>
                {
                    try
                    {
                        managerCommand.Invoke(window,
                            new object[] { window, new RoutedEventArgs() });
                    }
                    catch (Exception ex)
                    {
                        dialogError = ex;
                        completed = true;
                    }
                }));
            Until(window, () => completed,
                "Folder manager did not open/expand/close after Explorer Add.");
            if (dialogError is not null)
                throw new Exception(
                    "Folder manager failed after Explorer Add or restart.", dialogError);
            if (blocker is not null && blocker.IsCompleted)
                throw new Exception("The P0 blocker ended before the manager opened.");
        }
        finally
        {
            dialogTimer?.Stop();
            releaseBlocker.TrySetResult();
            foreach (var manager in Application.Current.Windows
                         .OfType<PCasa.App.FolderManagerWindow>()
                         .Where(w => w.IsVisible))
                manager.Close();
        }
        if (blocker is not null)
            Until(window, () => blocker.IsCompleted &&
                (pendingManual is null || pendingManual.IsCompleted),
                "P0 fixture did not finish after folder manager closed.");
    }

    private static string? ManagedPath(TreeViewItem node)
        => node.Tag?.GetType().GetProperty("LocalPath")
            ?.GetValue(node.Tag) as string;

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
            // Preserve idle dispatch for real gallery work, but do not let
            // a busy dispatcher hold the test inside PushFrame forever.
            var watchdog = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal,
                window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            watchdog.Tick += (_, _) => frame.Continue = false;
            watchdog.Start();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            watchdog.Stop();
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
