using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PCasa.App.Controls;
using PCasa.App.Models;
using PCasa.App.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // This test constructs and displays a complete MainWindow. Keep it off
        // users' machines: the GitHub-hosted Windows runner has an ephemeral,
        // isolated Windows profile and no real PCasa catalogue.
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                "Window smoke test requires an ephemeral GitHub Actions runner.");
            return 2;
        }

        // The WPF Application.Run loop normally installs this context.
        // Our console-hosted STA smoke test pumps Dispatcher frames manually;
        // install the same UI context before any async void WPF event fires.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        if (args.Contains("--tag-flow", StringComparer.OrdinalIgnoreCase))
            return TagFlow.Run();
        if (args.Contains("--folder-removal", StringComparer.OrdinalIgnoreCase))
            return FolderRemovalFlow.Run();
        if (args.Contains("--folder-add", StringComparer.OrdinalIgnoreCase))
            return FolderAddFlow.Run();
        if (args.Contains("--gallery-files", StringComparer.OrdinalIgnoreCase))
            return GalleryFileOperationFlow.Run();

        PCasa.App.MainWindow? window = null;
        try
        {
            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow();
            window.Show();
            Flush(window);

            var menu = Descendants<Menu>(window).FirstOrDefault()
                ?? throw new Exception("Main menu was not constructed.");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var labels = items.Select(item => item.Header?.ToString()).ToArray();
            if (!labels.SequenceEqual(new[] { "Datei", "Bearbeiten",
                    "Ordner", "Sortieren", "Optionen", "Hilfe" }))
                throw new Exception("Unexpected top-level menu labels: " +
                    string.Join(", ", labels));
            Console.WriteLine("PASS: Real MainWindow creates expected menu.");

            // The actual WPF layout determines the distance between adjacent
            // menu items. This detects changing margins or a wrong template.
            var gaps = new List<double>();
            for (var i = 1; i < items.Length; i++)
            {
                var before = items[i - 1].TransformToAncestor(menu).TransformBounds(
                    new Rect(new Point(0, 0), items[i - 1].RenderSize));
                var next = items[i].TransformToAncestor(menu).TransformBounds(
                    new Rect(new Point(0, 0), items[i].RenderSize));
                gaps.Add(next.Left - before.Right);
            }
            if (gaps.Count != items.Length - 1 ||
                gaps.Any(gap => gap < 0 || gap > 80) ||
                gaps.Max() - gaps.Min() > 2)
                throw new Exception("Inconsistent top-level menu spacing: " +
                    string.Join(", ", gaps.Select(gap => gap.ToString("F1"))));
            Console.WriteLine("PASS: Menu label gaps are consistent: " +
                string.Join(", ", gaps.Select(gap => gap.ToString("F1"))));

            // Opening the REAL submenu must install the configured rightward
            // placement callback, not fall back to WPF's alternating default.
            var options = items.Single(item => Equals(item.Header, "Optionen"));
            options.IsSubmenuOpen = true;
            Flush(window);
            var popup = options.Template.FindName("PART_Popup", options) as Popup
                ?? throw new Exception("Options submenu popup is missing.");
            if (popup.CustomPopupPlacementCallback is null)
                throw new Exception("Submenu placement callback was not installed.");
            options.IsSubmenuOpen = false;
            Flush(window);
            Console.WriteLine("PASS: Options submenu uses custom placement.");

            // Verify that the real theme handlers update both the WPF menu
            // state and readable foreground/background resource brushes.
            foreach (var name in new[] { "ThemeLightMenu", "ThemeDarkMenu",
                         "ThemeSystemMenu" })
            {
                var themeItem = window.FindName(name) as MenuItem
                    ?? throw new Exception("Theme choice missing: " + name);
                themeItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, themeItem));
                Flush(window);
                if (!themeItem.IsChecked)
                    throw new Exception("Theme did not become selected: " + name);
                var foreground = (SolidColorBrush)app.Resources["PcasaTextBrush"];
                var background = (SolidColorBrush)app.Resources["PcasaPanelBrush"];
                var foregroundLum = foreground.Color.R + foreground.Color.G +
                    foreground.Color.B;
                var backgroundLum = background.Color.R + background.Color.G +
                    background.Color.B;
                if (Math.Abs(foregroundLum - backgroundLum) < 300)
                    throw new Exception("Theme foreground and panel have low contrast: " + name);
            }
            Console.WriteLine("PASS: Light, dark and system menu/theme resources.");

            // Footer layout: two distinct icon-only sliders, one A/B button,
            // and the existing sidebar handlers via icon buttons.
            var thumbSize = (Slider?)window.FindName("ThumbnailSizeSlider")
                ?? throw new Exception("Thumbnail-size slider missing.");
            var thumbGap = (Slider?)window.FindName("ThumbnailSpacingSlider")
                ?? throw new Exception("Thumbnail-spacing slider missing.");
            if (thumbGap.Minimum != 0 || thumbGap.Maximum != 160 ||
                thumbSize.Minimum != 72 || thumbSize.Maximum != 600)
                throw new Exception("Thumbnail toolbar slider range changed.");
            var compareAb = (Button?)window.FindName("CompareAbButton")
                ?? throw new Exception("A/B comparison button missing.");
            if (!Equals(compareAb.Content, "A/B") ||
                window.FindName("CompareAaButton") is not null)
                throw new Exception("The footer must contain only A/B, not A/A.");
            foreach (var name in new[] { "TagSidebarButton", "GpsSidebarButton", "InfoSidebarButton" })
            {
                if (window.FindName(name) is not Button { Content: Viewbox })
                    throw new Exception("Sidebar toolbar is not an icon-only button: " + name);
            }
            if (items.SelectMany(item => item.Items.OfType<MenuItem>())
                .SelectMany(item => item.Items.OfType<MenuItem>())
                .Any(item => (item.Header?.ToString() ?? "").Contains(
                    "Thumbnail-Abstand einstellen", StringComparison.Ordinal)))
                throw new Exception("Obsolete thumbnail spacing dialog menu remains.");
            Console.WriteLine("PASS: Icon-only footer and thumbnail spacing slider replace old menu.");

            // Exercise the actual ScrollViewer and responsive gallery together.
            // The synthetic images deliberately do not refer to real source
            // files, so this test cannot modify a user's picture collection.
            using var pages = new BrowseThumbnailPageCache();
            var synthetic = Enumerable.Range(0, 480)
                .Select(i => new BrowseThumbnailIndexEntry(1000 + i,
                    i < 240 ? 100 : 200, 192, "no-image.pack", 0, 1, 180, 120))
                .ToArray();
            var groups = new[] {
                new PhotoFolderSummary(100, 1, @"C:\\synthetic\\first",
                    @"C:\\synthetic\\first", "First", 240),
                new PhotoFolderSummary(200, 1, @"C:\\synthetic\\second",
                    @"C:\\synthetic\\second", "Second", 240)
            };
            pages.SetIndex(synthetic);
            var surface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Actual browse surface not found.");
            var scroller = (ScrollViewer?)window.FindName("BrowseScrollViewer")
                ?? throw new Exception("Actual browse scroll viewer not found.");
            surface.SetData(synthetic, groups, pages);
            Flush(window);
            scroller.ScrollToVerticalOffset(2800);
            Flush(window);
            surface.SetViewport(scroller.VerticalOffset,
                Math.Max(1, scroller.ViewportHeight));
            var anchored = surface.CaptureImageAnchor(scroller.VerticalOffset)
                ?? throw new Exception("Actual window could not capture image anchor.");
            var originalIndex = Array.FindIndex(synthetic,
                item => item.ImageId == anchored.ImageId);
            if (originalIndex < 0 || scroller.VerticalOffset < 200)
                throw new Exception("The WPF window did not scroll to synthetic images.");

            var sidebar = (FrameworkElement?)window.FindName("InfoSidebarPanel")
                ?? throw new Exception("Sidebar not found.");
            var tagsPanel = (FrameworkElement?)window.FindName("TagSidebar")
                ?? throw new Exception("Tags panel not found.");
            var infoPanel = (FrameworkElement?)window.FindName("GeneralSidebarScroll")
                ?? throw new Exception("Info panel not found.");

            foreach (var mode in new[] { "Tags", "Info", "GPS", "Tags" })
            {
                var button = Descendants<Button>(window)
                    .FirstOrDefault(candidate => Equals(candidate.Tag, mode))
                    ?? throw new Exception("Missing sidebar button " + mode);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                Flush(window);
                if (sidebar.Visibility != Visibility.Visible ||
                    tagsPanel.Visibility != (mode == "Tags"
                        ? Visibility.Visible : Visibility.Collapsed) ||
                    infoPanel.Visibility != (mode == "Tags"
                        ? Visibility.Collapsed : Visibility.Visible))
                    throw new Exception("Sidebar state inconsistent for " + mode);
                surface.SetViewport(scroller.VerticalOffset,
                    Math.Max(1, scroller.ViewportHeight));
                var visible = surface.GetVisibleIndexRange()
                    ?? throw new Exception("Gallery lost all images after opening " + mode);
                if (originalIndex < visible.First || originalIndex > visible.Last)
                    throw new Exception("Opening " + mode +
                        " jumped to unrelated thumbnails.");
            }
            Console.WriteLine("PASS: Tag/Info/GPS sidebar modes preserve actual gallery image.");

            var tagButton = Descendants<Button>(window)
                .First(button => Equals(button.Tag, "Tags"));
            tagButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, tagButton));
            Flush(window);
            if (sidebar.Visibility != Visibility.Collapsed)
                throw new Exception("Toggling active sidebar did not close it.");
            surface.SetViewport(scroller.VerticalOffset,
                Math.Max(1, scroller.ViewportHeight));
            var closedVisible = surface.GetVisibleIndexRange()
                ?? throw new Exception("Closing sidebar lost gallery images.");
            if (originalIndex < closedVisible.First || originalIndex > closedVisible.Last)
                throw new Exception("Closing sidebar changed active image.");
            Console.WriteLine("PASS: Closing sidebar preserves WPF gallery position.");

            // Live slider changes must preserve the current photo anchor and
            // reuse the original rendering/reflow path.
            thumbGap.Value = 19;
            Flush(window);
            if (Math.Abs(thumbGap.Value - 19) > 0.01)
                throw new Exception("Thumbnail spacing slider did not accept its value.");
            var spacingField = typeof(BrowseGallerySurface).GetField("_cellGap",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Shared gallery spacing field missing.");
            if (Math.Abs((double)spacingField.GetValue(surface)! - 19) > 0.01)
                throw new Exception("Toolbar spacing was not applied to the gallery.");
            Console.WriteLine("PASS: Thumbnail spacing slider updates the shared gallery layout.");

            // The actual spacing value is a shared minimum horizontal gap
            // and an equal vertical gap. Slider changes are not a second
            // layout algorithm and cannot affect just the vertical axis.
            thumbGap.Value = 125;
            Flush(window);
            var minHorizontalGap = typeof(BrowseGallerySurface).GetField("_cellGap",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var verticalGap = typeof(BrowseGallerySurface).GetField("_rowGap",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (Math.Abs((double)minHorizontalGap!.GetValue(surface)! - 125) > 0.01 ||
                Math.Abs((double)verticalGap!.GetValue(surface)! - 125) > 0.01)
                throw new Exception("Spacing controls did not update BOTH axes.");
            thumbGap.Value = 19;
            Flush(window);
            // The old preview also applies to live footer dragging, with
            // a shared column-count lock and a final normal layout.
            var previewStart = typeof(BrowseGallerySurface).GetMethod("BeginSpacingPreview")!;
            var previewEnd = typeof(BrowseGallerySurface).GetMethod("EndSpacingPreview")!;
            previewStart.Invoke(surface, null);
            if (typeof(BrowseGallerySurface).GetField("_spacingPreviewColumns",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)?.GetValue(surface) is not int)
                throw new Exception("Original spacing preview column lock missing.");
            previewEnd.Invoke(surface, null);
            Console.WriteLine("PASS: Wide spacing range, both axes and original preview lock.");

            // On size changes the visible cache tier must remain in place
            // until the slider drag completes. Image identity stays stable.
            var initialSize = surface.ThumbnailWidth;
            var imageBeforeSize = surface.CaptureImageAnchor(scroller.VerticalOffset);
            var beforeSizeIndex = typeof(PCasa.App.MainWindow).GetField("_activeBrowseTier",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(window);
            thumbSize.Value = 540;
            Flush(window);
            if (Math.Abs(surface.ThumbnailWidth - 540) > 0.01)
                throw new Exception("Thumbnail size is not updated in the next WPF frame.");
            if (!Equals(beforeSizeIndex, typeof(PCasa.App.MainWindow)
                    .GetField("_activeBrowseTier", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)?.GetValue(window)))
                throw new Exception("Sizing synchronously reloaded the thumbnail tier.");
            thumbSize.Value = initialSize;
            Flush(window);
            Console.WriteLine("PASS: Thumbnail size changes immediately without synchronous cache reload.");

            // Real TreeView geometry: status symbols must use the space right
            // next to its vertical scrollbar. A second reserved margin in
            // the header or in FoldersTree must not steal width from names.
            var folderTree = (TreeView?)window.FindName("FoldersTree")
                ?? throw new Exception("Folder tree not found.");
            folderTree.Items.Clear();
            for (var i = 0; i < 45; i++)
                folderTree.Items.Add(new TreeViewItem
                {
                    Header = "Very long folder name for regression test " + i,
                    Tag = new FolderLayoutSmokeTag
                    {
                        FullPath = @"C:\Pictures\Example" + i,
                        StatusCode = "Unknown"
                    }
                });
            Flush(window);
            var folderBar = Descendants<ScrollBar>(folderTree).FirstOrDefault(bar =>
                bar.Orientation == Orientation.Vertical && bar.IsVisible)
                ?? throw new Exception("Folder tree vertical scrollbar missing.");
            var folderStatus = Descendants<TextBlock>(folderTree).FirstOrDefault(block =>
                block.Name == "FolderStatusIcon" && block.IsVisible)
                ?? throw new Exception("Folder status symbol missing.");
            var statusBounds = folderStatus.TransformToAncestor(folderTree)
                .TransformBounds(new Rect(new Point(0, 0), folderStatus.RenderSize));
            var barBounds = folderBar.TransformToAncestor(folderTree)
                .TransformBounds(new Rect(new Point(0, 0), folderBar.RenderSize));
            var unusedWidth = barBounds.Left - statusBounds.Right;
            if (unusedWidth < 0 || unusedWidth > 23)
                throw new Exception(
                    $"Folder status/icon gutter wastes {unusedWidth:F1} WPF pixels before scrollbar.");
            Console.WriteLine(
                $"PASS: Folder tree status sits next to scrollbar (gap {unusedWidth:F1} px).");

            // The compact dialog belongs to the screen's right edge,
            // independently of the owner position on that same screen.
            var progressWindow = new PCasa.App.BrowseCacheProgressWindow { Owner = window };
            try
            {
                progressWindow.Show();
                Flush(progressWindow);
                var workArea = SystemParameters.WorkArea;
                var rightGap = workArea.Right -
                    (progressWindow.Left + progressWindow.ActualWidth);
                if (progressWindow.ActualHeight > 78 ||
                    rightGap < -1 || rightGap > 20)
                    throw new Exception(
                        $"Progress dialog bounds: height={progressWindow.ActualHeight:F1}, " +
                        $"right gap={rightGap:F1}.");
                var originalLeft = progressWindow.Left;
                var originalTop = progressWindow.Top;
                // Exercise the actual vertical-drag positioning method with
                // a synthetic pointer delta. Horizontal position must remain
                // locked, and moving PCasa may not reset the chosen height.
                var progressType = typeof(PCasa.App.BrowseCacheProgressWindow);
                var dragTop = progressType.GetField("_dragStartTop",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)
                    ?? throw new Exception("Vertical drag start position missing.");
                var dragY = progressType.GetField("_dragStartPointerY",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)
                    ?? throw new Exception("Vertical drag pointer position missing.");
                var applyDrag = progressType.GetMethod("ApplyVerticalDrag",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)
                    ?? throw new Exception("Vertical-only drag implementation missing.");
                dragTop.SetValue(progressWindow, originalTop);
                dragY.SetValue(progressWindow, 100.0);
                applyDrag.Invoke(progressWindow, [124.0]);
                Flush(progressWindow);
                if (Math.Abs(progressWindow.Top - (originalTop + 24)) > 1 ||
                    Math.Abs(progressWindow.Left - originalLeft) > 1)
                    throw new Exception("Progress drag did not move strictly vertically.");

                var ownerLeft = window.Left;
                try
                {
                    window.Left = ownerLeft + 28;
                    Flush(window);
                    Flush(progressWindow);
                    if (Math.Abs(progressWindow.Left - originalLeft) > 1 ||
                        Math.Abs(progressWindow.Top - (originalTop + 24)) > 1)
                        throw new Exception("PCasa movement reset screen-anchored progress position.");
                }
                finally
                {
                    window.Left = ownerLeft;
                    Flush(window);
                }
                Console.WriteLine(
                    $"PASS: Compact progress stays right-anchored and moves vertically only (gap {rightGap:F1} px).");

                // Real taskbar transition of an owned, borderless WPF
                // progress window. Previous behavior reanchored only when
                // the monitor changed, leaving a partially painted window.
                var displayedPhase = (TextBlock)progressWindow.FindName("PhaseText");
                progressWindow.UpdateScan(new ScanProgress(
                    3, 1, 0, 0, "photo-minimize.jpg"));
                var expectedPhase = displayedPhase.Text;
                var savedWindowState = window.WindowState;
                try
                {
                    window.WindowState = WindowState.Minimized;
                    Flush(window);
                    window.WindowState = WindowState.Normal;
                    Flush(window);
                    Flush(progressWindow);
                    if (!progressWindow.IsVisible ||
                        Math.Abs(progressWindow.ActualWidth - 348) > 1 ||
                        Math.Abs(progressWindow.ActualHeight - 76) > 1 ||
                        displayedPhase.Text != expectedPhase)
                        throw new Exception(
                            "Restoring PCasa did not restore the entire progress window.");
                    var restoredGap = SystemParameters.WorkArea.Right -
                        (progressWindow.Left + progressWindow.ActualWidth);
                    if (restoredGap < -1 || restoredGap > 20)
                        throw new Exception(
                            "Restored progress window has invalid desktop placement.");

                    var hide = progressType.GetMethod("Hide_Click",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)
                        ?? throw new Exception("Manual progress hide handler missing.");
                    hide.Invoke(progressWindow, [progressWindow, new RoutedEventArgs()]);
                    window.WindowState = WindowState.Minimized;
                    Flush(window);
                    window.WindowState = WindowState.Normal;
                    Flush(window);
                    if (progressWindow.IsVisible)
                        throw new Exception(
                            "User-hidden progress window reappeared on owner restore.");
                    progressWindow.ShowFromStatus();
                    Flush(progressWindow);
                    if (!progressWindow.IsVisible)
                        throw new Exception(
                            "Status click did not reopen hidden progress window.");
                    Console.WriteLine(
                        "PASS: Taskbar restore reconstructs progress window while respecting user Hide.");
                }
                finally
                {
                    window.WindowState = savedWindowState;
                    Flush(window);
                }

            }
            finally
            {
                progressWindow.Finish();
            }

            // The folder manager must never enumerate a potentially offline
            // NAS root via its own Task.Run. Expanding a real TreeViewItem
            // must submit its source probe through the shared scheduler.
            var managerScheduler = new PCasaScheduler();
            PCasa.App.FolderManagerWindow? manager = null;
            try
            {
                manager = new PCasa.App.FolderManagerWindow(
                    Array.Empty<(PCasa.App.Models.MonitoredFolder Folder,
                        IReadOnlyList<string> ExcludedPaths)>(),
                    managerScheduler, serverDatabaseConfigured: false)
                {
                    Owner = window
                };
                manager.Show();
                Flush(manager);
                var managedTree = (TreeView?)manager.FindName("FolderManagerTree")
                    ?? throw new Exception("Folder manager tree missing.");
                var firstManagedNode = managedTree.Items.OfType<TreeViewItem>().FirstOrDefault()
                    ?? throw new Exception("Folder manager has no expandable system folder.");
                firstManagedNode.IsExpanded = true;
                Flush(manager);
                if (!managerScheduler.Snapshot().Any(job =>
                        job.Key.StartsWith("folder-manager:expand:", StringComparison.Ordinal)
                        && job.Priority == SchedulerPriority.Interactive))
                    throw new Exception("Manager expansion bypassed the shared scheduler.");
                Console.WriteLine("PASS: Folder manager expansion uses scheduled source probe.");
            }
            finally
            {
                manager?.Close();
                // Never block the STA waiting for an async DisposeAsync which
                // must resume on this dispatcher: pump it like other WPF tests.
                var dispose = managerScheduler.DisposeAsync().AsTask();
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!dispose.IsCompleted && DateTime.UtcNow < deadline)
                    Flush(window);
                if (!dispose.IsCompleted)
                    throw new TimeoutException("Folder manager scheduler disposal stalled.");
                dispose.GetAwaiter().GetResult();
            }

            window.Close();
            window = null;
            Console.WriteLine("RESULT: Window smoke checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: Real WPF window smoke test: " + ex);
            return 1;
        }
        finally
        {
            if (window is not null)
            {
                try { window.Close(); }
                catch (Exception closeError)
                {
                    Console.Error.WriteLine("Window close: " + closeError);
                }
            }
        }
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        // Process WPF's deferred Loaded/ContextIdle layout restorations instead
        // of running an Invoke directly on the current STA thread.
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}

public sealed class FolderLayoutSmokeTag
{
    public string FullPath { get; init; } = "";
    public long? PhotoFolderId { get; init; }
    public string StatusCode { get; init; } = "Unknown";
}
