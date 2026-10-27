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
            // Prior WPF tests in this isolated runner can persist a 1044px
            // window size. The user's approved complete one-row toolbar
            // targets normal/wide windows; do not silently shrink old UI.
            window.WindowState = WindowState.Normal;
            window.Width = 1500;
            Flush(window);

            var menu = Descendants<Menu>(window).FirstOrDefault()
                ?? throw new Exception("Main menu was not constructed.");
            var items = menu.Items.OfType<MenuItem>().ToArray();
            var labels = items.Select(item => item.Header?.ToString()).ToArray();
            if (!labels.SequenceEqual(new[] { "Datei", "Bearbeiten",
                    "Ordner", "Optionen", "Hilfe" }))
                throw new Exception("Unexpected top-level menu labels: " +
                    string.Join(", ", labels));
            Console.WriteLine("PASS: Real MainWindow creates expected menu.");

            // All bundled textures must be external, discoverable JPGs,
            // selectable by filename and native-sized across DPI settings.
            var textures = PCasa.App.Services.GalleryTextureLibrary.GetFiles();
            if (textures.Count < 4 ||
                textures.Any(p => !p.StartsWith(
                    PCasa.App.Services.GalleryTextureLibrary.DirectoryPath,
                    StringComparison.OrdinalIgnoreCase)) ||
                !textures.Any(p => p.EndsWith("Leinwand hell - fein.jpg",
                    StringComparison.OrdinalIgnoreCase)) ||
                !textures.Any(p => p.EndsWith("Leinwand dunkel - fein.jpg",
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
            var applyTexture = typeof(PCasa.App.MainWindow).GetMethod(
                "ApplyGalleryTextureSelection", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Texture selection path missing.");
            var chosen = System.IO.Path.GetFileName(textures[0]);
            applyTexture.Invoke(window, [chosen]);
            var selectedTexture = (MenuItem?)window.FindName("GalleryBackgroundTextureMenu")
                ?? throw new Exception("Single Texture background menu missing.");
            if (!selectedTexture.IsChecked ||
                window.FindName("GalleryBackgroundCanvasMenu") is not null ||
                window.FindName("GalleryBackgroundCanvasDarkMenu") is not null)
                throw new Exception("Legacy hard-coded Leinwand choices remain visible.");
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
            var lightMenu = (MenuItem?)window.FindName("ThemeLightMenu")
                ?? throw new Exception("Light theme menu missing.");
            var darkMenu = (MenuItem?)window.FindName("ThemeDarkMenu")
                ?? throw new Exception("Dark theme menu missing.");
            var checkedTags = (RadioButton?)window.FindName("SearchTagsMode")
                ?? throw new Exception("Tags filter missing.");
            var checkedAll = (RadioButton?)window.FindName("SearchAllScope")
                ?? throw new Exception("Scope filter missing.");
            var uncheckedName = (RadioButton?)window.FindName("SearchFileNameMode")
                ?? throw new Exception("Name filter missing.");
            var notForColors = (ToggleButton?)window.FindName("InvertFilterToggle")
                ?? throw new Exception("NOT control missing.");
            lightMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, lightMenu));
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
            darkMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, darkMenu));
            Flush(window);
            if (checkedTags.Foreground is not SolidColorBrush darkText ||
                darkText.Color.R < 220 ||
                notForColors.Foreground is not SolidColorBrush darkNotText ||
                darkNotText.Color.R < 220)
                throw new Exception("Dark theme selected labels lost light text.");
            notForColors.IsChecked = false;
            Console.WriteLine("PASS: WAS/WO/NOT active colors match both themes and remain readable.");

            // Real menu action must update the one gallery renderer and persist.
            var filenameBoldMenu = (MenuItem?)window.FindName("ThumbnailBoldFileNamesMenu")
                ?? throw new Exception("Bold filename option missing.");
            var gallerySurface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Gallery surface missing.");
            var boldField = typeof(BrowseGallerySurface).GetField(
                "_boldFileNames", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Bold filename rendering field missing.");
            filenameBoldMenu.IsChecked = true;
            filenameBoldMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, filenameBoldMenu));
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
            filenameBoldMenu.IsChecked = false;
            filenameBoldMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, filenameBoldMenu));
            if ((bool)boldField.GetValue(gallerySurface)!)
                throw new Exception("Disabling bold filenames did not restore normal weight.");
            Console.WriteLine("PASS: Filename boldness toggles renderer and persists without another settings owner.");

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
            if (window.ActualWidth >= 1450 &&
                (gaps.Count != items.Length - 1 ||
                 gaps.Any(gap => gap < 0 || gap > 80) ||
                 gaps.Max() - gaps.Min() > 2))
                throw new Exception("Inconsistent top-level menu spacing: " +
                    string.Join(", ", gaps.Select(gap => gap.ToString("F1"))) +
                    $"; window={window.ActualWidth:F0}, menu={menu.ActualWidth:F0}," +
                    $" sort={((Button?)window.FindName("GallerySortSelectorButton"))?.ActualWidth:F0}");
            Console.WriteLine("PASS: Menu label gaps are consistent: " +
                string.Join(", ", gaps.Select(gap => gap.ToString("F1"))));

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
            if (sortItems.Count(item => item.IsCheckable) != 8 ||
                !sortItems.Any(item => Equals(item.Header, "Name")) ||
                !sortItems.Any(item => Equals(item.Header, "Aufnahmedatum")))
                throw new Exception("Search toolbar must offer six sort keys and two directions.");
            var notToggle = window.FindName("InvertFilterToggle") as ToggleButton
                ?? throw new Exception("Search inversion toggle is missing.");
            if (!Equals(notToggle.Content, "NOT"))
                throw new Exception("Search inversion toggle is not labelled NOT.");
            Console.WriteLine("PASS: Sort selector lives only in the search toolbar beside NOT.");

            // Restore the working original header order exactly:
            // main menu | new Sort | WAS | WO | NOT | old search field.
            // The menu and search field must not move or change dimensions
            // simply because the sort control was inserted before WAS.
            var searchField = window.FindName("TagSearchBox") as TextBox
                ?? throw new Exception("Search field not found.");
            var what = window.FindName("SearchTagsMode") as RadioButton
                ?? throw new Exception("WAS group not found.");
            var where = window.FindName("SearchAllScope") as RadioButton
                ?? throw new Exception("WO group not found.");
            var whatLabel = Descendants<TextBlock>(window).FirstOrDefault(
                block => block.Text == "WAS?")
                ?? throw new Exception("WAS caption missing.");
            var whereLabel = Descendants<TextBlock>(window).FirstOrDefault(
                block => block.Text == "WO?")
                ?? throw new Exception("WO caption missing.");

            Rect Bounds(FrameworkElement element) =>
                element.TransformToAncestor(window).TransformBounds(
                    new Rect(new Point(), element.RenderSize));

            if (Descendants<TextBlock>(sortButton).Any(block =>
                    block.Text == "⌄" || block.Text == "▼"))
                throw new Exception("Redundant triangle remains inside Sort selector.");
            if (window.FindName("GallerySortDirectionIcon")
                is not System.Windows.Shapes.Path)
                throw new Exception("LAP-style sort arrow is not rendered.");

            // The hosted Windows runner caps native WPF window width at
            // 1044px even when Window.Width is set to 1500 or 2048.
            // Validate the REAL DockPanel child order at any screen size;
            // only assert pixel geometry if a sufficiently wide WPF desktop
            // actually exists (do not claim unrendered widths were tested).
            if (menu.Parent is not DockPanel header ||
                !header.LastChildFill)
                throw new Exception("Original single-row DockPanel was replaced.");
            if (whereLabel.Parent is not StackPanel whereChoices ||
                whereChoices.Parent is not Border whereGroup ||
                whatLabel.Parent is not StackPanel whatChoices ||
                whatChoices.Parent is not Border whatGroup)
                throw new Exception("WAS and WO choices must each have their own visible group border.");
            if (Math.Abs(whereLabel.FontSize - where.FontSize) > 0.01 ||
                Math.Abs(whatLabel.FontSize - what.FontSize) > 0.01)
                throw new Exception("WAS/WO captions must match the choices' font size.");
            var expectedChildren = new UIElement[]
            {
                (UIElement)searchField.Parent,
                notToggle,
                whereGroup,
                whatGroup,
                sortButton,
                menu
            };
            if (!header.Children.Cast<UIElement>().SequenceEqual(expectedChildren))
                throw new Exception(
                    "Header order is not original Search, NOT, WO, WAS, new Sort, Menu.");
            foreach (var element in expectedChildren.Take(5))
                if (DockPanel.GetDock(element) != Dock.Right)
                    throw new Exception("Existing right-docked search controls were repositioned.");
            if (Math.Abs(searchField.ActualWidth - 180) > 1 ||
                sortButton.Width != 191)
                throw new Exception("Original search width or new sort width changed.");
            if (items.Any(item => Math.Abs(item.Margin.Right - 20) > 0.01))
                throw new Exception("Existing menu spacing margins changed.");
            notToggle.IsChecked = true;
            Flush(window);
            if (searchField.TextDecorations.Count != 1 ||
                searchField.TextDecorations[0].Location != TextDecorationLocation.Strikethrough)
                throw new Exception("NOT does not strike through the actual editable search text.");
            notToggle.IsChecked = false;
            Flush(window);
            if (searchField.TextDecorations.Count != 0)
                throw new Exception("Disabling NOT did not restore normal search text.");

            foreach (var requestedWidth in new[] { 1500.0, 2048.0 })
            {
                window.Width = requestedWidth;
                Flush(window);
                if (window.ActualWidth < 1450)
                    continue; // Offscreen desktop resolution limitation.
                var row = new[]
                {
                    Bounds(menu), Bounds(sortButton), Bounds(whatLabel),
                    Bounds(whereLabel), Bounds(notToggle), Bounds(searchField)
                };
                for (var i = 1; i < row.Length; i++)
                {
                    if (row[i].Left < row[i - 1].Right - 1 ||
                        Math.Abs(row[i].Top + row[i].Height / 2 -
                                 (row[0].Top + row[0].Height / 2)) > 12)
                        throw new Exception(
                            $"At rendered window width {window.ActualWidth}, " +
                            "one-row header overlaps or wraps.");
                }
                if (row[^1].Right > window.ActualWidth - 1)
                    throw new Exception("Original search field extends past wide window.");
            }
            window.Width = 1500;
            Flush(window);
            Console.WriteLine(
                "PASS: Original one-row menu | sort | WAS | WO | NOT | search DockPanel order.");
            Console.WriteLine(
                "PASS: WAS and WO are framed with equal caption sizes; NOT toggles search strikethrough.");
            if (window.ActualWidth < 1450)
                Console.WriteLine(
                    "INFO: Runner desktop is too narrow for visual wide-window validation.");

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

            var viewerFolderMenu = (MenuItem?)window.FindName("PhotoSequenceFolderMenu")
                ?? throw new Exception("Folder sequence menu missing.");
            var viewerGalleryMenu = (MenuItem?)window.FindName("PhotoSequenceGalleryMenu")
                ?? throw new Exception("Gallery sequence menu missing.");
            var savedScope = PhotoViewerSettings.Load().SequenceScope;
            try
            {
                viewerFolderMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,
                    viewerFolderMenu));
                if (!viewerFolderMenu.IsChecked || viewerGalleryMenu.IsChecked ||
                    PhotoViewerSettings.Load().SequenceScope != PhotoSequenceScope.CurrentFolder)
                    throw new Exception("Folder mode did not persist or update checked menu.");
                viewerGalleryMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent,
                    viewerGalleryMenu));
                if (!viewerGalleryMenu.IsChecked || viewerFolderMenu.IsChecked ||
                    PhotoViewerSettings.Load().SequenceScope != PhotoSequenceScope.Gallery)
                    throw new Exception("Gallery mode did not persist or update checked menu.");
            }
            finally
            {
                var restore = savedScope == PhotoSequenceScope.CurrentFolder
                    ? viewerFolderMenu : viewerGalleryMenu;
                restore.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, restore));
            }
            Console.WriteLine("PASS: One persistent image-sequence setting switches both modes.");

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

            // A fullscreen round trip is a change of viewport, not an
            // implicit Fit->percentage conversion. Use a synthetic image
            // description and the real WPF viewer windows, without file IO.
            var viewerType = typeof(PCasa.App.MainWindow);
            var reflectFlags = System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;
            var viewerOpenField = viewerType.GetField("_viewerOpen", reflectFlags)!;
            var viewerInfoField = viewerType.GetField("_viewerImageInfo", reflectFlags)!;
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
            try
            {
                viewerOpenField.SetValue(window, true);
                viewerInfoField.SetValue(window, new PhotoViewerImageInfo(
                    999, @"C:\synthetic\no-file.jpg", "no-file.jpg", 2400, 1600));
                panel.Visibility = Visibility.Visible;
                Flush(window);
                fitField.SetValue(window, true);
                zoomField.SetValue(window, 100d);
                applyLayout.Invoke(window, null);
                Flush(window);
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
                panel.Visibility = Visibility.Collapsed;
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
