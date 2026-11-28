using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PCasa.App;
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

        if (args.Length == 2 &&
            string.Equals(args[0], "--chromium-credits", StringComparison.OrdinalIgnoreCase))
            return ChromiumCreditsFlow.Run(args[1]);
        if (args.Contains("--alpha58-durable", StringComparer.OrdinalIgnoreCase))
            return Alpha58DurableFlow.Run();
        if (args.Contains("--crop-flow", StringComparer.OrdinalIgnoreCase))
            return CropFlow.Run();
        if (args.Contains("--tag-flow", StringComparer.OrdinalIgnoreCase))
            return TagFlow.Run();
        if (args.Contains("--folder-removal", StringComparer.OrdinalIgnoreCase))
            return FolderRemovalFlow.Run();
        if (args.Contains("--folder-add", StringComparer.OrdinalIgnoreCase))
            return FolderAddFlow.Run();
        if (args.Contains("--gallery-files", StringComparer.OrdinalIgnoreCase))
            return GalleryFileOperationFlow.Run();
        if (args.Contains("--taskmanager", StringComparer.OrdinalIgnoreCase))
            return TaskManagerFlow.Run();
        if (args.Contains("--taskmanager-global", StringComparer.OrdinalIgnoreCase))
            return GlobalTaskOrderFlow.Run();

        PCasa.App.MainWindow? window = null;
        try
        {
            ExternalEditSmokeFlow.Run();
            PsdCompositeSmokeFlow.Run();
            LocalizationFlow.Run();
            var canDeletePermanently = typeof(PCasa.App.MainWindow).GetMethod(
                "MayDeletePermanently",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)!;
            bool Permanent(string path) => (bool)canDeletePermanently.Invoke(
                null, new object[] { path })!;
            if (!Permanent(@"\\server\share\photo.jpg") ||
                Permanent(Path.Combine(Path.GetTempPath(), "photo.jpg")))
                throw new Exception(
                    "Deletion confirmation misclassified local recycle-bin and UNC paths.");
            Console.WriteLine(
                "PASS: Manual gallery deletion can classify recycle-bin policy without scheduler or NAS probe.");

            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow();
            window.Show();
            // A catalog folder can exist for ordering/navigation without
            // creating a local database thumbnail read for a hidden row.
            {
                var tagType = typeof(MainWindow).Assembly.GetType("PCasa.App.Models.FolderTreeTag")!;
                var tag = Activator.CreateInstance(tagType, new object[] { @"C:\\test", (long?)long.MaxValue })!;
                var requested = tagType.GetField("_thumbnailRequested", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!;
                var preview = new FolderTreeThumbnailImage { Folder = tag, Visibility = Visibility.Collapsed };
                var host = new Window { Content = preview, Width = 100, Height = 100, ShowInTaskbar = false };
                try
                {
                    host.Show();
                    Flush(host);
                    if (requested.GetValue(tag) is true)
                        throw new Exception("Hidden folder requested a thumbnail during tree construction.");
                    preview.Visibility = Visibility.Visible;
                    Until(host, () => requested.GetValue(tag) is true,
                        "Visible folder did not request its thumbnail.");
                }
                finally { host.Close(); }
            }
            // About has exactly one user-visible release identity: Version.
            var aboutDialogType = typeof(PCasa.App.MainWindow).Assembly
                .GetType("PCasa.App.PcasaAboutWindow")!;
            if (aboutDialogType.GetConstructors(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance).Single()
                .GetParameters().Length != 2)
                throw new Exception("About dialog still expects an optional build identifier.");
            // Prior WPF tests in this isolated runner can persist a 1044px
            // window size. The user's approved complete one-row toolbar
            // targets normal/wide windows; do not silently shrink old UI.
            window.WindowState = WindowState.Normal;
            window.Width = 1500;
            Flush(window);
            // Alpha.43 deliberately keeps the initial gallery layout
            // invisible while restoring the saved anchor and warming the
            // visible packed pages. Tests that inject SYNTHETIC gallery rows
            // later must not race with this real asynchronous startup read.
            // Verify the production startup contract before testing the
            // unrelated live-edge navigation with fake rows.
            Until(window, () =>
                typeof(PCasa.App.MainWindow).GetField(
                    "_startupGalleryPublished",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!
                    .GetValue(window) is true,
                "Initial gallery startup never published its restored viewport.");
            if (window.FindName("BrowseScrollViewer") is not ScrollViewer startupScroller ||
                startupScroller.Opacity != 1 || !startupScroller.IsHitTestVisible)
                throw new Exception(
                    "Startup gallery was not visible after the startup page warm completed.");
            // First frame is also the release fence for speculative pages and
            // background monitoring. A gallery that stays deferred forever
            // would appear initially but stop loading pages after navigation.
            Flush(window);
            if (window.FindName("BrowseSurface") is not BrowseGallerySurface startupSurface ||
                startupSurface.ViewportPageRequestsDeferred)
                throw new Exception(
                    "Viewport page requests remained suspended after first paint.");
            var startupFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            if (typeof(PCasa.App.MainWindow).GetField(
                    "_startupGalleryPresented", startupFlags)!.GetValue(window) is not true)
                throw new Exception("First gallery frame was not presented.");
            Alpha58WindowSmoke.Run(window);
            if (PicasaGalleryScrollBar.TryScrollTarget(0, 1000, -96, out _) ||
                PicasaGalleryScrollBar.TryScrollTarget(1000, 1000, 96, out _) ||
                PicasaGalleryScrollBar.TryScrollTarget(0, 0, 96, out _) ||
                !PicasaGalleryScrollBar.TryScrollTarget(0, 1000, 96, out var fromTop) ||
                fromTop != 96 ||
                !PicasaGalleryScrollBar.TryScrollTarget(1000, 1000, -96, out var fromBottom) ||
                fromBottom != 904 ||
                !PicasaGalleryScrollBar.TryScrollTarget(950, 1000, 96, out var clamped) ||
                clamped != 1000)
                throw new Exception("Gallery edge scroll did not suppress only outward movement.");
            Console.WriteLine("PASS: Gallery edge suppresses outward scroll and accepts immediate reversal.");
            // Use the real WPF popup and actual Click handlers. Decade
            // navigation must not masquerade as selectable year buttons.
            {
                const System.Reflection.BindingFlags privateInstance =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                var mainType = typeof(PCasa.App.MainWindow);
                var dateType = mainType.Assembly.GetType("PCasa.App.GalleryDateFilterWindow")!;
                var initial = mainType.GetField("_galleryDateStartParts", privateInstance)!
                    .GetValue(window)!;
                var dateField = mainType.GetField("_galleryDateField", privateInstance)!
                    .GetValue(window)!;
                var dateFilterDialog = (Window)Activator.CreateInstance(dateType,
                    [dateField, initial, null])!;
                dateFilterDialog.Owner = window;
                try
                {
                    dateFilterDialog.Show();
                    Flush(dateFilterDialog);
                    var buttons = (Button[])dateType.GetField("_startButtons", privateInstance)!
                        .GetValue(dateFilterDialog)!;
                    var yearButton = buttons[2];
                    var selectedYear = int.Parse(yearButton.Content.ToString()!);
                    yearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Flush(dateFilterDialog);
                    Popup CurrentPopup() => (Popup)dateType.GetField("_popup", privateInstance)!
                        .GetValue(dateFilterDialog)!;
                    StackPanel CurrentPanel() => (StackPanel)((Border)CurrentPopup().Child).Child;
                    Button[] Years() => ((UniformGrid)CurrentPanel().Children[1])
                        .Children.Cast<Button>().ToArray();
                    var decades = Years();
                    var starts = decades.Select(button =>
                        int.Parse(button.Content.ToString()!)).ToArray();
                    if (CurrentPanel().Children.Count != 2 ||
                        !starts.SequenceEqual(starts.OrderBy(year => year)) ||
                        !starts.Contains(selectedYear / 10 * 10) ||
                        !starts.Contains(1900) || starts.Any(year => year > DateTime.Today.Year))
                        throw new Exception("Decade popup must present an ascending grid independent of the catalog.");
                    var opened = CurrentPopup();
                    var now = DateTime.Today.Year;
                    var recentDecade = now / 10 * 10;
                    decades.Single(button => button.Content.ToString() == recentDecade.ToString())
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!Years().Select(button => int.Parse(button.Content.ToString()!))
                            .SequenceEqual(Enumerable.Range(recentDecade, now - recentDecade + 1)))
                        throw new Exception("Future years must not be offered.");
                    ((Button)((DockPanel)CurrentPanel().Children[0]).Children[0])
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    decades = Years();
                    decades.Single(button => button.Content.ToString() == "1980")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!opened.IsOpen || yearButton.Content.ToString() != selectedYear.ToString())
                        throw new Exception("Selecting a decade must navigate, not change the date or close the popup.");
                    var years = Years();
                    if (CurrentPanel().Children.Count != 2 ||
                        !years.Select(button => int.Parse(button.Content.ToString()!))
                            .SequenceEqual(Enumerable.Range(1980, 10)))
                        throw new Exception("The decade grid must be replaced by ten ascending years.");
                    ((Button)((DockPanel)CurrentPanel().Children[0]).Children[0])
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!Years().Any(button => button.Content.ToString() == "1980"))
                        throw new Exception("Year dateFilterDialog back button must restore the decade grid.");
                    Years().Single(button => button.Content.ToString() == "1980")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Years().Single(button => button.Content.ToString() == "1985")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Flush(dateFilterDialog);
                    if (opened.IsOpen || yearButton.Content.ToString() != "1985")
                        throw new Exception("A year click must immediately commit and close its popup.");
                    yearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Flush(dateFilterDialog);
                    if (!Years().Any(button => button.Content.ToString() == "1980"))
                        throw new Exception("Reopening the year popup must show the decade grid.");
                }
                finally { dateFilterDialog.Close(); }
                Console.WriteLine("PASS: Ascending decade and year grids are separate, catalog-independent, and commit on one year click.");
            }
            var deferredSurface = new BrowseGallerySurface();
            deferredSurface.DeferViewportPageRequests(true);
            deferredSurface.SetViewport(1000, 600);
            if (!deferredSurface.ViewportPageRequestsDeferred)
                throw new Exception("Provisional thumbnail loads were not suspended.");
            deferredSurface.DeferViewportPageRequests(false);
            if (deferredSurface.ViewportPageRequestsDeferred)
                throw new Exception("Provisional thumbnail loads cannot be released.");
            Console.WriteLine(
                "PASS: Startup warms the restored viewport before releasing background thumbnails.");

            // A submenu arrow is not sufficient: the application-wide WPF
            // template must contain a working PART_Popup for child programs.
            {
                var submenu = new MenuItem { Header = "Topaz" };
                var parentMenu = new MenuItem { Header = "Bearbeiten mit …" };
                parentMenu.Items.Add(submenu);
                var context = new ContextMenu { PlacementTarget = window };
                context.Items.Add(parentMenu);
                try
                {
                    context.IsOpen = true;
                    Flush(window);
                    parentMenu.IsSubmenuOpen = true;
                    Flush(window);
                    if (parentMenu.Template.FindName("PART_Popup", parentMenu)
                        is not Popup { IsOpen: true })
                        throw new Exception(
                            "The application's context-menu template does not open its child popup.");
                }
                finally { context.IsOpen = false; }
            }

            // The external batch and panorama decisions must use the SAME
            // existing three-button viewer footer as a single external edit.
            // Verify that Save cannot replace mismatched formats or panoramas.
            {
                const System.Reflection.BindingFlags privateInstance =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                var mainType = typeof(PCasa.App.MainWindow);
                var nested = mainType.GetNestedType("ExternalBatchReview",
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("Missing shared batch review state.");
                var reviewConstructor = nested.GetConstructors(
                    System.Reflection.BindingFlags.Public |
                    privateInstance).Single();
                var reviewField = mainType.GetField(
                    "_externalBatchReview", privateInstance)!;
                var refresh = mainType.GetMethod(
                    "UpdateEditControls", privateInstance)!;
                var outputsProperty = nested.GetProperty("Outputs")!;
                var cancelButton = (Button)window.FindName("CropCancelButton")!;
                var copyButton = (Button)window.FindName("CropSaveCopyButton")!;
                var saveButton = (Button)window.FindName("CropSaveButton")!;
                var sourceFiles = new[] { @"C:\smoke\a.jpg", @"C:\smoke\b.jpg" };
                var session = new PanoramaSession(Guid.NewGuid(),
                    [1, 2], sourceFiles, sourceFiles,
                    @"C:\smoke", [], null, null, IsPanorama: false);
                var review = reviewConstructor.Invoke([session]);
                try
                {
                    reviewField.SetValue(window, review);
                    refresh.Invoke(window, []);
                    if (!cancelButton.IsEnabled || copyButton.IsEnabled ||
                        saveButton.IsEnabled)
                        throw new Exception("Unfinished batch footer must offer Cancel only.");
                    outputsProperty.SetValue(review, new[]
                    {
                        new ExternalBatchOutput(0, @"C:\smoke\a.jpg"),
                        new ExternalBatchOutput(1, @"C:\smoke\b.jpg")
                    });
                    refresh.Invoke(window, []);
                    if (!cancelButton.IsEnabled || !copyButton.IsEnabled ||
                        !saveButton.IsEnabled)
                        throw new Exception("Same-format batch must offer all three choices.");
                    outputsProperty.SetValue(review, new[]
                    {
                        new ExternalBatchOutput(0, @"C:\smoke\a.png"),
                        new ExternalBatchOutput(1, @"C:\smoke\b.jpg")
                    });
                    refresh.Invoke(window, []);
                    if (!copyButton.IsEnabled || saveButton.IsEnabled)
                        throw new Exception("Changed-format batch must disable original Save.");
                    var panoramaSession = session with { IsPanorama = true };
                    var panoramaReview = reviewConstructor.Invoke([panoramaSession]);
                    outputsProperty.SetValue(panoramaReview,
                        new[] { new ExternalBatchOutput(0, @"C:\smoke\pano.jpg") });
                    reviewField.SetValue(window, panoramaReview);
                    refresh.Invoke(window, []);
                    if (!copyButton.IsEnabled || saveButton.IsEnabled)
                        throw new Exception("Panorama footer must allow Copy, not Save.");
                }
                finally
                {
                    reviewField.SetValue(window, null);
                    refresh.Invoke(window, []);
                }
                Console.WriteLine(
                    "PASS: Shared external batch/panorama footer has correct Cancel, Copy and Save gates.");
            }

            // Folder tree uses pixel rather than item-step scrolling, allowing
            // precision touchpads to move in smaller increments. This must
            // not alter the gallery's independent scrolling behavior.
            var folderTreeForScroll = (TreeView?)window.FindName("FoldersTree");
            if (folderTreeForScroll is null ||
                ScrollViewer.GetCanContentScroll(folderTreeForScroll))
                throw new Exception("Folder tree did not enable fine pixel scrolling.");
            Console.WriteLine("PASS: Folder tree uses pixel-based scrolling.");

            var menu = Descendants<Menu>(window).FirstOrDefault()
                ?? throw new Exception("Main menu was not constructed.");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var labels = items.Select(item => item.Header?.ToString()).ToArray();
            if (!labels.SequenceEqual(new[] {
                    "Datei", "Bearbeiten", "Darstellung", "Optionen", "Hilfe" }))
                throw new Exception("Unexpected top-level menu labels: " +
                    string.Join(", ", labels));
            var fileMenu = items.Single(item => Equals(item.Header, "Datei"));
            if (fileMenu.Items.OfType<MenuItem>().Count() != 1 ||
                !Equals(fileMenu.Items.OfType<MenuItem>().Single().Header, "Beenden"))
                throw new Exception("File menu must contain only Exit.");
            var optionsMenu = items.Single(item => Equals(item.Header, "Optionen"));
            var optionLabels = optionsMenu.Items.OfType<MenuItem>()
                .Select(item => item.Header?.ToString()).ToArray();
            if (!optionLabels.SequenceEqual(new[]
                {
                    "Ordnermanager", "Sprache", "Externe Programme",
                    "GPX Import", "Slideshow", "Taskmanager...",
                    "Datenbankverwaltung...", "Schreibfehler (GPS/Tags)"
                }))
                throw new Exception("Incorrect Options menu order: " +
                    string.Join(", ", optionLabels));
            if (items.Any(item => Equals(item.Header, "Ordner") ||
                Equals(item.Header, "Extras")))
                throw new Exception("Obsolete top-level menu remains.");
            if (window.FindName("EditCompareSingleButton") is not Button ||
                window.FindName("EditCompareSideButton") is not Button ||
                window.FindName("EditCompareSplitButton") is not Button)
                throw new Exception("Original/edit comparison mode buttons missing.");
            var editMenu = items.Single(item => Equals(item.Header, "Bearbeiten"));
            var restoreMenu = editMenu.Items.OfType<MenuItem>().Single(item =>
                Equals(item.Header, "Bild wiederherstellen"));
            if (restoreMenu.IsEnabled)
                throw new Exception("Restore must be disabled without a selected, restorable image.");
            var helpMenu = items.Single(item => Equals(item.Header, "Hilfe"));
            var helpCommands = helpMenu.Items.OfType<MenuItem>().ToArray();
            if (helpCommands.Length != 2 ||
                !Equals(helpCommands[0].Header, "Nach Updates suchen") ||
                !Equals(helpCommands[1].Header, "Über PCasa"))
                throw new Exception("Help must contain exactly Check for Update and About.");
            Console.WriteLine("PASS: Menu reduced and reorganized with bilingual Help commands.");

            // Alpha.29: one manual refresh command applies to ANY photo
            // or video and only to the selected image IDs. No other image
            // in a half-million entry catalog should enter its P0 package.
            var thumbEntries = new BrowseThumbnailIndexEntry[]
            {
                new(10, 2, 192, "", 0, 0, 1, 1, FileName: "one.mp4"),
                new(11, 2, 192, "", 0, 0, 1, 1, FileName: "two.mov"),
                new(12, 2, 192, "", 0, 0, 1, 1, FileName: "photo.jpg"),
                new(13, 2, 192, "", 0, 0, 1, 1, FileName: "other.jpg")
            };
            var selectThumbnailMethod = typeof(PCasa.App.MainWindow).GetMethod(
                "SelectThumbnailIds",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)!;
            var mixed = (long[])selectThumbnailMethod.Invoke(null,
                [12L, new long[] { 10, 11, 12 }, thumbEntries])!;
            if (!mixed.OrderBy(id => id).SequenceEqual(new long[] { 10, 11, 12 }))
                throw new Exception(
                    "Refresh Thumbnail excluded selected photos or included another photo.");
            var onlyClicked = (long[])selectThumbnailMethod.Invoke(null,
                [13L, new long[] { 10, 11, 12 }, thumbEntries])!;
            if (!onlyClicked.SequenceEqual(new long[] { 13 }))
                throw new Exception(
                    "Right-clicking an unselected photo refreshed the whole selection.");
            var old = thumbEntries[2] with { HasRestore = true, HasGps = true };
            var renewed = old with
            {
                PackFile = "new.pack", ByteOffset = 314, ByteLength = 19,
                PixelWidth = 200, PixelHeight = 150, HasRestore = false
            };
            var replaced = ThumbnailRefreshProjection.ReplaceLocation(old, renewed);
            if (!replaced.HasRestore || !replaced.HasGps ||
                replaced.ByteOffset != 314 || replaced.ImageId != old.ImageId)
                throw new Exception(
                    "Targeted thumbnail refresh changed catalog badges or image identity.");
            var changedList = ThumbnailRefreshProjection.Replace(thumbEntries,
                new Dictionary<long, BrowseThumbnailIndexEntry> { [12L] = renewed });
            if (!ReferenceEquals(changedList[0], thumbEntries[0]) ||
                !ReferenceEquals(changedList[1], thumbEntries[1]) ||
                !ReferenceEquals(changedList[3], thumbEntries[3]))
                throw new Exception("One thumbnail refresh replaced unrelated index entries.");
            Console.WriteLine(
                "PASS: Photo/video thumbnail refresh honors selected IDs and preserves unrelated index entries.");

            // The five editors use identically sized ICON buttons with
            // captions outside them; the three autos form their own row.
            var initEditing = typeof(PCasa.App.MainWindow).GetMethod(
                "InitializeCropTools",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            initEditing.Invoke(window, null);
            Flush(window);
            // Rendering is size-driven, not an initial imperative draw.
            if (window.FindName("ViewerStraightenGrid") is not StraightenGridOverlay)
                throw new Exception("Straighten grid uses the old Canvas painter.");
            if (StraightenGridOverlay.Divisions != 24)
                throw new Exception("Straighten grid spacing was not halved.");
            var gridProbe = new StraightenGridOverlay { Width = 480, Height = 320 };
            void AssertGridPaint(int width, int height)
            {
                gridProbe.Width = width;
                gridProbe.Height = height;
                gridProbe.Measure(new Size(width, height));
                gridProbe.Arrange(new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96,
                    PixelFormats.Pbgra32);
                bitmap.Render(gridProbe);
                var bytes = new byte[width * height * 4];
                bitmap.CopyPixels(bytes, width * 4, 0);
                var x = width / 24; // A new line absent in the 12-part grid.
                var painted = 0;
                for (var y = 4; y < height - 4; y++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (bytes[(y * width + x + dx) * 4 + 3] > 0)
                            painted++;
                if (painted < 5)
                    throw new Exception("Straighten grid missing after WPF layout.");
            }
            AssertGridPaint(480, 320);
            gridProbe.Visibility = Visibility.Collapsed;
            gridProbe.Visibility = Visibility.Visible;
            AssertGridPaint(240, 160);
            Console.WriteLine("PASS: Fine straighten grid is rendered and survives layout changes.");

            var manualTools = (WrapPanel)window.FindName("ViewerToolsList")!;
            var automaticTools = (StackPanel)window.FindName("ViewerAutoToolsList")!;
            if (manualTools.Children.Count != 2 ||
                automaticTools.Children.Count != 3 ||
                automaticTools.Orientation != Orientation.Horizontal)
                throw new Exception("Expected two manual tools and three autos in one row.");
            // Automatic sharpening belongs next to its radius/detail
            // controls, not as a fourth icon overflowing the auto row.
            var sharpenAuto = (Button)window.FindName("SharpenAutoButton")!;
            var sharpenRadius = (Slider)window.FindName("SharpenRadiusSlider")!;
            var sharpenDetail = (Slider)window.FindName("SharpenDetailSlider")!;
            if (sharpenAuto is null || sharpenRadius is null ||
                sharpenDetail is null ||
                !Equals(sharpenAuto.Content, UiLanguage.T("Schärfen (automatisch)")))
                throw new Exception(
                    "Auto-sharpen and radius/detail controls are missing from the tonal editor.");
            // Alpha.31: four truly readable tabs; unfinished masks removed.
            var editPanel = (Grid)window.FindName("ViewerEditingPanel")!;
            static bool HasEditorScroll(DependencyObject node)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(node)
                             .OfType<DependencyObject>())
                    if (child is ScrollViewer || HasEditorScroll(child)) return true;
                return false;
            }
            if (HasEditorScroll(editPanel))
                throw new Exception("Editor tools gained a scrollbar.");
            if (window.FindName("EditorTabMasks") is not null ||
                window.FindName("ViewerPanelMasks") is not null ||
                window.FindName("ViewerMaskPaintOverlay") is not null ||
                window.FindName("EditorMaskEnabled") is not null)
                throw new Exception("Retired mask UI is still reachable.");
            var tabs = (TabControl)window.FindName("EditorToolTabs")!;
            if (tabs.Items.Count != 4)
                throw new Exception("Expected Quick, Light, Color and Details only.");
            var quickTab = (TabItem)window.FindName("EditorTabQuick")!;
            var lightTab = (TabItem)window.FindName("EditorTabLight")!;
            var detailPanel = (StackPanel)window.FindName("ViewerPanelDetail")!;
            // Inspect the ACTUAL TextBlock in the applied control template.
            // TabItem.Foreground alone can be white while its header glyphs
            // still render black because of the third-party WPF theme.
            lightTab.IsSelected = true;
            lightTab.ApplyTemplate();
            var lightCaption = (TextBlock?)lightTab.Template.FindName(
                "TabCaption", lightTab);
            var editorTextColor = ((SolidColorBrush)Application.Current.Resources[
                "PcasaTextBrush"]).Color;
            if (lightCaption?.Foreground is not SolidColorBrush lightBrush ||
                lightBrush.Color != editorTextColor)
                throw new Exception("Selected tab caption does not follow the active theme.");
            quickTab.IsSelected = true;
            quickTab.ApplyTemplate();
            var quickCaption = (TextBlock?)quickTab.Template.FindName(
                "TabCaption", quickTab);
            if (quickCaption?.Foreground is not SolidColorBrush quickBrush ||
                quickBrush.Color != editorTextColor)
                throw new Exception("Unselected tab caption does not follow the active theme.");
            lightTab.IsSelected = true;
            if (window.FindName("EditUndoOptionsMenuItem") is not MenuItem ||
                window.FindName("ExternalEditorsOptionsMenuItem") is not MenuItem)
                throw new Exception("Undo settings and external program setup missing from their main menus.");
            // Reuse the already-resolved top-level menus from the initial
            // smoke assertions, rather than redeclaring names in this scope.
            if (!editMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Name == "EditUndoOptionsMenuItem") ||
                !optionsMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Name == "ExternalEditorsOptionsMenuItem") ||
                optionsMenu.Items.OfType<MenuItem>().Any(item =>
                    item.Header?.ToString() is "Image editing" or "Bildbearbeitung"))
                throw new Exception("Undo or editor program configuration is in the wrong menu.");
            // Startup and refresh load a full local catalog index. Their potentially
            // costly folder sort runs on the foreground catalog-read
            // worker, but the publication must reject stale tree order or
            // a sort setting changed while SQLite was being read.
            var startupSortGuard = typeof(PCasa.App.MainWindow).GetMethod(
                "CanReuseReadOrdering",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Catalog sort publication guard missing.");
            var startupOrderBefore = new Dictionary<long, int>
            {
                [30] = 0, [20] = 1
            };
            var startupOrderEqual = new Dictionary<long, int>
            {
                [20] = 1, [30] = 0
            };
            var startupOrderReversed = new Dictionary<long, int>
            {
                [30] = 1, [20] = 0
            };
            bool ReuseStartup(
                IReadOnlyDictionary<long, int>? previous,
                IReadOnlyDictionary<long, int> published,
                GallerySortOptions priorSort, GallerySortOptions actualSort) =>
                (bool)startupSortGuard.Invoke(null,
                    [previous, published, priorSort, actualSort])!;
            if (!ReuseStartup(startupOrderBefore, startupOrderEqual,
                    GallerySortOptions.Default, GallerySortOptions.Default) ||
                ReuseStartup(startupOrderBefore, startupOrderReversed,
                    GallerySortOptions.Default, GallerySortOptions.Default) ||
                ReuseStartup(startupOrderBefore, startupOrderEqual,
                    GallerySortOptions.Default,
                    new GallerySortOptions(GallerySortField.Captured, false)) ||
                ReuseStartup(startupOrderBefore,
                    new Dictionary<long, int> { [20] = 1 },
                    GallerySortOptions.Default, GallerySortOptions.Default) ||
                ReuseStartup(null, startupOrderEqual,
                    GallerySortOptions.Default, GallerySortOptions.Default))
                throw new Exception(
                    "Background sort reused a stale WPF tree/sort snapshot.");
            Console.WriteLine(
                "PASS: Background sort accepts only current folder order and sort choice.");

            // Slideshow Play belongs permanently at the LEFT edge of the
            // search toolbar; horizontal search scrolling may not displace it.
            var slideshowPlay = window.FindName("SlideshowStartButton") as Button
                ?? throw new Exception("Slideshow Play button missing.");
            var searchScroller = (ScrollViewer)window.FindName(
                "SearchToolbarScrollViewer")!;
            for (DependencyObject? ancestor = slideshowPlay;
                 ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ReferenceEquals(ancestor, searchScroller))
                    throw new Exception("Slideshow Play scrolls away with search controls.");
            var slideshowOptions = window.FindName("SlideshowOptionsMenuItem") as MenuItem
                ?? throw new Exception("Slideshow options menu missing.");
            var slideshowMenuItems = optionsMenu.Items.Cast<object>().ToList();
            var slideshowMenuPosition = slideshowMenuItems.IndexOf(slideshowOptions);
            var taskMenuPosition = slideshowMenuItems.IndexOf(
                window.FindName("TaskManagerMenuItem"));
            var separatorPosition = slideshowMenuItems.FindIndex(x => x is Separator);
            if (slideshowMenuPosition < 0 || separatorPosition <= slideshowMenuPosition ||
                taskMenuPosition <= separatorPosition)
                throw new Exception("Slideshow and Task Manager options have wrong menu grouping.");
            var slideshowDefaults = new SlideshowSettings();
            if (slideshowDefaults.BackgroundMode != "gallery" ||
                slideshowDefaults.DisplayMode != "gallery" ||
                slideshowDefaults.Transition != "fade" ||
                slideshowDefaults.IntervalSeconds != 5 ||
                slideshowDefaults.TransitionMilliseconds != 700 ||
                !slideshowDefaults.PreloadNext)
                throw new Exception("Kodi-like slideshow defaults or inherited gallery texture changed.");
            var normalizedSlideshow = new SlideshowSettings
            {
                IntervalSeconds = -42,
                Transition = "unknown",
                BackgroundMode = "invalid"
            }.Normalize();
            if (normalizedSlideshow.IntervalSeconds != 1 ||
                normalizedSlideshow.Transition != "fade" ||
                normalizedSlideshow.BackgroundMode != "gallery")
                throw new Exception("Slideshow settings normalization is unsafe.");
            foreach (var mode in new[] { "projector", "cube", "cards", "zoomfade",
                "wipe-left", "wipe-right", "wipe-up", "wipe-down" })
                if (new SlideshowSettings { Transition = mode }.Normalize().Transition != mode)
                    throw new Exception("A GPU transition was discarded: " + mode);
            Console.WriteLine(
                "PASS: Left-anchored slideshow Play, all GPU transitions and inherited gallery texture.");

            // The original tone sliders had no direction markings.
            // Each axis must display its endpoint meaning immediately below
            // the track, with language-aware, non-hit-testing captions.
            foreach (var (toneName, low, high) in new[]
            {
                ("BrightnessSlider", "−", "+"),
                ("HighlightsSlider", "−", "+"),
                ("ShadowsSlider", "−", "+"),
                ("ContrastSlider", "−", "+"),
                ("TemperatureSlider", "Kühler", "Wärmer"),
                ("TintSlider", "Grün", "Magenta"),
                ("SaturationSlider", "Weniger", "Mehr"),
                ("DenoiseLumaSlider", "Weniger", "Mehr"),
                ("DenoiseChromaSlider", "Weniger", "Mehr"),
                ("SharpenSlider", "Weniger", "Mehr"),
                ("SharpenRadiusSlider", "Klein", "Groß"),
                ("SharpenDetailSlider", "Weniger", "Mehr")
            })
            {
                if (window.FindName(toneName) is not Slider tone ||
                    window.FindName(toneName + "Axis") is not Grid axis ||
                    axis.Children.OfType<TextBlock>().Count() != (toneName == "BrightnessSlider" ? 2 : 3) ||
                    axis.Children.OfType<TextBlock>().Single(label => label.HorizontalAlignment == HorizontalAlignment.Left).Text != UiLanguage.T(low) ||
                    axis.Children.OfType<TextBlock>().Single(label => label.HorizontalAlignment == HorizontalAlignment.Right).Text != UiLanguage.T(high) ||
                    (toneName != "BrightnessSlider" && !axis.Children.OfType<TextBlock>().Any(label =>
                        label.HorizontalAlignment == HorizontalAlignment.Center &&
                        label.GetBindingExpression(TextBlock.TextProperty) is not null)) ||
                    axis.IsHitTestVisible ||
                    axis.Children.OfType<TextBlock>().Any(label =>
                        label.FontSize > 10) ||
                    tone.Parent != axis.Parent)
                    throw new Exception(
                        "Tone slider axis lacks thin language-aware direction labels: " + toneName);
            }
            Console.WriteLine("PASS: Light/Color/Details tone axes show localized directions.");
            var brightness = (Slider)window.FindName("BrightnessSlider")!;
            var highlights = (Slider)window.FindName("HighlightsSlider")!;
            if (brightness.Minimum != -1 || brightness.Maximum != 1 ||
                !double.IsNaN(highlights.Width) || highlights.SmallChange > .01)
                throw new Exception("Light sliders lack fine adjustments.");
            var leftCropTools = window.FindName("ViewerOnImageToolsBar") as Border;
            if (leftCropTools is null || !LogicalTreeHelper.GetChildren(editPanel)
                .OfType<DependencyObject>().Any(child => Contains(child, leftCropTools)))
                throw new Exception("Crop controls are not located in the left editor pane.");
            static bool Contains(DependencyObject node, DependencyObject target) =>
                ReferenceEquals(node, target) ||
                LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()
                    .Any(child => Contains(child, target));
            quickTab.IsSelected = true;
            if (detailPanel.Visibility != Visibility.Collapsed ||
                !quickTab.IsSelected ||
                window.FindName("ExternalEditorToolsList") is not WrapPanel ||
                window.FindName("ExternalEditQuickButton") is not null)
                throw new Exception("Quick must show configured program icons, not a dropdown.");
            var topazPhoto = new ExternalEditorProgram("Topaz Photo",
                @"C:\Topaz\Photo.exe", "photo");
            var topazVideo = new ExternalEditorProgram("Topaz Video",
                @"C:\Topaz\Video.exe", "video");
            var topazGigapixel = new ExternalEditorProgram("Topaz Gigapixel",
                @"C:\Topaz\Gigapixel.exe", ".jpg;.png;.arw");
            if (!topazPhoto.Matches("image.jpg") ||
                topazPhoto.Matches("clip.mp4") ||
                !topazVideo.Matches("clip.mp4") ||
                topazVideo.Matches("image.jpg") ||
                !topazGigapixel.Matches("image.png") ||
                topazGigapixel.Matches("clip.mov"))
                throw new Exception("Configured external programs ignore their media formats.");
            var emptyProgram = new ExternalEditorProgram("Unconfigured", "", "photo");
            if (emptyProgram.Matches("image.jpg"))
                throw new Exception("An unconfigured editor became executable.");
            // No RAW file can be sent to any external editor, not even by a
            // wildcard, a configured RAW extension, or the photo category.
            foreach (var rawExtension in new[] { ".arw", ".nef", ".nrw",
                ".cr2", ".cr3", ".dng", ".raf", ".rw2", ".orf", ".pef",
                ".srw", ".crw" })
            {
                var rawFile = "photo" + rawExtension;
                if (topazPhoto.Matches(rawFile) ||
                    new ExternalEditorProgram("Any", "editor.exe", "*").Matches(rawFile) ||
                    new ExternalEditorProgram("Explicit", "editor.exe",
                        rawExtension).Matches(rawFile))
                    throw new Exception("RAW was offered for external editing.");
            }
            if (new ExternalEditorSettings
                {
                    Programs = [new ExternalEditorProgram("Any", "editor.exe", "*")]
                }.ForFile("image.nef").Count != 0)
                throw new Exception("RAW editor slipped through the settings filter.");
            // Icons are generated from the real executable path.
            // The menu order and 74x49 button geometry must match Crop.
            var configuredField = typeof(PCasa.App.MainWindow).GetField(
                "_externalEditors", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var originalSettings = configuredField.GetValue(window);
            var refreshCards = typeof(PCasa.App.MainWindow).GetMethod(
                "RefreshExternalEditorToolButtons",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var externalCards = (WrapPanel)window.FindName("ExternalEditorToolsList")!;
            var runningExecutable = Environment.ProcessPath
                ?? throw new Exception("Test process has no executable path.");
            try
            {
                configuredField.SetValue(window, new ExternalEditorSettings
                {
                    Programs =
                    [
                        new("Topaz Photo", runningExecutable, "photo"),
                        new("Topaz Video", runningExecutable, "video"),
                        new("Topaz Gigapixel", runningExecutable, ".jpg;.png")
                    ]
                });
                refreshCards.Invoke(window, [1001L, "picture.jpg"]);
                if (externalCards.Children.Count != 2)
                    throw new Exception("External program cards did not filter by image type.");
                var cards = externalCards.Children.Cast<StackPanel>().ToArray();
                var cardButtons = cards.Select(card => card.Children.OfType<Button>()
                    .Single()).ToArray();
                var titles = cards.Select(card =>
                    card.Children.OfType<TextBlock>().Single().Text).ToArray();
                if (!titles.SequenceEqual(new[] { "Topaz Photo", "Topaz Gigapixel" }) ||
                    cardButtons.Any(btn => btn.Width != 64 || btn.Height != 43 ||
                        btn.Content is not FrameworkElement) ||
                    cards.Any(card => card.Width != 80))
                    throw new Exception("External editor cards are not icon/caption tools.");
                refreshCards.Invoke(window, [1002L, "movie.mp4"]);
                if (externalCards.Children.Count != 1 ||
                    ((StackPanel)externalCards.Children[0]).Children
                        .OfType<TextBlock>().Single().Text != "Topaz Video")
                    throw new Exception("Video editor cards showed photo applications.");
            }
            finally
            {
                configuredField.SetValue(window, originalSettings);
                refreshCards.Invoke(window, [0L, "none.jpg"]);
            }
            // The external editor setup dialog must not regress to its
            // squeezed 120-px list, template buttons or decorative headings.
            var externalSettingsDialog = new ExternalEditorsWindow(
                new ExternalEditorSettings());
            try
            {
                if (externalSettingsDialog.Content is not Grid editorRoot ||
                    editorRoot.ColumnDefinitions[0].Width.Value < 290 ||
                    editorRoot.Children.OfType<Grid>().Any())
                    throw new Exception("External editor list is too narrow.");
                static IEnumerable<DependencyObject> Descendants(DependencyObject node)
                {
                    foreach (var child in LogicalTreeHelper.GetChildren(node)
                                 .OfType<DependencyObject>())
                    {
                        yield return child;
                        foreach (var descendant in Descendants(child))
                            yield return descendant;
                    }
                }
                var editorText = Descendants(editorRoot)
                    .OfType<TextBlock>().Select(label => label.Text).ToArray();
                if (editorText.Any(label => label is
                    "Topaz templates (then choose the executable):" or
                    "Applications (menu order)" or "Editing application" or
                    "Applications start only when you explicitly select them."))
                    throw new Exception("Removed external editor headings/templates returned.");
                var controls = Descendants(editorRoot).OfType<Button>()
                    .Select(button => button.Content?.ToString()).ToArray();
                if (!controls.Contains("+") || !controls.Contains("−") ||
                    controls.Any(label => label is "Topaz Photo" or
                        "Topaz Gigapixel" or "Topaz Video"))
                    throw new Exception("External app manager does not use + / − controls.");
            }
            finally { externalSettingsDialog.Close(); }
            Console.WriteLine(
                "PASS: Alpha.33 high-contrast themed tabs and compact external editor dialog.");
            var toolCards = manualTools.Children.Cast<StackPanel>()
                .Concat(automaticTools.Children.Cast<StackPanel>()).ToArray();
            var toolButtons = toolCards.Select(card =>
                card.Children.OfType<Button>().Single()).ToArray();
            if (toolButtons.Any(button =>
                    button.Width != toolButtons[0].Width ||
                    button.Height != toolButtons[0].Height) ||
                toolCards.Any(card =>
                    card.Children.OfType<TextBlock>().Count() != 1))
                throw new Exception("Editor icon controls are not uniform.");
            // Alpha.35: RAW viewing must disable EVERY editing tab and
            // quick-action icon before a click, not show a two-click error.
            var seqField = typeof(PCasa.App.MainWindow).GetField("_viewerSequence",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var indexField = typeof(PCasa.App.MainWindow).GetField("_viewerCurrentIndex",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var controlsMethod = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateEditControls", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var oldSequence = seqField.GetValue(window);
            var oldIndex = indexField.GetValue(window);
            var refinedField = typeof(PCasa.App.MainWindow).GetField("_viewerHasOriginal",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var oldRefined = refinedField.GetValue(window);
            try
            {
                // A fully decoded RAW must still reject all internal editing.
                refinedField.SetValue(window, true);
                var rawIndex = new[] { thumbEntries[2] with { FileName = "photo.arw" } };
                seqField.SetValue(window, PhotoSequence.Create(rawIndex,
                    new HashSet<long>(), 12L, PhotoSequenceScope.Gallery));
                indexField.SetValue(window, 0);
                controlsMethod.Invoke(window, null);
                if (tabs.IsEnabled || toolButtons.Any(btn => btn.IsEnabled))
                    throw new Exception("RAW editor remains clickable.");

                var jpegIndex = new[] { thumbEntries[2] };
                seqField.SetValue(window, PhotoSequence.Create(jpegIndex,
                    new HashSet<long>(), 12L, PhotoSequenceScope.Gallery));
                refinedField.SetValue(window, false);
                controlsMethod.Invoke(window, null);
                if (tabs.IsEnabled || toolButtons.Any(btn => btn.IsEnabled))
                    throw new Exception("JPEG thumbnail unlocked editing before refined decode.");
                refinedField.SetValue(window, true);
                controlsMethod.Invoke(window, null);
                if (!tabs.IsEnabled || toolButtons.Any(btn => !btn.IsEnabled))
                    throw new Exception("Refined JPEG editor did not become available.");
                var decisionRow = (StackPanel)window.FindName("ViewerToolDecisionRow")!;
                var apply = (Button)window.FindName("CropApplyButton")!;
                var cancelDraft = (Button)window.FindName("ViewerToolCancelButton")!;
                if (decisionRow.Visibility != Visibility.Visible ||
                    apply.Parent != decisionRow || cancelDraft.Parent != decisionRow ||
                    ((Border)window.FindName("ViewerOnImageToolsBar")!).Visibility != Visibility.Collapsed)
                    throw new Exception("Ordinary tonal tool decisions depend on the crop-only container.");
            }
            finally
            {
                seqField.SetValue(window, oldSequence);
                indexField.SetValue(window, oldIndex);
                refinedField.SetValue(window, oldRefined);
                controlsMethod.Invoke(window, null);
            }

            var activeLanguage = UiLanguage.Current.Code;
            try
            {
                UiLanguage.Current.Select("en");
                Flush(window);
                var translatedAuto = (TextBlock)
                    ((StackPanel)automaticTools.Children[1]).Children[1];
                if (translatedAuto.Text != "Contrast (automatic)")
                    throw new Exception(
                        "Dynamic editor tool captions did not switch to English.");
                var brightnessReset =
                    (Button)window.FindName("BrightnessResetButton")!;
                if (!Equals(brightnessReset.Content, "↺") ||
                    !Equals(brightnessReset.ToolTip,
                        "Reset brightness adjustment to zero") ||
                    !Equals(((CheckBox)window.FindName("StraightenWideRangeCheckBox")!).Content,
                        "Extended range ±45°") ||
                    !Equals(((Slider)window.FindName("StraightenAngleSlider")!).ToolTip,
                        "Fine adjustment: arrow keys 0.05°. Extended range up to ±45°."))
                    throw new Exception(
                        "Editor controls retained untranslated German captions or tooltips.");
            }
            finally
            {
                UiLanguage.Current.Select(activeLanguage);
                Flush(window);
            }
            var floatingTools = (Border)window.FindName("ViewerOnImageToolsBar")!;
            var cropOptions = (StackPanel)window.FindName("ViewerCropOptions")!;
            var straightenOptions = (StackPanel)window.FindName("ViewerStraightenOptions")!;
            // The floating toolbar is Collapsed until Crop/Straighten starts.
            // A collapsed WPF subtree need not exist in the VISUAL tree yet,
            // but its XAML logical ancestry is still fully defined.
            // Verify the actual logical parent chain instead of depending on
            // whether a hidden ScrollViewer has generated a visual template.
            static bool IsWithinLogicalParent(DependencyObject item,
                DependencyObject ancestor)
            {
                for (DependencyObject? parent = item; parent is not null;
                     parent = LogicalTreeHelper.GetParent(parent))
                    if (ReferenceEquals(parent, ancestor)) return true;
                return false;
            }
            if (!IsWithinLogicalParent(cropOptions, floatingTools) ||
                !IsWithinLogicalParent(straightenOptions, floatingTools))
                throw new Exception(
                    "Crop/Straighten options are not in image overlay's logical tree.");
            var toolbarFooter = (Grid)window.FindName("ViewerCropFooter")!;
            var histogram = (Canvas)window.FindName("ViewerHistogramCanvas")!;
            var histogramBorder = (Border)((StackPanel)histogram.Parent).Parent;
            if (Grid.GetRow(toolbarFooter) != 2 ||
                Grid.GetRow(histogramBorder) != 3 ||
                toolbarFooter.Children.OfType<Button>().Count() != 3)
                throw new Exception("Histogram footer is not a stable single-row toolbar.");
            var toneReset = (Button)window.FindName("BrightnessResetButton")!;
            var toneSlider = (Slider)window.FindName("BrightnessSlider")!;
            if (toneReset.Parent != toneSlider.Parent)
                throw new Exception("Brighten/Darken Reset must sit directly by slider.");
            Console.WriteLine(
                "PASS: Uniform translated editor icons, Picasa-style floating tool panel, stable footer and bottom histogram.");

            // Undo/Redo tips must name the actual next edit, not only the
            // generic operation. The same persisted cursor drives both.
            var sessionField = typeof(PCasa.App.MainWindow).GetField(
                "_photoEditSession", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var updateEditor = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateEditControls", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var editOriginal = BitmapSource.Create(2, 2, 96, 96,
                PixelFormats.Bgra32, null, new byte[16], 8);
            var editorSession = new PhotoEditSession(777, editOriginal);
            var previousSession = sessionField.GetValue(window);
            try
            {
                editorSession.Apply(new PhotoEditStep("auto-contrast"));
                sessionField.SetValue(window, editorSession);
                updateEditor.Invoke(window, null);
                var undoTip = Convert.ToString(
                    ((Button)window.FindName("CropUndoButton")!).ToolTip);
                if (undoTip is null || !undoTip.Contains("Kontrast (automatisch)"))
                    throw new Exception("Undo tooltip did not describe next edit.");
                editorSession.Undo();
                updateEditor.Invoke(window, null);
                var redoTip = Convert.ToString(
                    ((Button)window.FindName("CropRedoButton")!).ToolTip);
                if (redoTip is null || !redoTip.Contains("Kontrast (automatisch)"))
                    throw new Exception("Redo tooltip did not describe restored edit.");
            }
            finally
            {
                sessionField.SetValue(window, previousSession);
                updateEditor.Invoke(window, null);
            }
            Console.WriteLine("PASS: Undo/Redo tooltips name the next actual image edit.");

            var appearanceMenu = (MenuItem?)window.FindName("AppearanceSettingsMenuItem")
                ?? throw new Exception("Single appearance dialog menu missing.");
            void ConfigureAppearance(Action<PCasa.App.AppearanceSettingsWindow> configure,
                bool accept = true)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    var dlg = Application.Current.Windows
                        .OfType<PCasa.App.AppearanceSettingsWindow>().Single();
                    configure(dlg);
                    if (accept)
                    {
                        var ok = Descendants<Button>(dlg)
                            .Single(b => Equals(b.Content, UiLanguage.T("OK")));
                        ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, ok));
                    }
                    else dlg.Close();
                }), DispatcherPriority.ContextIdle);
                appearanceMenu.RaiseEvent(
                    new RoutedEventArgs(MenuItem.ClickEvent, appearanceMenu));
                Flush(window);
            }
            void ChooseAppearance(string group, string choice)
                => ConfigureAppearance(dlg =>
                    Descendants<RadioButton>(dlg).Single(button =>
                        button.GroupName == "Appearance_" + group &&
                        Equals(button.Tag, choice)).IsChecked = true);
            ConfigureAppearance(dlg =>
            {
                var cards = Descendants<Border>(dlg).Where(b =>
                    b.Child is StackPanel group &&
                    group.Children.Count >= 2 &&
                    group.Children[0] is TextBlock title &&
                    title.FontWeight == FontWeights.SemiBold)
                    .ToDictionary(b => ((TextBlock)((StackPanel)b.Child).Children[0]).Text);
                if (!cards.TryGetValue("Design", out var design) ||
                    !cards.TryGetValue("Galeriehintergrund", out var background) ||
                    !cards.TryGetValue("Dateinamen", out var names) ||
                    !cards.TryGetValue("Galerieoptionen", out var options) ||
                    !cards.TryGetValue("Ordnerstatus", out var status))
                    throw new Exception("Reorganized appearance groups are missing.");
                var contentIn = (Border b) => (StackPanel)((StackPanel)b.Child).Children[1];
                if (!Descendants<CheckBox>(contentIn(names)).Contains(dlg.FileNamesCheck) ||
                    !Descendants<CheckBox>(contentIn(names)).Contains(dlg.BoldFileNamesCheck) ||
                    Descendants<CheckBox>(contentIn(options)).Contains(dlg.FileNamesCheck) ||
                    !Descendants<RadioButton>(contentIn(names)).Any(r =>
                        Equals(r.Tag, "10") && Equals(r.Content, "Standard (10)")))
                    throw new Exception("Filename checkboxes and three sizes need a separate card with Standard.");
                if (!Descendants<RadioButton>(contentIn(names)).Where(r =>
                    r.GroupName == "Appearance_caption").Select(r => r.Tag?.ToString())
                    .SequenceEqual(new[] { "10", "12", "14" }))
                    throw new Exception("Caption font-size choices were lost or reordered.");
                var statusSymbols = Descendants<TextBlock>(contentIn(status))
                    .Where(b => b.Text is "✓" or "↻" or "◷" or "⊘" or "!" or "?" or "●")
                    .Select(b => b.Text).ToArray();
                if (Descendants<Button>(contentIn(status)).Any() ||
                    !Descendants<TextBlock>(contentIn(status)).Any(b =>
                        b.Text == "Legende") ||
                    !statusSymbols.SequenceEqual(new[] { "✓", "↻", "◷", "⊘", "!", "?", "●" }))
                    throw new Exception("Folder status must display all seven legend symbols permanently, without a button.");

                Rect BoundsIn(FrameworkElement e) =>
                    e.TransformToAncestor(dlg).TransformBounds(new Rect(
                        new Point(), e.RenderSize));
                var d = BoundsIn(design); var bg = BoundsIn(background);
                if (d.Bottom > bg.Top + 1 || d.Left != bg.Left)
                    throw new Exception("Design must be directly above gallery background.");
                var sectionBottom = cards.Values.Max(card => BoundsIn(card).Bottom);
                var footerTop = BoundsIn(dlg.CancelButton).Top;
                if (footerTop - sectionBottom > 45 || dlg.SizeToContent != SizeToContent.Height)
                    throw new Exception("Appearance dialog retained a large blank band before its footer.");
                // The radio/check values are still owned by the existing
                // detached snapshot; this is presentation-only.
            }, false);
            Console.WriteLine("PASS: Compact appearance layout, filename card, theme order and permanent symbol legend.");
            ConfigureAppearance(dlg => dlg.TooltipsCheck.IsChecked = false, false);
            // Options belongs to the main-menu level, rather than being
            // nested under the legacy Options dropdown.
            if (appearanceMenu.Parent is not Menu)
                throw new Exception("Appearance is not a direct main-menu item.");
            if (window.FindName("TooltipsEnabledMenu") is not null ||
                window.FindName("PhotoSequenceFolderMenu") is not null ||
                window.FindName("GalleryBackgroundTextureMenu") is not null)
                throw new Exception("Legacy appearance menu still exposes duplicate settings.");
            Console.WriteLine("PASS: All display settings are staged by one dialog; cancel closes without applying.");

            // Exercise the real menu action, live resource bindings and
            // appearance.json persistence. This must work WITHOUT restarting
            // WPF and without creating a second UI-translation mechanism.
            var englishMenu = window.FindName("LanguageEnglishMenu") as MenuItem
                ?? throw new Exception("English language selector is missing.");
            var germanMenu = window.FindName("LanguageGermanMenu") as MenuItem
                ?? throw new Exception("German language selector is missing.");
            if (!germanMenu.IsChecked || englishMenu.IsChecked)
                throw new Exception("German should be the initial UI language.");
            // A runtime-generated selection must respond to language changes
            // as reliably as the XAML-bound sort menu items.
            var localizedSortButton = window.FindName("GallerySortSelectorButton") as Button
                ?? throw new Exception("Sort selector missing during language test.");
            var localizedSortLabel = window.FindName("GallerySortSelectedFieldLabel") as TextBlock
                ?? throw new Exception("Sort field label missing during language test.");
            var localizedSortChoices = localizedSortButton.ContextMenu?.Items
                .OfType<MenuItem>().ToArray()
                ?? throw new Exception("Sort choices missing during language test.");
            MenuItem Choice(string key) => localizedSortChoices.Single(
                item => Equals(item.Tag, key));
            var sortDirectionButton = (Button?)window.FindName("GallerySortDirectionButton")
                ?? throw new Exception("Sort direction toggle missing.");
            if (sortDirectionButton.ToolTip?.ToString() == "Absteigend")
                sortDirectionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, sortDirectionButton));
            Choice("Captured").RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent, Choice("Captured")));
            Flush(window);
            if (localizedSortLabel.Text != "Aufnahmedatum" ||
                localizedSortButton.ToolTip?.ToString() !=
                    "Sortierung: Aufnahmedatum, aufsteigend")
                throw new Exception("German selected sort field/tooltip is incorrect.");
            englishMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, englishMenu));
            Flush(window);
            if (UiLanguage.Current.Code != "en" ||
                !englishMenu.IsChecked || germanMenu.IsChecked ||
                !menu.Items.OfType<MenuItem>()
                    .Select(item => item.Header?.ToString())
                    .SequenceEqual(new[] { "File", "Edit", "Appearance", "Options", "Help" }))
                throw new Exception("English translations were not applied live.");
            AssertCaptionGaps("en");
            if ((window.FindName("GalleryViewFilterToggle") as ToggleButton)
                    ?.Content?.ToString() != "Gallery view")
                throw new Exception("Open WPF search bar did not switch to English.");
            if (localizedSortLabel.Text != "Date taken" ||
                localizedSortButton.ToolTip?.ToString() !=
                    "Sort: Date taken, ascending" ||
                !Equals(Choice("Captured").Header, "Date taken") ||
                !Equals(Choice("Modified").Header, "Date modified") ||
                sortDirectionButton.ToolTip?.ToString() != "Ascending")
                throw new Exception("Selected sort field, tooltip or choices did not switch to English.");
            var localeConfig = (string?)typeof(PCasa.App.MainWindow).GetField(
                "_appearanceFile", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(window)
                ?? throw new Exception("Appearance settings path missing.");
            using (var languageSettings = System.Text.Json.JsonDocument.Parse(
                System.IO.File.ReadAllText(localeConfig)))
            {
                if (languageSettings.RootElement.GetProperty("Language")
                    .GetString() != "en")
                    throw new Exception("Selected language was not persisted.");
            }
            germanMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, germanMenu));
            Flush(window);
            if (UiLanguage.Current.Code != "de" || !germanMenu.IsChecked ||
                englishMenu.IsChecked ||
                menu.Items.OfType<MenuItem>().First().Header?.ToString() != "Datei")
                throw new Exception("Returning to German did not refresh the UI.");
            if (localizedSortLabel.Text != "Aufnahmedatum" ||
                localizedSortButton.ToolTip?.ToString() !=
                    "Sortierung: Aufnahmedatum, aufsteigend")
                throw new Exception("Sort selector did not return to German.");
            Choice("Name").RaiseEvent(
                new RoutedEventArgs(MenuItem.ClickEvent, Choice("Name")));
            Flush(window);
            Console.WriteLine(
                "PASS: Central English/German UI live switch, sort selector and appearance persistence.");


            // All bundled textures must be external, discoverable JPGs,
            // selectable by filename and native-sized across DPI settings.
            // In the installer, default textures belong to scope-specific DATA,
            // not the build/output directory. Seed the isolated WPF test profile.
            var testRoot = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (testRoot is not null &&
                   !System.IO.Directory.Exists(System.IO.Path.Combine(
                       testRoot.FullName, "default-textures")))
                testRoot = testRoot.Parent;
            if (testRoot is null)
                throw new Exception("Test fixture default-textures directory missing.");
            var defaultTextures = System.IO.Path.Combine(
                testRoot.FullName,"default-textures");
            System.IO.Directory.CreateDirectory(
                PCasa.App.Services.GalleryTextureLibrary.DirectoryPath);
            foreach (var original in System.IO.Directory.GetFiles(defaultTextures,"*.jpg"))
            {
                var dest = System.IO.Path.Combine(
                    PCasa.App.Services.GalleryTextureLibrary.DirectoryPath,
                    System.IO.Path.GetFileName(original));
                System.IO.File.Copy(original,dest,overwrite:true);
            }
            var textures = PCasa.App.Services.GalleryTextureLibrary.GetFiles();
            if (textures.Count != 12 ||
                textures.Any(p => !p.StartsWith(
                    PCasa.App.Services.GalleryTextureLibrary.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase)) ||
                !textures.Any(p => p.EndsWith("Stein - Nachtblau.jpg",
                    StringComparison.OrdinalIgnoreCase)) ||
                !textures.Any(p => p.EndsWith("seide1.jpg",
                    StringComparison.OrdinalIgnoreCase)) ||
                textures.Any(p => System.IO.Path.GetFileName(p).StartsWith("Leinwand",
                    StringComparison.OrdinalIgnoreCase)) ||
                PCasa.App.Services.GalleryTextureLibrary.FindFile("../wrong.jpg") is not null)
                throw new Exception("External JPG texture directory is missing or unsafe.");
            foreach (var path in textures)
            {
                var bitmap = PCasa.App.Services.GalleryTextureLibrary.LoadBitmap(path);
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    var brush = PCasa.App.Services.GalleryTextureLibrary.CreateBrush(
                        bitmap, new DpiScale(scale, scale));
                    if (brush.TileMode != TileMode.FlipXY ||
                        brush.ViewportUnits != BrushMappingMode.Absolute ||
                        Math.Abs(brush.Viewport.Width * scale - bitmap.PixelWidth) > .001 ||
                        Math.Abs(brush.Viewport.Height * scale - bitmap.PixelHeight) > .001)
                        throw new Exception("A JPG texture has been enlarged by display scaling.");
                }
            }
            var chosen = System.IO.Path.GetFileName(textures[0]);
            ConfigureAppearance(dlg =>
            {
                var tile = Descendants<Button>(dlg).FirstOrDefault(button =>
                    button.Tag is string name &&
                    string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception("Texture filmstrip has no JPG tile.");
                tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tile));
            });
            if (window.FindName("GalleryBackgroundTextureMenu") is not null ||
                window.FindName("GalleryBackgroundCanvasMenu") is not null)
                throw new Exception("Legacy background submenu is still present.");
            var savedPath = typeof(PCasa.App.MainWindow).GetField(
                "_appearanceFile", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(window) as string
                ?? throw new Exception("Appearance file path missing.");
            using (var saved = System.Text.Json.JsonDocument.Parse(
                System.IO.File.ReadAllText(savedPath)))
            {
                if (saved.RootElement.GetProperty("Background").GetString() != "Texture" ||
                    saved.RootElement.GetProperty("TextureFileName").GetString() != chosen)
                    throw new Exception("JPG texture choice did not persist.");
            }
            Console.WriteLine("PASS: Editable JPG textures, native pixels and one persisted menu entry.");
            // GPX interpolation uses UTC, a configurable camera offset and a
            // hard gap cutoff; no catalog or source file is changed.
            var gpxTestRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "pcasa-gpx-undo-tests-" +
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(gpxTestRoot);
            try
            {
                var gpxFile = System.IO.Path.Combine(gpxTestRoot, "track.gpx");
                System.IO.File.WriteAllText(gpxFile,
                    "<gpx><trk><trkseg>" +
                    "<trkpt lat='10' lon='20'><time>2026-11-10T10:00:00Z</time></trkpt>" +
                    "<trkpt lat='10.0002' lon='20.0002'><time>2026-11-10T10:00:10Z</time></trkpt>" +
                    "</trkseg></trk></gpx>");
                var points = PCasa.App.Services.GpxTrackReader.Read(gpxFile);
                if (points.Count != 2) throw new Exception("GPX parser lost time points.");
                var midpoint = PCasa.App.Services.GpxTrackReader.Match(points,
                    new DateTimeOffset(2026,11,10,10,0,5,TimeSpan.Zero),60);
                if (midpoint is not { } p ||
                    Math.Abs(p.Latitude-10.0001)>0.00001 ||
                    Math.Abs(p.Longitude-20.0001)>0.00001 ||
                    PCasa.App.Services.GpxTrackReader.Match(points,
                        new DateTimeOffset(2026,11,10,10,0,5,TimeSpan.Zero),5) is not null)
                    throw new Exception("GPX interpolation or gap limit is wrong.");
                var gpxTestOptions = new PCasa.App.Services.GpxOptions
                {
                    DirectoryPath = gpxTestRoot, CameraTimeZoneId = "UTC",
                    CameraOffsetSeconds = 10, MaximumGapSeconds = 60
                };
                if (PCasa.App.Services.GpxTrackReader.CameraToUtc(
                    new DateTime(2026,11,10,10,0,10),gpxTestOptions) !=
                    new DateTimeOffset(2026,11,10,10,0,0,TimeSpan.Zero))
                    throw new Exception("Camera clock offset is applied in the wrong direction.");
                Console.WriteLine("PASS: Secure GPX reader, UTC alignment, time offset and interpolation gap.");

                var sourcePhoto = System.IO.Path.Combine(gpxTestRoot,"photo.jpg");
                var undoOptions = new PCasa.App.Services.UndoOptions
                {
                    DirectoryPath = System.IO.Path.Combine(gpxTestRoot,"Undo"),
                    MaximumVersionsPerImage = 2,
                    MaximumBytes = 16*1024*1024
                };
                var archive = new PCasa.App.Services.UndoHistoryService(undoOptions);
                foreach (var value in new[] { "first", "second", "third" })
                {
                    System.IO.File.WriteAllText(sourcePhoto,value);
                    archive.CaptureBeforeSave(sourcePhoto,CancellationToken.None);
                }
                var saved = archive.GetVersions(sourcePhoto);
                if (saved.Count != 2 ||
                    System.IO.File.ReadAllText(saved[0].SnapshotPath) != "third" ||
                    System.IO.File.ReadAllText(saved[1].SnapshotPath) != "second")
                    throw new Exception("Central Undo archive did not retain the last two full states.");
                Console.WriteLine("PASS: Central Undo full-file backups, SHA-256 and per-image retention.");
            }
            finally
            {
                System.IO.Directory.Delete(gpxTestRoot,recursive:true);
            }


            // Exercise the real graphical picker: each JPG is represented by
            // an actual clickable swatch. One click selects and closes.
            var picker = new PCasa.App.GalleryTexturePickerWindow(chosen)
            {
                Owner = window
            };
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                var preview = Descendants<Button>(picker)
                    .FirstOrDefault(b => b.Tag is string file &&
                        string.Equals(file, chosen, StringComparison.Ordinal));
                if (preview is null) picker.Close();
                else preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, preview));
            }), DispatcherPriority.ContextIdle);
            if (picker.ShowDialog() != true ||
                picker.SelectedFileName != chosen)
                throw new Exception("JPG swatch did not select its texture in one click.");
            Console.WriteLine("PASS: Real graphical texture picker selects JPG with one click.");

            // Checked WAS/WO/NOT use the same theme-aware palette. In the
            // light theme they must be pale gray with dark labels; in the
            // dark theme they keep the original charcoal/white appearance.
            ChooseAppearance("theme", "Light");
            // App palettes alone are not sufficient: native dialog controls
            // and window chrome must follow the WPF Fluent theme too.
#pragma warning disable WPF0001
            if (Application.Current.ThemeMode != ThemeMode.Light)
                throw new Exception("Light appearance left WPF controls in Dark mode.");
#pragma warning restore WPF0001
            ConfigureAppearance(dialog =>
            {
                var nativeControls = Descendants<Control>(dialog)
                    .Where(control => control is RadioButton or CheckBox or Button).ToArray();
                if (nativeControls.Length < 20 || nativeControls.Any(control =>
                        control.Foreground is not SolidColorBrush foreground ||
                        foreground.Color.R > 100 ||
                        foreground.Color.G > 100 ||
                        foreground.Color.B > 100))
                    throw new Exception("Light appearance dialog has unreadable control text.");
                foreach (var button in nativeControls.OfType<Button>())
                {
                    if (button.Template.FindName("ButtonBorder", button) is not Border ||
                        button.Background is not SolidColorBrush surface ||
                        surface.Color.R < 180 || surface.Color.G < 180)
                        throw new Exception("Light dialog button surface has wrong contrast.");
                }
            }, false);
            var checkedTags = (RadioButton?)window.FindName("SearchTagsMode")
                ?? throw new Exception("Tags filter missing.");
            var checkedAll = (RadioButton?)window.FindName("SearchAllScope")
                ?? throw new Exception("Scope filter missing.");
            var uncheckedName = (RadioButton?)window.FindName("SearchFileNameMode")
                ?? throw new Exception("Name filter missing.");
            var notForColors = (ToggleButton?)window.FindName("InvertFilterToggle")
                ?? throw new Exception("NOT control missing.");
            notForColors.IsChecked = true;
            Flush(window);
            foreach (var selected in new RadioButton[] { checkedTags, checkedAll })
            {
                var border = selected.Template.FindName("ChoiceBorder", selected) as Border;
                if (border?.Background is not SolidColorBrush bg ||
                    bg.Color.R < 195 || bg.Color.G < 195 || bg.Color.B < 195 ||
                    selected.Foreground is not SolidColorBrush fg ||
                    fg.Color.R > 85 || fg.Color.G > 85 || fg.Color.B > 85)
                    throw new Exception("Light checked WAS/WO is not light-gray with dark text.");
            }
            var invertBorder = notForColors.Template.FindName("InvertBorder", notForColors)
                as Border;
            if (invertBorder?.Background is not SolidColorBrush notBg ||
                notBg.Color.R < 195 ||
                notForColors.Foreground is not SolidColorBrush notFg ||
                notFg.Color.R > 85 ||
                uncheckedName.Foreground is not SolidColorBrush inactiveFg ||
                inactiveFg.Color.R > 100)
                throw new Exception("Light NOT or inactive label has wrong contrast.");
            ChooseAppearance("theme", "Dark");
            Flush(window);
#pragma warning disable WPF0001
            if (Application.Current.ThemeMode != ThemeMode.Dark)
                throw new Exception("Dark appearance left WPF controls in Light mode.");
#pragma warning restore WPF0001
            ConfigureAppearance(dialog =>
            {
                var nativeControls = Descendants<Control>(dialog)
                    .Where(control => control is RadioButton or CheckBox or Button).ToArray();
                if (nativeControls.Length < 20 || nativeControls.Any(control =>
                        control.Foreground is not SolidColorBrush foreground ||
                        foreground.Color.R < 180 ||
                        foreground.Color.G < 180 ||
                        foreground.Color.B < 180))
                    throw new Exception("Dark appearance dialog has unreadable control text.");
                foreach (var button in nativeControls.OfType<Button>())
                {
                    if (button.Template.FindName("ButtonBorder", button) is not Border ||
                        button.Background is not SolidColorBrush surface ||
                        surface.Color.R > 110 || surface.Color.G > 110)
                        throw new Exception("Dark dialog button has a pale native background.");
                }
            }, false);
            if (checkedTags.Foreground is not SolidColorBrush darkText ||
                darkText.Color.R < 220 ||
                notForColors.Foreground is not SolidColorBrush darkNotText ||
                darkNotText.Color.R < 220)
                throw new Exception("Dark theme selected labels lost light text.");
            notForColors.IsChecked = false;
            // Filename captions are placed by the ACTUAL image rect,
            // not the row's tallest thumbnail. Portrait and panorama in
            // the same row must therefore have different caption baselines.
            var fit = typeof(BrowseGallerySurface).GetMethod(
                "FitInto", System.Reflection.BindingFlags.Static |
                           System.Reflection.BindingFlags.NonPublic)!;
            var captionTopMethod = typeof(BrowseGallerySurface).GetMethod(
                "CaptionTop", System.Reflection.BindingFlags.Static |
                              System.Reflection.BindingFlags.NonPublic)!;
            var slot = new Rect(0, 0, 200, 180);
            var panorama = (Rect)fit.Invoke(null, new object[] { slot, 800, 200 })!;
            var portrait = (Rect)fit.Invoke(null, new object[] { slot, 200, 800 })!;
            double CaptionTopFor(Rect box) =>
                (double)captionTopMethod.Invoke(null, new object[] { box })!;
            if (Math.Abs(CaptionTopFor(panorama) - panorama.Bottom - 4) > 0.1 ||
                Math.Abs(CaptionTopFor(portrait) - portrait.Bottom - 4) > 0.1 ||
                Math.Abs(CaptionTopFor(panorama) - CaptionTopFor(portrait)) < 30)
                throw new Exception("Filename captions still align on the row baseline.");
            Console.WriteLine("PASS: Thumbnail captions have an image-relative four-pixel gap.");
            Console.WriteLine("PASS: WAS/WO/NOT active colors match both themes and remain readable.");

            // Real menu action must update the one gallery renderer and persist.
            ConfigureAppearance(dlg => dlg.BoldFileNamesCheck.IsChecked = true);
            var gallerySurface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Gallery surface missing.");
            var boldField = typeof(BrowseGallerySurface).GetField(
                "_boldFileNames", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Bold filename rendering field missing.");
            if (!(bool)boldField.GetValue(gallerySurface)!)
                throw new Exception("Bold option did not reach gallery renderer.");
            var appearancePath = typeof(PCasa.App.MainWindow).GetField(
                "_appearanceFile", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)?.GetValue(window) as string
                ?? throw new Exception("Appearance path missing.");
            using (var savedAppearance = System.Text.Json.JsonDocument.Parse(
                System.IO.File.ReadAllText(appearancePath)))
            {
                if (!savedAppearance.RootElement
                    .GetProperty("BoldThumbnailFileNames").GetBoolean())
                    throw new Exception("Bold filename choice did not persist.");
            }
            ConfigureAppearance(dlg => dlg.BoldFileNamesCheck.IsChecked = false);
            if ((bool)boldField.GetValue(gallerySurface)!)
                throw new Exception("Disabling bold filenames did not restore normal weight.");
            Console.WriteLine("PASS: Filename boldness toggles renderer and persists without another settings owner.");

            // Measure WHAT THE USER SEES: gap between actual header TEXT
            // presenters, not between MenuItem outer rectangles. WPF's
            // submenu-star column previously fooled the margin-only check.
            Rect HeaderTextBounds(MenuItem item)
            {
                item.ApplyTemplate();
                var presenter = item.Template.FindName(
                    "MenuHeaderPresenter", item) as ContentPresenter
                    ?? throw new Exception("Top-level menu header presenter missing.");
                return presenter.TransformToAncestor(menu).TransformBounds(
                    new Rect(new Point(), presenter.RenderSize));
            }
            void AssertCaptionGaps(string language)
            {
                var positions = items.Select(HeaderTextBounds).ToArray();
                var gaps = Enumerable.Range(1, positions.Length - 1)
                    .Select(i => positions[i].Left - positions[i - 1].Right).ToArray();
                if (positions[0].Left is < 17 or > 27 ||
                    gaps.Any(gap => gap is < 20 or > 40) ||
                    gaps.Max() - gaps.Min() > 1.5)
                    throw new Exception($"Actual menu caption gaps ({language}) differ: " +
                        string.Join(", ", gaps.Select(gap => gap.ToString("F1"))) +
                        $"; left={positions[0].Left:F1}");
            }
            if (items.Single(x => Equals(x.Header, "Darstellung")).Role != MenuItemRole.TopLevelItem ||
                items.Where(x => !Equals(x.Header, "Darstellung")).Any(x =>
                    x.Role != MenuItemRole.TopLevelHeader))
                throw new Exception("Unexpected root-menu WPF roles; both header and action role must share geometry.");
            foreach (var item in items)
            {
                item.ApplyTemplate();
                var checkColumn = (ColumnDefinition)item.Template.FindName("MenuCheckColumn", item)!;
                var arrowColumn = (ColumnDefinition)item.Template.FindName("MenuArrowColumn", item)!;
                if (checkColumn.ActualWidth > 0.5 || arrowColumn.ActualWidth > 0.5)
                    throw new Exception("Invisible icon/arrow gutter distorts root-menu label spacing: " + item.Header);
            }
            AssertCaptionGaps("de");
            Console.WriteLine("PASS: Actual menu caption gaps, measured from visible text, are equal.");

            // A highlighted submenu row must occupy the entire dropdown
            // width, not just the length of its own caption.
            var optionsForWidth = items.Single(item => Equals(item.Header, "Optionen"));
            optionsForWidth.IsSubmenuOpen = true;
            Flush(window);
            var submenuEntries = optionsForWidth.Items.OfType<MenuItem>().ToArray();
            var menuRowWidths = submenuEntries.Select(item =>
            {
                item.ApplyTemplate();
                return (item.Template.FindName("ItemBackground", item) as Border)?.ActualWidth ?? 0;
            }).ToArray();
            optionsForWidth.IsSubmenuOpen = false;
            Flush(window);
            if (menuRowWidths.Length < 2 ||
                menuRowWidths.Any(width => width < 60) ||
                menuRowWidths.Max() - menuRowWidths.Min() > 2)
                throw new Exception("Submenu highlights do not span the entire dropdown width: " +
                    string.Join(", ", menuRowWidths.Select(width => width.ToString("F1"))));

            // Opening the REAL submenu must install the configured rightward
            // placement callback, not fall back to WPF's alternating default.
            var options = items.Single(item => Equals(item.Header, "Optionen"));
            if (options.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, "Sortieren")))
                throw new Exception("Obsolete sorting submenu remains under Options.");
            var sortButton = window.FindName("GallerySortSelectorButton") as Button
                ?? throw new Exception("Search toolbar sort selector is missing.");
            var sortItems = sortButton.ContextMenu?.Items.OfType<MenuItem>().ToArray()
                ?? throw new Exception("Search toolbar sorting choices are missing.");
            if (sortItems.Count(item => item.IsCheckable) != 6 ||
                !sortItems.Any(item => Equals(item.Header, "Name")) ||
                !sortItems.Any(item => Equals(item.Header, "Aufnahmedatum")))
                throw new Exception("Sort field menu must offer six criteria only.");
            var notToggle = window.FindName("InvertFilterToggle") as ToggleButton
                ?? throw new Exception("Search inversion toggle is missing.");
            var galleryToggle = window.FindName("GalleryViewFilterToggle") as ToggleButton
                ?? throw new Exception("Galerieansicht search toggle is missing.");
            if (!Equals(galleryToggle.Content, "Galerieansicht"))
                throw new Exception("Gallery search toggle has incorrect text.");
            if (!Equals(notToggle.Content, "NOT"))
                throw new Exception("Search inversion toggle is not labelled NOT.");
            foreach (var buttonName in new[]
            {
                "FolderExpandAllButton", "FolderCollapseAllButton"
            })
            {
                var treeButton = (Button)window.FindName(buttonName)!;
                var textStrokePaths = Descendants<System.Windows.Shapes.Path>(treeButton)
                    .Where(path => path.Stroke is SolidColorBrush brush &&
                        brush.Color == ((SolidColorBrush)app.Resources["PcasaTextBrush"]).Color);
                if (!textStrokePaths.Any())
                    throw new Exception("Light folder expand icons contain invisible white strokes: " + buttonName);
            }
            Console.WriteLine("PASS: Folder expand/collapse symbols use theme-aware strokes.");
            Console.WriteLine("PASS: Sort selector lives only in the search toolbar beside NOT.");

            // Main menu and search occupy two PERMANENT rows. The user's
            // resized-window screenshot exposed DockPanel starve/clipping.
            // Keep every original command and the search control order.
            var searchField = window.FindName("TagSearchBox") as TextBox
                ?? throw new Exception("Search field not found.");
            var what = window.FindName("SearchTagsMode") as RadioButton
                ?? throw new Exception("WAS group not found.");
            var where = window.FindName("SearchAllScope") as RadioButton
                ?? throw new Exception("WO group not found.");
            var whatLabel = window.FindName("SearchWhatCaption") as TextBlock
                ?? throw new Exception("WAS caption missing.");
            var whereLabel = window.FindName("SearchWhereCaption") as TextBlock
                ?? throw new Exception("WO caption missing.");
            var sortFrame = window.FindName("GallerySortGroupFrame") as Border
                ?? throw new Exception("Sort frame missing.");
            var searchFrame = window.FindName("SearchGroupFrame") as Border
                ?? throw new Exception("Search frame missing.");
            var direction = window.FindName("GallerySortDirectionButton") as Button
                ?? throw new Exception("Sort arrow missing.");
            var attributeFrame = window.FindName("GalleryAttributeFilterFrame") as Border
                ?? throw new Exception("Compact property filter frame is missing.");
            var invertSelectionToggle = window.FindName("GalleryInvertSelectionToggle") as ToggleButton
                ?? throw new Exception("Select-invert toggle must be left of the attribute filter.");
            var invertFrame = window.FindName("GalleryInvertSelectionFrame") as Border
                ?? throw new Exception("Invert selection needs the shared toolbar group frame.");
            if (invertSelectionToggle.IsChecked == true ||
                !ReferenceEquals(invertSelectionToggle.Parent, invertFrame) ||
                invertFrame.Parent is not StackPanel toolbarPanel ||
                toolbarPanel.Children.IndexOf(invertFrame) + 1 !=
                    toolbarPanel.Children.IndexOf(attributeFrame) ||
                invertSelectionToggle.ToolTip?.ToString() !=
                    "Auswahl invertieren (aktueller Ordner)" ||
                Math.Abs(invertFrame.ActualHeight - attributeFrame.ActualHeight) > 0.5 ||
                !Equals(invertFrame.BorderBrush, attributeFrame.BorderBrush))
                throw new Exception("Selection inversion toggle lacks the same frame as Filter/Sort/Search.");
            var attributeLabel = window.FindName("GalleryAttributeFilterLabel") as TextBlock
                ?? throw new Exception("Property filter must display its active state.");
            var attributeInvert = window.FindName("GalleryAttributeFilterInvertButton") as Button
                ?? throw new Exception("Click-to-invert funnel is missing.");
            var attributeClear = window.FindName("GalleryAttributeFilterClearButton") as Button
                ?? throw new Exception("Separate red circled clear button is missing.");
            var attributeSelector = window.FindName("GalleryAttributeFilterSelectorButton") as Button
                ?? throw new Exception("Property filter selection is missing.");
            if (attributeSelector.ContextMenu?.Items.Count != 9 ||
                 !new[] { "None", "VideoPhotos", "Video", "Gps", "Tags", "Raw", "ImportError", "Date" }
                     .All(tag => attributeSelector.ContextMenu.Items.OfType<MenuItem>()
                         .Any(item => item.Tag is string actual && actual == tag)) ||
                attributeInvert.IsEnabled || attributeClear.IsEnabled ||
                attributeLabel.Text != "Filter")
                throw new Exception("Property filter initial state and eight media/property choices (including date and import errors) are incorrect.");
            var navigationBar = window.FindName("GalleryNavigationBar") as Border
                ?? throw new Exception("Navigation bar must be outside the scrollable gallery.");
            var navigationNames = new[] { "FolderStart", "FolderEnd",
                "PreviousFolder", "NextFolder", "GalleryStart", "GalleryEnd" };
            if (navigationBar.Height > 34 || Grid.GetRow(navigationBar) != 0 ||
                navigationNames.Any(action =>
                    window.FindName("GalleryNavigate" + action + "Button") is not Button))
                throw new Exception("Compact gallery navigation bar is missing one of six commands.");

            Rect Bounds(FrameworkElement element) =>
                element.TransformToAncestor(window).TransformBounds(
                    new Rect(new Point(), element.RenderSize));

            if (Bounds(attributeFrame).Right > Bounds(sortFrame).Left + 1 ||
                Bounds(sortFrame).Right > Bounds(searchFrame).Left + 1 ||
                attributeFrame.ActualHeight != sortFrame.ActualHeight)
                throw new Exception("Property filter must be compact and directly before the unchanged sort/search group.");
            if (window.FindName("GalleryRememberPositionToggle") is not ToggleButton remembered ||
                window.FindName("GalleryReturnToRememberedButton") is not Button returnToRemembered ||
                window.FindName("GalleryReturnToDepartureButton") is not Button returnToDeparture ||
                remembered.IsChecked == true || returnToRemembered.IsEnabled ||
                returnToDeparture.IsEnabled)
                throw new Exception("Three position buttons must start with inactive bookmark/return states.");
            if (what.Template.FindName("SelectedTick", what) is not null ||
                where.Template.FindName("SelectedTick", where) is not null ||
                notToggle.Template.FindName("InvertCheck", notToggle) is not null)
                throw new Exception("Active WAS/WO/NOT are blue-framed and must not show redundant ticks.");
            foreach (var action in new[] { "PreviousFolder", "NextFolder" })
            {
                var button = (Button)window.FindName(
                    "GalleryNavigate" + action + "Button")!;
                var icon = Descendants<System.Windows.Shapes.Path>(button).FirstOrDefault()
                    ?? throw new Exception("Missing folder navigation icon.");
                if (icon.Data.Bounds.Height < 18 ||
                    icon.Data.Bounds.Height > 22)
                    throw new Exception("Folder navigation icons must match the other navigation icon heights.");
            }
            if (app.Resources["PcasaButtonDepthBrush"] is not LinearGradientBrush ||
                app.Resources["PcasaButtonShadow"] is not System.Windows.Media.Effects.DropShadowEffect)
                throw new Exception("Global 3D button surfaces are missing.");
            var explorerButton = (Button)window.FindName("ShowAllFilesystemFoldersButton")!;
            var explorerField = typeof(PCasa.App.MainWindow).GetField(
                "_allFolderProjectionRootPath",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var explorerRefresh = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateAllFoldersButtonState",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var oldExplorerRoot = explorerField.GetValue(window);
            try
            {
                explorerField.SetValue(window, @"C:\synthetic");
                explorerRefresh.Invoke(window, null);
                Flush(window);
                var explorerOutline = explorerButton.Template.FindName("Outline", explorerButton) as Border;
                if (!Equals(explorerButton.Tag, "ActiveGold") ||
                    explorerOutline?.BorderBrush is not SolidColorBrush activeBrush ||
                    activeBrush.Color != ((SolidColorBrush)app.Resources["PcasaAccentBrush"]).Color)
                    throw new Exception("Active Explorer view must have the shared blue outline.");
            }
            finally
            {
                explorerField.SetValue(window, oldExplorerRoot);
                explorerRefresh.Invoke(window, null);
                Flush(window);
            }
            Console.WriteLine("PASS: Shared 3D surfaces and blue active-state frame, including Explorer view.");

            Console.WriteLine("PASS: Compact property filter, six unchanged navigation icons and two bookmark buttons.");

            if (Descendants<TextBlock>(sortButton).Any(block =>
                    block.Text == "⌄" || block.Text == "▼"))
                throw new Exception("Redundant triangle remains inside Sort selector.");
            if (window.FindName("GallerySortDirectionToggleIcon")
                    is not System.Windows.Shapes.Path ||
                window.FindName("GallerySortDirectionIcon") is not null)
                throw new Exception("Direction arrow must be in its own toggle, not the sort field.");

            if (menu.Parent is not Border menuBorder ||
                menuBorder.Parent is not Grid twoRowHeader ||
                Grid.GetRow(twoRowHeader) != 0 ||
                Grid.GetRow(menuBorder) != 0)
                throw new Exception("The main menu is not in its own top header row.");
            var searchScroll = window.FindName("SearchToolbarScrollViewer") as ScrollViewer
                ?? throw new Exception("Search row needs a separate horizontal scroller.");
            // Alpha.44: the slideshow Play button stays visible to the LEFT
            // while only the unchanged filter/sort/search controls scroll.
            // Both sit together in the permanent second menu row.
            if (searchScroll.Parent is not Grid searchShell ||
                searchShell.Parent is not Border searchBorder ||
                !ReferenceEquals(searchBorder.Parent, twoRowHeader) ||
                Grid.GetRow(searchBorder) != 1 ||
                searchShell.ColumnDefinitions.Count != 2 ||
                !ReferenceEquals(slideshowPlay.Parent, searchShell) ||
                Grid.GetColumn(slideshowPlay) != 0 ||
                Grid.GetColumn(searchScroll) != 1 ||
                searchScroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto ||
                searchScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                searchScroll.Content is not StackPanel searchRow ||
                searchRow.Orientation != Orientation.Horizontal)
                throw new Exception(
                    "Permanent search row must contain fixed left slideshow Play and right scrolling filters.");
            if (sortFrame.Child is not StackPanel sortGroup ||
                searchFrame.Child is not StackPanel searchGroup ||
                !ReferenceEquals(sortButton.Parent, sortGroup) ||
                !ReferenceEquals(direction.Parent, sortGroup) ||
                !ReferenceEquals(whatLabel.Parent, searchGroup) ||
                !ReferenceEquals(whereLabel.Parent, searchGroup) ||
                !ReferenceEquals(galleryToggle.Parent, searchGroup) ||
                !ReferenceEquals(notToggle.Parent, searchGroup) ||
                !ReferenceEquals(searchField.Parent, searchGroup.Children[^1]))
                throw new Exception("Sort and search must retain their original separate group frames.");
            if (window.FindName("GalleryDateFilterFrame") is not null ||
                window.FindName("GalleryAttributeDateMenu") is not MenuItem { Tag: "Date" })
                throw new Exception("Date filter must be inside the existing filter dropdown.");
            if (!searchRow.Children.Cast<UIElement>().SequenceEqual(
                    new UIElement[] { invertFrame, attributeFrame, sortFrame, searchFrame }))
                throw new Exception("Date filter changed the original toolbar width/order.");
            if (Math.Abs(sortFrame.ActualHeight - searchFrame.ActualHeight) > .5 ||
                !Equals(sortFrame.BorderBrush, searchFrame.BorderBrush))
                throw new Exception("Group frames differ in height or border brightness.");
            var toolbarButtons = new FrameworkElement[]
            {
                sortButton, direction, what, (FrameworkElement)window.FindName("SearchFileNameMode")!,
                where, (FrameworkElement)window.FindName("SearchTreeScope")!,
                (FrameworkElement)window.FindName("SearchCurrentScope")!,
                galleryToggle, notToggle, searchField
            };
            foreach (var control in toolbarButtons)
            {
                if (Math.Abs(control.ActualHeight - 27) > .6 ||
                    Math.Abs(((control as Control)?.FontSize ?? 12) - 12) > .1)
                    throw new Exception("All toolbar controls must be 27px high at 12pt: " + control.Name);
            }
            var searchMagnifier = window.FindName("SearchGroupLeadingMagnifier") as Border
                ?? throw new Exception("Search group needs an added leading magnifier before WAS.");
            var searchGroupRow = searchMagnifier.Parent as StackPanel
                ?? throw new Exception("Search pictogram must sit inside the existing search group.");
            if (searchMagnifier.Child is not Grid magnifierContent ||
                !Descendants<ContentControl>(magnifierContent).Any() ||
                searchGroupRow.Children.IndexOf(searchMagnifier) + 1 !=
                    searchGroupRow.Children.IndexOf(whatLabel) ||
                searchField.Parent is not Grid searchInputGrid ||
                !Descendants<ContentControl>(searchInputGrid).Any() ||
                Math.Abs(searchInputGrid.ActualWidth - 180) > 1 ||
                Math.Abs(searchField.Padding.Left - 29) > .1)
                throw new Exception("Search must have both original in-field and added leading magnifier.");
            if (Math.Abs(sortButton.Width - 145) > .1 ||
                items.Any(item => Math.Abs(item.Margin.Right - 12) > .01) ||
                Math.Abs(whatLabel.FontSize - 12) > .1 ||
                Math.Abs(whereLabel.FontSize - 12) > .1)
                throw new Exception("Search field geometry, sort width or font is incorrect.");
            Console.WriteLine("PASS: Compact 27px controls; 12pt fonts; identical sort/search frames.");
            notToggle.IsChecked = true;
            Flush(window);
            if (searchField.TextDecorations.Count != 1 ||
                searchField.TextDecorations[0].Location != TextDecorationLocation.Strikethrough)
                throw new Exception("NOT lost its strikethrough text behavior.");
            notToggle.IsChecked = false;
            Flush(window);
            if (searchField.TextDecorations.Count != 0)
                throw new Exception("Disabling NOT did not restore normal search text.");

            var narrowViewportTested = false;
            foreach (var requestedWidth in new[] { 1000.0, 1100.0, 1500.0 })
            {
                window.Width = requestedWidth;
                Flush(window);
                var menuBounds = Bounds(menu);
                var sortBounds = Bounds(sortButton);
                if (menuBounds.Right > window.ActualWidth + 1 ||
                    menuBounds.Left < -1 ||
                    sortBounds.Top < menuBounds.Bottom - 1)
                    throw new Exception(
                        $"Main menu clipped or overlapped the search row at {window.ActualWidth:F0}px.");
                if (menu.ActualWidth < 250)
                    throw new Exception("Resizing squeezed main menu captions away.");
                if (searchScroll.ScrollableWidth < 1)
                {
                    var trailingGap = Bounds(searchScroll).Right -
                        Bounds((FrameworkElement)searchField.Parent).Right;
                    // The textbox has a 10px trailing margin plus the
                    // ScrollViewer's padding. It should be at the RIGHT,
                    // not at the left edge leaving hundreds of blank pixels.
                    if (trailingGap is < 0 or > 45)
                        throw new Exception(
                            $"Search row is not right-aligned: gap={trailingGap:F1}px");
                }

                var row = new FrameworkElement[]
                {
                    sortFrame, searchFrame
                };
                for (var i = 1; i < row.Length; i++)
                    if (Math.Abs(Bounds(row[i]).Top + Bounds(row[i]).Height / 2 -
                                 (Bounds(row[0]).Top + Bounds(row[0]).Height / 2)) > 12)
                        throw new Exception("Search controls wrapped into another row.");

                if (window.ActualWidth <= 1110 && searchScroll.ScrollableWidth > 0)
                {
                    narrowViewportTested = true;
                    searchScroll.ScrollToRightEnd();
                    Flush(window);
                    var viewportRight = Bounds(searchScroll).Right;
                    if (Bounds((FrameworkElement)searchField.Parent).Right >
                        viewportRight + 2)
                        throw new Exception("Search textbox cannot be scrolled into view.");
                    if (Bounds(menu).Right > window.ActualWidth + 1)
                        throw new Exception("Scrolling the search row displaced the main menu.");
                    searchScroll.ScrollToLeftEnd();
                    Flush(window);
                }
                else if (window.ActualWidth >= 1450)
                {
                    if (searchScroll.ScrollableWidth > 1)
                        throw new Exception("Wide search row scrolls unnecessarily.");
                }
            }
            if (!narrowViewportTested)
            {
                // At 1000px the two-row toolbar may ALREADY fit completely.
                // WPF should then avoid a useless scroll bar. Force a
                // narrower search viewport to verify actual overflow logic,
                // without shrinking the main menu or changing production UI.
                searchScroll.MaxWidth = 590;
                Flush(window);
                if (searchScroll.ScrollableWidth <= 0)
                    throw new Exception("Forced narrow search row lacks scrolling.");
                searchScroll.ScrollToRightEnd();
                Flush(window);
                if (Bounds((FrameworkElement)searchField.Parent).Right >
                    Bounds(searchScroll).Right + 2)
                    throw new Exception("Overflowing search field cannot be reached.");
                if (Bounds(menu).Right > window.ActualWidth + 1)
                    throw new Exception("A narrow search row displaced the main menu.");
                searchScroll.ScrollToLeftEnd();
                searchScroll.ClearValue(FrameworkElement.MaxWidthProperty);
                Flush(window);
            }
            window.Width = 1500;
            Flush(window);
            Console.WriteLine(
                "PASS: Permanent two-row menu and search, correct controls/order at all sizes.");
            Console.WriteLine(
                "PASS: Narrow search row scrolls while the complete main menu remains visible.");
            Console.WriteLine(
                "PASS: WAS/WO borders and NOT strikethrough remain unchanged.");

            options.IsSubmenuOpen = true;
            Flush(window);
            var popup = options.Template.FindName("PART_Popup", options) as Popup
                ?? throw new Exception("Options submenu popup is missing.");
            if (popup.CustomPopupPlacementCallback is null)
                throw new Exception("Submenu placement callback was not installed.");
            options.IsSubmenuOpen = false;
            Flush(window);
            Console.WriteLine("PASS: Options submenu uses custom placement.");

            // Theme radio selection runs through the one dialog and the
            // existing central brush-update code path.
            foreach (var name in new[] { "Light", "Dark", "System" })
            {
                ChooseAppearance("theme", name);
                Flush(window);
                var foreground = (SolidColorBrush)app.Resources["PcasaTextBrush"];
                var background = (SolidColorBrush)app.Resources["PcasaPanelBrush"];
                var foregroundLum = foreground.Color.R + foreground.Color.G +
                    foreground.Color.B;
                var backgroundLum = background.Color.R + background.Color.G +
                    background.Color.B;
                if (Math.Abs(foregroundLum - backgroundLum) < 300)
                    throw new Exception("Theme foreground and panel have low contrast: " + name);
            }
            Console.WriteLine("PASS: Light, dark and system appearance dialog/theme resources.");

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

            // A real packed bitmap must survive reordering without a second
            // decode, but another byte span for the same photo must not.
            {
                var cacheRoot = Path.Combine(PcasaDataPaths.SharedDataRoot, "browse-cache");
                Directory.CreateDirectory(cacheRoot);
                var packName = "page-reuse-" + Guid.NewGuid().ToString("N") + ".pack";
                var packPath = Path.Combine(cacheRoot, packName);
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                        1, 1, 96, 96, PixelFormats.Bgra32, null,
                        new byte[] { 10, 20, 30, 255 }, 4)));
                    using var buffer = new MemoryStream();
                    encoder.Save(buffer);
                    var data = buffer.ToArray();
                    File.WriteAllBytes(packPath, data.Concat(data).ToArray());
                    using var reused = new BrowseThumbnailPageCache();
                    void WarmAndWait()
                    {
                        var warm = reused.WarmRangeAsync(0, 1);
                        // The STA test installs a DispatcherSynchronizationContext.
                        // Pump it while waiting, with the standard finite timeout.
                        Until(window, () => warm.IsCompleted, "Thumbnail page warm timed out.");
                        warm.GetAwaiter().GetResult();
                    }
                    var firstEntry = new BrowseThumbnailIndexEntry(100, 1, 96,
                        packName, 0, data.Length, 1, 1);
                    var secondEntry = firstEntry with { ImageId = 200 };
                    reused.SetIndex(new[] { firstEntry, secondEntry });
                    WarmAndWait();
                    if (!reused.TryGet(0, out var initialBitmap) || initialBitmap is null)
                        throw new Exception("Packed bitmap test fixture did not load.");
                    reused.SetIndex(new[] { secondEntry, firstEntry });
                    WarmAndWait();
                    if (!reused.TryGet(1, out var retainedBitmap) ||
                        !ReferenceEquals(initialBitmap, retainedBitmap))
                        throw new Exception("Unchanged packed span was decoded again after reordering.");
                    reused.SetIndex(new[] { secondEntry, firstEntry with { ByteOffset = data.Length } });
                    WarmAndWait();
                    if (!reused.TryGet(1, out var renewedBitmap) || renewedBitmap is null ||
                        ReferenceEquals(initialBitmap, renewedBitmap))
                        throw new Exception("Changed packed span reused stale pixels.");
                }
                finally { File.Delete(packPath); }
            }

            // Exercise the actual ScrollViewer and responsive gallery together.
            // The synthetic images deliberately do not refer to real source
            // files, so this test cannot modify a user's picture collection.
            using var pages = new BrowseThumbnailPageCache();
            var synthetic = Enumerable.Range(0, 480)
                .Select(i => new BrowseThumbnailIndexEntry(1000 + i,
                    i < 240 ? 100 : 200, 192, "no-image.pack", 0, 1, 180, 120))
                .ToArray();
            // Test GPS/tag/RAW predicates directly against the already
            // materialized browse-index flags: no NAS or SQLite rescanning.
            var matcher = typeof(PCasa.App.MainWindow).GetMethod(
                "AttributeFilterMatches",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)!;
            var propertyPhoto = synthetic[0] with {
                HasGps = true, HasTags = false, IsRaw = true
            };
            bool Matching(string kind, bool inverse) =>
                (bool)matcher.Invoke(null, new object[] { propertyPhoto, kind, inverse })!;
            if (!Matching("Gps", false) || Matching("Gps", true) ||
                Matching("Tags", false) || !Matching("Tags", true) ||
                !Matching("Raw", false) || Matching("Raw", true) ||
                !Matching("None", false))
                throw new Exception("GPS, Tags and RAW filter predicates/inversion are incorrect.");
            Console.WriteLine("PASS: Photo property filtering and inversion use indexed GPS/tag/RAW flags.");
            var videoEntry = synthetic[1] with { FileName = "scene.mp4" };
            var photoEntry = synthetic[2] with { FileName = "scene.jpg" };
            bool IsMatch(BrowseThumbnailIndexEntry image, string kind, bool inverted) =>
                (bool)matcher.Invoke(null, new object[] { image, kind, inverted })!;
            if (!IsMatch(videoEntry, "Video", false) ||
                IsMatch(videoEntry, "Video", true) ||
                IsMatch(photoEntry, "Video", false) ||
                !IsMatch(photoEntry, "Video", true) ||
                !IsMatch(videoEntry, "None", false) ||
                !IsMatch(photoEntry, "None", false))
                throw new Exception("All media/photos/videos filtering produced incorrect matches.");
            Console.WriteLine("PASS: All media, photos and video share the existing gallery property filter.");
            var brokenEntry = photoEntry with { HasImportError = true };
            if (!IsMatch(brokenEntry, "ImportError", false) ||
                IsMatch(brokenEntry, "ImportError", true) ||
                IsMatch(photoEntry, "ImportError", false) ||
                !IsMatch(photoEntry, "ImportError", true))
                throw new Exception("Import error filter did not use the authoritative indexed flag.");
            Console.WriteLine("PASS: Import errors and their inverse use the same gallery property projection.");

            // A property toggle must preserve a matching current image and
            // otherwise choose the nearest following/preceding match.
            var nearest = typeof(PCasa.App.MainWindow).GetMethod(
                "NearestPropertyFilterImage",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)!;
            var originalPhotos = new[]
            {
                synthetic[0] with { HasGps = true },
                synthetic[1] with { HasGps = false },
                synthetic[2] with { HasGps = false },
                synthetic[3] with { HasGps = true },
                synthetic[4] with { HasGps = false }
            };
            long? Nearest(long id, params int[] visibleIndexes) =>
                (long?)nearest.Invoke(null, new object?[] {
                    originalPhotos, visibleIndexes.Select(i => originalPhotos[i]).ToArray(),
                    (long?)id, (long?)100L });
            if (Nearest(originalPhotos[3].ImageId, 0, 3) != originalPhotos[3].ImageId ||
                Nearest(originalPhotos[2].ImageId, 0, 3) != originalPhotos[3].ImageId ||
                Nearest(originalPhotos[4].ImageId, 0, 3) != originalPhotos[3].ImageId ||
                Nearest(originalPhotos[2].ImageId, 0) != originalPhotos[0].ImageId)
                throw new Exception("Property filter must preserve existing image or choose nearest.");
            Console.WriteLine("PASS: Property-filter switching chooses nearest matching image instead of gallery start.");

            var groups = new[] {
                new PhotoFolderSummary(100, 1, @"C:\\synthetic\\first",
                    @"C:\\synthetic\\first", "First", 240),
                new PhotoFolderSummary(200, 1, @"C:\\synthetic\\second",
                    @"C:\\synthetic\\second", "Second", 240)
            };
            // Viewer/slideshow image order comes from the already displayed
            // gallery index, not a second traversal of sibling directories.
            // Search results are represented by exactly the input projection.
            var visibleOrder = new[] { synthetic[0], synthetic[240],
                synthetic[1], synthetic[241] };
            var allVisible = PCasa.App.Services.PhotoSequence.Create(
                visibleOrder, new HashSet<long>(), synthetic[0].ImageId,
                PhotoSequenceScope.Gallery);
            if (allVisible.Count != 4 ||
                allVisible.At(1).ImageId != synthetic[240].ImageId ||
                allVisible.Move(3, +1) != 0 ||
                allVisible.Move(0, -1) != 3)
                throw new Exception("Gallery sequence does not preserve visible order and wrapping.");

            var folderOnly = PCasa.App.Services.PhotoSequence.Create(
                visibleOrder, new HashSet<long>(), synthetic[0].ImageId,
                PhotoSequenceScope.CurrentFolder);
            if (folderOnly.Count != 2 ||
                folderOnly.At(0).ImageId != synthetic[0].ImageId ||
                folderOnly.At(1).ImageId != synthetic[1].ImageId ||
                folderOnly.Move(1, +1) != 0 ||
                folderOnly.Move(0, -1) != 1)
                throw new Exception("Current-folder sequence includes siblings or fails wrapping.");

            var hiddenSiblings = PCasa.App.Services.PhotoSequence.Create(
                visibleOrder, new HashSet<long> { 200 }, synthetic[0].ImageId,
                PhotoSequenceScope.Gallery);
            if (hiddenSiblings.Count != 2 ||
                hiddenSiblings.At(1).ImageId != synthetic[1].ImageId ||
                PCasa.App.Services.PhotoSequence.Create(
                    visibleOrder, new HashSet<long> { 100 }, synthetic[0].ImageId,
                    PhotoSequenceScope.Gallery).Count != 0)
                throw new Exception("Gallery sequence ignored collapsed-folder visibility.");

            var filteredResults = new[] { synthetic[240], synthetic[241] };
            var filteredSequence = PCasa.App.Services.PhotoSequence.Create(
                filteredResults, new HashSet<long>(), synthetic[241].ImageId,
                PhotoSequenceScope.Gallery);
            if (filteredSequence.Count != 2 ||
                filteredSequence.IndexOfImage(synthetic[241].ImageId) != 1 ||
                filteredSequence.IndexOfImage(synthetic[1].ImageId) >= 0)
                throw new Exception("Filtered gallery sequence added images not shown.");
            Console.WriteLine("PASS: Viewer sequences use gallery order, search, collapsed folders and cyclic limits.");
            // The slideshow reuses the very same filtered PhotoSequence,
            // skipping movies without creating a second index or filesystem
            // traversal. Arrows must work without waiting for its timer.
            // Real WPF: verify selected captions (not just ComboBoxItem
            // data) remain legible in the selected dark or light theme.
            var slideshowDialog = new SlideshowSettingsWindow(new SlideshowSettings())
            {
                Owner = window
            };
            try
            {
                slideshowDialog.Show();
                Flush(slideshowDialog);
                var slideshowChoices = Descendants<ComboBox>(slideshowDialog).ToArray();
                if (slideshowChoices.Length != 3)
                    throw new Exception("Slideshow needs three accessible dropdowns.");
                foreach (var choice in slideshowChoices)
                {
                    choice.ApplyTemplate();
                    var selectedCaption = choice.Template.FindName(
                        "SelectedCaption", choice) as ContentPresenter;
                    var selectedText = selectedCaption is null ? null :
                        Descendants<TextBlock>(selectedCaption).FirstOrDefault();
                    var captionText = (choice.SelectedItem as ComboBoxItem)?.Content as string;
                    if (selectedText is null || string.IsNullOrWhiteSpace(selectedText.Text) ||
                        selectedText.Text != captionText)
                        throw new Exception(
                            "Slideshow selected choice is visually empty in dark theme.");
                    if (selectedText.Foreground is SolidColorBrush ink &&
                        choice.Background is SolidColorBrush paper &&
                        Math.Abs(ink.Color.R - paper.Color.R) +
                        Math.Abs(ink.Color.G - paper.Color.G) +
                        Math.Abs(ink.Color.B - paper.Color.B) < 240)
                        throw new Exception(
                            "Slideshow choice foreground and background are too similar.");
                    choice.IsDropDownOpen = true;
                    Flush(slideshowDialog);
                    var slideshowChoicePopup = choice.Template.FindName(
                        "PART_Popup", choice) as Popup;
                    if (slideshowChoicePopup?.IsOpen != true || choice.Items.OfType<ComboBoxItem>()
                        .Any(item => string.IsNullOrWhiteSpace(item.Content as string) ||
                            !Equals(item.Foreground, selectedText.Foreground)))
                        throw new Exception(
                            "Slideshow dropdown is empty, inaccessible or illegible.");
                    choice.IsDropDownOpen = false;
                    Flush(slideshowDialog);
                }
                // Also exercise the shared template with a data-bound dropdown,
                // as used for the GPX time zones (not ComboBoxItem objects).
                var boundChoice = new ComboBox
                {
                    DisplayMemberPath = "DisplayName", SelectedValuePath = "Id",
                    ItemsSource = TimeZoneInfo.GetSystemTimeZones(), SelectedIndex = 0,
                    MaxDropDownHeight = 240
                };
                var boundWindow = new Window { Owner = window, Content = boundChoice,
                    Width = 550, Height = 100, ShowInTaskbar = false };
                try
                {
                    boundWindow.Show();
                    Flush(boundWindow);
                    boundChoice.ApplyTemplate();
                    var caption = boundChoice.Template.FindName("SelectedCaption", boundChoice)
                        as ContentPresenter;
                    var visible = caption is null ? null : Descendants<TextBlock>(caption)
                        .FirstOrDefault();
                    if (visible?.Text != ((TimeZoneInfo)boundChoice.SelectedItem).DisplayName)
                        throw new Exception("Shared data-bound time zone selection is not rendered.");
                    boundChoice.IsDropDownOpen = true;
                    Flush(boundWindow);
                    var timeZonePopup = boundChoice.Template.FindName("PART_Popup", boundChoice) as Popup;
                    if (timeZonePopup?.IsOpen != true || timeZonePopup.Child is not Border frame ||
                        frame.Background is not SolidColorBrush paper ||
                        boundChoice.Foreground is not SolidColorBrush ink ||
                        Math.Abs(ink.Color.R - paper.Color.R) +
                        Math.Abs(ink.Color.G - paper.Color.G) +
                        Math.Abs(ink.Color.B - paper.Color.B) < 240 ||
                        boundChoice.ItemContainerGenerator.ContainerFromIndex(0) is not ComboBoxItem item ||
                        Descendants<TextBlock>(item).FirstOrDefault()?.Text !=
                            ((TimeZoneInfo)boundChoice.Items[0]).DisplayName)
                        throw new Exception("Shared data-bound time zone popup is empty or illegible.");
                    boundChoice.IsDropDownOpen = false;
                }
                finally { boundWindow.Close(); }
                // Alpha.60: both Viewmaster choices are separate, selectable
                // and persist under independent transition IDs.
                var transitions = slideshowChoices.Single(choice =>
                    choice.Items.OfType<ComboBoxItem>().Any(item =>
                        Equals(item.Tag, "viewmaster-a")));
                var viewmasterNames = transitions.Items.OfType<ComboBoxItem>()
                    .Where(item => item.Tag is "viewmaster-a" or "viewmaster-b")
                    .ToArray();
                if (viewmasterNames.Length != 2 ||
                    !Equals(viewmasterNames[0].Tag, "viewmaster-a") ||
                    !Equals(viewmasterNames[1].Tag, "viewmaster-b") ||
                    !Equals(viewmasterNames[0].Content, "Viewmaster A") ||
                    !Equals(viewmasterNames[1].Content, "Viewmaster B") ||
                    new SlideshowSettings { Transition = "viewmaster-a" }
                        .Normalize().Transition != "viewmaster-a" ||
                    new SlideshowSettings { Transition = "viewmaster-b" }
                        .Normalize().Transition != "viewmaster-b")
                    throw new Exception(
                        "Viewmaster A/B are missing, renamed or not persisted as separate effects.");
                Console.WriteLine(
                    "PASS: Alpha.60 Viewmaster A and B choices keep exact names and distinct IDs.");
                var numericSliders = Descendants<Slider>(slideshowDialog).ToArray();
                if (numericSliders.Length != 7 ||
                    numericSliders.Any(slider =>
                        Math.Abs(slider.Width - numericSliders[0].Width) > .01 ||
                        !slider.IsSnapToTickEnabled) ||
                    numericSliders.Any(slider => slider.Parent is not StackPanel row ||
                        row.Children.OfType<UIElement>().LastOrDefault() is not TextBlock))
                    throw new Exception(
                        "Slideshow numeric settings need seven equally wide stepped sliders and readouts.");
                numericSliders[0].Value = 90;
                Flush(slideshowDialog);
                if (numericSliders[0].Parent is not StackPanel sliderRow ||
                    sliderRow.Children.OfType<UIElement>().LastOrDefault() is not TextBlock liveNumber ||
                    !liveNumber.Text.Contains("90", StringComparison.Ordinal))
                    throw new Exception("Slideshow slider readout did not update.");
                var slideshowBackgroundPicker = (Microsoft.Win32.OpenFileDialog)
                    typeof(SlideshowSettingsWindow).GetMethod(
                        "CreateBackgroundPicker",
                        System.Reflection.BindingFlags.Static |
                        System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(null, null)!;
                if (!string.Equals(slideshowBackgroundPicker.InitialDirectory,
                    GalleryTextureLibrary.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "Slideshow background picker did not open at texture directory.");
            }
            finally { slideshowDialog.Close(); }
            Console.WriteLine(
                "PASS: Slideshow dropdowns render real text and use the gallery texture directory.");

            var slideItems = new[]
            {
                synthetic[0] with { FileName = "first.jpg" },
                synthetic[1] with { FileName = "movie.mp4" },
                synthetic[2] with { FileName = "third.jpg" }
            };
            var slideshowSequence = PhotoSequence.Create(slideItems,
                new HashSet<long>(), slideItems[0].ImageId, PhotoSequenceScope.Gallery);
            if (SlideshowWindow.NextStillIndex(slideshowSequence, 0, +1) != 2 ||
                SlideshowWindow.NextStillIndex(slideshowSequence, 2, -1) != 0 ||
                SlideshowWindow.NextStillIndex(slideshowSequence, 2, +1) != 0)
                throw new Exception("Slideshow movie skipping/wrapping disagrees with gallery sequence.");
            var previewPixels = new byte[64 * 48 * 4];
            for (var j = 3; j < previewPixels.Length; j += 4)
                previewPixels[j] = 255;
            var slideshowPreview = BitmapSource.Create(64, 48, 96, 96,
                PixelFormats.Bgra32, null, previewPixels, 64 * 4);
            slideshowPreview.Freeze();
            // The second still is PORTRAIT: a slideshow border sized to
            // the empty stage rather than the actual photo fails this check.
            var portraitPixels = new byte[48 * 64 * 4];
            for (var j = 3; j < portraitPixels.Length; j += 4)
                portraitPixels[j] = 255;
            var slideshowPortrait = BitmapSource.Create(48, 64, 96, 96,
                PixelFormats.Bgra32, null, portraitPixels, 48 * 4);
            slideshowPortrait.Freeze();
            var landscapeFrame = SlideshowWindow.FitPhotoFrame(
                640, 480, 800, 600, 10);
            var portraitFrame = SlideshowWindow.FitPhotoFrame(
                480, 640, 800, 600, 10);
            if (Math.Abs((landscapeFrame.Width - 20) /
                (landscapeFrame.Height - 20) - 640d / 480) > .001 ||
                Math.Abs((portraitFrame.Width - 20) /
                (portraitFrame.Height - 20) - 480d / 640) > .001 ||
                portraitFrame.Width >= landscapeFrame.Width)
                throw new Exception(
                    "Slideshow photo frame does not follow photo aspect ratio.");

            var galleryBrush = new SolidColorBrush(Colors.SlateGray);
            var slideshowDatabase = (PCasa.App.Data.LocalDatabase)
                typeof(PCasa.App.MainWindow).GetField("_database",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var slideshow = new SlideshowWindow(window, slideshowSequence, 0,
                slideshowDatabase, galleryBrush, new SlideshowSettings
                {
                    IntervalSeconds = 3600,
                    PreloadNext = false,
                    Transition = "none"
                }, index => index == 2 ? slideshowPortrait : slideshowPreview)
            {
                WindowState = WindowState.Normal,
                Width = 640, Height = 480
            };
            try
            {
                slideshow.Show();
                Flush(slideshow);
                if (!ReferenceEquals(slideshow.Background, galleryBrush) ||
                    slideshow.CurrentImageIndex != 0 || slideshow.IsPaused)
                    throw new Exception("Slideshow failed to show the gallery-texture default.");
                // The native Direct3D HWND is a rendering surface, not an
                // extra slideshow control or caption. WPF's fallback Grid
                // stays in the same root, but must not add label overlays.
                if (slideshow.Content is not Grid slideRoot ||
                    slideRoot.Children.Count != 2 ||
                    slideRoot.Children[0] is not Grid ||
                    slideRoot.Children[1] is not System.Windows.Interop.HwndHost gpuElement)
                    throw new Exception(
                        "Slideshow added an unexpected overlay or is missing its GPU surface.");
                // Alpha.53: no pointer in either WPF or native GPU airspace,
                // and the gallery brush must cover the host until Direct3D
                // has presented its first frame (never show a white HWND).
                if (slideshow.Cursor != System.Windows.Input.Cursors.None ||
                    !slideshow.ForceCursor ||
                    slideRoot.Cursor != System.Windows.Input.Cursors.None ||
                    !slideRoot.ForceCursor ||
                    gpuElement.Cursor != System.Windows.Input.Cursors.None ||
                    !ReferenceEquals(slideRoot.Background, galleryBrush))
                    throw new Exception(
                        "Slideshow cursor is visible or startup gallery backdrop is missing.");
                var hostMessage = gpuElement.GetType().GetMethod(
                    "WndProc", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)
                    ?? throw new Exception("Native slideshow message handler missing.");
                var eraseArgs = new object[] {
                    IntPtr.Zero, 0x0014, IntPtr.Zero, IntPtr.Zero, false
                };
                if (hostMessage.Invoke(gpuElement, eraseArgs) is not IntPtr eraseResult ||
                    eraseResult != new IntPtr(1) || eraseArgs[4] is not true)
                    throw new Exception(
                        "Native slideshow host can still paint a white GDI background.");
                var cursorArgs = new object[] {
                    IntPtr.Zero, 0x0020, IntPtr.Zero, IntPtr.Zero, false
                };
                if (hostMessage.Invoke(gpuElement, cursorArgs) is not IntPtr cursorResult ||
                    cursorResult != new IntPtr(1) || cursorArgs[4] is not true)
                    throw new Exception(
                        "Native Direct3D host does not suppress Windows' mouse cursor.");
                Console.WriteLine(
                    "PASS: Slideshow uses original backdrop before first GPU frame; no mouse cursor or white native paint.");
                var slidesFlags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                var currentLayerField = typeof(SlideshowWindow).GetField(
                    "_active", slidesFlags)!;
                Border PhotoFrame(SlideshowWindow presentation)
                {
                    var layer = currentLayerField.GetValue(presentation)!;
                    return (Border)layer.GetType().GetField(
                        "Frame", System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance)!
                        .GetValue(layer)!;
                }
                var currentFrame = PhotoFrame(slideshow);
                if (currentFrame.CacheMode is not BitmapCache ||
                    currentFrame.Effect is not
                        System.Windows.Media.Effects.DropShadowEffect shadow ||
                    Math.Abs(shadow.Direction - 315) > .01 ||
                    shadow.Opacity < .75 ||
                    Math.Abs((currentFrame.Width - 20) /
                        (currentFrame.Height - 20) - 64d / 48) > .001)
                    throw new Exception(
                        "Landscape slideshow frame/shadow does not match image bounds.");
                typeof(SlideshowWindow).GetMethod("Navigate",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(slideshow, [1]);
                Flush(slideshow);
                if (slideshow.CurrentImageIndex != 2)
                    throw new Exception("Manual slideshow Right did not skip video instantly.");
                var frameAfter = PhotoFrame(slideshow);
                if (Math.Abs((frameAfter.Width - 20) /
                    (frameAfter.Height - 20) - 48d / 64) > .001)
                    throw new Exception(
                        "Portrait slideshow border stayed at the old landscape bounds.");
                // A single click anywhere in the show exits just as in the
                // ordinary PCasa photo viewer.
                slideshow.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
                });
                Flush(window);
                if (slideshow.IsVisible)
                    throw new Exception("Single click did not close slideshow.");
            }
            finally { if (slideshow.IsVisible) slideshow.Close(); }
            Console.WriteLine(
                "PASS: WPF slideshow displays local pixels, uses gallery brush and changes image immediately.");
            // Even with look-ahead enabled, missing a high-resolution
            // prefetch must not make manual Next wait for the NAS. The local
            // gallery thumbnail appears immediately, while any replacement
            // of its pixels is deferred until the WPF transition completes.
            var preloadShow = new SlideshowWindow(window, slideshowSequence, 0,
                slideshowDatabase, galleryBrush, new SlideshowSettings
                {
                    IntervalSeconds = 3600, PreloadNext = true,
                    Transition = "zoom", TransitionMilliseconds = 700
                }, index => index == 2 ? slideshowPortrait : slideshowPreview)
            {
                WindowState = WindowState.Normal, Width = 640, Height = 480
            };
            try
            {
                preloadShow.Show();
                Flush(preloadShow);
                var preloadFlags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                var goNext = typeof(SlideshowWindow).GetMethod("Navigate", preloadFlags)!;
                goNext.Invoke(preloadShow, [1]);
                var shown = (int)typeof(SlideshowWindow).GetField(
                    "_shownIndex", preloadFlags)!.GetValue(preloadShow)!;
                var transitioning = (bool)typeof(SlideshowWindow).GetField(
                    "_transitioning", preloadFlags)!.GetValue(preloadShow)!;
                var nextLayer = typeof(SlideshowWindow).GetField(
                    "_incoming", preloadFlags)!.GetValue(preloadShow)!;
                var nextPicture = (Image)nextLayer.GetType().GetField(
                    "Picture", System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance)!
                    .GetValue(nextLayer)!;
                if (shown != 2 || !transitioning ||
                    !ReferenceEquals(nextPicture.Source, slideshowPortrait))
                    throw new Exception(
                        "Slideshow with prefetch enabled delayed manual Next instead of showing local cached pixels.");
                var improvedPixels = new byte[96 * 128 * 4];
                for (var j = 3; j < improvedPixels.Length; j += 4)
                    improvedPixels[j] = 255;
                var improved = BitmapSource.Create(96, 128, 96, 96,
                    PixelFormats.Bgra32, null, improvedPixels, 96 * 4);
                improved.Freeze();
                typeof(SlideshowWindow).GetField(
                    "_afterTransitionPixels", preloadFlags)!
                    .SetValue(preloadShow, improved);
                if (!ReferenceEquals(nextPicture.Source, slideshowPortrait))
                    throw new Exception(
                        "High-resolution result interrupted a still-running slideshow transition.");
                typeof(SlideshowWindow).GetMethod("FinishTransition", preloadFlags)!
                    .Invoke(preloadShow, null);
                var activeLayer = typeof(SlideshowWindow).GetField(
                    "_active", preloadFlags)!.GetValue(preloadShow)!;
                var activePicture = (Image)activeLayer.GetType().GetField(
                    "Picture", System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance)!
                    .GetValue(activeLayer)!;
                if (!ReferenceEquals(activePicture.Source, improved))
                    throw new Exception(
                        "Slideshow failed to swap in high-resolution pixels after animation finished.");
            }
            finally { if (preloadShow.IsVisible) preloadShow.Close(); }
            Console.WriteLine(
                "PASS: Prefetched slideshow navigates from local cache and applies refined pixels after transition.");

            // Alpha.50 restores the projector as a native Direct3D effect.
            // Existing projector preferences must now remain selected,
            // while unknown legacy values still normalize to a dissolve.
            if (new SlideshowSettings { Transition = "projector" }
                    .Normalize().Transition != "projector" ||
                new SlideshowSettings { Transition = "unknown-legacy" }
                    .Normalize().Transition != "fade")
                throw new Exception(
                    "Restored projector or unknown transition normalization failed.");
            var sliding = new SlideshowWindow(window, slideshowSequence, 0,
                slideshowDatabase, galleryBrush, new SlideshowSettings
                {
                    IntervalSeconds = 3600, PreloadNext = false,
                    Transition = "slide", TransitionMilliseconds = 700
                }, index => index == 2 ? slideshowPortrait : slideshowPreview)
            {
                WindowState = WindowState.Normal, Width = 640, Height = 480
            };
            try
            {
                sliding.Show();
                Flush(sliding);
                var flags = System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                typeof(SlideshowWindow).GetMethod("Navigate", flags)!
                    .Invoke(sliding, [1]);
                if (typeof(SlideshowWindow).GetField("_transitioning", flags)!
                    .GetValue(sliding) is not true)
                    throw new Exception("Slide did not start its transition.");
                var incomingLayer = typeof(SlideshowWindow).GetField(
                    "_incoming", flags)!.GetValue(sliding)!;
                var shift = (TranslateTransform)incomingLayer.GetType().GetField(
                    "Shift", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public)!.GetValue(incomingLayer)!;
                var incomingFrame = (Border)incomingLayer.GetType().GetField(
                    "Frame", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public)!.GetValue(incomingLayer)!;
                if (incomingFrame.RenderTransform is not TransformGroup transforms ||
                    !transforms.Children.Contains(shift))
                    throw new Exception("Slide transform is not attached to its image.");
                // BeginAnimation queues its clock for the NEXT composition
                // pass. Querying IsAnimated before pumping the STA dispatcher
                // races WPF and spuriously reports that there is no clock.
                // Verify actual interpolation instead of merely relaxing
                // the check: both the attached clock and moving pixels are
                // required while the transition is active.
                var observedClock = false;
                var observedMotion = false;
                var highestX = shift.X;
                var animationDeadline = DateTime.UtcNow.AddMilliseconds(550);
                while (DateTime.UtcNow < animationDeadline)
                {
                    Flush(sliding);
                    observedClock |= DependencyPropertyHelper.GetValueSource(
                        shift, TranslateTransform.XProperty).IsAnimated;
                    var currentX = shift.X;
                    highestX = Math.Max(highestX, currentX);
                    if (highestX > 8 && currentX < highestX - 5 &&
                        currentX > 0)
                        observedMotion = true;
                    if (observedClock && observedMotion) break;
                    System.Threading.Thread.Sleep(15);
                }
                var gpu = typeof(SlideshowWindow).GetField("_gpu", flags)!
                    .GetValue(sliding);
                if (gpu?.GetType().GetProperty("IsReady")?.GetValue(gpu)
                    is true)
                {
                    // Direct3D11 uses an independent vsync-paced flip chain:
                    // there MUST NOT be a WPF animation clock in this mode.
                    if (observedClock)
                        throw new Exception("GPU slide still animated WPF.");
                    var slideMath = gpu.GetType().GetMethod("SlideTravel")!;
                    var travel = (double)slideMath.Invoke(null,
                        [640.0, 400.0, 320.0])!;
                    var gap = travel - 400 / 2.0 - 320 / 2.0;
                    if (Math.Abs(gap - (640 - 400) / 2.0) > .01)
                        throw new Exception(
                            "Slide image gap differs from previous right margin.");
                    var exitMethod = gpu.GetType().GetMethod("SlideExitTravel")!;
                    foreach (var photoWidth in new[] { 320.0, 400.0, 550.0, 620.0 })
                    {
                        const double screen = 640, shadowExtent = 65;
                        var exitTravel = (double)exitMethod.Invoke(null,
                            [screen, photoWidth, shadowExtent])!;
                        var oldRightAfterSlide =
                            (screen + photoWidth) / 2 - exitTravel + shadowExtent;
                        if (oldRightAfterSlide >= 0)
                            throw new Exception(
                                "Wide outgoing slide/shadow still visible after transition.");
                    }
                    var shadowMethod = gpu.GetType().GetMethod("ShadowRect")!;
                    var sharp = (ValueTuple<float,float,float,float>)shadowMethod.Invoke(null,
                        [100f, 80f, 400f, 300f, 25f, 0f])!;
                    var broad = (ValueTuple<float,float,float,float>)shadowMethod.Invoke(null,
                        [100f, 80f, 400f, 300f, 25f, 40f])!;
                    if (sharp.Item1 != 125 || sharp.Item2 != 105 ||
                        sharp.Item3 != 400 || sharp.Item4 != 300 ||
                        broad.Item1 != sharp.Item1 || broad.Item2 != sharp.Item2 ||
                        broad.Item3 != 440 || broad.Item4 != 340)
                        throw new Exception(
                            "Shadow expanded above/left or sharp shadow lost its offset.");
                }
                else if (!observedClock || !observedMotion)
                    throw new Exception(
                        "Fallback slide did not animate an attached WPF transform " +
                        $"(clock={observedClock}, moving={observedMotion}, x={shift.X:F1}).");
                Until(sliding, () =>
                    typeof(SlideshowWindow).GetField("_transitioning", flags)!
                        .GetValue(sliding) is false,
                    "Slide transition never completed.");
                if (Math.Abs(shift.X) > 0.01)
                    throw new Exception("Slide did not restore its destination position.");
                if (sliding.CurrentImageIndex != 2)
                    throw new Exception("Slide completion lost the destination.");

                // Alpha.60: Left must reverse the travel of BOTH photos, not
                // just decrease the sequence index. The native GPU uses the
                // same signed direction; WPF fallback starts from negative X.
                typeof(SlideshowWindow).GetMethod("Navigate", flags)!
                    .Invoke(sliding, [-1]);
                Flush(sliding);
                if (sliding.CurrentImageIndex != 0 ||
                    typeof(SlideshowWindow).GetField(
                        "_navigationDirection", flags)!.GetValue(sliding) is not -1)
                    throw new Exception("Left navigation did not reverse slideshow direction.");
                if (gpu?.GetType().GetProperty("IsReady")?.GetValue(gpu) is not true)
                {
                    var reversedLayer = typeof(SlideshowWindow).GetField(
                        "_incoming", flags)!.GetValue(sliding)!;
                    var reversedShift = (TranslateTransform)reversedLayer.GetType()
                        .GetField("Shift", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.Public)!.GetValue(reversedLayer)!;
                    if (reversedShift.X >= 0)
                        throw new Exception(
                            "WPF reverse slide still enters from the right.");
                }
                Until(sliding, () =>
                    typeof(SlideshowWindow).GetField("_transitioning", flags)!
                        .GetValue(sliding) is false,
                    "Reverse slide transition never completed.");
                Console.WriteLine(
                    "PASS: Alpha.60 manual Left reverses slideshow navigation and slide travel.");
            }
            finally { sliding.Close(); }
            // Geometry-only assertions also run when CI has no D3D device.
            var cubeType = typeof(SlideshowWindow).Assembly.GetType(
                "PCasa.App.Services.Direct3DSlideshowSurface")!;
            var cubeFlags = System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var cubePoint = cubeType.GetMethod("CubePoint", cubeFlags)!;
            var cubeRect = cubeType.GetMethod("CubeImageRect", cubeFlags)!;

            // Alpha.60: identical circular geometry for GPU and WPF
            // fallbacks. Rotation changes DIRECTION but never flips,
            // stretches or skews the underlying photo texture.
            var poseMethod = cubeType.GetMethod("ViewmasterPose", cubeFlags)
                ?? throw new Exception("Viewmaster reel geometry is missing.");
            (float X, float Y, float Scale, float Tilt) Viewmaster(
                int slot, float progress, int direction, float width = 1280,
                float height = 720)
                => ((float, float, float, float))poseMethod.Invoke(null,
                    [slot, progress, direction, width, height])!;
            var startPhoto = Viewmaster(0, 0, 1);
            var finishPhoto = Viewmaster(1, 1, 1);
            if (Math.Abs(startPhoto.X) > .001f ||
                Math.Abs(startPhoto.Y) > .001f ||
                Math.Abs(startPhoto.Scale - 1f) > .001f ||
                Math.Abs(startPhoto.Tilt) > .001f ||
                Math.Abs(finishPhoto.X) > .001f ||
                Math.Abs(finishPhoto.Y) > .001f ||
                Math.Abs(finishPhoto.Scale - 1f) > .001f ||
                Math.Abs(finishPhoto.Tilt) > .001f)
                throw new Exception("Viewmaster did not settle upright and centered.");
            var entryRight = Viewmaster(1, 0, +1);
            var entryLeft = Viewmaster(1, 0, -1);
            if (entryRight.X <= 0 || entryLeft.X >= 0 ||
                Math.Abs(entryRight.Y - entryLeft.Y) > .001 ||
                Math.Abs(entryRight.Scale - entryLeft.Scale) > .001 ||
                Math.Abs(entryRight.Tilt + entryLeft.Tilt) > .001)
                throw new Exception("Viewmaster Left does not reverse reel direction.");
            foreach (var width in new[] { 720f, 1280f, 1920f })
            foreach (var height in new[] { 600f, 1080f })
            foreach (var t in new[] { 0f, .20f, .5f, .8f, 1f })
            foreach (var reelPosition in new[] { -2, -1, 0, 1, 2 })
            {
                var forward = Viewmaster(reelPosition, t, +1, width, height);
                var backward = Viewmaster(reelPosition, t, -1, width, height);
                if (!float.IsFinite(forward.X) || !float.IsFinite(forward.Y) ||
                    !float.IsFinite(forward.Tilt) ||
                    forward.Scale is < .45f or > 1.001f ||
                    Math.Abs(forward.X + backward.X) > .01f ||
                    Math.Abs(forward.Y - backward.Y) > .01f ||
                    Math.Abs(forward.Scale - backward.Scale) > .001f ||
                    Math.Abs(forward.Tilt + backward.Tilt) > .001f)
                    throw new Exception(
                        "Viewmaster reel geometry loses scale, reverse or window-size invariants.");
            }
            var middleOld = Viewmaster(0, .5f, +1);
            var middleNext = Viewmaster(1, .5f, +1);
            if (Math.Abs(middleOld.X + middleNext.X) > .01f ||
                Math.Abs(middleOld.Y - middleNext.Y) > .01f ||
                Math.Abs(middleOld.Scale - middleNext.Scale) > .001f ||
                middleOld.Tilt >= 0 || middleNext.Tilt <= 0)
                throw new Exception(
                    "Viewmaster A is not a common circular arc for both pictures.");
            Console.WriteLine(
                "PASS: Alpha.60 GPU/WPF Viewmaster A/B circular reel geometry, reverse travel, " +
                "upright images and five photo slots.");
            (float X, float Y, float W) Point(float u, float v,
                bool incoming, float progress)
                => ((float, float, float))cubePoint.Invoke(null,
                    [900f, 600f, u, v, incoming, progress])!;
            // The reversed cube must rotate around the opposite physical
            // hinge without mirroring the PHOTO pixels. Forward geometry is
            // unchanged; reverse maps u -> 1-u and negates only the X point.
            var reverseCube = cubeType.GetMethod("CubePointWithDirection", cubeFlags)
                ?? throw new Exception("GPU cube has no reverse-direction geometry.");
            (float X, float Y, float W) ReversePoint(float u, float v,
                bool incoming, float progress)
                => ((float, float, float))reverseCube.Invoke(null,
                    [900f, 600f, u, v, incoming, progress, -1])!;
            foreach (var cubeProgress in new[] { 0f, .15f, .5f, .85f, 1f })
            {
                var outgoingHinge = ReversePoint(0, .5f, false, cubeProgress);
                var incomingHinge = ReversePoint(1, .5f, true, cubeProgress);
                if (Math.Abs(outgoingHinge.X - incomingHinge.X) > .005f ||
                    Math.Abs(outgoingHinge.Y - incomingHinge.Y) > .005f ||
                    Math.Abs(outgoingHinge.W - incomingHinge.W) > .005f)
                    throw new Exception("Reversed cube faces separated at left hinge.");
                foreach (var u in new[] { 0f, .2f, .8f, 1f })
                {
                    var reverse = ReversePoint(u, .5f, false, cubeProgress);
                    var forwardMirror = Point(1f - u, .5f, false, cubeProgress);
                    if (Math.Abs(reverse.X + forwardMirror.X) > .005f ||
                        Math.Abs(reverse.W - forwardMirror.W) > .005f)
                        throw new Exception("Reversed cube does not mirror 3D travel.");
                }
            }
            if (Math.Abs(ReversePoint(0, .5f, false, 0).X + 450) > .01f ||
                Math.Abs(ReversePoint(1, .5f, false, 0).X - 450) > .01f ||
                Math.Abs(ReversePoint(0, .5f, true, 1).X + 450) > .01f ||
                Math.Abs(ReversePoint(1, .5f, true, 1).X - 450) > .01f)
                throw new Exception("Reversed cube mirrored the photo itself.");
            Console.WriteLine(
                "PASS: Alpha.60 reverse cube has opposite hinge, mirrored motion, upright photos.");
            foreach (var position in new[] { 0f, .15f, .5f, .85f, 1f })
            {
                var aHinge = Point(1, .5f, false, position);
                var bHinge = Point(0, .5f, true, position);
                if (Math.Abs(aHinge.X - bHinge.X) > .005f ||
                    Math.Abs(aHinge.Y - bHinge.Y) > .005f ||
                    Math.Abs(aHinge.W - bHinge.W) > .005f)
                    throw new Exception("Cube faces separated at common 3D hinge.");
            }
            if (Math.Abs(Point(0, .5f, false, 0).X + 450) > .01 ||
                Math.Abs(Point(1, .5f, true, 1).X - 450) > .01 ||
                Math.Abs(Point(0, .5f, false, .5f).W -
                         Point(1, .5f, false, .5f).W) < .1f)
                throw new Exception("Cube is still a flat accordion.");
            foreach (var size in new[] { (6000f, 4000f), (3000f, 6000f),
                                         (8000f, 900f), (900f, 8000f) })
            {
                var cubeFit = ((float U, float V, float Width, float Height))
                    cubeRect.Invoke(null, [900f, 600f, size.Item1, size.Item2, 10f])!;
                if (Math.Abs(cubeFit.U + cubeFit.Width * .5f - .5f) > .0001 ||
                    Math.Abs(cubeFit.V + cubeFit.Height * .5f - .5f) > .0001 ||
                    Math.Abs((cubeFit.Width * 900 / (cubeFit.Height * 600)) -
                             size.Item1 / size.Item2) > .0001 ||
                    cubeFit.Width > 1 || cubeFit.Height > 1)
                    throw new Exception("Cube distorted or mis-centered photo.");
            }
            // Alpha.54: each cube photograph gets its OWN bounded shadow.
            // Shadows cannot protrude onto a different perspective face.
            var cubeShadow = cubeType.GetMethod("CubeShadowRect", cubeFlags)
                ?? throw new Exception("Cube faces have no photo shadow geometry.");
            var photoShadow = ((float U, float V, float Width, float Height))
                cubeShadow.Invoke(null, [.1f, .2f, .6f, .5f,
                    .02f, .03f, .1f, .1f])!;
            var clippedShadow = ((float U, float V, float Width, float Height))
                cubeShadow.Invoke(null, [.8f, .85f, .2f, .15f,
                    .03f, .04f, .1f, .1f])!;
            if (Math.Abs(photoShadow.U - .12f) > .0001 ||
                Math.Abs(photoShadow.V - .23f) > .0001 ||
                photoShadow.Width <= .6f || photoShadow.Height <= .5f ||
                clippedShadow.U + clippedShadow.Width > 1.0001f ||
                clippedShadow.V + clippedShadow.Height > 1.0001f)
                throw new Exception("Cube shadow protrudes past its photo face.");
            // Alpha.55 regression: both right AND lower shadow borders
            // must survive for landscape, portrait and panorama images.
            // The old centered-on-full-face geometry clipped one of them.
            var withMargin = cubeType.GetMethod(
                "CubeImageRectWithShadow", cubeFlags)
                ?? throw new Exception("Cube does not reserve both shadow margins.");
            foreach (var dimensions in new[] {
                (6000f, 4000f), (3000f, 6000f),
                (8000f, 900f), (900f, 8000f) })
            {
                var shadowFit = ((float U, float V, float Width, float Height))
                    withMargin.Invoke(null, [900f, 600f,
                        dimensions.Item1, dimensions.Item2, 10f, 38f])!;
                var shadow = ((float U, float V, float Width, float Height))
                    cubeShadow.Invoke(null, [
                        shadowFit.U - 10f / 900f, shadowFit.V - 10f / 600f,
                        shadowFit.Width + 20f / 900f, shadowFit.Height + 20f / 600f,
                        12f / 900f, 12f / 600f, 26f / 900f, 26f / 600f])!;
                var photoRight = shadowFit.U + shadowFit.Width + 10f / 900f;
                var photoBottom = shadowFit.V + shadowFit.Height + 10f / 600f;
                if (shadow.U + shadow.Width <= photoRight + .005f ||
                    shadow.V + shadow.Height <= photoBottom + .005f ||
                    shadow.U + shadow.Width > 1.0001f ||
                    shadow.V + shadow.Height > 1.0001f ||
                    Math.Abs((shadowFit.Width * 900 / (shadowFit.Height * 600)) -
                        dimensions.Item1 / dimensions.Item2) > .0001f)
                    throw new Exception(
                        "Cube still clips its right or lower photo shadow for " +
                        $"{dimensions.Item1}x{dimensions.Item2}.");
            }
            Console.WriteLine(
                "PASS: Cube reserves both right and lower shadow margins for every photo format.");

            Console.WriteLine(
                "PASS: GPU slide fully ejects wide photo, preserves entry gap and has right-bottom-only shadows.");

            // S: and other drive/parent nodes do not own a photo-folder ID.
            // They must navigate to their FIRST indexed descendant without
            // any filesystem probe; an unrelated drive is never eligible.
            var descendantsMethod = typeof(PCasa.App.MainWindow).GetMethod(
                "PhotoFoldersBelowTreePath", System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)
                ?? throw new Exception("Parent/drive navigation resolver missing.");
            var sFolders = new[] {
                new PhotoFolderSummary(901, 1, @"S:\Albums\First",
                    @"S:\Albums\First", "First", 20),
                new PhotoFolderSummary(902, 1, @"S:\Albums\Second",
                    @"S:\Albums\Second", "Second", 40),
                new PhotoFolderSummary(903, 2, @"G:\Photos",
                    @"G:\Photos", "Other drive", 12)
            };
            var sPositions = new Dictionary<long, int> {
                [902] = 0, [901] = 1, [903] = 2
            };
            IReadOnlyList<long> ForRoot(string path) =>
                (IReadOnlyList<long>)descendantsMethod.Invoke(null,
                    [path, sFolders, sPositions])!;
            if (!ForRoot(@"S:\").SequenceEqual(new long[] { 902, 901 }) ||
                !ForRoot(@"S:\Albums").SequenceEqual(new long[] { 902, 901 }) ||
                !ForRoot(@"S:\Albums\First").SequenceEqual(new long[] { 901 }) ||
                ForRoot(@"S:\Albums\Missing").Count != 0 ||
                !ForRoot(@"G:\").SequenceEqual(new long[] { 903 }))
                throw new Exception(
                    "Drive and parent folder clicks resolve to missing/wrong cached photo folders.");
            Console.WriteLine(
                "PASS: Drive S: and parent folders resolve indexed children without source IO.");

            // Alpha.56: offscreen S: children may have NO realized WPF
            // LogicalTree parent. Their already-catalogued thumbnails still
            // need a deterministic navigation path: expand by ItemsControl
            // membership, never by LogicalTreeHelper.GetParent(target).
            var nasTree = new TreeView();
            var nasRoot = new TreeViewItem { Header = "S:", IsExpanded = false };
            var nasParent = new TreeViewItem {
                Header = "Albums", IsExpanded = false
            };
            var nasChild = new TreeViewItem { Header = "2026" };
            nasRoot.Items.Add(nasParent);
            nasParent.Items.Add(nasChild);
            nasTree.Items.Add(nasRoot);
            if (!FolderTreeNavigation.ExpandAncestors(nasTree, nasChild) ||
                !nasRoot.IsExpanded || !nasParent.IsExpanded ||
                FolderTreeNavigation.ExpandAncestors(nasTree, nasChild))
                throw new Exception(
                    "Unrealized S: descendants could not expand without NAS I/O.");
            Console.WriteLine(
                "PASS: Collapsed NAS tree children expand without WPF realization or disk reads.");

            // Startup must position the HEADER, not the full expanded item's
            // visual bounds, and commit the offset before a later rebuild.
            var restoreTree = new TreeView { Margin = new Thickness(2) };
            ScrollViewer.SetCanContentScroll(restoreTree, false);
            VirtualizingPanel.SetIsVirtualizing(restoreTree, false);
            for (var row = 0; row < 80; row++)
                restoreTree.Items.Add(new TreeViewItem { Header = "Folder " + row });
            var restoredRow = (TreeViewItem)restoreTree.Items[65];
            for (var child = 0; child < 40; child++)
                restoredRow.Items.Add(new TreeViewItem { Header = "Child " + child });
            restoredRow.IsExpanded = true;
            var restoreWindow = new Window
            {
                Width = 300, Height = 260, Content = restoreTree, ShowInTaskbar = false,
                Owner = window
            };
            try
            {
                restoreWindow.Show();
                Flush(restoreWindow);
                restoredRow.IsSelected = true;
                ScrollViewer? FindScroll(DependencyObject root)
                {
                    if (root is ScrollViewer viewer) return viewer;
                    for (int child = 0; child < VisualTreeHelper.GetChildrenCount(root); child++)
                        if (FindScroll(VisualTreeHelper.GetChild(root, child)) is { } found)
                            return found;
                    return null;
                }
                var restoreScroll = FindScroll(restoreTree)
                    ?? throw new Exception("Folder restoration scroll viewer missing.");
                restoreScroll.ScrollToVerticalOffset(0);
                Flush(restoreWindow);
                if (!FolderTreeNavigation.RevealHeader(restoreTree, restoredRow, restoreScroll))
                    throw new Exception("Selected folder header could not be restored.");
                Flush(restoreWindow);
                restoredRow.ApplyTemplate();
                var restoredHeader = restoredRow.Template?.FindName("PART_Header", restoredRow)
                    as FrameworkElement ?? throw new Exception("Selected folder header missing.");
                var restoredViewport = restoreScroll.Template.FindName("PART_ScrollContentPresenter", restoreScroll)
                    as ScrollContentPresenter ?? throw new Exception("Folder scroll content presenter missing.");
                var restoredTop = restoredHeader.TranslatePoint(new Point(0, 0), restoredViewport).Y;
                if (restoreScroll.VerticalOffset <= 0 || restoredTop < -1 ||
                    restoredTop + restoredHeader.ActualHeight > restoreScroll.ViewportHeight + 2)
                    throw new Exception($"Selected folder header is outside the restored viewport: offset={restoreScroll.VerticalOffset}, top={restoredTop}, header={restoredHeader.ActualHeight}, viewport={restoreScroll.ViewportHeight}, extent={restoreScroll.ExtentHeight}.");
            }
            finally { restoreWindow.Close(); }
            Console.WriteLine("PASS: Selected expanded folder header is in the restored viewport.");

            // Alpha.56: a large S: library may contain thousands of closed
            // parents. The visibility mask must inspect only each candidate's
            // OWN ancestors (O(depth)), not every collapsed folder (O(N^2)).
            var hiddenCheck = typeof(PCasa.App.MainWindow).GetMethod(
                "IsDescendantOfCollapsedPath",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public)
                ?? throw new Exception("Exact collapsed-parent lookup missing.");
            var closedPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase) { @"S:\Albums" };
            bool IsHidden(string path, IReadOnlySet<string> closed) =>
                (bool)hiddenCheck.Invoke(null, [path, closed])!;
            if (!IsHidden(@"S:\Albums\2026", closedPaths) ||
                !IsHidden(@"S:\Albums\2026\Trip", closedPaths) ||
                IsHidden(@"S:\Albums", closedPaths) ||
                IsHidden(@"S:\AlbumsBackup\2026", closedPaths) ||
                IsHidden(@"G:\Albums\2026", closedPaths))
                throw new Exception(
                    "Collapsed folder masks hide unrelated paths or own photos.");
            var manyClosed = Enumerable.Range(0, 5000)
                .Select(i => $@"S:\Other\Folder{i:D4}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            manyClosed.Add(@"S:\Albums");
            for (var i = 0; i < 500; i++)
                if (IsHidden($@"S:\Albums\{i:D4}", manyClosed) != true)
                    throw new Exception("Large NAS collapsed-mask lookup lost a photo folder.");
            Console.WriteLine(
                "PASS: Thousands of closed S: folders use exact ancestry rather than quadratic searches.");

            var savedScope = PhotoViewerSettings.Load().SequenceScope;
            try
            {
                ChooseAppearance("sequence", "CurrentFolder");
                if (PhotoViewerSettings.Load().SequenceScope != PhotoSequenceScope.CurrentFolder)
                    throw new Exception("Folder sequence choice failed to persist.");
                ChooseAppearance("sequence", "Gallery");
                if (PhotoViewerSettings.Load().SequenceScope != PhotoSequenceScope.Gallery)
                    throw new Exception("Gallery sequence choice failed to persist.");
            }
            finally
            {
                ChooseAppearance("sequence", savedScope.ToString());
            }
            Console.WriteLine("PASS: One persistent image-sequence setting switches both modes.");

            var syntheticFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var syntheticType = typeof(PCasa.App.MainWindow);
            var syntheticInitializedField = syntheticType.GetField(
                "_browseInitialized", syntheticFlags)!;
            var beforeSyntheticBrowseInitialized =
                (bool)syntheticInitializedField.GetValue(window)!;
            // This fixture lives only in memory; the real test SQLite
            // catalogue is deliberately empty. With longer GPU slideshow
            // checks, a startup/pending async publisher can otherwise replace
            // the artificial folder positions after the fixture is installed.
            // Quiesce the production publisher IN THE TEST ONLY before any
            // gallery navigation assertions, rather than hiding a missing
            // folder behind Nullable<T>.Value or changing app behavior.
            syntheticInitializedField.SetValue(window, false);
            ((LatestViewRequest)syntheticType.GetField(
                "_browseRefreshRequests", syntheticFlags)!.GetValue(window)!)
                .Invalidate();
            var syntheticPublicationTimer = (DispatcherTimer?)syntheticType.GetField(
                "_catalogPublicationTimer", syntheticFlags)!.GetValue(window);
            syntheticPublicationTimer?.Stop();
            syntheticType.GetField("_catalogPublicationPending", syntheticFlags)!
                .SetValue(window, 0);
            var pendingSyntheticPublisher = syntheticType.GetField(
                "_galleryRefreshCompletion", syntheticFlags)!;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                Flush(window);
                if (pendingSyntheticPublisher.GetValue(window) is not Task pending ||
                    pending.IsCompleted)
                    break;
                System.Threading.Thread.Sleep(20);
            }
            if (pendingSyntheticPublisher.GetValue(window) is Task unfinished &&
                !unfinished.IsCompleted)
                throw new Exception(
                    "Catalog publisher still running before gallery navigation test.");

            pages.SetIndex(synthetic);
            var surface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Actual browse surface not found.");
            var scroller = (ScrollViewer?)window.FindName("BrowseScrollViewer")
                ?? throw new Exception("Actual browse scroll viewer not found.");
            surface.SetData(synthetic, groups, pages);
            Flush(window);
            if (surface.GetFolderOffset(100) is null ||
                surface.GetFolderOffset(200) is null)
                throw new Exception(
                    "Synthetic gallery navigation fixture did not expose both folders.");
            if (surface.GetFirstVisibleFolderId(
                    new HashSet<long> { 100, 200 }) != 100 ||
                surface.GetFirstVisibleFolderId(
                    new HashSet<long> { 200 }) != 200 ||
                surface.GetFirstVisibleFolderId(
                    new HashSet<long> { 999 }) is not null)
                throw new Exception(
                    "Photo-less folder navigation cannot find its first local gallery section.");

            // Re-measuring at an unchanged width must keep the same row
            // geometry, even when WPF offers another available height.
            var layoutFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var measureGallery = typeof(BrowseGallerySurface).GetMethod(
                "MeasureOverride", layoutFlags)!;
            var sections = (System.Collections.IList)typeof(BrowseGallerySurface)
                .GetField("_sections", layoutFlags)!.GetValue(surface)!;
            var layoutWidth = Math.Max(1, surface.ActualWidth);
            measureGallery.Invoke(surface, [new Size(layoutWidth, 600)]);
            var originalSection = sections[0];
            measureGallery.Invoke(surface, [new Size(layoutWidth, 900)]);
            if (!ReferenceEquals(originalSection, sections[0]))
                throw new Exception("Unchanged gallery width rebuilt all row geometry.");
            measureGallery.Invoke(surface, [new Size(layoutWidth + 20, 900)]);
            if (ReferenceEquals(originalSection, sections[0]))
                throw new Exception("Changed gallery width did not rebuild row geometry.");
            measureGallery.Invoke(surface, [new Size(layoutWidth, 600)]);
            Console.WriteLine("PASS: Gallery reuses exact geometry at unchanged width.");
            
            // Alpha.57: the REAL folder-click helper must navigate an entire
            // folder excluded by the GPS property filter. The selected photo
            // directory must be a visible header even if its filter matches
            // are zero. Do not silently switch off the filter or include a
            // single unfiltered thumbnail.
            var filterFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var filterType = typeof(PCasa.App.MainWindow);
            var fullField = filterType.GetField("_browseIndex", filterFlags)!;
            var filteredField = filterType.GetField("_displayedBrowseIndex", filterFlags)!;
            var foldersField = filterType.GetField("_photoFolders", filterFlags)!;
            var activeField = filterType.GetField("_activePhotoFolderId", filterFlags)!;
            var appearanceField = filterType.GetField("_appearance", filterFlags)!;
            var appearance = appearanceField.GetValue(window)!;
            var gpsProperty = appearance.GetType().GetProperty("GalleryAttributeFilter")!;
            var previousFilter = gpsProperty.GetValue(appearance);
            var previousFull = fullField.GetValue(window);
            var previousFiltered = filteredField.GetValue(window);
            var previousFolders = foldersField.GetValue(window);
            var previousActive = activeField.GetValue(window);
            var previousHidden = surface.HiddenFolderIds;
            var previousEmpty = surface.EmptyNavigationFolderId;
            var emptyPanel = (FrameworkElement?)window.FindName("EmptyPanel")
                ?? throw new Exception("Global empty-gallery panel missing.");
            var previousEmptyPanelVisibility = emptyPanel.Visibility;
            var navigateEmpty = filterType.GetMethod(
                "TryNavigateToFilteredEmptyFolder", filterFlags)
                ?? throw new Exception("Filtered folder navigation handler missing.");
            try
            {
                gpsProperty.SetValue(appearance, "Gps");
                fullField.SetValue(window, synthetic);
                foldersField.SetValue(window, groups);
                surface.SetHiddenFolders(new HashSet<long>());

                // The LAST folder has 240 local photos, none matching GPS.
                // Its empty header must have enough scroll extent to reach
                // the top of the actual WPF ScrollViewer viewport.
                var firstOnly = synthetic.Take(240).ToArray();
                filteredField.SetValue(window, firstOnly);
                pages.SetIndex(firstOnly);
                surface.SetData(firstOnly, groups, pages);
                Flush(window);
                if (navigateEmpty.Invoke(window, [200L]) is not true)
                    throw new Exception("GPS-filtered last folder rejected its tree click.");
                Flush(window);
                var lastOffset = surface.GetFolderOffset(200)
                    ?? throw new Exception("Filtered last folder has no gallery section.");
                if (Math.Abs(scroller.VerticalOffset - lastOffset) > 3 ||
                    surface.GetFolderIdAtOffset(lastOffset + 4) != 200 ||
                    surface.GetVisibleFolderIds().Count != 2 ||
                    surface.GetImageAnchorOffset(synthetic[240].ImageId, 0) is not null)
                    throw new Exception(
                        "Filtered last folder did not jump to its own empty header.");

                // The FIRST folder has no GPS matches, but the following
                // folder does. Its header must precede all visible images.
                var secondOnly = synthetic.Skip(240).ToArray();
                filteredField.SetValue(window, secondOnly);
                pages.SetIndex(secondOnly);
                surface.SetData(secondOnly, groups, pages);
                surface.SetEmptyNavigationFolder(null);
                Flush(window);
                if (navigateEmpty.Invoke(window, [100L]) is not true)
                    throw new Exception("GPS-filtered first folder rejected its tree click.");
                Flush(window);
                if (surface.GetFolderOffset(100) is not double firstOffset ||
                    Math.Abs(firstOffset) > 1 ||
                    surface.GetVisibleFolderIds()[0] != 100 ||
                    surface.GetImageAnchorOffset(synthetic[0].ImageId, 0) is not null)
                    throw new Exception(
                        "Filtered first folder was not inserted before the visible gallery.");

                // With NO GPS photos anywhere, rendering an empty selected
                // section must not require a dummy thumbnail or cache page.
                var noImages = Array.Empty<BrowseThumbnailIndexEntry>();
                filteredField.SetValue(window, noImages);
                emptyPanel.Visibility = Visibility.Visible;
                pages.SetIndex(noImages);
                surface.SetData(noImages, groups, pages);
                surface.SetEmptyNavigationFolder(null);
                Flush(window);
                if (navigateEmpty.Invoke(window, [100L]) is not true)
                    throw new Exception("All-zero GPS results prevented folder navigation.");
                Flush(window);
                if (surface.GetVisibleFolderIds().Count != 1 ||
                    surface.GetVisibleFolderIds()[0] != 100 ||
                    surface.GetVisibleIndexRanges().Count != 0 ||
                    surface.GetFolderOffset(100) is null ||
                    emptyPanel.Visibility != Visibility.Collapsed)
                    throw new Exception(
                        "Entirely empty filtered gallery lost its navigable folder header.");
                Console.WriteLine(
                    "PASS: GPS filter preserves clickable first/last/entirely empty folders.");
            }
            finally
            {
                surface.SetEmptyNavigationFolder(null);
                surface.SetHiddenFolders(previousHidden);
                fullField.SetValue(window, previousFull);
                filteredField.SetValue(window, previousFiltered);
                foldersField.SetValue(window, previousFolders);
                activeField.SetValue(window, previousActive);
                gpsProperty.SetValue(appearance, previousFilter);
                emptyPanel.Visibility = previousEmptyPanelVisibility;
                pages.SetIndex(synthetic);
                surface.SetData(synthetic, groups, pages);
                Flush(window);
            }

            // Real regression: a GPS-marked photo in folder 100 must never
            // pull the gallery back after the user explicitly enters folder
            // 200. Repeat with a GPS-free photo: stale layout anchors are
            // invalid by FOLDER identity, not by a special GPS flag.
            var navFlags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            var navType = typeof(PCasa.App.MainWindow);
            var navDisplayedField = navType.GetField("_displayedBrowseIndex", navFlags)!;
            var navPendingField = navType.GetField("_pendingGalleryAnchor", navFlags)!;
            var navTargetField = navType.GetField("_explicitGalleryFolderTargetId", navFlags)!;
            var navScrollMethod = navType.GetMethod(
                "ScrollToExplicitGalleryFolder", navFlags)!;
            var navMarkMethod = navType.GetMethod(
                "MarkExplicitGalleryNavigation", navFlags)!;
            var navRestoreMethod = navType.GetMethod(
                "RestorePendingGalleryAnchor", navFlags)!;
            var navReflowMethod = navType.GetMethod(
                "BrowseSurface_LayoutReflowed", navFlags)!;
            var navMapPhotoMethod = navType.GetMethod(
                "FocusGpsMapPhotoWithoutGalleryNavigation", navFlags)!;
            var navPointsField = navType.GetField("_gpsPoints", navFlags)!;
            var navPointType = navType.GetNestedType("GpsPoint",
                System.Reflection.BindingFlags.NonPublic)!;
            var navOldDisplayed = navDisplayedField.GetValue(window);
            var navOldPoints = navPointsField.GetValue(window);
            var navOldTarget = navTargetField.GetValue(window);
            var navOldPending = navPendingField.GetValue(window);
            var navOldFolder = navType.GetField(
                "_activePhotoFolderId", navFlags)!.GetValue(window);
            try
            {
                navDisplayedField.SetValue(window,
                    synthetic.Select((entry, index) =>
                        entry with { HasGps = index == 0 }).ToArray());
                var newFolderStart = surface.GetFolderOffset(200)
                    ?? throw new Exception("Synthetic destination folder missing.");
                navMarkMethod.Invoke(window, [(long?)200]);
                navScrollMethod.Invoke(window, [200L, newFolderStart]);
                // WPF may apply ScrollViewer.VerticalOffset in a later
                // dispatcher pass. Request the destination's thumbnail page
                // NOW, before the old scroll position can monopolize cache IO.
                var galleryViewport = (double)typeof(BrowseGallerySurface)
                    .GetField("_viewportTop", System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)!
                    .GetValue(surface)!;
                if (Math.Abs(galleryViewport - newFolderStart) > 1)
                    throw new Exception(
                        "Folder click still requests OLD thumbnail pages before the target viewport.");
                Flush(window);
                var destination = scroller.VerticalOffset;
                if (Math.Abs(destination - newFolderStart) > 3 ||
                    !Equals(navTargetField.GetValue(window), 200L))
                    throw new Exception("Explicit folder navigation was lost before GPS checks.");

                foreach (var oldImageId in new[] {
                    synthetic[0].ImageId, // has GPS
                    synthetic[1].ImageId  // has NO GPS
                })
                {
                    var oldAnchor = (ImageId: oldImageId, RelativeY: 0d);
                    // Both a delayed pending anchor and a resize/reflow
                    // callback from the old folder must be rejected.
                    navPendingField.SetValue(window, oldAnchor);
                    navRestoreMethod.Invoke(window, null);
                    navReflowMethod.Invoke(window, [surface, oldAnchor]);
                    Flush(window);
                    if (Math.Abs(scroller.VerticalOffset - destination) > 3 ||
                        navPendingField.GetValue(window) is not null ||
                        !Equals(navTargetField.GetValue(window), 200L))
                        throw new Exception(
                            "Old folder anchor hijacked explicit navigation " +
                            "(HasGps=" + (oldImageId == synthetic[0].ImageId) + ").");
                }

                var navPoint = Activator.CreateInstance(navPointType,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, new object[] {
                        synthetic[0].ImageId, "older-gps-photo.jpg",
                        28.417d, -16.567d, true }, null)!;
                var navPoints = Array.CreateInstance(navPointType, 1);
                navPoints.SetValue(navPoint, 0);
                navPointsField.SetValue(window, navPoints);
                navMapPhotoMethod.Invoke(window, [synthetic[0].ImageId]);
                Flush(window);
                if (Math.Abs(scroller.VerticalOffset - destination) > 3 ||
                    !Equals(navTargetField.GetValue(window), 200L))
                    throw new Exception("GPS map photo focus scrolled the gallery to its old folder.");

                // A photo without GPS has no marker. Even a stale synthetic
                // map callback referencing its old ID must do nothing.
                navPointsField.SetValue(window, Array.CreateInstance(navPointType, 0));
                navMapPhotoMethod.Invoke(window, [synthetic[1].ImageId]);
                Flush(window);
                if (Math.Abs(scroller.VerticalOffset - destination) > 3 ||
                    !Equals(navTargetField.GetValue(window), 200L))
                    throw new Exception("GPS-free photo callback navigated the gallery.");

                Console.WriteLine(
                    "PASS: Old GPS and GPS-free photo callbacks cannot hijack a newly selected gallery folder.");
            }
            finally
            {
                navDisplayedField.SetValue(window, navOldDisplayed);
                navPointsField.SetValue(window, navOldPoints);
                navTargetField.SetValue(window, navOldTarget);
                navPendingField.SetValue(window, navOldPending);
                navType.GetField("_activePhotoFolderId", navFlags)!
                    .SetValue(window, navOldFolder);
                if (window.FindName("GpsPhotoPopup") is Border navPopup)
                    navPopup.Visibility = Visibility.Collapsed;
                Flush(window);
            }

            // The synthetic gallery deliberately keeps production catalog
            // publication disabled. Real button handlers require an initialized
            // browse state, so enable it only for their synchronous click and
            // immediately restore the isolated fixture before dispatcher work.
            void ClickSyntheticGalleryButton(Button button)
            {
                syntheticInitializedField.SetValue(window, true);
                try
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                }
                finally
                {
                    syntheticInitializedField.SetValue(window, false);
                }
                Flush(window);
            }

            // The bookmark is a toggle, not a new scheduler/navigation stack.
            // It must restore a stable arbitrary picture-relative position
            // without selecting a thumbnail.
            scroller.ScrollToVerticalOffset(2800);
            Flush(window);
            var markedOffset = scroller.VerticalOffset;
            remembered.IsChecked = true;
            Flush(window);
            if (!returnToRemembered.IsEnabled || remembered.IsChecked != true ||
                remembered.Template.FindName("ToggleBorder", remembered) is not Border markBorder ||
                markBorder.BorderBrush is not SolidColorBrush blueBorder ||
                blueBorder.Color != ((SolidColorBrush)app.Resources["PcasaAccentBrush"]).Color)
                throw new Exception("Marked position does not show blue toggle state.");
            scroller.ScrollToTop();
            Flush(window);
            if (returnToDeparture.IsEnabled)
                throw new Exception("Back to departure must be disabled before jumping to bookmark.");
            var departureOffset = scroller.VerticalOffset;
            ClickSyntheticGalleryButton(returnToRemembered);
            if (Math.Abs(scroller.VerticalOffset - markedOffset) > 3 ||
                !returnToDeparture.IsEnabled)
                throw new Exception("Bookmark jump failed or did not enable return to departure.");
            ClickSyntheticGalleryButton(returnToDeparture);
            if (Math.Abs(scroller.VerticalOffset - departureOffset) > 3 ||
                returnToDeparture.IsEnabled || !returnToRemembered.IsEnabled ||
                remembered.IsChecked != true)
                throw new Exception("Returning to departure failed to restore the origin or consumed bookmark.");

            // A repeated jump must take the NEW departure, not use the old
            // one or open an unbounded navigation stack.
            scroller.ScrollToVerticalOffset(1530);
            Flush(window);
            var secondOrigin = scroller.VerticalOffset;
            ClickSyntheticGalleryButton(returnToRemembered);
            if (!returnToDeparture.IsEnabled ||
                Math.Abs(scroller.VerticalOffset - markedOffset) > 3)
                throw new Exception("Second bookmark jump did not capture a new departure.");
            ClickSyntheticGalleryButton(returnToDeparture);
            if (Math.Abs(scroller.VerticalOffset - secondOrigin) > 3 ||
                returnToDeparture.IsEnabled)
                throw new Exception("Return used stale departure instead of second origin.");

            remembered.IsChecked = false;
            Flush(window);
            if (returnToRemembered.IsEnabled || returnToDeparture.IsEnabled)
                throw new Exception("Clearing bookmark should disable both return actions.");
            Console.WriteLine("PASS: Bookmark jump and single-use return correctly restore both origin positions.");

            // Real WPF buttons must scroll within the gallery surface, not
            // mutate folder tree selection or move the viewer.
            void Navigate(string action)
            {
                var button = (Button)window.FindName(
                    "GalleryNavigate" + action + "Button")!;
                ClickSyntheticGalleryButton(button);
            }
            Navigate("GalleryEnd");
            if (scroller.VerticalOffset < 100)
                throw new Exception("Gallery end button did not navigate.");
            Navigate("PreviousFolder");
            if (Math.Abs(scroller.VerticalOffset - surface.GetFolderOffset(100)!.Value) > 3)
                throw new Exception("Previous folder button did not navigate to folder start.");
            Navigate("NextFolder");
            if (Math.Abs(scroller.VerticalOffset - surface.GetFolderOffset(200)!.Value) > 3)
                throw new Exception("Next folder button did not navigate to folder start.");
            Navigate("FolderEnd");
            if (Math.Abs(scroller.VerticalOffset - surface.GetFolderTrailingOffset(
                    200, scroller.ViewportHeight)!.Value) > 3)
                throw new Exception("Folder end button did not land on last thumbnail rows.");
            Navigate("FolderStart");
            if (Math.Abs(scroller.VerticalOffset - surface.GetFolderOffset(200)!.Value) > 3)
                throw new Exception("Folder start button did not land on the folder header.");
            Navigate("GalleryStart");
            if (scroller.VerticalOffset > 3)
                throw new Exception("Gallery start button did not land at global beginning.");
            Console.WriteLine("PASS: Six icon navigation commands retain exact gallery/folder destinations.");
            // Regression guard: these handlers must never await SQLite or a
            // scheduler-backed refresh before applying a virtual jump.
            foreach (var name in new[]
            {
                "GalleryNavigate_Click", "GalleryReturnToRemembered_Click",
                "GalleryReturnToDeparture_Click"
            })
            {
                var handler = typeof(PCasa.App.MainWindow).GetMethod(name,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!;
                if (handler.GetCustomAttributes(
                        typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute),
                        inherit: false).Length != 0)
                    throw new Exception(
                        name + " awaited background catalog work before navigation.");
            }
            Console.WriteLine("PASS: Gallery and bookmark jumps do not await catalog refresh.");
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

            // Continuous user scroll must own the published viewport. A
            // scanner notification must never insert/reflow thumbnails under
            // the cursor while the user is moving. Use real WPF scroll offsets
            // and the same auto-publication gate as the background scanner.
            var scrollFlags = System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var scrollType = typeof(PCasa.App.MainWindow);
            var scrollMark = scrollType.GetMethod(
                "RegisterGalleryUserScroll", scrollFlags)!;
            var scrollDefer = scrollType.GetMethod(
                "DeferScanGalleryPublication", scrollFlags)!;
            var scanDirtyField = scrollType.GetField(
                "_scanGalleryPublicationDeferred", scrollFlags)!;
            var scannerNotification = scrollType.GetMethod(
                "OnCatalogImageIndexed", scrollFlags)!;
            var folderBeforeScroll = scrollType.GetField(
                "_activePhotoFolderId", scrollFlags)!;
            var activeFolderBeforeScroll = folderBeforeScroll.GetValue(window);
            var tierForScanner = scrollType.GetField(
                "_activeBrowseTier", scrollFlags)!;
            var previousTierForScanner = tierForScanner.GetValue(window);
            var browseInitializedField = scrollType.GetField(
                "_browseInitialized", scrollFlags)!;
            var wasBrowseInitialized = beforeSyntheticBrowseInitialized;
            // Restore production notification handling for the scanner
            // deferral checks, after the isolated bookmark/navigation tests.
            // Those checks intentionally exercise REAL OnCatalogImageIndexed,
            // rather than the disabled fixture-only catalogue publisher.
            browseInitializedField.SetValue(window, wasBrowseInitialized);
            try
            {
                folderBeforeScroll.SetValue(window, (long?)100);
                tierForScanner.SetValue(window, (int?)192);
                for (var step = 0; step < 24; step++)
                {
                    scroller.ScrollToVerticalOffset(2200 + (step % 7) * 60);
                    scrollMark.Invoke(window, null);
                    // The rendered image list must not be replaced by a
                    // newly indexed file while this scroll burst is active.
                    scannerNotification.Invoke(window,
                        new object[] { @"C:\synthetic\pending-photo.jpg", 100L });
                }
                Flush(window);
                var scanHeld = (bool)scanDirtyField.GetValue(window)!;
                var stationaryAnchor = surface.CaptureImageAnchor(scroller.VerticalOffset);
                if (!scanHeld || stationaryAnchor is null ||
                    (bool)scrollDefer.Invoke(window, new object?[] { 100L })! != true)
                    throw new Exception("Automatic scan publication not held in gallery interior.");

                // The user has STOPPED scrolling: scanner UI notifications
                // must continue to leave this published view untouched.
                // Build #901 incorrectly flushed after 500 ms of inactivity.
                for (var image = 0; image < 12; image++)
                {
                    scannerNotification.Invoke(window,
                        new object[] { $@"C:\synthetic\stationary-{image}.jpg", 100L });
                    Flush(window);
                    if (!(bool)scanDirtyField.GetValue(window)! ||
                        surface.CaptureImageAnchor(scroller.VerticalOffset) != stationaryAnchor)
                        throw new Exception("Stationary viewport shifted after a scanner image.");
                }
                // This deliberately spans the old 500-ms idle flush.
                // A stopped user must never see a delayed scan reflow.
                System.Threading.Thread.Sleep(850);
                Flush(window);
                if (!(bool)scanDirtyField.GetValue(window)! ||
                    surface.CaptureImageAnchor(scroller.VerticalOffset) != stationaryAnchor)
                    throw new Exception("Idle scanner timer displaced stationary gallery.");
                var refresh = scrollType.GetMethod(
                    "RefreshBrowseGalleryAsync", scrollFlags)!;
                var deferredTask = (Task)refresh.Invoke(window,
                    new object[] { true, false, true })!;
                if (!deferredTask.IsCompleted ||
                    !(bool)scanDirtyField.GetValue(window)!)
                    throw new Exception("Scanner refresh bypassed stable middle viewport.");
                Console.WriteLine(
                    "PASS: Scan commits never reflow a stationary middle viewport.");

                // The NEXT folder's first row is live even with the
                // previous folder still visible above it. Its section is
                // not the global ScrollViewer beginning.
                var secondStart = surface.GetFolderOffset(200)!.Value;
                // The previous synthetic scroll test deliberately left a
                // deferred full refresh. Clear it before navigating, or the
                // test harness would replace its own synthetic index with
                // the (empty) real test catalog on reaching this live edge.
                scanDirtyField.SetValue(window, false);
                // The preceding twelve fake scanner notifications schedule
                // the REAL 750ms catalog-publication timer. Its SQLite
                // catalog is empty and must not overwrite this synthetic
                // 480-photo viewport while checking the LIVE second-folder
                // edge. Quiesce only that fixture-generated publication;
                // the ScrollViewer and DeferScanGalleryPublication code
                // under test are unchanged.
                var syntheticTimer = (DispatcherTimer?)scrollType.GetField(
                    "_catalogPublicationTimer", scrollFlags)!.GetValue(window);
                syntheticTimer?.Stop();
                scrollType.GetField("_catalogPublicationPending", scrollFlags)!
                    .SetValue(window, 0);
                // A timer tick may ALREADY have dispatched an async catalog
                // snapshot before Stop() ran. The synthetic images are not
                // in the real (empty) test database. Invalidate its request,
                // drain any in-flight publisher and then restore the fixture
                // before checking the real ScrollViewer's folder edge.
                // Never relax the folder-edge assertions themselves.
                browseInitializedField.SetValue(window, false);
                ((LatestViewRequest)scrollType.GetField(
                    "_browseRefreshRequests", scrollFlags)!.GetValue(window)!)
                    .Invalidate();
                var edgePublisherField = scrollType.GetField(
                    "_galleryRefreshCompletion", scrollFlags)!;
                for (var retry = 0; retry < 100; retry++)
                {
                    Flush(window);
                    if (edgePublisherField.GetValue(window) is not Task pending ||
                        pending.IsCompleted)
                        break;
                    System.Threading.Thread.Sleep(20);
                }
                if (edgePublisherField.GetValue(window) is Task edgePublisher &&
                    !edgePublisher.IsCompleted)
                    throw new Exception(
                        "Catalog publisher did not settle before live folder-edge probe.");
                syntheticTimer?.Stop();
                scrollType.GetField("_catalogPublicationPending", scrollFlags)!
                    .SetValue(window, 0);
                pages.SetIndex(synthetic);
                surface.SetData(synthetic, groups, pages);
                Flush(window);
                if (Math.Abs((surface.GetFolderOffset(200) ?? -1) - secondStart) > 1)
                    throw new Exception(
                        "Synthetic second-folder offset changed before live-edge probe.");
                scroller.ScrollToVerticalOffset(secondStart - 60);
                Flush(window);
                if (scroller.VerticalOffset <= 2 ||
                    surface.GetLiveFolderEdge(
                        scroller.VerticalOffset, scroller.ViewportHeight, 200)
                        is not { AtStart: true } ||
                    (bool)scrollDefer.Invoke(window, new object?[] { 200L })!)
                    throw new Exception(
                        $"Visible non-global folder start was not live: " +
                        $"Start={secondStart:F1}, Offset={scroller.VerticalOffset:F1}, " +
                        $"Viewport={scroller.ViewportHeight:F1}, " +
                        $"Edge={surface.GetLiveFolderEdge(scroller.VerticalOffset, scroller.ViewportHeight, 200)}, " +
                        $"Deferred={scanDirtyField.GetValue(window)}");

                // The previous check deliberately visits a LIVE folder edge.
                // There, pending scanner work may legally refresh from the
                // runner's real (empty) catalog, replacing our synthetic
                // gallery. Start the separate interior-policy assertion with
                // its synthetic fixture again; never interpret an empty
                // real catalog as a failure of the interior boundary rule.
                // This next test uses SYNTHETIC rows that do not exist in
                // the runner's empty SQLite catalog. The preceding live-edge
                // probe can legitimately schedule a real catalog refresh.
                // Disable real catalog publication ONLY for this synthetic
                // test phase (not in PCasa's production code), and invalidate
                // any already in-flight async read. Without BOTH guards a
                // late empty-catalog result races with SetData(synthetic)
                // and incorrectly makes this old smoke fixture disappear.
                browseInitializedField.SetValue(window, false);
                ((LatestViewRequest)scrollType.GetField(
                    "_browseRefreshRequests", scrollFlags)!.GetValue(window)!)
                    .Invalidate();

                // Drain the existing real catalog publisher before placing
                // synthetic data back into the WPF gallery surface.
                var publisherField = scrollType.GetField(
                    "_galleryRefreshCompletion", scrollFlags)!;
                for (var retry = 0; retry < 100; retry++)
                {
                    Flush(window);
                    if (publisherField.GetValue(window) is not Task active ||
                        active.IsCompleted)
                        break;
                    System.Threading.Thread.Sleep(20);
                }
                if (publisherField.GetValue(window) is Task pendingPublisher &&
                    !pendingPublisher.IsCompleted)
                    throw new Exception("Catalog publisher did not settle before isolated gallery test.");
                // The synthetic gallery is not a real database snapshot.
                // Prevent a previously scheduled 750ms catalog-timer tick
                // from publishing the empty test database over that fixture.
                var fixtureCatalogTimer = (DispatcherTimer?)scrollType.GetField(
                    "_catalogPublicationTimer", scrollFlags)!.GetValue(window);
                fixtureCatalogTimer?.Stop();
                scrollType.GetField("_catalogPublicationPending", scrollFlags)!
                    .SetValue(window, 0);
                scanDirtyField.SetValue(window, false);
                pages.SetIndex(synthetic);
                surface.SetData(synthetic, groups, pages);
                Flush(window);
                if (surface.GetFolderOffset(100) is null ||
                    surface.GetFolderOffset(200) is null)
                    throw new Exception("Synthetic gallery fixture not restored before interior test.");
                // No first/last row is visible deep inside the large first
                // folder. Null here means INTERIOR, not an empty gallery.
                scroller.ScrollToVerticalOffset(2200);
                Flush(window);
                var interiorEdge = surface.GetLiveFolderEdge(
                    scroller.VerticalOffset, scroller.ViewportHeight, 100);
                var interiorDeferred = (bool)scrollDefer.Invoke(
                    window, new object?[] { 100L })!;
                if (interiorEdge is not null || !interiorDeferred)
                    throw new Exception(
                        $"Folder interior was mistaken for an empty/live gallery: " +
                        $"Offset={scroller.VerticalOffset:F1}, Viewport={scroller.ViewportHeight:F1}, " +
                        $"FolderStart={surface.GetFolderOffset(100):F1}, " +
                        $"FolderEnd={surface.GetFolderBottomOffset(100):F1}, " +
                        $"Edge={interiorEdge}, Deferred={interiorDeferred}");

                // Top and bottom remain live. Do not flush the synthetic
                // index into the actual empty runner database during this
                // policy test; only evaluate the publication gate.
                scanDirtyField.SetValue(window, false);
                scroller.ScrollToTop();
                Flush(window);
                if ((bool)scrollDefer.Invoke(window, new object?[] { 100L })!)
                    throw new Exception("First folder's own start unexpectedly froze publication.");
                scroller.ScrollToBottom();
                Flush(window);
                if ((bool)scrollDefer.Invoke(window, new object?[] { 200L })!)
                    throw new Exception("Last folder's own end unexpectedly froze publication.");
                Console.WriteLine("PASS: Gallery first and last rows remain live during scans.");

                // A foreign-folder commit must NOT disrupt an unrelated
                // viewport, even when that viewport is at a GLOBAL edge.
                // The changed folder's own edge is the only live boundary.
                var catalogPending = scrollType.GetField(
                    "_catalogPublicationPending", scrollFlags)!;
                var catalogTimer = scrollType.GetField(
                    "_catalogPublicationTimer", scrollFlags)!;
                foreach (var atStart in new[] { true, false })
                {
                    catalogPending.SetValue(window, 0);
                    scanDirtyField.SetValue(window, false);
                    if (atStart) scroller.ScrollToTop();
                    else scroller.ScrollToBottom();
                    Flush(window);
                    var edge = surface.GetLiveFolderEdge(
                        scroller.VerticalOffset, scroller.ViewportHeight)
                        ?? throw new Exception("Global edge has no source folder.");
                    var incomingOtherFolder = edge.FolderId == 100L ? 200L : 100L;
                    if ((bool)scrollDefer.Invoke(window,
                            new object?[] { edge.FolderId })!)
                        throw new Exception("Current folder's own edge was frozen.");
                    // The isolated fixture disables the REAL catalog
                    // publisher while synthetic rows are installed. But
                    // OnCatalogImageIndexed deliberately ignores notifications
                    // until the browse gallery is initialized. Temporarily
                    // enable that normal notification gate while exercising
                    // a FOREIGN-folder event: its own defer rule prevents any
                    // catalog read, so the synthetic index stays intact.
                    // Otherwise this test would assert on an event that the
                    // harness silently discarded itself.
                    try
                    {
                        browseInitializedField.SetValue(window, true);
                        scannerNotification.Invoke(window, new object[] {
                            atStart ? @"C:\synthetic\other-folder-top.jpg" :
                                @"C:\synthetic\other-folder-bottom.jpg",
                            incomingOtherFolder });
                        Flush(window);
                    }
                    finally
                    {
                        browseInitializedField.SetValue(window, false);
                    }
                    var queued = (int)catalogPending.GetValue(window)!;
                    var deferred = (bool)scanDirtyField.GetValue(window)!;
                    if (queued != 0 || !deferred)
                        throw new Exception(
                            $"Foreign image did not preserve current folder: " +
                            $"AtStart={atStart}; Queued={queued}; Deferred={deferred}.");
                    ((DispatcherTimer?)catalogTimer.GetValue(window))?.Stop();
                    catalogPending.SetValue(window, 0);
                }
                Console.WriteLine(
                    "PASS: Own folder edges are live; foreign-folder scans remain deferred.");
            }
            finally
            {
                scanDirtyField.SetValue(window, false);
                folderBeforeScroll.SetValue(window, activeFolderBeforeScroll);
                tierForScanner.SetValue(window, previousTierForScanner);
                scroller.ScrollToVerticalOffset(2800);
                Flush(window);
                // End of synthetic scanner publication policy probe. The
                // normal gallery publisher is available again afterwards.
                browseInitializedField.SetValue(window, wasBrowseInitialized);
            }

            // Regression: a background import inserts entire rows while the
            // image viewer/GPS sidebar is open. Closing must recover the SAME
            // photo identity, not the now-stale opening pixel coordinate.
            var returnFlags = System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var returnType = typeof(PCasa.App.MainWindow);
            var returnFocus = (GallerySelection)returnType.GetField(
                "_gallerySelection", returnFlags)!.GetValue(window)!;
            var displayedField = returnType.GetField(
                "_displayedBrowseIndex", returnFlags)!;
            var openingAnchorField = returnType.GetField(
                "_viewerOpeningGalleryAnchor", returnFlags)!;
            var openingFocusField = returnType.GetField(
                "_viewerOpeningFocusedImageId", returnFlags)!;
            var openingOffsetField = returnType.GetField(
                "_viewerOpeningScrollOffset", returnFlags)!;
            var viewerSequenceField = returnType.GetField(
                "_viewerSequence", returnFlags)!;
            var viewerIndexField = returnType.GetField(
                "_viewerCurrentIndex", returnFlags)!;
            var viewerActiveField = returnType.GetField(
                "_viewerOpen", returnFlags)!;
            var returnQueue = returnType.GetMethod(
                "QueueGalleryAnchorRestore", returnFlags, null, Type.EmptyTypes, null)!;
            var closeViewer = returnType.GetMethod("ClosePhotoViewer", returnFlags)!;
            var originalDisplayed = displayedField.GetValue(window);
            var returnSettings = PhotoViewerSettings.Load();
            var savedReturnPosition = returnSettings.ReturnPosition;
            var inserted = Enumerable.Range(0, 80)
                .Select(i => new BrowseThumbnailIndexEntry(9000 + i,
                    100, 192, "no-image.pack", 0, 1, 180, 120))
                .ToArray();
            var expandedIndex = inserted.Concat(synthetic).ToArray();
            var expandedGroups = new[] {
                new PhotoFolderSummary(100, 1, @"C:\synthetic\first",
                    @"C:\synthetic\first", "First", 320),
                groups[1]
            };
            try
            {
                foreach (var mode in new[] {
                    ViewerReturnPosition.OpeningPosition,
                    ViewerReturnPosition.CurrentImage
                })
                {
                    pages.SetIndex(synthetic);
                    surface.SetData(synthetic, groups, pages);
                    Flush(window);
                    scroller.ScrollToVerticalOffset(2800);
                    Flush(window);
                    var startingAnchor = surface.CaptureImageAnchor(
                        scroller.VerticalOffset)
                        ?? throw new Exception("No viewer opening anchor.");
                    var savedOffset = scroller.VerticalOffset;
                    var viewedId = mode == ViewerReturnPosition.CurrentImage
                        ? synthetic[300].ImageId : startingAnchor.ImageId;
                    var sequence = PhotoSequence.Create(synthetic,
                        new HashSet<long>(), viewedId, PhotoSequenceScope.Gallery);
                    returnSettings.ReturnPosition = mode;
                    returnSettings.Save();
                    returnFocus.SetFocus(startingAnchor.ImageId);
                    displayedField.SetValue(window, synthetic);
                    openingAnchorField.SetValue(window, startingAnchor);
                    openingFocusField.SetValue(window, (long?)startingAnchor.ImageId);
                    openingOffsetField.SetValue(window, savedOffset);
                    viewerSequenceField.SetValue(window, sequence);
                    viewerIndexField.SetValue(window, sequence.IndexOfImage(viewedId));
                    viewerActiveField.SetValue(window, true);
                    returnQueue.Invoke(window, null);
                    pages.SetIndex(expandedIndex);
                    surface.SetData(expandedIndex, expandedGroups, pages);
                    displayedField.SetValue(window, expandedIndex);
                    Flush(window);
                    // Simulate the old pixel coordinate being kept despite
                    // new rows. This is exactly what the former close did.
                    scroller.ScrollToVerticalOffset(savedOffset);
                    Flush(window);
                    closeViewer.Invoke(window, null);
                    Flush(window);
                    var expected = mode == ViewerReturnPosition.OpeningPosition
                        ? surface.GetImageAnchorOffset(
                            startingAnchor.ImageId, startingAnchor.RelativeY)
                        : surface.GetImageOffset(Array.FindIndex(expandedIndex,
                            item => item.ImageId == viewedId));
                    if (expected is null ||
                        Math.Abs(scroller.VerticalOffset - expected.Value) > 3 ||
                        returnFocus.FocusedId != (mode == ViewerReturnPosition.CurrentImage
                            ? viewedId : startingAnchor.ImageId))
                        throw new Exception("Viewer return lost stable image focus " +
                            "after an in-flight catalog insertion (" + mode + ").");
                }
                Console.WriteLine(
                    "PASS: Viewer/GPS return keeps focused image and anchor across live scan insertions.");
            }
            finally
            {
                returnSettings.ReturnPosition = savedReturnPosition;
                returnSettings.Save();
                viewerActiveField.SetValue(window, false);
                viewerSequenceField.SetValue(window, null);
                viewerIndexField.SetValue(window, -1);
                openingAnchorField.SetValue(window, null);
                openingFocusField.SetValue(window, null);
                displayedField.SetValue(window, originalDisplayed);
                pages.SetIndex(synthetic);
                surface.SetData(synthetic, groups, pages);
                Flush(window);
                var originalTop = surface.GetImageAnchorOffset(
                    anchored.ImageId, anchored.RelativeY);
                if (originalTop is double top)
                    scroller.ScrollToVerticalOffset(top);
                Flush(window);
            }

            var sidebar = (FrameworkElement?)window.FindName("InfoSidebarPanel")
                ?? throw new Exception("Sidebar not found.");
            var tagsPanel = (FrameworkElement?)window.FindName("TagSidebar")
                ?? throw new Exception("Tags panel not found.");
            var infoPanel = (FrameworkElement?)window.FindName("GeneralSidebarScroll")
                ?? throw new Exception("Info panel not found.");
            var gpsPanel = (FrameworkElement?)window.FindName("GpsMapPanel")
                ?? throw new Exception("GPS map panel not found.");

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
                    infoPanel.Visibility != (mode == "Info"
                        ? Visibility.Visible : Visibility.Collapsed) ||
                    gpsPanel.Visibility != (mode == "GPS"
                        ? Visibility.Visible : Visibility.Collapsed))
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
            var infoGpsCopy = window.FindName("InfoCopyGpsButton") as Button
                ?? throw new Exception("Image Info GPS copy button missing.");
            if (infoGpsCopy.Visibility != Visibility.Collapsed)
                throw new Exception("GPS copy offered without resolved Info coordinates.");
            var googleButton = window.FindName("GpsGoogleButton") as Button
                ?? throw new Exception("Google Maps button missing.");
            if (googleButton.Content is not StackPanel ||
                !((StackPanel)googleButton.Content).Children.OfType<TextBlock>()
                    .Any(text => text.Name == "GpsGoogleButtonCaption"))
                throw new Exception("Google Maps button icon/caption were replaced.");
            var formatGps = typeof(PCasa.App.MainWindow).GetMethod(
                "FormatGpsCoordinates", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
                ?? throw new Exception("Shared GPS formatter missing.");
            var formattedGps = formatGps.Invoke(null, new object[] { 53.502345d, 10.123456d });
            if (!Equals(formattedGps, "53.502345, 10.123456"))
                throw new Exception("Info and map must copy the same invariant GPS coordinates.");
            var mapsUrl = typeof(PCasa.App.MainWindow).GetMethod(
                "BuildGoogleMapsWebsiteUrl", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
                ?? throw new Exception("Google Maps coordinate URL builder missing.");
            var googleUrl = mapsUrl.Invoke(null, new object[] { 53.502345d, 10.123456d }) as string;
            if (googleUrl is null || !googleUrl.StartsWith(
                    "https://www.google.com/maps/search/?api=1&query=", StringComparison.Ordinal) ||
                !Uri.UnescapeDataString(googleUrl).Contains("53.502345,10.123456"))
                throw new Exception("Google Maps URL does not address the current GPS point.");
            Console.WriteLine("PASS: Info GPS-copy control, shared coordinate format and external Maps URL.");
            // Three labeled groups are deliberately allowed to wrap on narrow sidebars.
            var gpsControls = window.FindName("GpsControlsPanel") as Grid
                ?? throw new Exception("GPS controls grid missing.");
            var gpsHeader = window.FindName("InfoSidebarHeader") as Grid
                ?? throw new Exception("GPS header must be a grid.");
            var gpsCoords = window.FindName("GpsCoordinatesText") as TextBlock
                ?? throw new Exception("GPS coordinates missing.");
            var gpsActions = window.FindName("GpsEditPanel") as WrapPanel
                ?? throw new Exception("GPS actions must wrap instead of clipping.");
            var headerCopy = window.FindName("GpsCopyButton") as Button
                ?? throw new Exception("Copy GPS icon missing next to coordinates.");
            var gpxEmpty = window.FindName("GpsGpxEmptyText") as TextBlock
                ?? throw new Exception("GPX empty-folder message missing.");
            if (gpsCoords.Parent is not StackPanel coordinateGroup ||
                Grid.GetColumn(coordinateGroup) != 1 ||
                !coordinateGroup.Children.Contains(headerCopy) ||
                gpsControls.RowDefinitions.Count != 8 ||
                Grid.GetRow(gpsActions) != 2 ||
                gpsActions.Visibility != Visibility.Visible ||
                gpsControls.HorizontalAlignment != HorizontalAlignment.Stretch ||
                gpsHeader.HorizontalAlignment != HorizontalAlignment.Stretch ||
                !Equals(gpxEmpty.Text, "Keine GPX-Dateien.") ||
                headerCopy.IsEnabled)
                throw new Exception("GPS grouped controls or coordinate copy are inconsistent.");
            var gpsPin = window.FindName("GpsFilmstripFreezeToggle") as ToggleButton;
            var gpsTransfer = window.FindName("GpsFilmstripToGalleryButton") as Button;
            var gpsReplace = window.FindName("GpsReplaceButton") as Button;
            var gpsRemove = window.FindName("GpsRemoveButton") as Button;
            var gpsStrip = window.FindName("GpsFilmstripList") as ListBox;
            var rememberedJump = window.FindName("GalleryReturnToRememberedButton") as Button;
            var departureJump = window.FindName("GalleryReturnToDepartureButton") as Button;
            if (gpsPin is null || gpsTransfer is null ||
                gpsReplace is null || gpsRemove is null || gpsStrip is null ||
                rememberedJump is null || departureJump is null ||
                window.FindName("GpsApplyMissingButton") is not null ||
                window.FindName("GpsFilmstripClearButton") is not null ||
                gpsStrip.SelectionMode != SelectionMode.Extended ||
                !gpsActions.Children.Contains(gpsReplace) ||
                !gpsActions.Children.Contains(gpsRemove) ||
                Grid.GetRow(gpsStrip) != 1 ||
                !ToolTipService.GetShowOnDisabled(gpsReplace) ||
                !ToolTipService.GetShowOnDisabled(gpsRemove) ||
                !ToolTipService.GetShowOnDisabled(gpsTransfer) ||
                !ToolTipService.GetShowOnDisabled(rememberedJump) ||
                !ToolTipService.GetShowOnDisabled(departureJump))
                throw new Exception("GPS filmstrip actions or disabled-button tooltips are missing.");
            Console.WriteLine("PASS: GPS filmstrip actions and disabled-button explanations are present.");
            Console.WriteLine("PASS: GPS edit/source/view grouping, responsive buttons and header coordinate copy.");


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

                    // The preceding test deliberately opened a running
                    // scan and exercised taskbar restore. End that scan
                    // explicitly before testing a merely QUEUED cache pass;
                    // a real running NAS scan must never be hidden by it.
                    progressWindow.EndScanPhase();
                    // A queued background cache pass must not display a
                    // permanent 'Vorschaudaten werden vorbereitet 0/0'.
                    progressWindow.BeginCachePhase();
                    Flush(window);
                    if (progressWindow.IsVisible)
                        throw new Exception(
                            "Idle cache preparation left a visible 0/0 progress window.");
                    progressWindow.Update(new BrowseCacheBuildProgress(
                        3, 1, 0, "image.jpg", null));
                    Flush(window);
                    if (!progressWindow.IsVisible)
                        throw new Exception(
                            "Actual cache progress did not restore the progress window.");
                    Console.WriteLine(
                        "PASS: Cache progress remains hidden until real work produces a result.");
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

            // Manager shows the full physical tree on demand, including
            // non-indexed directories, without reading the entire NAS at launch.
            // Known indexed folders stay visible even while disconnected.
            var fixtureDirectory = Path.Combine(Path.GetTempPath(),
                "PCasa-ManagerForeground-" + Guid.NewGuid().ToString("N"));
            var fixtureChild = Path.Combine(fixtureDirectory, "NeverIndexed");
            var cataloguedChild = Path.Combine(fixtureDirectory,
                "AlreadyIndexed", "2026");
            Directory.CreateDirectory(fixtureChild);
            PCasa.App.FolderManagerWindow? manager = null;
            try
            {
                var fixture = new MonitoredFolder
                {
                    Id = 100,
                    LocalPath = fixtureDirectory,
                    CanonicalPath = fixtureDirectory
                };
                manager = new PCasa.App.FolderManagerWindow(
                    new[] { (Folder: fixture,
                        ExcludedPaths: (IReadOnlyList<string>)Array.Empty<string>()) },
                    new ForegroundReadService(), serverDatabaseConfigured: false,
                    catalogPhotoFolders:
                    [
                        new PhotoFolderSummary(
                            -200, fixture.Id, cataloguedChild,
                            cataloguedChild, "2026", 37)
                    ])
                {
                    Owner = window
                };
                manager.Show();
                Flush(manager);
                var managedTree = (TreeView?)manager.FindName("FolderManagerTree")
                    ?? throw new Exception("Folder manager tree missing.");
                var galleryFolderTree = (TreeView?)window.FindName("FoldersTree")
                    ?? throw new Exception("Gallery folder tree missing.");
                if (ToolTipService.GetIsEnabled(managedTree) ||
                    ToolTipService.GetIsEnabled(galleryFolderTree))
                    throw new Exception("Folder tree still allows hover tooltips.");
                static string? ManagerPath(TreeViewItem item)
                    => item.Tag?.GetType().GetProperty("LocalPath")
                        ?.GetValue(item.Tag) as string;
                static IEnumerable<TreeViewItem> NestedManagerItems(ItemsControl root)
                {
                    foreach (var child in root.Items.OfType<TreeViewItem>())
                    {
                        yield return child;
                        foreach (var grandchild in NestedManagerItems(child))
                            yield return grandchild;
                    }
                }
                var fixtureNode = NestedManagerItems(managedTree)
                    .Single(item => string.Equals(ManagerPath(item),
                        fixtureDirectory, StringComparison.OrdinalIgnoreCase));
                fixtureNode.IsExpanded = true;
                Flush(manager);
                Until(manager, () => NestedManagerItems(managedTree).Any(item =>
                        string.Equals(ManagerPath(item), fixtureChild,
                            StringComparison.OrdinalIgnoreCase)),
                    "Manager failed to enumerate physical non-indexed child.");
                var displayed = NestedManagerItems(managedTree)
                    .Where(item => item.Tag is not null).ToArray();
                if (displayed.Length == 0 ||
                    !displayed.Any(item => string.Equals(
                        ManagerPath(item), fixtureDirectory,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Monitored directory missing from manager.");
                var catalogItem = displayed.SingleOrDefault(item =>
                    string.Equals(ManagerPath(item), cataloguedChild,
                        StringComparison.OrdinalIgnoreCase));
                if (catalogItem is null ||
                    catalogItem.Header is not CheckBox catalogCheck ||
                    !catalogCheck.IsEnabled ||
                    catalogCheck.IsChecked != true ||
                    !Convert.ToString(catalogCheck.Content)!.Contains("37"))
                    throw new Exception(
                        "Catalogued child must have an enabled checkbox " +
                        "within its monitored parent.");
                if (!displayed.Any(item => string.Equals(
                        ManagerPath(item),
                        Path.GetDirectoryName(cataloguedChild),
                        StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Catalogued folder hierarchy missing intermediate node.");
                // Explicitly exclude/re-include a NEVER-indexed physical
                // child, without mutating catalog or creating a second root.
                var physical = displayed.Single(item =>
                    string.Equals(ManagerPath(item), fixtureChild,
                        StringComparison.OrdinalIgnoreCase));
                if (physical.Header is not CheckBox physicalCheck ||
                    !physicalCheck.IsEnabled || physicalCheck.IsChecked != true ||
                    physical.ToolTip is not null || physicalCheck.ToolTip is not null)
                    throw new Exception("Non-indexed folder has tooltip or no inclusion checkbox.");
                physicalCheck.IsChecked = false;
                Flush(manager);
                var rootSummaryCheckbox = (CheckBox)fixtureNode.Header;
                if (rootSummaryCheckbox.IsChecked is not null)
                    throw new Exception("Parent of excluded child must be partially checked.");
                var temporarilyExcluded = manager.SelectedConfiguration.Roots.Single();
                if (!temporarilyExcluded.ExcludedPaths.Any(path =>
                        string.Equals(path, fixtureChild, StringComparison.OrdinalIgnoreCase)) ||
                    Convert.ToString(physicalCheck.Content)!.StartsWith("⊘"))
                    throw new Exception("Excluded folder lost its rule or retained the redundant marker.");
                // A click on a gray box must mean "include ALL children",
                // never the WPF default null -> unchecked cycle.
                var toggle = typeof(PCasa.App.FolderManagerWindow).GetMethod(
                    "ToggleInclusionCheckbox", System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("Explicit checkbox click handling is missing.");
                toggle.Invoke(null, [rootSummaryCheckbox]);
                Flush(manager);
                if (rootSummaryCheckbox.IsChecked != true ||
                    physicalCheck.IsChecked != true ||
                    manager.SelectedConfiguration.Roots.Single().ExcludedPaths.Count != 0)
                    throw new Exception("Clicking mixed parent did not include the full subtree.");

                // Explicitly exclude an indexed parent but then include a
                // deeper child: both the broad root and the excluded parent
                // become gray, while the re-included grandchild stays checked.
                var ancestorPath = Path.GetDirectoryName(cataloguedChild)!;
                var catalogAncestor = displayed.Single(item =>
                    string.Equals(ManagerPath(item), ancestorPath,
                        StringComparison.OrdinalIgnoreCase));
                var catalogAncestorCheck = (CheckBox)catalogAncestor.Header;
                catalogAncestorCheck.IsChecked = false;
                Flush(manager);
                if (catalogCheck.IsChecked != false)
                    throw new Exception("Excluded parent did not exclude indexed descendants.");
                catalogCheck.IsChecked = true;
                Flush(manager);
                var nestedSelection = manager.SelectedConfiguration.Roots.Single();
                if (catalogAncestorCheck.IsChecked is not null ||
                    rootSummaryCheckbox.IsChecked is not null ||
                    catalogCheck.IsChecked != true ||
                    !nestedSelection.ExcludedPaths.Any(path =>
                        string.Equals(path, ancestorPath, StringComparison.OrdinalIgnoreCase)) ||
                    !nestedSelection.IncludedPaths!.Any(path =>
                        string.Equals(path, cataloguedChild, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception("Nested inclusion did not create partial parent states.");
                toggle.Invoke(null, [catalogAncestorCheck]);
                Flush(manager);
                if (catalogAncestorCheck.IsChecked != true ||
                    rootSummaryCheckbox.IsChecked != true ||
                    manager.SelectedConfiguration.Roots.Single().ExcludedPaths.Count != 0 ||
                    manager.SelectedConfiguration.Roots.Single().IncludedPaths!.Count != 0)
                    throw new Exception("Re-checking partial ancestor did not remove nested rules.");

                var configured = manager.SelectedConfiguration.Roots;
                if (configured.Count != 1 ||
                    !string.Equals(configured[0].LocalPath,
                        fixtureDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "Read-only catalog projection changed monitored-root configuration.");
                Console.WriteLine(
                    "PASS: Alpha.52 folder trees have no tooltips, mixed parent boxes and binary subtree clicks.");

                // Alpha.25: only the left tree owns the folder selection.
                // Expanding one level must not expand nested levels. No
                // catalogued child may be repeated as a right-side checkbox.
                var rightList = (StackPanel)manager.FindName("MonitoredRootsList");
                if (Descendants<CheckBox>(rightList).Any())
                    throw new Exception("Right pane still duplicates editable folder tree.");
                var fixtureAfter = NestedManagerItems(managedTree).Single(item =>
                    string.Equals(ManagerPath(item), fixtureDirectory,
                        StringComparison.OrdinalIgnoreCase));
                var intermediatePath = Path.GetDirectoryName(cataloguedChild)!;
                var intermediateItem = NestedManagerItems(fixtureAfter).Single(item =>
                    string.Equals(ManagerPath(item), intermediatePath,
                        StringComparison.OrdinalIgnoreCase));
                if (intermediateItem.IsExpanded)
                    throw new Exception("Expanding one tree level automatically opened its descendants.");
                // Alpha.52: the folder manager checkbox must never cycle
                // into a user-created indeterminate state on a second click.
                var oldRootCheck = (CheckBox)fixtureAfter.Header;
                oldRootCheck.IsChecked = false;
                Flush(manager);
                var childAfter = NestedManagerItems(managedTree).Single(item =>
                    string.Equals(ManagerPath(item), cataloguedChild,
                        StringComparison.OrdinalIgnoreCase));
                if (childAfter.Header is not CheckBox leftChild ||
                    !leftChild.IsEnabled || leftChild.IsChecked != false)
                    throw new Exception(
                        "Left tree did not allow choosing an indexed child after removing its broad root.");
                leftChild.IsChecked = true;
                Flush(manager);
                var narrowedRoots = manager.SelectedConfiguration.Roots;
                if (narrowedRoots.Count != 1 ||
                    !string.Equals(narrowedRoots[0].LocalPath,
                        cataloguedChild, StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "Left-tree selection failed to replace the root without rescanning.");
                Console.WriteLine(
                    "PASS: One-level expansion and left-tree-only catalog-preserving root replacement.");
            }
            finally
            {
                manager?.Close();
                try { Directory.Delete(fixtureDirectory, recursive: true); }
                catch (IOException) { }
            }

            // A fullscreen round trip is a change of viewport, not an
            // implicit Fit->percentage conversion. Use a synthetic image
            // description and the real WPF viewer windows, without file IO.
            var viewerType = typeof(PCasa.App.MainWindow);
            var reflectFlags = System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var viewerOpenField = viewerType.GetField("_viewerOpen", reflectFlags)!;
            var viewerInfoField = viewerType.GetField("_viewerImageInfo", reflectFlags)!;
            var viewerRefinedField = viewerType.GetField("_viewerHasOriginal", reflectFlags)!;
            var previousRefined = viewerRefinedField.GetValue(window);
            // The synthetic photo is not in the catalog. Isolate its
            // viewer state from any real photo sequence created by an
            // earlier smoke fixture: delayed Fit refinement must not
            // start ShowViewerImage and clear the synthetic image info.
            var viewerSequenceForFixture = viewerType.GetField("_viewerSequence", reflectFlags)!;
            var previousViewerSequence = viewerSequenceForFixture.GetValue(window);
            var fitField = viewerType.GetField("_viewerFitToScreen", reflectFlags)!;
            var zoomField = viewerType.GetField("_viewerZoomPercent", reflectFlags)!;
            var fullscreenField = viewerType.GetField("_viewerFullscreenWindow", reflectFlags)!;
            var toggleFullscreen = viewerType.GetMethod("ViewerToggleFullscreen", reflectFlags)!;
            var leaveFullscreen = viewerType.GetMethod("LeaveViewerFullscreen", reflectFlags)!;
            var applyLayout = viewerType.GetMethod("ApplyViewerImageLayout", reflectFlags)!;
            var panel = (FrameworkElement?)window.FindName("PhotoViewerPanel")
                ?? throw new Exception("Viewer panel missing.");
            var viewerScroller = (ScrollViewer?)window.FindName("PhotoViewerScroller")
                ?? throw new Exception("Viewer scroller missing.");
            var viewerImage = (Image?)window.FindName("PhotoViewerImage")
                ?? throw new Exception("Viewer image missing.");
            var viewerPanelGrid = (Grid)panel;
            var comparisonPanel = (Grid?)window.FindName("ComparisonPanel")
                ?? throw new Exception("Comparison panel missing.");
            var galleryViewerBackground = ((ScrollViewer)window.FindName("BrowseScrollViewer")!).Background;
            if (!ReferenceEquals(viewerPanelGrid.Background, galleryViewerBackground) ||
                !ReferenceEquals(comparisonPanel.Background, galleryViewerBackground))
                throw new Exception("Viewer and comparison empty areas must use the real gallery background.");
            Console.WriteLine("PASS: Viewer and comparison use gallery background without fixed black bars.");
            try
            {
                viewerSequenceForFixture.SetValue(window, null);
                viewerOpenField.SetValue(window, true);
                viewerRefinedField.SetValue(window, true); // Synthetic bitmap represents a completed refined decode.
                viewerInfoField.SetValue(window, new PhotoViewerImageInfo(
                    999, @"C:\synthetic\no-file.jpg", "no-file.jpg", 4000, 1000));
                panel.Visibility = Visibility.Visible;
                // Alpha.27: every viewer exit uses ConfirmCropExit, which must
                // NEVER block when a durable image-save recipe is running.
                // The tested synthetic photo needs no real source file.
                var editPending = viewerType.GetField("_cropSavePending", reflectFlags)!;
                var confirmEditorExit = viewerType.GetMethod("ConfirmCropExit", reflectFlags)!;
                editPending.SetValue(window, true);
                try
                {
                    if (!(bool)confirmEditorExit.Invoke(window, null)!)
                        throw new Exception(
                            "Gallery navigation is blocked by an in-flight image save.");
                }
                finally { editPending.SetValue(window, false); }
                Console.WriteLine(
                    "PASS: Save-as-copy does not block viewer-to-gallery navigation.");
                Flush(window);

                // A real before/after is allowed only for an UNSAVED
                // changed editing session. An Undo badge or a previous
                // external session must never enable this toolbar.
                var editStateFixture = viewerType.GetField(
                    "_photoEditSession", reflectFlags)!;
                var oldEditSessionFixture = editStateFixture.GetValue(window);
                var oldViewerInfo = viewerInfoField.GetValue(window);
                var oldViewerPixels = viewerImage.Source;
                var originalData = new byte[120 * 80 * 4];
                for (var i = 0; i < originalData.Length; i += 4)
                {
                    originalData[i] = originalData[i + 1] = originalData[i + 2] = (byte)(32 + (i / 4) % 192);
                    originalData[i + 3] = 255;
                }
                var originalPixels = BitmapSource.Create(120, 80, 96, 96,
                    PixelFormats.Bgra32, null, originalData, 120 * 4);
                bool SameComparisonPixels(ImageSource? left, ImageSource? right)
                {
                    if (left is not BitmapSource l || right is not BitmapSource r ||
                        l.PixelWidth != r.PixelWidth || l.PixelHeight != r.PixelHeight) return false;
                    var stride = l.PixelWidth * 4;
                    var lp = new byte[stride * l.PixelHeight];
                    var rp = new byte[lp.Length];
                    new FormatConvertedBitmap(l, PixelFormats.Bgra32, null, 0).CopyPixels(lp, stride, 0);
                    new FormatConvertedBitmap(r, PixelFormats.Bgra32, null, 0).CopyPixels(rp, stride, 0);
                    return lp.SequenceEqual(rp);
                }
                var editedPixels = BitmapSource.Create(121, 81, 96, 96,
                    PixelFormats.Bgra32, null, new byte[121 * 81 * 4], 121 * 4);
                var showComparison = viewerType.GetMethod(
                    "ShowEditComparisonAsync", reflectFlags)!;
                var resetComparison = viewerType.GetMethod(
                    "ResetEditComparison", reflectFlags)!;
                var updateComparison = viewerType.GetMethod(
                    "UpdateEditComparisonAvailability", reflectFlags)!;
                var compareMode = viewerType.GetNestedType("EditCompareMode",
                    System.Reflection.BindingFlags.NonPublic)!;
                var side = (Grid)window.FindName("EditCompareSidePanel")!;
                var swipe = (Grid)window.FindName("EditCompareSwipePanel")!;
                var beforeImage = (Image)window.FindName("EditCompareSwipeOriginal")!;
                var afterImage = (Image)window.FindName("EditCompareSwipeEdited")!;
                var compareSide = (Button)window.FindName("EditCompareSideButton")!;
                var compareSplit = (Button)window.FindName("EditCompareSplitButton")!;
                var compareSingle = (Button)window.FindName("EditCompareSingleButton")!;
                var editControls = viewerType.GetMethod("UpdateEditControls", reflectFlags)!;
                var internalTabs = (TabControl)window.FindName("EditorToolTabs")!;
                var internalTools = (WrapPanel)window.FindName("ViewerToolsList")!;
                var internalAuto = (StackPanel)window.FindName("ViewerAutoToolsList")!;
                var externalTools = (WrapPanel)window.FindName("ExternalEditorToolsList")!;
                try
                {
                    // Noneditable PSD still offers configured external apps,
                    // but NO ghosted internal tools. Switching back to an
                    // editable JPEG restores them without restarting PCasa.
                    viewerInfoField.SetValue(window, new PhotoViewerImageInfo(
                        999, @"C:\synthetic\portrait.psd", "portrait.psd", 120, 180));
                    editControls.Invoke(window, null);
                    if (internalTabs.Visibility != Visibility.Collapsed ||
                        internalTools.Visibility != Visibility.Collapsed ||
                        internalAuto.Visibility != Visibility.Collapsed ||
                        externalTools.Visibility == Visibility.Collapsed)
                        throw new Exception(
                            "Unsupported PSD displayed disabled internal editor tools.");
                    viewerInfoField.SetValue(window, oldViewerInfo);
                    viewerRefinedField.SetValue(window, false);
                    editControls.Invoke(window, null);
                    if (internalTabs.IsEnabled || ((StackPanel)window.FindName("ViewerPanelLight")!).IsEnabled)
                        throw new Exception("Thumbnail-only preview allowed image editing.");
                    viewerRefinedField.SetValue(window, true);
                    editControls.Invoke(window, null);
                    if (internalTabs.Visibility != Visibility.Visible ||
                        internalTools.Visibility != Visibility.Visible ||
                        internalAuto.Visibility != Visibility.Visible)
                        throw new Exception(
                            "Editable JPEG failed to show internal editor tools again.");
                    Console.WriteLine(
                        "PASS: Internal edit tools vanish for PSD and return for editable JPEG.");
                    // A Topaz transaction must not hide tabs or the tools
                    // while an editable JPEG remains the current picture.
                    var externalField = viewerType.GetField(
                        "_externalEditReview", reflectFlags)!;
                    var externalType = viewerType.GetNestedType(
                        "ExternalEditReview",
                        System.Reflection.BindingFlags.NonPublic)!;
                    var externalCtor = externalType.GetConstructors(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance).Single();
                    var originalExternal = externalField.GetValue(window);
                    try
                    {
                        var snapshot = new ExternalEditSnapshot(
                            Guid.NewGuid(), @"C:\synthetic\photo.jpg",
                            @"C:\synthetic\topaz-work\photo.jpg",
                            "", 1024, DateTime.UtcNow, DateTime.UtcNow, []);
                        var review = externalCtor.Invoke(
                            [snapshot, 999L, "Topaz", false]);
                        externalField.SetValue(window, review);
                        editControls.Invoke(window, null);
                        if (internalTabs.Visibility != Visibility.Visible ||
                            internalTools.Visibility != Visibility.Visible ||
                            internalAuto.Visibility != Visibility.Visible ||
                            internalTabs.IsEnabled)
                            throw new Exception(
                                "Topaz launch hid JPEG edit tools or allowed conflicting edits.");
                        // After a real external result has arrived and PCasa
                        // has displayed those pixels, Crop and other internal
                        // tools must edit that PENDING result before Save/Copy.
                        // A missing preview must not edit the old original.
                        var resultProp = externalType.GetProperty("ResultPath")!;
                        var readyProp = externalType.GetProperty("Ready")!;
                        var previewProp = externalType.GetProperty("PreviewReady")!;
                        var busyProp = externalType.GetProperty("Busy")!;
                        resultProp.SetValue(review, @"C:\synthetic\topaz-work\result.jpg");
                        readyProp.SetValue(review, true);
                        editControls.Invoke(window, null);
                        if (internalTabs.IsEnabled)
                            throw new Exception(
                                "An unrendered Topaz result unlocked internal edits on the old original.");
                        previewProp.SetValue(review, true);
                        editControls.Invoke(window, null);
                        if (!internalTabs.IsEnabled)
                            throw new Exception(
                                "A displayed Topaz result failed to unlock internal crop.");
                        busyProp.SetValue(review, true);
                        editControls.Invoke(window, null);
                        if (internalTabs.IsEnabled)
                            throw new Exception(
                                "Busy external Save allowed conflicting internal changes.");
                        externalField.SetValue(window, null);
                        editControls.Invoke(window, null);
                        if (internalTabs.Visibility != Visibility.Visible ||
                            internalTools.Visibility != Visibility.Visible ||
                            !internalTabs.IsEnabled)
                            throw new Exception(
                                "Returning from Topaz failed to restore JPEG edit tools.");
                    }
                    finally
                    {
                        externalField.SetValue(window, originalExternal);
                        editControls.Invoke(window, null);
                    }
                    Console.WriteLine(
                        "PASS: Topaz does not hide internal tools and restores access after return.");

                    // The splitter's vertical marker extends ABOVE and
                    // BELOW its enclosing rectangle, unlike the adjacent
                    // two-pane symbol. WPF path bounds are checked directly.
                    var splitCanvas = (Canvas)compareSplit.Content;
                    var glyph = splitCanvas.Children.OfType<System.Windows.Shapes.Path>().Single();
                    if (glyph.Data.Bounds.Top >= 3.5 ||
                        glyph.Data.Bounds.Bottom <= 16.5)
                        throw new Exception(
                            "Slider icon divider does not extend outside its frame.");
                    editStateFixture.SetValue(window, null);
                    updateComparison.Invoke(window, null);
                    if (compareSingle.IsEnabled || compareSide.IsEnabled ||
                        compareSplit.IsEnabled ||
                        compareSingle.Visibility != Visibility.Collapsed ||
                        compareSide.Visibility != Visibility.Collapsed ||
                        compareSplit.Visibility != Visibility.Collapsed)
                        throw new Exception(
                            "A saved image must HIDE every comparison icon outside a pending edit.");
                    var edit = new PhotoEditSession(999, originalPixels);
                    // PhotoEditSession intentionally DECOUPLES its original
                    // pixels from the source BitmapSource/decoder. The split
                    // must use that immutable session source, not the same
                    // object reference that created the session.
                    var actualBefore = edit.Original;
                    var originalBytes = new byte[originalPixels.PixelWidth *
                        originalPixels.PixelHeight * 4];
                    var beforeBytes = new byte[actualBefore.PixelWidth *
                        actualBefore.PixelHeight * 4];
                    originalPixels.CopyPixels(originalBytes,
                        originalPixels.PixelWidth * 4, 0);
                    actualBefore.CopyPixels(beforeBytes,
                        actualBefore.PixelWidth * 4, 0);
                    if (actualBefore.PixelWidth != originalPixels.PixelWidth ||
                        actualBefore.PixelHeight != originalPixels.PixelHeight ||
                        !originalBytes.SequenceEqual(beforeBytes))
                        throw new Exception(
                            "Edit session detached the wrong original pixels.");
                    // Exercise actual slider and button events, not a direct
                    // UpdateEditControls invocation after changing the draft.
                    // Both Save variants must be enabled BEFORE Apply.
                    {
                        var beginStraighten = viewerType.GetMethod("BeginStraighten", reflectFlags)!;
                        var leaveStraighten = viewerType.GetMethod("LeaveStraighten", reflectFlags)!;
                        var straightenActive = viewerType.GetField("_straightenActive", reflectFlags)!;
                        var angleSlider = (Slider)window.FindName("StraightenAngleSlider")!;
                        var applyDraft = (Button)window.FindName("CropApplyButton")!;
                        var cancelDraft = (Button)window.FindName("ViewerToolCancelButton")!;
                        var saveDraft = (Button)window.FindName("CropSaveButton")!;
                        var copyDraft = (Button)window.FindName("CropSaveCopyButton")!;
                        var quarterTurn = (Button)window.FindName("StraightenRightButton")!;
                        var rotationEdit = new PhotoEditSession(999, originalPixels);
                        editStateFixture.SetValue(window, rotationEdit);
                        viewerImage.Source = originalPixels;
                        bool AllEnabled() => applyDraft.IsEnabled && cancelDraft.IsEnabled &&
                            saveDraft.IsEnabled && copyDraft.IsEnabled;
                        bool AllDisabled() => !applyDraft.IsEnabled && !cancelDraft.IsEnabled &&
                            !saveDraft.IsEnabled && !copyDraft.IsEnabled;
                        try
                        {
                            beginStraighten.Invoke(window, null);
                            if (!AllDisabled())
                                throw new Exception("Unchanged straighten draft enabled write actions.");
                            angleSlider.Value = 4.5;
                            if (!AllEnabled() || rotationEdit.HasChanges || !compareSplit.IsEnabled)
                                throw new Exception("Straighten slider did not unlock live decisions.");
                            angleSlider.Value = 0;
                            if (!AllDisabled() || compareSplit.IsEnabled)
                                throw new Exception("Zero straighten angle left stale draft decisions.");
                            quarterTurn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (!AllEnabled())
                                throw new Exception("Quarter-turn did not unlock live decisions.");
                            cancelDraft.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (straightenActive.GetValue(window) is not true ||
                                angleSlider.Value != 0 || rotationEdit.HasChanges || !AllDisabled())
                                throw new Exception("Cancel did not reset straighten while retaining its tool.");
                            angleSlider.Value = -3.5;
                            applyDraft.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (rotationEdit.AppliedSteps.Count != 1 ||
                                rotationEdit.AppliedSteps[0].Degrees != -3.5 ||
                                applyDraft.IsEnabled || cancelDraft.IsEnabled ||
                                !saveDraft.IsEnabled || !copyDraft.IsEnabled)
                                throw new Exception("Straighten Apply did not commit exactly one rotation and unlock Save/Copy.");
                        }
                        finally
                        {
                            if (straightenActive.GetValue(window) is true)
                                leaveStraighten.Invoke(window, null);
                            editStateFixture.SetValue(window, null);
                            viewerImage.Source = originalPixels;
                            editControls.Invoke(window, null);
                        }
                        Console.WriteLine("PASS: Straighten slider and quarter turns update all decision buttons; Cancel stays in tool and Apply commits once.");
                    }
                    // A real-time adjustment is already comparable before
                    // it is accepted into Undo history or saved to disk.
                    var advancedActive = viewerType.GetField(
                        "_advancedToneActive", reflectFlags)!;
                    var advancedKind = viewerType.GetField(
                        "_advancedToneKind", reflectFlags)!;
                    var advancedAmount = viewerType.GetField(
                        "_advancedToneAmount", reflectFlags)!;
                    var oldAdvancedActive = advancedActive.GetValue(window);
                    var oldAdvancedKind = advancedKind.GetValue(window);
                    var oldAdvancedAmount = advancedAmount.GetValue(window);
                    try
                    {
                        editStateFixture.SetValue(window, edit);
                        viewerImage.Source = originalPixels;
                        advancedActive.SetValue(window, true);
                        advancedKind.SetValue(window, "denoise-luma");
                        advancedAmount.SetValue(window, .35d);
                        updateComparison.Invoke(window, null);
                        if (!compareSplit.IsEnabled ||
                            compareSplit.Visibility != Visibility.Visible ||
                            edit.HasChanges)
                            throw new Exception(
                                "Live Details preview had no comparison before applying.");
                        var liveTask = (Task)showComparison.Invoke(window,
                            [Enum.Parse(compareMode, "Split")])!;
                        Until(window, () => liveTask.IsCompleted,
                            "Live Details comparison did not render.");
                        liveTask.GetAwaiter().GetResult();
                        Flush(window);
                        if (swipe.Visibility != Visibility.Visible ||
                            !ReferenceEquals(beforeImage.Source, actualBefore) ||
                            afterImage.Source is null)
                            throw new Exception(
                                "Live Details comparison lost its before/after pair.");
                        resetComparison.Invoke(window, null);
                    }
                    finally
                    {
                        advancedActive.SetValue(window, oldAdvancedActive);
                        advancedKind.SetValue(window, oldAdvancedKind);
                        advancedAmount.SetValue(window, oldAdvancedAmount);
                    }
                    Console.WriteLine(
                        "PASS: Unsaved live Details preview enables real before/after.");

                    edit.Apply(new PhotoEditStep("contrast", Amount: 0.25));
                    editStateFixture.SetValue(window, edit);
                    var expectedEdited = edit.Render();
                    if (SameComparisonPixels(expectedEdited, actualBefore))
                        throw new Exception("Comparison fixture did not change any pixels.");
                    // Deliberately inject a stale, differently sized viewer bitmap.
                    // Comparison must prefer the authoritative edit history.
                    viewerImage.Source = editedPixels;
                    updateComparison.Invoke(window, null);
                    if (!compareSide.IsEnabled || !compareSplit.IsEnabled ||
                        compareSingle.Visibility != Visibility.Visible ||
                        compareSide.Visibility != Visibility.Visible ||
                        compareSplit.Visibility != Visibility.Visible)
                        throw new Exception("Actual unsaved edit did not SHOW comparison icons.");
                    // Alpha.47 computes the geometrically aligned original on
                    // a worker. Blocking this STA dispatcher on GetResult()
                    // deadlocks the UI continuation and times out CI.
                    var initialSplitTask = (Task)showComparison.Invoke(window,
                        [Enum.Parse(compareMode, "Split")])!;
                    Until(window, () => initialSplitTask.IsCompleted,
                        "Split comparison did not finish rendering.");
                    initialSplitTask.GetAwaiter().GetResult();
                    Flush(window);
                    if (side.Visibility != Visibility.Collapsed ||
                        swipe.Visibility != Visibility.Visible ||
                        swipe.ActualWidth < 50 ||
                        !ReferenceEquals(beforeImage.Source, actualBefore) ||
                        !SameComparisonPixels(afterImage.Source, expectedEdited))
                        throw new Exception(
                            "Split mode silently switched to side-by-side or loaded incorrect pixels.");
                    var thumb = (System.Windows.Controls.Primitives.Thumb)
                        window.FindName("EditCompareSplitHandle")!;
                    if (thumb.Width < 20 || thumb.Width > 28 ||
                        thumb.Cursor != System.Windows.Input.Cursors.SizeWE)
                        throw new Exception(
                            "Split divider must expose a narrow invisible hit zone with the native horizontal resize cursor, not a separate visible grip.");
                    var pointerToFraction = viewerType.GetMethod(
                        "SplitFractionForPointer",
                        reflectFlags | System.Reflection.BindingFlags.Static)!;
                    double FromPointer(double pointerX, double grab,
                        double viewport, double image, double zoom, double pan) =>
                        (double)pointerToFraction.Invoke(null,
                            [pointerX, grab, viewport, image, zoom, pan])!;
                    if (Math.Abs(FromPointer(390, 10, 1000, 600, 1, 0) - .30) > .001 ||
                        Math.Abs(FromPointer(270, 10, 1000, 600, 2, 0) - .30) > .001 ||
                        Math.Abs(FromPointer(-1000, 0, 1000, 600, 1, 0) - .02) > .001 ||
                        Math.Abs(FromPointer(2000, 0, 1000, 600, 1, 0) - .98) > .001)
                        throw new Exception(
                            "Captured splitter coordinates must be normalized in the IMAGE, never the viewport.");
                    var dividerFraction = viewerType.GetField(
                        "_editCompareSplitFraction", reflectFlags)!;
                    dividerFraction.SetValue(window, .37d);
                    fitField.SetValue(window, true);
                    var recomputeGeometry = viewerType.GetMethod(
                        "UpdateEditComparisonZoom", reflectFlags)!;
                    recomputeGeometry.Invoke(window, null);
                    Flush(window);
                    var dividerFitX = Canvas.GetLeft(thumb);
                    var fitPercent = (double)viewerType.GetField(
                        "_editCompareFitPercent", reflectFlags)!.GetValue(window)!;
                    var currentZoom = (double)viewerType.GetMethod(
                        "CurrentViewerZoomPercent", reflectFlags)!
                        .Invoke(window, null)!;
                    if (Math.Abs(fitPercent - currentZoom) > .001)
                        throw new Exception(
                            "Comparison Fit zoom incorrectly used the hidden single-view scroller.");
                    var fitFrameWidth = ((Grid)window.FindName(
                        "EditCompareImageFrame")!).Width;
                    var fitClip = beforeImage.Clip as RectangleGeometry;
                    if (fitClip is null ||
                        Math.Abs(fitClip.Rect.Width - .37 * fitFrameWidth) > .5)
                        throw new Exception("Fitted picture clip is not in image coordinates.");
                    fitField.SetValue(window, false);
                    zoomField.SetValue(window, 220d);
                    recomputeGeometry.Invoke(window, null);
                    Flush(window);
                    var dividerZoomX = Canvas.GetLeft(thumb);
                    var zoomClip = beforeImage.Clip as RectangleGeometry;
                    var currentFactor = (double)viewerType.GetField(
                        "_editCompareZoomFactor", reflectFlags)!.GetValue(window)!;
                    var panX = (double)viewerType.GetField(
                        "_editComparePanX", reflectFlags)!.GetValue(window)!;
                    var expectedScreenX = swipe.ActualWidth / 2 + panX +
                        (.37 - .5) * fitFrameWidth * currentFactor - thumb.Width / 2;
                    if (zoomClip is null ||
                        Math.Abs(zoomClip.Rect.Width - .37 * fitFrameWidth) > .5 ||
                        Math.Abs(dividerZoomX - expectedScreenX) > .5 ||
                        Math.Abs(dividerZoomX - dividerFitX) < 1 ||
                        dividerZoomX < -thumb.Width ||
                        dividerZoomX > swipe.ActualWidth + thumb.Width ||
                        thumb.Height > swipe.ActualHeight + 1)
                        throw new Exception(
                            "Zoom moved the divider away from its image coordinate or hid its handle.");
                    fitField.SetValue(window, true);
                    recomputeGeometry.Invoke(window, null);
                    Flush(window);
                    if (Math.Abs(Canvas.GetLeft(thumb) - dividerFitX) > .5 ||
                        Math.Abs((double)dividerFraction.GetValue(window)! - .37d) > .001)
                        throw new Exception(
                            "Returning to Fit changed the picture-relative splitter position.");
                    Console.WriteLine(
                        "PASS: Splitter follows photo X through Fit/zoom/pan and stays reachable.");
                    // Side-by-side must not let a zoomed original paint over
                    // the edited image: each image has a separately clipped
                    // half-width visual parent.
                    var sideBySideTask = (Task)showComparison.Invoke(window,
                        [Enum.Parse(compareMode, "SideBySide")])!;
                    Until(window, () => sideBySideTask.IsCompleted,
                        "Side-by-side comparison did not finish rendering.");
                    sideBySideTask.GetAwaiter().GetResult();
                    Flush(window);
                    var sideBefore = (Image)window.FindName("EditCompareSideOriginal")!;
                    var sideAfter = (Image)window.FindName("EditCompareSideEdited")!;
                    if (side.Visibility != Visibility.Visible ||
                        swipe.Visibility != Visibility.Collapsed ||
                        sideBefore.Source is null || sideAfter.Source is null ||
                        sideBefore.Parent is not Grid originalHalf ||
                        sideAfter.Parent is not Grid editedHalf ||
                        !originalHalf.ClipToBounds || !editedHalf.ClipToBounds ||
                        originalHalf.ActualWidth <= 0 || editedHalf.ActualWidth <= 0)
                        throw new Exception(
                            "Side-by-side shows only one image or allows either image to cover its neighbor.");
                    var returnSplitTask = (Task)showComparison.Invoke(window,
                        [Enum.Parse(compareMode, "Split")])!;
                    Until(window, () => returnSplitTask.IsCompleted,
                        "Returning to split comparison did not finish rendering.");
                    returnSplitTask.GetAwaiter().GetResult();
                    Flush(window);
                    var firstBounds = beforeImage.TransformToAncestor(swipe)
                        .TransformBounds(new Rect(new Point(0, 0), beforeImage.RenderSize));
                    var secondBounds = afterImage.TransformToAncestor(swipe)
                        .TransformBounds(new Rect(new Point(0, 0), afterImage.RenderSize));
                    if (Math.Abs(firstBounds.Left - secondBounds.Left) > 0.5 ||
                        Math.Abs(firstBounds.Top - secondBounds.Top) > 0.5 ||
                        Math.Abs(firstBounds.Width - secondBounds.Width) > 0.5 ||
                        Math.Abs(firstBounds.Height - secondBounds.Height) > 0.5)
                        throw new Exception(
                            $"Split frames diverged in WPF layout: before={firstBounds}; " +
                            $"after={secondBounds}; viewport={swipe.ActualWidth}x{swipe.ActualHeight}.");
                    if (beforeImage.Clip is not RectangleGeometry clip ||
                        clip.Rect.Width <= 0 || clip.Rect.Width >= swipe.ActualWidth)
                        throw new Exception(
                            "Split did not expose the original with a clipped divider.");
                    // Undo keeps zoom while there are accepted edits.
                    // Undo of the LAST effect exits comparison to single
                    // view and hides its icons; Redo re-enables them.
                    var comparePanXField = viewerType.GetField(
                        "_editComparePanX", reflectFlags)!;
                    var comparePanYField = viewerType.GetField(
                        "_editComparePanY", reflectFlags)!;
                    var updateCompareZoom = viewerType.GetMethod(
                        "UpdateEditComparisonZoom", reflectFlags)!;
                    comparePanXField.SetValue(window, 135d);
                    comparePanYField.SetValue(window, -240d);
                    fitField.SetValue(window, true);
                    updateCompareZoom.Invoke(window, null);
                    Flush(window);
                    if (Math.Abs((double)comparePanXField.GetValue(window)!) > .01 ||
                        Math.Abs((double)comparePanYField.GetValue(window)!) > .01)
                        throw new Exception(
                            "Fit-to-window retained stale pan translation in split comparison.");
                    var editModeField = viewerType.GetField(
                        "_editCompareMode", reflectFlags)!;
                    var splitFractionField = viewerType.GetField(
                        "_editCompareSplitFraction", reflectFlags)!;
                    var moveHistory = viewerType.GetMethod(
                        "MoveEditHistory", reflectFlags)!;
                    fitField.SetValue(window, false);
                    zoomField.SetValue(window, 175d);
                    splitFractionField.SetValue(window, .32d);
                    edit.Apply(new PhotoEditStep("brightness", Amount: .2));
                    viewerImage.Source = edit.Render();
                    editControls.Invoke(window, null);
                    Flush(window);
                    moveHistory.Invoke(window, [-1]);
                    Flush(window);
                    if (Convert.ToString(editModeField.GetValue(window)) != "Split" ||
                        swipe.Visibility != Visibility.Visible ||
                        Math.Abs((double)zoomField.GetValue(window)! - 175d) > .01 ||
                        Math.Abs((double)splitFractionField.GetValue(window)! - .32d) > .001 ||
                        !SameComparisonPixels(afterImage.Source, viewerImage.Source))
                        throw new Exception(
                            "Undo changed zoom/split mode or retained stale After pixels.");
                    moveHistory.Invoke(window, [-1]);
                    Flush(window);
                    if (edit.HasChanges ||
                        Convert.ToString(editModeField.GetValue(window)) != "Single" ||
                        swipe.Visibility != Visibility.Collapsed ||
                        compareSplit.Visibility != Visibility.Collapsed ||
                        Math.Abs((double)zoomField.GetValue(window)! - 175d) > .01)
                        throw new Exception(
                            "Undo of last effect must close comparison and hide its buttons.");
                    moveHistory.Invoke(window, [1]);
                    Flush(window);
                    if (Convert.ToString(editModeField.GetValue(window)) != "Single" ||
                        compareSplit.Visibility != Visibility.Visible ||
                        afterImage.Source is not null ||
                        !SameComparisonPixels(viewerImage.Source, edit.Render()))
                        throw new Exception(
                            "Redo must show comparison controls without reviving an old mode.");
                    Console.WriteLine(
                        "PASS: Undo to original hides comparison; Redo restores icons in single view.");
                    editStateFixture.SetValue(window, null); // Save/Cancel completion.
                    updateComparison.Invoke(window, null);
                    if (compareSingle.IsEnabled || compareSide.IsEnabled ||
                        compareSplit.IsEnabled || swipe.Visibility != Visibility.Collapsed ||
                        compareSingle.Visibility != Visibility.Collapsed ||
                        compareSide.Visibility != Visibility.Collapsed ||
                        compareSplit.Visibility != Visibility.Collapsed)
                        throw new Exception(
                            "Save/Cancel failed to HIDE all compare icons and restore single view.");
                    Console.WriteLine(
                        "PASS: True WPF split alignment, active-only comparison, disabled after Save.");
                }
                finally
                {
                    resetComparison.Invoke(window, null);
                    editStateFixture.SetValue(window, oldEditSessionFixture);
                    viewerInfoField.SetValue(window, oldViewerInfo);
                    viewerImage.Source = oldViewerPixels;
                }
                // Restore has its OWN pair of photos, independent of any
                // editing/Undo marker in the main viewer. Exercise all
                // comparison modes as actual measured WPF layouts.
                var restoreFolder = Path.Combine(Path.GetTempPath(),
                    "PCasa-RestoreSplit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(restoreFolder);
                try
                {
                    var currentFile = Path.Combine(restoreFolder, "current.jpg");
                    var restoreFile = Path.Combine(restoreFolder, "saved.jpg");
                    void SaveFixture(string file, byte channel)
                    {
                        var pixels = new byte[96 * 64 * 4];
                        for (var pixel = 0; pixel < pixels.Length; pixel += 4)
                        { pixels[pixel] = channel; pixels[pixel+3] = 255; }
                        var image = BitmapSource.Create(96, 64, 96, 96,
                            PixelFormats.Bgra32, null, pixels, 96 * 4);
                        var encoder = new JpegBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using var output = File.Create(file);
                        encoder.Save(output);
                    }
                    SaveFixture(currentFile, 60);
                    SaveFixture(restoreFile, 200);
                    var version = new UndoVersion(Guid.NewGuid(), currentFile,
                        restoreFile, DateTimeOffset.UtcNow, 1, "synthetic");
                    var restoreType = typeof(PCasa.App.MainWindow).Assembly
                        .GetType("PCasa.App.UndoCompareWindow")
                        ?? throw new Exception("Restore comparison dialog missing.");
                    var constructor = restoreType.GetConstructors(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)
                        .Single();
                    var dialog = (Window)constructor.Invoke([
                        window, currentFile, new UndoVersion[] { version }]);
                    try
                    {
                        dialog.Show();
                        var setMode = restoreType.GetMethod("SetCompareMode", reflectFlags)!;
                        var splitPanel = (Grid)restoreType.GetField(
                            "_splitPanel", reflectFlags)!.GetValue(dialog)!;
                        var candidateImage = (Image)restoreType.GetField(
                            "_splitCandidate", reflectFlags)!.GetValue(dialog)!;
                        setMode.Invoke(dialog, [2]);
                        Flush(window);
                        if (splitPanel.Visibility != Visibility.Visible ||
                            candidateImage.Source is null ||
                            candidateImage.Clip is not RectangleGeometry section ||
                            section.Rect.Width <= 0 ||
                            section.Rect.Width >= splitPanel.ActualWidth)
                            throw new Exception(
                                "Restore split did not display a clipped candidate on the current image.");
                        setMode.Invoke(dialog, [0]);
                        Flush(window);
                        setMode.Invoke(dialog, [1]);
                        Flush(window);
                        Console.WriteLine(
                            "PASS: Restore dialog has independently selectable Single/Side/Split modes.");
                    }
                    finally { dialog.Close(); }
                }
                finally { Directory.Delete(restoreFolder, true); }

                fitField.SetValue(window, true);
                zoomField.SetValue(window, 100d);
                applyLayout.Invoke(window, null);
                Flush(window);
                // Alpha.28 regression: replacing an edit preview at 200%
                // must NOT reinterpret its 1000px width as the original
                // physical 4000px width or recenter the visible photo.
                var editStateField = viewerType.GetField(
                    "_photoEditSession", reflectFlags)!;
                var oldEditState = editStateField.GetValue(window);
                var oldViewerSource = viewerImage.Source;
                try
                {
                    var editPixels = BitmapSource.Create(1000, 250, 96, 96,
                        PixelFormats.Bgra32, null,
                        new byte[1000 * 250 * 4], 1000 * 4);
                    var editState = new PhotoEditSession(999, editPixels);
                    editStateField.SetValue(window, editState);
                    viewerImage.Source = editPixels;
                    fitField.SetValue(window, false);
                    zoomField.SetValue(window, 200d);
                    applyLayout.Invoke(window, null);
                    Flush(window);
                    var widthBeforeSlider = viewerImage.Width;
                    var preview = PhotoEditSession.Render(editPixels,
                        [new PhotoEditStep("contrast", Amount: .25)]);
                    viewerImage.Source = preview;
                    applyLayout.Invoke(window, null);
                    Flush(window);
                    if ((bool)fitField.GetValue(window)! ||
                        Math.Abs((double)zoomField.GetValue(window)! - 200) > .01 ||
                        Math.Abs(widthBeforeSlider - viewerImage.Width) > 1)
                        throw new Exception(
                            "Alpha.28 filter preview reset zoom from the original pixels.");
                    Console.WriteLine(
                        "PASS: WPF 200% edit-preview replacement retains physical zoom.");
                }
                finally
                {
                    editStateField.SetValue(window, oldEditState);
                    viewerImage.Source = oldViewerSource;
                    fitField.SetValue(window, true);
                    zoomField.SetValue(window, 100d);
                    applyLayout.Invoke(window, null);
                    Flush(window);
                }
                // Exercise a real measured sidebar transition while the
                // picture viewer is active, without invoking network/GPS IO.
                var sidebarModeField = viewerType.GetField("_sidebarMode", reflectFlags)!;
                var relayoutWorkspace = viewerType.GetMethod(
                    "UpdateWorkspaceLayoutPreservingGallery", reflectFlags)!;
                var previousMode = sidebarModeField.GetValue(window);
                try
                {
                    sidebarModeField.SetValue(window, null);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    var fullWidth = viewerScroller.ViewportWidth;
                    var fullImageWidth = viewerImage.Width;
                    sidebarModeField.SetValue(window, "GPS");
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    var narrowedWidth = viewerScroller.ViewportWidth;
                    var narrowedImageWidth = viewerImage.Width;
                    if (narrowedWidth >= fullWidth - 50 ||
                        narrowedImageWidth >= fullImageWidth - 20 ||
                        narrowedImageWidth > narrowedWidth + 2)
                        throw new Exception("GPS sidebar did not refit the open viewer.");
                    sidebarModeField.SetValue(window, null);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    if (viewerScroller.ViewportWidth <= narrowedWidth + 50 ||
                        viewerImage.Width <= narrowedImageWidth + 20 ||
                        viewerImage.Width > viewerScroller.ViewportWidth + 2 ||
                        !(bool)fitField.GetValue(window)!)
                        throw new Exception("Closing GPS did not fill the restored Fit viewport.");

                    fitField.SetValue(window, false);
                    zoomField.SetValue(window, 135d);
                    applyLayout.Invoke(window, null);
                    Flush(window);
                    var manualWidth = viewerImage.Width;
                    sidebarModeField.SetValue(window, "GPS");
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    sidebarModeField.SetValue(window, null);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    if ((bool)fitField.GetValue(window)! ||
                        Math.Abs((double)zoomField.GetValue(window)! - 135d) > .01 ||
                        Math.Abs(viewerImage.Width - manualWidth) > 2)
                        throw new Exception("Sidebar switch changed manual zoom percentage.");
                    Console.WriteLine(
                        "PASS: GPS toggle refits Fit and preserves explicit zoom.");
                }
                finally
                {
                    sidebarModeField.SetValue(window, previousMode);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    fitField.SetValue(window, true);
                    zoomField.SetValue(window, 100d);
                    applyLayout.Invoke(window, null);
                    Flush(window);
                }
                // GPS fullscreen is an overlay on the MAP, not a request to
                // close the map and expand the two-column photo layout.
                // Use the active synthetic viewer photo, with no map/network IO.
                var gpsFullscreenFlag = viewerType.GetField("_gpsFullscreen", reflectFlags)!;
                var gpsFullscreenClick = viewerType.GetMethod("GpsFullscreen_Click", reflectFlags)!;
                var gpsPhotoShow = viewerType.GetMethod("ShowGpsFullscreenImage", reflectFlags)!;
                var gpsOverlay = (Grid?)window.FindName("GpsFullscreenImageOverlay")
                    ?? throw new Exception("GPS photo overlay missing.");
                var gpsPhotoFrame = (Border?)window.FindName("GpsFullscreenImageFrame")
                    ?? throw new Exception("Floating photo frame missing.");
                var workspace = (Grid?)window.FindName("BrowseWorkspaceGrid")
                    ?? throw new Exception("Workspace grid missing.");
                var gpsSidebar = (Border?)window.FindName("InfoSidebarPanel")
                    ?? throw new Exception("GPS sidebar missing.");
                var previousGpsMode = sidebarModeField.GetValue(window);
                // A filmstrip double-click requires a photo represented in
                // the GPS points or in the actual gallery selection.
                var fullscreenPointField = viewerType.GetField("_gpsPoints", reflectFlags)!;
                var previousFullscreenPoints = fullscreenPointField.GetValue(window);
                var fullscreenSelectedIds = (HashSet<long>)viewerType.GetField(
                    "_selectedImageIds", reflectFlags)!.GetValue(window)!;
                var hadFullscreenSelectedId = fullscreenSelectedIds.Contains(999L);
                var fullscreenPointType = viewerType.GetNestedType("GpsPoint",
                    System.Reflection.BindingFlags.NonPublic)!;
                var fullscreenPoint = Activator.CreateInstance(fullscreenPointType,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, new object[] { 999L, "synthetic-viewer-photo.jpg",
                        28.416d, -16.567d, true }, null)!;
                var fullscreenPointArray = Array.CreateInstance(fullscreenPointType, 1);
                fullscreenPointArray.SetValue(fullscreenPoint, 0);
                try
                {
                    sidebarModeField.SetValue(window, "GPS");
                    fullscreenPointField.SetValue(window, fullscreenPointArray);
                    fullscreenSelectedIds.Add(999L);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                    gpsFullscreenClick.Invoke(window, new object[] {
                        window, new RoutedEventArgs(Button.ClickEvent) });
                    Flush(window);
                    if (!(bool)gpsFullscreenFlag.GetValue(window)! ||
                        gpsOverlay.Visibility != Visibility.Visible ||
                        Grid.GetColumn(gpsOverlay) != 0 ||
                        Grid.GetColumnSpan(gpsOverlay) != 5 ||
                        Panel.GetZIndex(gpsOverlay) <= Panel.GetZIndex(gpsSidebar) ||
                        Math.Abs(gpsPhotoFrame.ActualWidth - workspace.ActualWidth) > 2 ||
                        Math.Abs(gpsPhotoFrame.ActualHeight - workspace.ActualHeight) > 2)
                        throw new Exception(
                            "The fullscreen photo does not cover the entire GPS workspace. " +
                            $"GpsFullscreen={(bool)gpsFullscreenFlag.GetValue(window)!}; " +
                            $"Overlay={gpsOverlay.Visibility}; " +
                            $"GridColumn={Grid.GetColumn(gpsOverlay)}; " +
                            $"GridSpan={Grid.GetColumnSpan(gpsOverlay)}; " +
                            $"OverlayZ={Panel.GetZIndex(gpsOverlay)}; " +
                            $"SidebarZ={Panel.GetZIndex(gpsSidebar)}; " +
                            $"Frame={gpsPhotoFrame.ActualWidth:F2}x{gpsPhotoFrame.ActualHeight:F2}; " +
                            $"OverlaySize={gpsOverlay.ActualWidth:F2}x{gpsOverlay.ActualHeight:F2}; " +
                            $"Workspace={workspace.ActualWidth:F2}x{workspace.ActualHeight:F2}; " +
                            $"Sidebar={gpsSidebar.ActualWidth:F2}x{gpsSidebar.ActualHeight:F2}");
                    // A real filmstrip double-click (not only the image
                    // loader method) must reopen the floating window while
                    // preserving the fullscreen GPS mode.
                    viewerType.GetMethod("CloseGpsFullscreenImage", reflectFlags)!
                        .Invoke(window, null);
                    var filmstripList = (ListBox?)window.FindName("GpsFilmstripList")
                        ?? throw new Exception("GPS filmstrip list missing.");
                    // Use a synthetic GPS-valid point without an actual photo file.
                    // Suppress the real selection-triggered catalog request
                    // while preparing this isolated double-click fixture.
                    var stripUpdating = viewerType.GetField("_gpsFilmstripUpdating", reflectFlags)!;
                    stripUpdating.SetValue(window, true);
                    try
                    {
                        filmstripList.ItemsSource = new[] {
                            new PCasa.App.MainWindow.GpsFilmstripEntry {
                                Id = 999, Name = "synthetic-viewer-photo.jpg"
                            }
                        };
                        filmstripList.SelectedIndex = 0;
                    }
                    finally
                    {
                        stripUpdating.SetValue(window, false);
                    }
                    // A preceding asynchronous GPS catalog refresh can
                    // replace the synthetic point fixture between UI flushes.
                    // Reestablish its selected identity at the actual click
                    // boundary, not while unrelated catalog work is running.
                    fullscreenPointField.SetValue(window, fullscreenPointArray);
                    fullscreenSelectedIds.Add(999L);
                    if (filmstripList.SelectedItem is not
                        PCasa.App.MainWindow.GpsFilmstripEntry { Id: 999L })
                        throw new Exception("Synthetic filmstrip selection was replaced before double-click.");
                    var filmstripDblClick = viewerType.GetMethod(
                        "GpsFilmstrip_MouseDoubleClick", reflectFlags)!;
                    // This is a SYNCHRONOUS MouseDoubleClick handler.
                    // Assert its immediate visible result BEFORE pumping
                    // unrelated dispatcher work. The synthetic ID 999 is
                    // deliberately absent from SQLite: a pending real GPS
                    // catalog refresh can (correctly) remove that phantom
                    // selection and its point during Flush(window). That
                    // would test a background catalog reconciliation rather
                    // than filmstrip double-click behavior.
                    filmstripDblClick.Invoke(window, new object[] {
                        filmstripList,
                        new System.Windows.Input.MouseButtonEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, 0,
                            System.Windows.Input.MouseButton.Left)
                    });
                    if (!(bool)gpsFullscreenFlag.GetValue(window)! ||
                        gpsOverlay.Visibility != Visibility.Visible ||
                        gpsOverlay.Tag is not long shownId || shownId != 999L)
                        throw new Exception(
                            "Filmstrip double-click exited fullscreen or failed to show the photo." +
                            $" Fullscreen={gpsFullscreenFlag.GetValue(window)};" +
                            $" Mode={sidebarModeField.GetValue(window)};" +
                            $" Overlay={gpsOverlay.Visibility}; Tag={gpsOverlay.Tag};" +
                            $" SelectedItem={(filmstripList.SelectedItem as PCasa.App.MainWindow.GpsFilmstripEntry)?.Id};" +
                            $" Selected999={fullscreenSelectedIds.Contains(999L)};" +
                            $" GpsPoint999={((System.Collections.IEnumerable)fullscreenPointField.GetValue(window)!).Cast<object>().Any(point => (long)point.GetType().GetProperty("Id")!.GetValue(point)! == 999L)}");
                    filmstripList.ItemsSource = null;
                    gpsFullscreenClick.Invoke(window, new object[] {
                        window, new RoutedEventArgs(Button.ClickEvent) });
                    Flush(window);
                    if ((bool)gpsFullscreenFlag.GetValue(window)! ||
                        gpsOverlay.Visibility == Visibility.Visible ||
                        !panel.IsVisible)
                        throw new Exception(
                            "Exiting GPS fullscreen did not restore the open viewer.");
                    Console.WriteLine(
                        "PASS: GPS fullscreen overlays photo without changing map mode.");
                }
                finally
                {
                    if ((bool)gpsFullscreenFlag.GetValue(window)!)
                        gpsFullscreenClick.Invoke(window, new object[] {
                            window, new RoutedEventArgs(Button.ClickEvent) });
                    if (!hadFullscreenSelectedId) fullscreenSelectedIds.Remove(999L);
                    fullscreenPointField.SetValue(window, previousFullscreenPoints);
                    sidebarModeField.SetValue(window, previousGpsMode);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                }

                // A large selected set must fit its camera FIRST, then
                // cluster at that exact zoom. Previously the map clustered
                // at its old zoom, overpainting Europe with black marker
                // borders until a manual zoom forced a fresh projection.
                var parseGpsMessage = viewerType.GetMethod(
                    "ParseGpsJavascriptMessage",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)
                    ?? throw new Exception("Static GPS bridge parser missing.");
                const string fitAckJson =
                    "{\"type\":\"gps-fit-ready\",\"version\":47,\"zoom\":1.25}";
                var fitAck = parseGpsMessage.Invoke(null, new object[] { fitAckJson })
                    ?? throw new Exception("GPS camera-fit acknowledgment not parsed.");
                var fitAckType = fitAck.GetType();
                if (!Equals(fitAckType.GetProperty("Kind")?.GetValue(fitAck), "gps-fit-ready") ||
                    !Equals(fitAckType.GetProperty("Id")?.GetValue(fitAck), 47L) ||
                    fitAckType.GetProperty("Zoom")?.GetValue(fitAck) is not double fittedZoom ||
                    Math.Abs(fittedZoom - 1.25) > .001 ||
                    parseGpsMessage.Invoke(null, new object[] {
                        "{\"type\":\"gps-fit-ready\",\"version\":47,\"zoom\":25}"
                    }) is not null)
                    throw new Exception("GPS camera fit bridge accepts stale/invalid zoom.");

                var viewportAck = parseGpsMessage.Invoke(null, new object[] {
                    "{\"type\":\"viewport\",\"lat\":53.5,\"lon\":10,\"zoom\":1.25,\"fitVersion\":47,\"cameraRevision\":8}"
                }) ?? throw new Exception("GPS fit viewport not parsed.");
                if (!Equals(viewportAck.GetType().GetProperty("Version")?.GetValue(viewportAck), 47L) ||
                    !Equals(viewportAck.GetType().GetProperty("CameraRevision")?.GetValue(viewportAck), 8L))
                    throw new Exception("GPS fit viewport lost camera ordering or fit identity.");
                var staleTransfer = parseGpsMessage.Invoke(null, new object[] {
                    "{\"type\":\"gps-projection-stale\",\"version\":71,\"zoom\":1.1,\"cameraRevision\":9}"
                }) ?? throw new Exception("GPS stale transfer callback not parsed.");
                if (!Equals(staleTransfer.GetType().GetProperty("Id")?.GetValue(staleTransfer), 71L) ||
                    !Equals(staleTransfer.GetType().GetProperty("CameraRevision")?.GetValue(staleTransfer), 9L))
                    throw new Exception("GPS stale transfer missing revision or camera ordering.");

                using (var gpsHtmlStream = typeof(PCasa.App.MainWindow).Assembly
                    .GetManifestResourceStream("PCasa.App.Resources.gps-map.html"))
                {
                    if (gpsHtmlStream is null)
                        throw new Exception("GPS map HTML resource missing.");
                    using var gpsHtmlReader = new System.IO.StreamReader(gpsHtmlStream);
                    var gpsHtml = gpsHtmlReader.ReadToEnd();
                    var prepare = gpsHtml.IndexOf("window.pcasaPrepareGpsExtent",
                        StringComparison.Ordinal);
                    var fitCallOffset = gpsHtml.IndexOf("map.fitBounds(", prepare,
                        StringComparison.Ordinal);
                    var ack = gpsHtml.IndexOf("type:'gps-fit-ready'", prepare,
                        StringComparison.Ordinal);
                    if (prepare < 0 || fitCallOffset < prepare || ack < fitCallOffset ||
                        (!gpsHtml.Contains("fitVersion:latestFitQueryVersion", StringComparison.Ordinal) ||
                         gpsHtml.Contains("window.pcasaFitGpsExtent=", StringComparison.Ordinal)))
                        throw new Exception("Map draws clusters before fitting selection viewport.");
                }

                // Many image identities spanning continents are ALWAYS
                // retained. A smaller fitted map zoom collapses more of
                // the same GPS data than the previous (stale) zoom.
                var worldwide = Enumerable.Range(0, 16000).Select(i =>
                    new GpsPhotoPosition(i + 1L,
                        (i % 8) * 10d - 35 + (i % 31) * 0.15,
                        (i % 16) * 17d - 130 + (i % 29) * 0.2)).ToArray();
                var atWorldZoom = GpsGeographicClusterer.Cluster(worldwide, 1.1);
                var atOldZoom = GpsGeographicClusterer.Cluster(worldwide, 7.5);
                if (atWorldZoom.Count >= atOldZoom.Count ||
                    atWorldZoom.Sum(g => g.ImageIds.Count) != worldwide.Length ||
                    atWorldZoom.SelectMany(g => g.ImageIds).Distinct().Count()
                        != worldwide.Length)
                    throw new Exception("World-scale cluster radius or image identities are wrong.");
                // The map badge measures 27 px plus outline and shadow.
                // Every cluster center must therefore have at least the
                // required screen-space spacing, including at high latitudes
                // and across the International Date Line.
                static (double X, double Y) Mercator(GpsPhotoCluster g, double zoom)
                {
                    var scale = 512d * Math.Pow(2d, zoom);
                    var lat = Math.Clamp(g.Latitude, -85.0511287798066,
                        85.0511287798066) * Math.PI / 180d;
                    var sine = Math.Sin(lat);
                    return ((g.Longitude + 180d) / 360d * scale,
                        (0.5d - Math.Log((1 + sine) / (1 - sine)) /
                            (4d * Math.PI)) * scale);
                }

                var pixelWorld = 512d * Math.Pow(2d, 1.1d);
                for (var i = 0; i < atWorldZoom.Count; i++)
                for (var j = i + 1; j < atWorldZoom.Count; j++)
                {
                    var a = Mercator(atWorldZoom[i], 1.1);
                    var b = Mercator(atWorldZoom[j], 1.1);
                    var horizontal = Math.Abs(a.X - b.X);
                    horizontal = Math.Min(horizontal, pixelWorld - horizontal);
                    var vertical = a.Y - b.Y;
                    if (Math.Sqrt(horizontal * horizontal + vertical * vertical) <
                        GpsGeographicClusterer.MinimumMarkerSpacingPixels - .01)
                        throw new Exception("World-scale GPS markers overlap on the initial frame.");
                }
                var dateLinePhotos = new[] {
                    new GpsPhotoPosition(800001, 35.1, 179.995),
                    new GpsPhotoPosition(800002, 35.1, -179.995),
                    new GpsPhotoPosition(800003, 35.1, 20)
                };
                var dateLineClusters = GpsGeographicClusterer.Cluster(
                    dateLinePhotos, 5);
                if (dateLineClusters.Count != 2 ||
                    !dateLineClusters.Any(g => g.ImageIds.Count == 2 &&
                        g.ImageIds.Contains(800001) && g.ImageIds.Contains(800002)) ||
                    !dateLineClusters.Any(g => g.ImageIds.Count == 1 &&
                        g.ImageIds[0] == 800003 && g.Latitude == 35.1 &&
                        g.Longitude == 20))
                    throw new Exception("GPS dateline merge or singleton original coordinates broken.");
                Console.WriteLine(
                    "PASS: GPS first-frame 16,000-photo cluster spacing, photo membership and dateline.");

                // Provider-neutral map click must resolve real photo IDs in
                // the existing WPF filmstrip, never just zoom without photos.
                var clusterClick = viewerType.GetMethod(
                    "ShowGpsClusterById", reflectFlags)!;
                var clusterField = viewerType.GetField(
                    "_gpsPresentedClusters", reflectFlags)!;
                var catalogField = viewerType.GetField(
                    "_browseIndex", reflectFlags)!;
                var versionField = viewerType.GetField(
                    "_gpsMapTransferRevision", reflectFlags)!;
                var savedClusters = clusterField.GetValue(window);
                var savedCatalog = catalogField.GetValue(window);
                var savedVersion = versionField.GetValue(window);
                var gpsPointsField = viewerType.GetField("_gpsPoints", reflectFlags)!;
                var savedGpsPoints = gpsPointsField.GetValue(window);
                var gpsPointType = viewerType.GetNestedType("GpsPoint",
                    System.Reflection.BindingFlags.NonPublic)!;
                var syntheticPoint = Activator.CreateInstance(gpsPointType,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null, new object[] { 1003L, "synthetic.jpg",
                        28.416d, -16.567d, true }, null)!;
                var gpsPointArray = Array.CreateInstance(gpsPointType, 1);
                gpsPointArray.SetValue(syntheticPoint, 0);
                var savedGpsMode = sidebarModeField.GetValue(window);
                var strip = (ListBox?)window.FindName("GpsFilmstripList")
                    ?? throw new Exception("GPS filmstrip missing.");
                var stripToggle = (ToggleButton?)window.FindName("GpsFilmstripToggle")
                    ?? throw new Exception("GPS filmstrip toggle missing.");
                var savedToggle = stripToggle.IsChecked;
                try
                {
                    sidebarModeField.SetValue(window, "GPS");
                    // The GPS Filmstrip now admits only geotagged sources.
                    // Make all four synthetic map-marker images GPS-valid,
                    // rather than using the generic non-GPS gallery fixture.
                    var gpsSourceCatalog = synthetic.Select(image =>
                        image.ImageId is >= 1000 and <= 1003
                            ? image with { HasGps = true } : image).ToArray();
                    catalogField.SetValue(window, gpsSourceCatalog);
                    gpsPointsField.SetValue(window, gpsPointArray);
                    versionField.SetValue(window, 77L);
                    clusterField.SetValue(window,
                        new[] { new GpsPhotoCluster(1000, 28.415, -16.568,
                            new long[] { 1000, 1001, 1002 }) });
                    clusterClick.Invoke(window, new object[] { 1000L, 77L });
                    var immediateClusterCount = strip.Items.Count;
                    var immediatePointGroup = viewerType.GetField("_gpsFilmstripPointIds",
                        reflectFlags)!.GetValue(window) as HashSet<long>;
                    var immediatePointGroupCount = immediatePointGroup?.Count ?? -1;
                    // The click synchronously updates ItemsSource. A later
                    // unrelated asynchronous gallery refresh is tested
                    // separately and must not mutate this synthetic fixture.
                    if (strip.Items.Count != 3 ||
                        !strip.Items.Cast<PCasa.App.MainWindow.GpsFilmstripEntry>()
                            .Select(x => x.Id).Order()
                            .SequenceEqual(new long[] { 1000, 1001, 1002 }))
                        throw new Exception("Cluster click did not show its three photos: " +
                            $"actual={string.Join(",", strip.Items.Cast<PCasa.App.MainWindow.GpsFilmstripEntry>().Select(x => x.Id))}; " +
                            $"immediate={immediateClusterCount}; pointGroupBeforeFlush={immediatePointGroupCount}; " +
                            $"pointGroupAfterFlush={(viewerType.GetField("_gpsFilmstripPointIds", reflectFlags)!.GetValue(window) as HashSet<long>)?.Count ?? -1}; " +
                            $"revision={versionField.GetValue(window)}; clusterCount={((IReadOnlyList<GpsPhotoCluster>)clusterField.GetValue(window)!).Count}; " +
                            $"toggle={stripToggle.IsChecked}; visible={strip.IsVisible}; " +
                            $"frozen={viewerType.GetField("_gpsFilmstripFrozen", reflectFlags)!.GetValue(window)}; " +
                            $"mode={sidebarModeField.GetValue(window)}; " +
                            $"indexGps={string.Join(",", gpsSourceCatalog.Take(4).Select(x => $"{x.ImageId}:{x.HasGps}"))}");
                    clusterClick.Invoke(window, new object[] { 1000L, 76L });
                    if (strip.Items.Count != 3)
                        throw new Exception("Stale cluster click changed photo group.");
                    clusterField.SetValue(window,
                        new[] { new GpsPhotoCluster(1003, 28.416, -16.567,
                            new long[] { 1003 }) });
                    // The map click is synchronous. Verify immediately
                    // before pumping unrelated dispatcher callbacks: the
                    // synthetic ID 1003 is deliberately absent from SQLite,
                    // so pending real catalog refreshes may remove it.
                    clusterClick.Invoke(window, new object[] { 1003L, 77L });
                    var gpsMarkerPopup = (FrameworkElement?)window.FindName("GpsPhotoPopup")
                        ?? throw new Exception("GPS popup missing.");
                    if (strip.Items.Count != 1 ||
                        gpsMarkerPopup.Tag is not long popupId || popupId != 1003 ||
                        gpsMarkerPopup.Visibility != Visibility.Visible)
                        throw new Exception(
                            "Single-marker click failed to show its photo immediately. " +
                            $"Filmstrip={string.Join(",", strip.Items.Cast<PCasa.App.MainWindow.GpsFilmstripEntry>().Select(x => x.Id))}; " +
                            $"PopupTag={gpsMarkerPopup.Tag}; PopupVisibility={gpsMarkerPopup.Visibility}; " +
                            $"MapVersion={versionField.GetValue(window)}; " +
                            $"Fullscreen={viewerType.GetField("_gpsFullscreen", reflectFlags)!.GetValue(window)}");
                    Console.WriteLine(
                        "PASS: Map cluster clicks show photo IDs; singles open preview.");

                    // The old no-selection code silently queried 5,000
                    // unrelated photo locations. A folder change with no
                    // gallery selection must synchronously EMPTY the map,
                    // stale cluster projection and photo strip, without
                    // entering the scheduler or waiting for a NAS scan.
                    var idsField = viewerType.GetField("_selectedImageIds",
                        reflectFlags)!;
                    var originalSelection = ((HashSet<long>)idsField.GetValue(window)!)
                        .ToArray();
                    try
                    {
                        ((HashSet<long>)idsField.GetValue(window)!).Clear();
                        var refreshGps = viewerType.GetMethod(
                            "RefreshGpsMapAsync", reflectFlags)!;
                        ((Task)refreshGps.Invoke(window, null)!).GetAwaiter()
                            .GetResult();
                        if (((System.Collections.IEnumerable)gpsPointsField
                                .GetValue(window)!).Cast<object>().Any() ||
                            ((System.Collections.IEnumerable)clusterField
                                .GetValue(window)!).Cast<object>().Any() ||
                            strip.Items.Count != 0 ||
                            gpsCoords.Text != "Keine Fotos ausgewählt")
                            throw new Exception(
                                "Zero photo selection retained stale GPS points, clusters or filmstrip.");
                        Console.WriteLine(
                            "PASS: No-selection folder change immediately clears all GPS context.");
                    }
                    finally
                    {
                        ((HashSet<long>)idsField.GetValue(window)!)
                            .UnionWith(originalSelection);
                    }
                }
                finally
                {
                    clusterField.SetValue(window, savedClusters);
                    catalogField.SetValue(window, savedCatalog);
                    versionField.SetValue(window, savedVersion);
                    gpsPointsField.SetValue(window, savedGpsPoints);
                    if (stripToggle.IsChecked != savedToggle)
                        stripToggle.IsChecked = savedToggle;
                    sidebarModeField.SetValue(window, savedGpsMode);
                    relayoutWorkspace.Invoke(window, null);
                    Flush(window);
                }

                toggleFullscreen.Invoke(window, null);
                Flush(window);
                if (fullscreenField.GetValue(window) is not Window)
                    throw new Exception("Fullscreen viewer did not open.");
                if (!(bool)fitField.GetValue(window)! ||
                    Math.Abs((double)zoomField.GetValue(window)! - 100d) > .01)
                    throw new Exception("Entering fullscreen changed Fit or percentage.");
                leaveFullscreen.Invoke(window, null);
                Flush(window);
                if (!(bool)fitField.GetValue(window)! ||
                    Math.Abs((double)zoomField.GetValue(window)! - 100d) > .01 ||
                    fullscreenField.GetValue(window) is not null ||
                    viewerImage.Width > viewerScroller.ViewportWidth + 2 ||
                    viewerImage.Height > viewerScroller.ViewportHeight + 2)
                    throw new Exception("Leaving fullscreen no longer fits the entire image.");

                // A user-selected percentage must remain unchanged as well.
                fitField.SetValue(window, false);
                zoomField.SetValue(window, 135d);
                applyLayout.Invoke(window, null);
                Flush(window);
                toggleFullscreen.Invoke(window, null);
                Flush(window);
                leaveFullscreen.Invoke(window, null);
                Flush(window);
                if ((bool)fitField.GetValue(window)! ||
                    Math.Abs((double)zoomField.GetValue(window)! - 135d) > .01)
                    throw new Exception("Fullscreen changed explicit 135% zoom.");

                // Alt+F4 closes the WPF window directly, not via the
                // right-click method. Both exit routes retain Fit.
                fitField.SetValue(window, true);
                zoomField.SetValue(window, 100d);
                applyLayout.Invoke(window, null);
                Flush(window);
                toggleFullscreen.Invoke(window, null);
                Flush(window);
                (fullscreenField.GetValue(window) as Window)?.Close();
                Flush(window);
                if (!(bool)fitField.GetValue(window)! ||
                    fullscreenField.GetValue(window) is not null ||
                    viewerImage.Width > viewerScroller.ViewportWidth + 2 ||
                    viewerImage.Height > viewerScroller.ViewportHeight + 2)
                    throw new Exception("Closing fullscreen via window event lost Fit.");
                Console.WriteLine("PASS: Fullscreen round trip retains Fit/explicit zoom through both exits.");
            }
            finally
            {
                if (fullscreenField.GetValue(window) is Window stillOpen)
                    stillOpen.Close();
                viewerOpenField.SetValue(window, false);
                viewerInfoField.SetValue(window, null);
                viewerRefinedField.SetValue(window, previousRefined);
                viewerSequenceForFixture.SetValue(window, previousViewerSequence);
                panel.Visibility = Visibility.Collapsed;
            }

            // Exercise the REAL shutdown lifecycle with one busy background
            // worker. Closing must return immediately to the dispatcher,
            // display progress and wait for the single scheduler's disposal.
            var closingScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var startedShutdownWorker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var working = closingScheduler.EnqueueAsync("test:shutdown-cancellation",
                SchedulerPriority.Background, async token =>
                {
                    startedShutdownWorker.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            var enteredDeadline = DateTime.UtcNow.AddSeconds(12);
            while (!startedShutdownWorker.Task.IsCompleted &&
                DateTime.UtcNow < enteredDeadline)
                Flush(window);
            if (!startedShutdownWorker.Task.IsCompleted)
                throw new Exception("Shutdown test worker never started.");
            // A network directory query may ignore cancellation indefinitely.
            // Only idle/background I/O is simulated: no native writer or P0.
            // Closing the app must not wait for this unrelated kernel read.
            var stalledStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseStalledRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stalledRead = closingScheduler.EnqueueAsync(
                "test:shutdown-stalled-read", SchedulerPriority.Background,
                async token =>
                {
                    stalledStarted.TrySetResult();
                    await releaseStalledRead.Task; // deliberately ignores token
                }, workKind: SchedulerWorkKind.SourceProbe);
            var stalledDeadline = DateTime.UtcNow.AddSeconds(8);
            while (!stalledStarted.Task.IsCompleted && DateTime.UtcNow < stalledDeadline)
                Flush(window);
            if (!stalledStarted.Task.IsCompleted)
                throw new Exception("Shutdown test source read never started.");
            var interactiveReadStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseInteractiveRead = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var stuckInteractiveRead = closingScheduler.EnqueueAsync(
                "test:shutdown-stalled-catalog-read", SchedulerPriority.Interactive,
                async token =>
                {
                    interactiveReadStarted.TrySetResult();
                    await releaseInteractiveRead.Task; // simulates blocked SMB read
                }, workKind: SchedulerWorkKind.CatalogRead);
            var interactiveDeadline = DateTime.UtcNow.AddSeconds(8);
            while (!interactiveReadStarted.Task.IsCompleted &&
                DateTime.UtcNow < interactiveDeadline)
                Flush(window);
            if (!interactiveReadStarted.Task.IsCompleted)
                throw new Exception("Shutdown interactive read never started.");
            var shuttingDownAt = DateTime.UtcNow;
            window.Close();
            var progress = Application.Current.Windows
                .OfType<PCasa.App.ShutdownProgressWindow>().SingleOrDefault();
            var mainLayout = (Grid?)window.FindName("MainLayout");
            if (!window.IsVisible || !window.IsEnabled ||
                mainLayout is null || mainLayout.IsHitTestVisible ||
                progress is null || !progress.IsVisible)
                throw new Exception(
                    "Shutdown failed to block interaction without washing out the main UI.");
            // Repeated Close does not create another shutdown or dialog.
            window.Close();
            if (Application.Current.Windows
                    .OfType<PCasa.App.ShutdownProgressWindow>().Count() != 1)
                throw new Exception("Repeated Close started a duplicate shutdown.");
            var exitDeadline = DateTime.UtcNow.AddSeconds(18);
            while (window.IsVisible && DateTime.UtcNow < exitDeadline)
                Flush(window);
            if (window.IsVisible || !working.IsCompleted ||
                Application.Current.Windows
                    .OfType<PCasa.App.ShutdownProgressWindow>().Any(w => w.IsVisible) ||
                ExifToolKeywordWriter.HasLiveSessions ||
                (DateTime.UtcNow - shuttingDownAt) > TimeSpan.FromSeconds(8))
                throw new Exception("Shutdown waited for a canceled but blocked background read.");
            if (stalledRead.IsCompleted || stuckInteractiveRead.IsCompleted)
                throw new Exception("Shutdown test did not keep network reads blocked.");
            releaseStalledRead.TrySetResult();
            releaseInteractiveRead.TrySetResult();
            Console.WriteLine("PASS: Shutdown cancels normal scans and does not wait for stalled noncritical source reads.");
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

    // Pump the real WPF dispatcher while waiting for async startup to
    // publish the gallery. A bounded wait makes a broken startup fail the
    // smoke test rather than hanging a GitHub Actions runner indefinitely.
    private static void Until(Window window, Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new Exception(failureMessage);
            Flush(window);
            System.Threading.Thread.Sleep(20);
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
