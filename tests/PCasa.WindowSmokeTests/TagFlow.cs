using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PCasa.App.Controls;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

internal static class TagFlow
{
    public static int Run()
    {
        // The entrypoint blocks execution outside ephemeral GitHub Actions.
        var sourceRoot = Path.Combine(Path.GetTempPath(),
            "PCasa-WpfTagFlow-" + Guid.NewGuid().ToString("N"));
        var albumA = Path.Combine(sourceRoot, "A");
        var albumB = Path.Combine(sourceRoot, "B");
        Directory.CreateDirectory(albumA);
        Directory.CreateDirectory(albumB);

        PCasa.App.MainWindow? window = null;
        try
        {
            for (var i = 0; i < 12; i++)
            {
                CreatePhoto(Path.Combine(albumA,
                    i < 4 ? $"a-{i:00}-topaz.jpg" : $"a-{i:00}.jpg"));
                CreatePhoto(Path.Combine(albumB,
                    i < 6 ? $"b-{i:00}-topaz.jpg" : $"b-{i:00}.jpg"));
            }

            var db = new LocalDatabase();
            db.Initialize();
            var repo = new FolderRepository(db);
            repo.Add(sourceRoot);
            var root = repo.GetAll().Single(folder =>
                string.Equals(folder.LocalPath, sourceRoot,
                    StringComparison.OrdinalIgnoreCase));
            var scanner = new FolderScanner(new ImageCatalogRepository(db),
                new ThumbnailCache(), repo, new BrowseThumbnailProfileRepository(db));
            scanner.Scan(root);

            var catalog = new ImageCatalogRepository(db);
            var folders = catalog.GetPhotoFolders();
            var a = folders.Single(folder =>
                string.Equals(folder.LocalPath, albumA, StringComparison.OrdinalIgnoreCase));
            var b = folders.Single(folder =>
                string.Equals(folder.LocalPath, albumB, StringComparison.OrdinalIgnoreCase));
            if (catalog.CountAll() != 24)
                throw new Exception("Synthetic WPF catalog is incomplete.");

            // One GPS photo per album. Selecting album A must keep its point
            // visible while the context markers from album B remain present.
            long gpsA, gpsB;
            using (var connection = db.OpenConnection())
            {
                long SeedGps(long photoFolderId, double latitude, double longitude)
                {
                    using var insert = connection.CreateCommand();
                    insert.CommandText = """
                        INSERT INTO image_exif (
                            image_id, source_modified_utc_ticks, source_file_size,
                            exif_json, gps_latitude, gps_longitude)
                        SELECT id, modified_utc_ticks, file_size, '{}', $lat, $lon
                        FROM images WHERE photo_folder_id = $folder
                        ORDER BY id LIMIT 1
                        ON CONFLICT(image_id) DO UPDATE SET
                            gps_latitude = excluded.gps_latitude,
                            gps_longitude = excluded.gps_longitude;
                        """;
                    insert.Parameters.AddWithValue("$folder", photoFolderId);
                    insert.Parameters.AddWithValue("$lat", latitude);
                    insert.Parameters.AddWithValue("$lon", longitude);
                    insert.ExecuteNonQuery();
                    using var query = connection.CreateCommand();
                    query.CommandText =
                        "SELECT MIN(id) FROM images WHERE photo_folder_id = $folder";
                    query.Parameters.AddWithValue("$folder", photoFolderId);
                    return (long)query.ExecuteScalar()!;
                }
                gpsA = SeedGps(a.Id, 40.4168, -3.7038);
                gpsB = SeedGps(b.Id, 50.108, 11.4625);
            }

            using (var connection = db.OpenConnection())
            using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE image_tags
                    SET keywords = CASE
                        WHEN image_id IN (
                            SELECT id FROM images WHERE photo_folder_id = $a
                        ) THEN 'roma'
                        ELSE 'berlin'
                    END;
                    """;
                update.Parameters.AddWithValue("$a", a.Id);
                update.ExecuteNonQuery();
            }
            // A controlled UI test should not run an independent folder monitor.
            new BackgroundScanSettings { Enabled = false }.Save();

            var app = new PCasa.App.App();
            app.InitializeComponent();
            window = new PCasa.App.MainWindow
            {
                // Narrower viewport forces several thumbnail rows per folder;
                // the second folder can then occupy the top of the viewport.
                Width = 1040,
                Height = 670
            };
            window.Show();

            var surface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Missing real gallery surface.");
            var scroller = (ScrollViewer?)window.FindName("BrowseScrollViewer")
                ?? throw new Exception("Missing gallery ScrollViewer.");
            var tree = (TreeView?)window.FindName("FoldersTree")
                ?? throw new Exception("Missing folder tree.");
            var search = (TextBox?)window.FindName("TagSearchBox")
                ?? throw new Exception("Missing tag search field.");
            var status = (TextBlock?)window.FindName("StatusText")
                ?? throw new Exception("Missing status line.");

            Until(window, () => surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is not null,
                "Catalog pictures never appeared in the WPF gallery.");

            // The agreed default is Name ASCENDING, independently within
            // each photo folder. Changing the menu direction must not merge
            // the two sections or disrupt the existing selection/search.
            var sortButton = (Button?)window.FindName("GallerySortSelectorButton")
                ?? throw new Exception("Sorting is not in the search toolbar.");
            var sortItems = sortButton.ContextMenu?.Items.OfType<MenuItem>().ToArray()
                ?? throw new Exception("Search bar sort choices not available.");
            MenuItem SortItem(string key) =>
                sortItems.Single(item => Equals(item.Tag, key));
            var sortName = SortItem("Name");
            var sortDirectionButton = (Button?)window.FindName("GallerySortDirectionButton")
                ?? throw new Exception("Independent direction button missing.");
            var sortDirection = (System.Windows.Shapes.Path?)window.FindName(
                "GallerySortDirectionToggleIcon")
                ?? throw new Exception("Separate direction arrow not found.");
            var downArrow = Geometry.Parse(
                "M 12,2 L 12,20 M 6,14 L 12,20 L 18,14").ToString();
            var upArrow = Geometry.Parse(
                "M 12,20 L 12,2 M 6,8 L 12,2 L 18,8").ToString();
            var sortLabel = (TextBlock?)window.FindName("GallerySortSelectedFieldLabel")
                ?? throw new Exception("Sort field label missing.");
            if (!sortName.IsChecked ||
                sortDirection.Data?.ToString() != downArrow ||
                sortLabel.Text != "Name")
                throw new Exception("Default Name ascending sort not displayed.");
            var sortIndexField = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            IReadOnlyList<BrowseThumbnailIndexEntry> SortedGallery()
                => (IReadOnlyList<BrowseThumbnailIndexEntry>)
                    sortIndexField.GetValue(window)!;
            sortDirectionButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, sortDirectionButton));
            Until(window, () =>
            {
                var images = SortedGallery();
                var bImages = images.Where(image => image.PhotoFolderId == b.Id)
                    .Select(image => image.FileName).ToArray();
                return sortName.IsChecked &&
                    sortDirection.Data?.ToString() == upArrow && sortLabel.Text == "Name" &&
                    images.Count == 24 && bImages.Length == 12 &&
                    bImages.SequenceEqual(bImages.OrderByDescending(
                        name => name, StringComparer.OrdinalIgnoreCase)) &&
                    images.Take(12).All(image => image.PhotoFolderId == a.Id) &&
                    images.Skip(12).All(image => image.PhotoFolderId == b.Id);
            }, "Descending sort toggle changed folder order or failed within each folder.");
            sortDirectionButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, sortDirectionButton));
            Until(window, () =>
            {
                var images = SortedGallery();
                var names = images.Where(image => image.PhotoFolderId == b.Id)
                    .Select(image => image.FileName).ToArray();
                return sortDirection.Data?.ToString() == downArrow &&
                    names.Length == 12 &&
                    names.SequenceEqual(names.OrderBy(
                        name => name, StringComparer.OrdinalIgnoreCase));
            }, "Ascending direction toggle failed.");
            Console.WriteLine("PASS: Independent direction toggle preserves folder grouping and sort order.");

            var monitoredRootItem = Walk(tree).FirstOrDefault(item =>
                string.Equals(ReadPath(item), sourceRoot,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Monitored root is missing from tree.");
            var albumBItem = Walk(tree).FirstOrDefault(item =>
                string.Equals(ReadPath(item), albumB,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Album B missing from tree.");
            FolderTreeNavigation.ExpandAncestors(monitoredRootItem);
            monitoredRootItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, monitoredRootItem),
                "Parent folder could not be selected.");

            var albumBTop = surface.GetFolderOffset(b.Id)!.Value;
            scroller.ScrollToVerticalOffset(albumBTop + 100);
            Until(window,
                () => surface.GetFolderIdAtOffset(scroller.VerticalOffset + 4) == b.Id,
                "Cannot scroll second album into top of viewport; target=" +
                albumBTop.ToString("F1") + ", actual=" +
                scroller.VerticalOffset.ToString("F1") + ", extent=" +
                scroller.ExtentHeight.ToString("F1") + ", viewport=" +
                scroller.ViewportHeight.ToString("F1"));
            surface.SetViewport(scroller.VerticalOffset,
                Math.Max(1, scroller.ViewportHeight));
            var original = surface.CaptureImageAnchor(scroller.VerticalOffset)
                ?? throw new Exception("Cannot remember original photo.");

            // ScrollChanged normally synchronizes tree selection to the album.
            // Explicitly choose the parent AFTER scrolling so this test
            // captures an intentional parent-folder focus before searching.
            monitoredRootItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, monitoredRootItem),
                "Could not restore explicit parent selection before search.");

            search.Text = "roma";
            Until(window, () => status.Text.Contains("Bilder mit Tags: roma",
                       StringComparison.OrdinalIgnoreCase) &&
                   surface.GetFolderOffset(a.Id) is not null &&
                   surface.GetFolderOffset(b.Id) is null,
                "Live tag search did not filter to album A.");
            if (!ReferenceEquals(tree.SelectedItem, monitoredRootItem))
                throw new Exception("Tag search changed the original parent focus.");
            Console.WriteLine("PASS: Real tag search filters SQLite-indexed photos without changing tree focus.");
            // Exercise the actual WPF typing/debounce and SQLite search for
            // multi-keyword AND. There is exactly one photo with BOTH tags.
            // Restore its original catalog keyword before all later tests.
            var extraTaggedImage = Path.Combine(albumA, "a-00-topaz.jpg");
            void SetExtraKeyword(string keywords)
            {
                using var connection = db.OpenConnection();
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE image_tags SET keywords=$keywords
                    WHERE image_id=(SELECT id FROM images
                                    WHERE local_path=$path COLLATE NOCASE);
                    """;
                update.Parameters.AddWithValue("$keywords", keywords);
                update.Parameters.AddWithValue("$path", extraTaggedImage);
                if (update.ExecuteNonQuery() != 1)
                    throw new Exception("Could not seed multi-Tag search fixture.");
            }
            SetExtraKeyword("roma\nReise Hamburg");
            try
            {
                search.Text = "roma, reise hamburg";
                Until(window, () => status.Text.Contains("roma, reise hamburg",
                        StringComparison.OrdinalIgnoreCase) &&
                    SortedGallery().Count == 1 &&
                    SortedGallery()[0].FileName == "a-00-topaz.jpg",
                    "Comma-separated Tag AND did not return exactly one matching photo.");
                search.Text = "reise hamburg;ROM";
                Until(window, () => status.Text.Contains("reise hamburg;ROM",
                        StringComparison.OrdinalIgnoreCase) &&
                    SortedGallery().Count == 1 &&
                    SortedGallery()[0].FileName == "a-00-topaz.jpg",
                    "Semicolon-separated Tag AND did not use case-insensitive prefixes.");
                search.Text = "roma, berlin";
                Until(window, () => status.Text.Contains("roma, berlin",
                        StringComparison.OrdinalIgnoreCase) &&
                    SortedGallery().Count == 0,
                    "Multi-Tag AND matched pictures without all required Tags.");
                search.Text = "roma; reise hamburg, missing";
                Until(window, () => status.Text.Contains("roma; reise hamburg, missing",
                        StringComparison.OrdinalIgnoreCase) &&
                    SortedGallery().Count == 0,
                    "Mixed separators incorrectly ignored a missing required Tag.");
            }
            finally
            {
                SetExtraKeyword("roma");
                search.Text = "roma";
            }
            Until(window, () => status.Text.Contains("Bilder mit Tags: roma",
                    StringComparison.OrdinalIgnoreCase) &&
                SortedGallery().Count == 12,
                "Multi-Tag test did not restore the single-Tag gallery.");
            Console.WriteLine("PASS: WPF comma/semicolon multi-Tag AND search and single-Tag restoration.");


            FolderTreeNavigation.ExpandAncestors(albumBItem);
            albumBItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, albumBItem),
                "Could not navigate inside search results.");

            search.Clear();
            Until(window, () => !status.Text.Contains("Bilder mit Tags:",
                       StringComparison.OrdinalIgnoreCase) &&
                   surface.GetFolderOffset(b.Id) is not null &&
                   ReferenceEquals(tree.SelectedItem, monitoredRootItem),
                "Leaving search did not restore the original tree focus.");

            surface.SetViewport(scroller.VerticalOffset,
                Math.Max(1, scroller.ViewportHeight));
            var restoredOffset = surface.GetImageAnchorOffset(
                original.ImageId, original.RelativeY)
                ?? throw new Exception("Original image vanished when search ended.");
            if (Math.Abs(restoredOffset - scroller.VerticalOffset) >
                    Math.Max(200, surface.ThumbnailWidth * 2))
                throw new Exception("Leaving tag search did not restore photo position.");
            Console.WriteLine("PASS: Clearing live search restores original parent selection and photo anchor.");

            // Search type is independent of scope. File-name matching uses
            // the existing catalogue index (no filesystem walk).
            var fileMode = (RadioButton?)window.FindName("SearchFileNameMode")
                ?? throw new Exception("Filename search mode missing.");
            var tagMode = (RadioButton?)window.FindName("SearchTagsMode")
                ?? throw new Exception("Tag search mode missing.");
            var global = (RadioButton?)window.FindName("SearchAllScope")
                ?? throw new Exception("Global scope missing.");
            var treeScope = (RadioButton?)window.FindName("SearchTreeScope")
                ?? throw new Exception("Tree scope missing.");
            var current = (RadioButton?)window.FindName("SearchCurrentScope")
                ?? throw new Exception("Current-only scope missing.");
            // A collapsed subtree is a display option, never a search
            // restriction. Check global tag/filename results through WPF.
            var appearanceMenu = (MenuItem?)window.FindName("AppearanceSettingsMenuItem")
                ?? throw new Exception("Appearance dialog option missing.");
            void SetHideCollapsed(bool enabled)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = Application.Current.Windows
                        .OfType<PCasa.App.AppearanceSettingsWindow>().Single();
                    dialog.HideCollapsedCheck.IsChecked = enabled;
                    dialog.ConfirmButton.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent, dialog.ConfirmButton));
                }), DispatcherPriority.ContextIdle);
                appearanceMenu.RaiseEvent(
                    new RoutedEventArgs(MenuItem.ClickEvent, appearanceMenu));
            }
            search.Clear();
            SetHideCollapsed(true);
            FolderTreeNavigation.ExpandAncestors(monitoredRootItem);
            monitoredRootItem.IsExpanded = false;
            Until(window, () => surface.HiddenFolderIds.Contains(a.Id) &&
                surface.HiddenFolderIds.Contains(b.Id),
                "Collapsed ordinary gallery did not hide descendant sections.");

            global.IsChecked = true;
            tagMode.IsChecked = true;
            search.Text = "berlin";
            Until(window, () => SortedGallery().Count == 12 &&
                SortedGallery().All(image => image.PhotoFolderId == b.Id) &&
                surface.GetFolderOffset(b.Id) is not null &&
                !surface.HiddenFolderIds.Contains(b.Id),
                "Global tag search excluded an indexed collapsed folder.");
            Console.WriteLine(
                "PASS: Global tag search includes collapsed folders despite gallery-only hide option.");
            // The new independent toggle limits the search universe to the
            // very same collapsed-folder mask as the ordinary gallery.
            // Existing global Tag searches must remain unrestricted when
            // the toggle is off.
            var galleryOnly = (System.Windows.Controls.Primitives.ToggleButton?)
                window.FindName("GalleryViewFilterToggle")
                ?? throw new Exception("Galerieansicht search toggle missing.");
            var galleryNot = (System.Windows.Controls.Primitives.ToggleButton?)
                window.FindName("InvertFilterToggle")
                ?? throw new Exception("NOT toggle missing.");
            galleryOnly.IsChecked = true;
            Until(window, () => SortedGallery().Count == 0,
                "Galerieansicht search included hidden descendants.");
            galleryNot.IsChecked = true;
            Until(window, () => status.Text.Contains("Bilder ohne Tags: berlin",
                    StringComparison.OrdinalIgnoreCase) &&
                SortedGallery().Count == 0,
                "NOT included hidden descendants outside the Galerieansicht universe.");
            galleryNot.IsChecked = false;
            monitoredRootItem.IsExpanded = true;
            Until(window, () => SortedGallery().Count == 12 &&
                SortedGallery().All(image => image.PhotoFolderId == b.Id),
                "Expanding the folder did not refresh Galerieansicht Tag hits.");
            galleryNot.IsChecked = true;
            Until(window, () => SortedGallery().Count == 12 &&
                SortedGallery().All(image => image.PhotoFolderId == a.Id),
                "NOT did not complement Tags within Galerieansicht.");
            galleryNot.IsChecked = false;
            monitoredRootItem.IsExpanded = false;
            Until(window, () => SortedGallery().Count == 0,
                "Collapsing the folder did not refresh Galerieansicht search.");
            galleryOnly.IsChecked = false;
            Until(window, () => SortedGallery().Count == 12 &&
                SortedGallery().All(image => image.PhotoFolderId == b.Id),
                "Disabling Galerieansicht did not restore unrestricted global hits.");
            Console.WriteLine(
                "PASS: Galerieansicht respects folder expansion; NOT stays inside visible universe.");


            fileMode.IsChecked = true;
            search.Text = "topaz";
            Until(window, () => SortedGallery().Count == 10 &&
                surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is not null &&
                surface.HiddenFolderIds.Count == 0,
                "Global filename search excluded hidden descendant folders.");
            Console.WriteLine(
                "PASS: Global filename search includes all collapsed descendants.");
            galleryOnly.IsChecked = true;
            Until(window, () => SortedGallery().Count == 0,
                "Galerieansicht did not limit filename search to the visible tree.");
            galleryOnly.IsChecked = false;
            Until(window, () => SortedGallery().Count == 10,
                "Disabling Galerieansicht did not restore filename matches.");
            Console.WriteLine(
                "PASS: Galerieansicht applies equally to indexed filename searches.");


            search.Clear();
            Until(window, () => surface.HiddenFolderIds.Contains(a.Id) &&
                surface.HiddenFolderIds.Contains(b.Id),
                "Clearing search failed to restore gallery-only collapsed-folder hiding.");
            monitoredRootItem.IsExpanded = true;
            SetHideCollapsed(false);
            Until(window, () => surface.HiddenFolderIds.Count == 0,
                "Disabling collapsed-folder option failed to restore ordinary gallery.");
            fileMode.IsChecked = true;
            global.IsChecked = true;
            search.Text = "topaz";
            Until(window, () => status.Text.Contains("Bilder mit Dateiname: topaz") &&
                surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is not null,
                "Global filename search did not match both folders.");

            monitoredRootItem.IsSelected = true;
            treeScope.IsChecked = true;
            Until(window, () => surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is not null,
                "Tree search lost child directories.");

            albumBItem.IsSelected = true;
            current.IsChecked = true;
            Until(window, () => surface.GetFolderOffset(b.Id) is not null &&
                surface.GetFolderOffset(a.Id) is null,
                "Current-only search included a sibling folder.");

            var invertFilter = (System.Windows.Controls.Primitives.ToggleButton?)
                window.FindName("InvertFilterToggle")
                ?? throw new Exception("Persistent toolbar inversion toggle missing.");
            var filteredIndex = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            invertFilter.IsChecked = true;
            Until(window, () => status.Text.Contains("Bilder ohne Dateiname: topaz") &&
                ((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    filteredIndex.GetValue(window)!).Count == 6,
                "Inverted filename filter did not show the six originals.");
            var complement = (IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                filteredIndex.GetValue(window)!;
            if (complement.Any(image => image.FileName.Contains(
                    "topaz", StringComparison.OrdinalIgnoreCase)) ||
                complement.Any(image => image.PhotoFolderId != b.Id))
                throw new Exception("Inverted results leaked matching files or other folders.");
            invertFilter.IsChecked = false;
            Until(window, () => status.Text.Contains("Bilder mit Dateiname: topaz"),
                "Switching back to matching results failed.");
            Console.WriteLine("PASS: Filter results invert independently within exact folder scope.");

            // The global filter has an inline circled red X only when
            // nonempty. Clearing through it retains NOT/filter semantics.
            var globalClear = (Button?)window.FindName("ClearTagSearchButton")
                ?? throw new Exception("Global search clear X missing.");
            if (globalClear.Visibility != Visibility.Visible)
                throw new Exception("Global search X not visible for entered text.");
            if (!ReferenceEquals(globalClear.Style,
                    window.FindResource("PcasaSearchClearButtonStyle")))
                throw new Exception("Global clear X diverges from shared PCasa style.");

            // NOT is an independent persistent state; clearing the query
            // must not silently reset a selected inversion toggle.
            invertFilter.IsChecked = true;
            search.Clear();
            Until(window, () => string.IsNullOrWhiteSpace(search.Text) &&
                ((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    filteredIndex.GetValue(window)!).Count > 0,
                "Clearing an inverted query did not restore normal unfiltered images.");
            if (invertFilter.IsChecked != true)
                throw new Exception("Clearing search silently changed the independent NOT toggle.");
            invertFilter.IsChecked = false;
            // Clearing search intentionally restores the pre-search tree
            // focus. Re-select the exact album before testing Current scope.
            albumBItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, albumBItem),
                "Could not reselect current album after clearing search.");
            search.Text = "topaz";
            Until(window, () => status.Text.Contains("Bilder mit Dateiname: topaz") &&
                surface.GetFolderOffset(b.Id) is not null,
                "Filename filter did not resume in the current album after persistent NOT toggle test.");
            Console.WriteLine("PASS: NOT stays selected until the user explicitly disables it.");


            var selectedField = typeof(PCasa.App.MainWindow).GetField(
                "_selectedImageIds", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var selected = (HashSet<long>)selectedField.GetValue(window)!;
            var filteredIndexField = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var displayed = (IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                filteredIndexField.GetValue(window)!;
            var selectFolder = typeof(PCasa.App.MainWindow).GetField(
                "_gallerySelection", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            ((GallerySelection)selectFolder.GetValue(window)!).SelectFolder(b.Id, displayed, selected);
            var invert = typeof(PCasa.App.MainWindow).GetMethod("InvertCurrentFolderSelection",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            invert.Invoke(window, null);
            if (selected.Count != 6)
                throw new Exception("Inverting 6 Topaz files must select 6 hidden originals.");
            search.Clear();
            Until(window, () => surface.GetFolderOffset(b.Id) is not null &&
                surface.GetFolderOffset(a.Id) is not null,
                "Clearing filename filter did not restore gallery.");
            if (selected.Count != 6)
                throw new Exception("Clearing filename filter lost inverted selection.");
            Console.WriteLine("PASS: Filename/tag switching, all three scopes and hidden-file inversion.");

            // GPS lives in the SAME resizable workspace. Expanding hides
            // (but does not unset) the previous folder-pane preference.
            // Fullscreen overlays this grid, never opens a second window.
            var workspace = (Grid?)window.FindName("BrowseWorkspaceGrid")
                ?? throw new Exception("Shared workspace grid is missing.");
            var folderPane = (Border?)window.FindName("FolderSidebarPanel")
                ?? throw new Exception("Folder panel is missing.");
            var folderColumn = (ColumnDefinition?)window.FindName("FolderSidebarColumn")
                ?? throw new Exception("Folder column is missing.");
            var galleryColumn = (ColumnDefinition?)window.FindName("GalleryWorkspaceColumn")
                ?? throw new Exception("Gallery column is missing.");
            var gpsColumn = (ColumnDefinition?)window.FindName("InfoSidebarColumn")
                ?? throw new Exception("GPS column is missing.");
            var folderToggle = (Button?)window.FindName("FolderSidebarToggleButton")
                ?? throw new Exception("Folder visibility toggle is missing.");
            var gpsToggle = (Button?)window.FindName("GpsSidebarButton")
                ?? throw new Exception("GPS sidebar toggle is missing.");
            var gpsExpand = (Button?)window.FindName("GpsExpandButton")
                ?? throw new Exception("GPS expand button is missing.");
            var gpsFullscreen = (Button?)window.FindName("GpsFullscreenButton")
                ?? throw new Exception("GPS fullscreen button is missing.");
            var sidebarPanel = (Border?)window.FindName("InfoSidebarPanel")
                ?? throw new Exception("GPS panel is missing.");

            // Picasa-inspired circled X is ONE centrally defined vector
            // drawing per semantic action: muted = dismiss, red = remove.
            var closeGlyph = (ControlTemplate?)window.FindResource(
                "PcasaCloseGlyphTemplate")
                ?? throw new Exception("Shared muted circled close glyph missing.");
            var deleteGlyph = (ControlTemplate?)window.FindResource(
                "PcasaDeleteGlyphTemplate")
                ?? throw new Exception("Shared red circled remove glyph missing.");
            if (ReferenceEquals(closeGlyph, deleteGlyph) ||
                window.FindResource("PcasaDeleteIconBrush")
                    is not SolidColorBrush deleteBrush ||
                deleteBrush.Color.R <= deleteBrush.Color.B)
                throw new Exception("Close and delete symbols lost distinct action semantics.");
            var sidebarClose = (Button?)window.FindName("CloseSidebarButton")
                ?? throw new Exception("Sidebar close icon button missing.");
            var popupClose = (Button?)window.FindName("GpsPhotoCloseButton")
                ?? throw new Exception("GPS preview close icon button missing.");
            if (sidebarClose.Content is not ContentControl sidebarGlyph ||
                !ReferenceEquals(sidebarGlyph.Template, closeGlyph) ||
                popupClose.Content is not ContentControl previewGlyph ||
                !ReferenceEquals(previewGlyph.Template, closeGlyph))
                throw new Exception("Sidebar and GPS photo use different close symbols.");

            // Expand/fullscreen may change direction, NOT control dimensions.
            // Stroke weight was reduced to match the other PCasa glyphs.
            var expandArrow = (System.Windows.Shapes.Path?)window.FindName(
                "GpsExpandArrow")
                ?? throw new Exception("GPS expand glyph missing.");
            var fullscreenCorners = (System.Windows.Shapes.Path?)window.FindName(
                "GpsFullscreenCorners")
                ?? throw new Exception("GPS fullscreen glyph missing.");
            if (gpsExpand.Width != 43 || gpsExpand.Height != 42 ||
                gpsFullscreen.Width != 43 || gpsFullscreen.Height != 42 ||
                expandArrow.StrokeThickness > 2.3 ||
                fullscreenCorners.StrokeThickness > 2.3)
                throw new Exception("Expand/fullscreen size or slim stroke regression.");

            var zoomNavigator = new PhotoZoomNavigator();
            var navigatorHeader = ((StackPanel)zoomNavigator.Child).Children[0] as Grid;
            var zoomClose = navigatorHeader?.Children.OfType<Button>().SingleOrDefault();
            if (zoomClose?.Content is not ContentControl zoomCloseGlyph ||
                !ReferenceEquals(zoomCloseGlyph.Template, closeGlyph))
                throw new Exception("Photo zoom navigator still uses an unrelated X.");
            Console.WriteLine("PASS: Circled close/remove glyphs are shared; GPS expand/fullscreen stroke is lighter.");
            void Click(Button button) =>
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

            // Establish a known folder-visible state even if saved appearance
            // from another smoke run exists on this test runner.
            if (sidebarPanel.Visibility == Visibility.Visible)
            {
                var close = typeof(PCasa.App.MainWindow).GetMethod(
                    "CloseSidebar_Click",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!;
                close.Invoke(window, [null, new RoutedEventArgs()]);
            }
            if (folderPane.Visibility != Visibility.Visible) Click(folderToggle);
            // Provider credentials are encrypted under the current Windows
            // account. Public PCasa distribution must never ship an API key.
            var keyProvider = new GpsMapProvider
            {
                Name = "User licensed map",
                Format = "xyz",
                Url = "https://example.org/tiles/{z}/{x}/{y}.png?key={key}",
                ProtectedKey = GpsMapProvider.EncryptKey("test-api-key")
            };
            if (!GpsMapProvider.IsValid(keyProvider) ||
                !keyProvider.ResolveUrl().Contains("test-api-key") ||
                System.Text.Json.JsonSerializer.Serialize(keyProvider)
                    .Contains("test-api-key", StringComparison.Ordinal))
                throw new Exception("Provider selection did not encrypt the personal API key.");
            if (GpsMapProvider.IsValid(new GpsMapProvider
                { Name = "Not HTTPS", Format = "xyz",
                  Url = "http://example.org/{z}/{x}/{y}.png" }))
                throw new Exception("Unencrypted tile endpoints must be rejected.");
            var selectionBeforeLayout = selected.ToHashSet();
            Click(gpsToggle);
            var mapButton = (Button?)window.FindName("GpsProviderButton")
                ?? throw new Exception("Map selection button missing.");
            Click(mapButton);
            if (mapButton.ContextMenu?.Items.Count < 4)
                throw new Exception("Map button lost provider profiles.");
            mapButton.ContextMenu.IsOpen = false;

            var readGps = typeof(PCasa.App.MainWindow).GetMethod("ReadGpsPoints",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS catalog projection not found.");
            var projected = ((System.Collections.IEnumerable)readGps.Invoke(
                window, [new long[] { gpsA }, b.Id, CancellationToken.None, null])!)
                .Cast<object>().ToArray();
            bool HasPoint(long id, bool isSelected) => projected.Any(point =>
                (long)point.GetType().GetProperty("Id")!.GetValue(point)! == id &&
                (bool)point.GetType().GetProperty("Selected")!.GetValue(point)! == isSelected);
            if (!HasPoint(gpsA, true) || !HasPoint(gpsB, false))
                throw new Exception("GPS selection dropped the surrounding folder's markers.");

            // GPS edits use the existing map and selected gallery images.
            // Search is a draft; only explicit buttons may write originals.
            foreach (var controlName in new[]
            {
                "GpsPlaceSearchBox", "GpsPlaceSearchButton", "GpsPlaceResults",
                "GpsPlaceResultsPopup", "GpsEditPanel",
                "GpsPlaceSearchClearButton",
                "GpsPickPositionToggle", "GpsApplyMissingButton",
                "GpsReplaceButton", "GpsRemoveButton", "GpsDraftCoordinates",
                "GpsShowAreaButton", "GpsClearAreaButton"
            })
                if (window.FindName(controlName) is null)
                    throw new Exception("GPS editing control missing: " + controlName);
            if (window.FindName("GpsApplyMissingButton") is not Button missingGpsButton ||
                missingGpsButton.IsEnabled ||
                window.FindName("GpsRemoveButton") is not Button removeGpsButton)
                throw new Exception("GPS editor must require a draft for assignment.");
            // Removal is enabled only when the actual gallery has selected
            // images. This test's explicit ReadGpsPoints projection does NOT
            // itself select a photo in the real gallery.
            if (removeGpsButton.IsEnabled && selected.Count == 0)
                throw new Exception("GPS removal enabled without an actual selection.");

            // The GPS editing row is permanently visible. At narrow widths
            // its captions collapse to icons, without moving the controls.
            var gpsToolsPanel = (Grid?)window.FindName("GpsEditPanel")
                ?? throw new Exception("GPS actions grid is missing.");
            if (gpsToolsPanel.Visibility != Visibility.Visible ||
                gpsToolsPanel.ColumnDefinitions.Count != 4)
                throw new Exception("GPS Pick/Assign/Replace/Remove must remain in one row.");
            Console.WriteLine("PASS: GPS tools are immediately available without an editor launcher.");

            // Search results are a popup UNDER the search box, not an
            // ever-visible white ComboBox consuming a second sidebar row.
            var resultPopup = (System.Windows.Controls.Primitives.Popup?)
                window.FindName("GpsPlaceResultsPopup")
                ?? throw new Exception("GPS place dropdown popup is missing.");
            var resultList = (ListBox?)window.FindName("GpsPlaceResults")
                ?? throw new Exception("GPS place dropdown list is missing.");
            var searchBox = (TextBox?)window.FindName("GpsPlaceSearchBox")
                ?? throw new Exception("GPS place search input is missing.");
            if (resultPopup.IsOpen ||
                resultPopup.PlacementTarget != searchBox ||
                resultList.Background is not SolidColorBrush resultBackground ||
                resultBackground.Color != ((SolidColorBrush)window.FindResource("PcasaPanelBrush")).Color)
                throw new Exception("GPS place results must open under the field using the PCasa theme.");
            var placeHitType = typeof(PCasa.App.MainWindow).GetNestedType("PlaceHit",
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("GPS result model is missing.");
            var hit = Activator.CreateInstance(placeHitType,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic,
                null, ["Chilehaus, Hamburg", 53.548, 10.0], null)
                ?? throw new Exception("Could not create test GPS place.");
            resultList.ItemsSource = new[] { hit };
            resultPopup.IsOpen = true;
            if (!resultPopup.IsOpen ||
                resultList.Items.Count != 1 ||
                resultList.SelectedItem is not null)
                throw new Exception("GPS popup did not display an uncommitted result.");
            resultList.SelectedIndex = 0;
            // Moving the ListBox highlight must NOT write coordinates.
            var draftField = typeof(PCasa.App.MainWindow).GetField("_gpsDraft",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS draft field missing.");
            var beforePlace = draftField.GetValue(window);
            var choosePlace = typeof(PCasa.App.MainWindow).GetMethod("ChooseGpsPlace",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS selection callback missing.");
            if (!Equals(beforePlace, draftField.GetValue(window)))
                throw new Exception("Highlighting a place changed GPS without confirmation.");
            choosePlace.Invoke(window, [hit]);
            if (resultPopup.IsOpen ||
                draftField.GetValue(window) is null ||
                resultList.SelectedItem is not null)
                throw new Exception("Chosen GPS place did not close popup and set the draft.");
            // Changing the query must invalidate the popup and old hits.
            resultList.ItemsSource = new[] { hit };
            resultPopup.IsOpen = true;
            searchBox.Text = "different query";
            if (resultPopup.IsOpen || resultList.ItemsSource is not null)
                throw new Exception("GPS search retained stale results after query changed.");
            var gpsClear = (Button?)window.FindName("GpsPlaceSearchClearButton")
                ?? throw new Exception("GPS search clear X missing.");
            if (gpsClear.Visibility != Visibility.Visible)
                throw new Exception("GPS clear X not shown for entered place text.");
            gpsClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, gpsClear));
            if (searchBox.Text.Length != 0 || gpsClear.Visibility != Visibility.Collapsed ||
                resultPopup.IsOpen || resultList.ItemsSource is not null)
                throw new Exception("GPS clear X failed to clear query and stale search results.");
            Console.WriteLine("PASS: GPS clear X only appears for text; place results reset after clearing.");
            Console.WriteLine("PASS: Theme-aware GPS result popup uses click/Enter confirmation and no reserved row.");

            var tagEntry = (TextBox?)window.FindName("TagEntryBox")
                ?? throw new Exception("Tag entry missing.");
            var tagEntryClear = (Button?)window.FindName("TagEntryClearButton")
                ?? throw new Exception("Tag entry clear X missing.");
            tagEntry.Text = "test draft";
            if (tagEntryClear.Visibility != Visibility.Visible)
                throw new Exception("Tag entry clear X not visible while typing.");
            tagEntryClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagEntryClear));
            if (tagEntry.Text.Length != 0 ||
                tagEntryClear.Visibility != Visibility.Collapsed)
                throw new Exception("Tag entry clear X did not clear only the editor.");
            Console.WriteLine("PASS: Tag entry uses the shared clear-X style.");

            // GPS drafts belong to the current selection, but the editing
            // row must stay visible after every gallery/filmstrip transition.
            var changeSelection = typeof(PCasa.App.MainWindow).GetMethod(
                "ResetGpsEditorOnSelectionChange",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS editor selection lifecycle owner missing.");
            searchBox.Text = "Burg Eltz";
            changeSelection.Invoke(window, [new long[] { gpsA }]);
            if (gpsToolsPanel.Visibility != Visibility.Visible ||
                resultPopup.IsOpen || searchBox.Text.Length != 0 ||
                resultList.ItemsSource is not null ||
                draftField.GetValue(window) is not null ||
                ((TextBlock)window.FindName("GpsDraftCoordinates")!).Text.Length != 0)
                throw new Exception("Changing photo left old GPS search or target visible.");
            searchBox.Text = "Next target in progress";
            changeSelection.Invoke(window, [new long[] { gpsA }]);
            if (gpsToolsPanel.Visibility != Visibility.Visible ||
                searchBox.Text != "Next target in progress")
                throw new Exception("Refreshing the same selection discarded a draft.");
            changeSelection.Invoke(window, [new long[] { gpsB }]);
            if (gpsToolsPanel.Visibility != Visibility.Visible ||
                searchBox.Text.Length != 0 ||
                !((TextBlock)window.FindName("GpsCoordinatesText")!).Text.Contains(
                    UiLanguage.T("GPS-Positionen werden geladen"), StringComparison.Ordinal))
                throw new Exception("Next photo retained old GPS text or hid the tools.");
            Console.WriteLine("PASS: GPS editor stays visible while draft state follows selected photos.");

            // Single-click filmstrip focus must preserve gallery multiselection
            // while replacing the coordinates displayed for the focused photo.
            var pointsField = typeof(PCasa.App.MainWindow).GetField("_gpsPoints",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS map projection missing.");
            var highlightedField = typeof(PCasa.App.MainWindow).GetField(
                "_gpsHighlightedImageId",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("GPS photo focus missing.");
            var oldPoints = pointsField.GetValue(window);
            var oldHighlight = highlightedField.GetValue(window);
            var pointType = projected[0].GetType();
            var suppliedPoints = Array.CreateInstance(pointType, projected.Length);
            for (var i = 0; i < projected.Length; i++)
                suppliedPoints.SetValue(projected[i], i);
            try
            {
                pointsField.SetValue(window, suppliedPoints);
                highlightedField.SetValue(window, gpsA);
                var focus = typeof(PCasa.App.MainWindow).GetMethod(
                    "FocusGpsPhotoForDisplay",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)
                    ?? throw new Exception("GPS focused photo display refresh missing.");
                searchBox.Text = "Old search from previous filmstrip photo";
                focus.Invoke(window, [gpsB]);
                var pointB = projected.Single(p =>
                    (long)p.GetType().GetProperty("Id")!.GetValue(p)! == gpsB);
                var latB = (double)pointB.GetType().GetProperty("Lat")!.GetValue(pointB)!;
                var lonB = (double)pointB.GetType().GetProperty("Lon")!.GetValue(pointB)!;
                var coordinateField = typeof(PCasa.App.MainWindow).GetField(
                    "_gpsCurrentCoordinates",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!;
                var shownGps = ((double Lat, double Lon)?)coordinateField.GetValue(window);
                if (gpsToolsPanel.Visibility != Visibility.Visible || searchBox.Text != string.Empty ||
                    shownGps is not { } pos || Math.Abs(pos.Lat-latB)>0.000001 ||
                    Math.Abs(pos.Lon-lonB)>0.000001 ||
                    !selected.SetEquals(selectionBeforeLayout))
                    throw new Exception("Filmstrip focus retained old search/GPS or lost gallery selection.");
            }
            finally
            {
                pointsField.SetValue(window, oldPoints);
                highlightedField.SetValue(window, oldHighlight);
            }
            Console.WriteLine("PASS: Filmstrip focus updates displayed GPS without losing selection or keeping the previous search.");

            var filmToggle = (System.Windows.Controls.Primitives.ToggleButton?)window.FindName("GpsFilmstripToggle")
                ?? throw new Exception("Optional GPS filmstrip toggle missing.");
            var filmPanel = (Border?)window.FindName("GpsFilmstripPanel")
                ?? throw new Exception("Optional GPS filmstrip view missing.");
            filmToggle.IsChecked = true;
            if (filmPanel.Visibility != Visibility.Visible)
                throw new Exception("GPS filmstrip could not be enabled.");

            // A single gallery selection without GPS coordinates is still a
            // FILMSTRIP PHOTO. The map may have no pin, but the strip must
            // show and open it, and closing the overlay must not remove it.
            long noGpsPhoto;
            using (var connection = db.OpenConnection())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id FROM images WHERE photo_folder_id=$folder AND id<>$gps LIMIT 1;";
                cmd.Parameters.AddWithValue("$folder", a.Id);
                cmd.Parameters.AddWithValue("$gps", gpsA);
                noGpsPhoto = (long)cmd.ExecuteScalar()!;
            }
            var preservedGpsSelection = selected.ToHashSet();
            var updateStrip = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateGpsFilmstrip", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var showStripPhoto = typeof(PCasa.App.MainWindow).GetMethod(
                "ShowGpsFullscreenImage", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var closeStripPhoto = typeof(PCasa.App.MainWindow).GetMethod(
                "CloseGpsFullscreenImage", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var stripOverlay = (Grid?)window.FindName("GpsFullscreenImageOverlay")
                ?? throw new Exception("GPS photo overlay missing.");
            try
            {
                selected.Clear();
                selected.Add(noGpsPhoto);
                updateStrip.Invoke(window, null);
                var stripRows = ((ListBox?)window.FindName("GpsFilmstripList"))?.Items
                    .Cast<PCasa.App.MainWindow.GpsFilmstripEntry>().ToArray()
                    ?? throw new Exception("GPS filmstrip list missing.");
                if (stripRows.Length != 1 || stripRows[0].Id != noGpsPhoto)
                    throw new Exception("Single photo without GPS was omitted from filmstrip.");
                showStripPhoto.Invoke(window, [noGpsPhoto]);
                if (stripOverlay.Visibility != Visibility.Visible ||
                    stripOverlay.Tag is not long openId || openId != noGpsPhoto)
                    throw new Exception("Filmstrip could not open selected photo without GPS.");
                closeStripPhoto.Invoke(window, null);
                updateStrip.Invoke(window, null);
                var surviving = ((ListBox)window.FindName("GpsFilmstripList")!)
                    .Items.Cast<PCasa.App.MainWindow.GpsFilmstripEntry>().ToArray();
                if (surviving.Length != 1 || surviving[0].Id != noGpsPhoto)
                    throw new Exception("Closing GPS photo removed selection from filmstrip.");
                Console.WriteLine("PASS: Single no-GPS photo remains in filmstrip after opening and closing photo.");
            }
            finally
            {
                closeStripPhoto.Invoke(window, null);
                selected.Clear();
                foreach (var imageId in preservedGpsSelection) selected.Add(imageId);
                updateStrip.Invoke(window, null);
            }

            // The scrollbar belongs UNDER the lower perforation; both bands
            // stay fixed across the full viewport instead of moving with items.
            if (filmPanel.Child is not ListBox filmListForStyle ||
                window.FindResource("GpsFilmPerforationBrush") is not DrawingBrush perforations ||
                perforations.TileMode != TileMode.Tile ||
                perforations.Drawing is not GeometryDrawing holes ||
                holes.Brush is not SolidColorBrush holeBrush ||
                holeBrush.Color.R < 180 || holeBrush.Color.G < 180 || holeBrush.Color.B < 180)
                throw new Exception("Continuous light GPS film perforations are missing.");
            filmListForStyle.ApplyTemplate();
            var filmScroller = filmListForStyle.Template.FindName(
                "GpsFilmScrollViewer", filmListForStyle) as ScrollViewer
                ?? throw new Exception("GPS film ScrollViewer is missing.");
            filmScroller.ApplyTemplate();
            var filmTrack = filmScroller.Template.FindName(
                "GpsFilmTrack", filmScroller) as Grid;
            var topHoles = filmScroller.Template.FindName(
                "GpsFilmPerforationsTop", filmScroller) as System.Windows.Shapes.Rectangle;
            var bottomHoles = filmScroller.Template.FindName(
                "GpsFilmPerforationsBottom", filmScroller) as System.Windows.Shapes.Rectangle;
            var horizontalScroll = filmScroller.Template.FindName(
                "PART_HorizontalScrollBar", filmScroller) as
                System.Windows.Controls.Primitives.ScrollBar;
            if (filmTrack is null || filmTrack.RowDefinitions.Count != 4 ||
                filmTrack.Children.OfType<System.Windows.Shapes.Rectangle>().Count() != 2 ||
                topHoles is null || Grid.GetRow(topHoles) != 0 ||
                bottomHoles is null || Grid.GetRow(bottomHoles) != 2 ||
                horizontalScroll is null || Grid.GetRow(horizontalScroll) != 3 ||
                filmScroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto)
                throw new Exception("Film scrollbar must be after BOTH uninterrupted perforation bands.");
            var filmStyle = filmListForStyle.ItemContainerStyle;
            var filmTemplate = filmStyle?.Setters.OfType<Setter>()
                .FirstOrDefault(setter => setter.Property == ListBoxItem.TemplateProperty)
                ?.Value as ControlTemplate;
            if (filmTemplate is null ||
                filmTemplate.LoadContent() is not Border filmFrame ||
                filmFrame.Margin.Left > 1 || filmFrame.Padding.Left > 0 ||
                !filmTemplate.Triggers.OfType<Trigger>().Any(trigger =>
                    trigger.Property == ListBoxItem.IsSelectedProperty &&
                    Equals(trigger.Value, true) &&
                    trigger.Setters.OfType<Setter>().Any(setter =>
                        setter.TargetName == "FilmFrame" &&
                        setter.Property == Border.BorderBrushProperty)))
                throw new Exception("Filmstrip has no custom selection frame; blue system selection may return.");

            filmToggle.IsChecked = false;
            if (filmPanel.Visibility != Visibility.Collapsed)
                throw new Exception("GPS filmstrip could not be hidden.");

            if (((Grid?)window.FindName("GpsMapPanel"))?.Visibility != Visibility.Visible ||
                ((Button?)window.FindName("GpsProviderButton"))?.ContextMenu?.Items.Count < 4 ||
                Grid.GetRow(filmPanel) != 3 ||
                Grid.GetRow(filmToggle) != 0 ||
                window.FindName("GpsMapAttribution") is not null ||
                window.FindName("GpsFullscreenCorners") is not System.Windows.Shapes.Path ||
                window.FindName("FolderSidebarSymbolArrow") is not System.Windows.Shapes.Path)
                throw new Exception("GPS provider choices or bold vector icons missing.");
            var googleUrlBuilder = typeof(PCasa.App.MainWindow).GetMethod(
                "BuildGoogleMapsWebsiteUrl",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
                ?? throw new Exception("Google Maps website URL formatter missing.");
            var googleAddress = (string)googleUrlBuilder.Invoke(
                null, [53.553333, 10.017222])!;
            if (!googleAddress.StartsWith(
                    "https://www.google.com/maps/search/?api=1&query=",
                    StringComparison.Ordinal) ||
                googleAddress.Contains("key=", StringComparison.Ordinal) ||
                !googleAddress.Contains("53.553333", StringComparison.Ordinal))
                throw new Exception("Google Maps coordinate search fallback changed unexpectedly.");

            // The parser returns ONLY immutable scalar values and strings.
            // Its JsonDocument has already been disposed by the time WPF
            // begins the deferred UI callback. The prior Clone-based test
            // didn't exercise the real payload representation.
            var parseGpsMessage = typeof(PCasa.App.MainWindow).GetMethod(
                "ParseGpsJavascriptMessage",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)
                ?? throw new Exception("Immutable GPS browser-message parser is missing.");
            foreach (var (message, field, expected) in new[]
            {
                ("""{"type":"draft","lat":48.8584,"lon":2.2945}""", "Latitude", 48.8584),
                ("""{"type":"bounds","south":47.0,"west":2.0,"north":49.0,"east":4.0}""", "West", 2.0),
                ("""{"type":"viewport","lat":53.55,"lon":10.0,"zoom":14.5}""", "Zoom", 14.5)
            })
            {
                var data = parseGpsMessage.Invoke(null, [message])
                    ?? throw new Exception("Valid GPS browser message rejected.");
                if (data.GetType().GetProperties().Any(property =>
                    property.PropertyType == typeof(System.Text.Json.JsonElement) ||
                    property.PropertyType == typeof(System.Text.Json.JsonDocument)))
                    throw new Exception("JSON document escaped asynchronous GPS callback.");
                var completed = false;
                var correct = false;
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // Map-to-WPF handoff after parsing document was released.
                    correct = data.GetType().GetProperty(field)?.GetValue(data)
                        is double number && Math.Abs(number - expected) < 0.00001;
                    completed = true;
                }));
                Until(window, () => completed,
                    "Deferred GPS message callback did not run.");
                if (!correct)
                    throw new Exception("GPS browser callback lost its independent scalar data.");
            }
            if (parseGpsMessage.Invoke(null, ["""{"type":"viewport","lat":"bad"}"""]) is not null)
                throw new Exception("Malformed GPS camera position was accepted.");
            Console.WriteLine("PASS: GPS address/map callbacks survive asynchronous WPF dispatch without any JSON document.");

            // A GPS job can finish while a generic progress presenter was
            // allocated for a background cache request that never produced
            // any progress. It must not stick at "2 / 2" with a fabricated
            // "Background operation running" label after the GPS edit ends.
            var idleProgress = new PCasa.App.BrowseCacheProgressWindow
                { Owner = window };
            try
            {
                idleProgress.BeginManualOperation("GPS in Originaldateien schreiben", 2);
                var count = (TextBlock?)idleProgress.FindName("CountText")
                    ?? throw new Exception("Missing common progress count.");
                count.Text = "2 / 2";
                var idleChanged = 0;
                idleProgress.ProgressPresentationChanged += (_, _) => idleChanged++;
                idleProgress.EndManualOperation();
                if (idleChanged != 0)
                    throw new Exception(
                        "Idle progress cleanup must not overwrite completed manual operation status.");
                if (idleProgress.IsManualPresentationActive ||
                    idleProgress.IsVisible ||
                    !string.IsNullOrEmpty(count.Text))
                    throw new Exception(
                        "Completed GPS job left a fake background window at 2 / 2.");
            }
            finally { idleProgress.Finish(); }
            Console.WriteLine("PASS: Completed GPS operation hides idle presenter and clears stale 2/2 count.");

            // Shared progress presenter: a cache request being queued must
            // NEVER hide a live NAS scan (including 0 newly added files).
            // A manual overlay must restore ONLY live scan activity, never
            // the last completed scanner snapshot.
            var scanPresentation = new PCasa.App.BrowseCacheProgressWindow
                { Owner = window };
            var activeScanOwner = new object();
            try
            {
                scanPresentation.BeginCachePhase();
                scanPresentation.UpdateScan(
                    new ScanProgress(12, 0, 12, 0, null, Added: 0),
                    scanOwner: activeScanOwner);
                var phase = (TextBlock?)scanPresentation.FindName("PhaseText");
                var count = (TextBlock?)scanPresentation.FindName("CountText");
                if (!scanPresentation.IsVisible || phase is null ||
                    !phase.Text.Contains(UiLanguage.T("Bilder werden eingelesen"),
                        StringComparison.OrdinalIgnoreCase) ||
                    count?.Text.Contains("12", StringComparison.OrdinalIgnoreCase) != true)
                    throw new Exception(
                        "Running NAS scan with zero new photos is not visible.");
                var queuedCacheOwner = new object();
                scanPresentation.BeginCachePhase(queuedCacheOwner);
                scanPresentation.Update(
                    new BrowseCacheBuildProgress(20, 4, 0, null, null),
                    queuedCacheOwner);
                if (!scanPresentation.IsVisible ||
                    !phase.Text.Contains(UiLanguage.T("Bilder werden eingelesen"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Queued cache hid the active NAS scan.");

                scanPresentation.BeginManualOperation("GPS in Originaldateien schreiben", 2);
                scanPresentation.UpdateScan(
                    new ScanProgress(13, 0, 13, 0, null, Added: 0),
                    scanOwner: activeScanOwner);
                scanPresentation.EndManualOperation();
                if (!scanPresentation.IsVisible ||
                    !phase.Text.Contains(UiLanguage.T("Bilder werden eingelesen"),
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Manual overlay did not restore the live NAS scan.");

                scanPresentation.EndScanPhase(activeScanOwner);
                if (!scanPresentation.IsVisible ||
                    count?.Text != "4 / 20")
                    throw new Exception("Finished NAS scan did not restore actual cache progress.");
                scanPresentation.EndCachePhase(queuedCacheOwner);
                scanPresentation.EndCachePhase();
                if (scanPresentation.IsVisible || !string.IsNullOrEmpty(count?.Text))
                    throw new Exception(
                        "Finished cache/scan resurrected a stale progress window.");
                scanPresentation.ShowFromStatus();
                if (scanPresentation.IsVisible)
                    throw new Exception("Status click reopened an idle progress presenter.");
            }
            finally
            {
                scanPresentation.EndScanPhase(activeScanOwner);
                scanPresentation.EndCachePhase();
                scanPresentation.Finish();
            }
            Console.WriteLine("PASS: NAS scan, cache and manual P0 share one priority-correct presenter.");

            // Two cache jobs may be present: one running, one queued.
            // A queued one must not erase or retire real cache progress.
            var independentPresenter = new PCasa.App.BrowseCacheProgressWindow
                { Owner = window };
            var liveCacheOwner = new object();
            var pendingCacheOwner = new object();
            try
            {
                independentPresenter.BeginCachePhase(liveCacheOwner);
                independentPresenter.Update(
                    new BrowseCacheBuildProgress(100, 30, 0, null, null),
                    liveCacheOwner);
                independentPresenter.BeginCachePhase(pendingCacheOwner);
                var independentCount = (TextBlock?)independentPresenter.FindName("CountText");
                if (!independentPresenter.IsVisible ||
                    independentCount?.Text != "30 / 100")
                    throw new Exception("Queued cache erased a live cache progress count.");
                independentPresenter.EndCachePhase(pendingCacheOwner);
                if (!independentPresenter.IsVisible ||
                    independentCount?.Text != "30 / 100")
                    throw new Exception("Queued cache completion closed a running cache.");
                independentPresenter.EndCachePhase(liveCacheOwner);
                if (independentPresenter.IsVisible)
                    throw new Exception("Last cache completion kept idle progress visible.");
            }
            finally
            {
                independentPresenter.EndCachePhase(pendingCacheOwner);
                independentPresenter.EndCachePhase(liveCacheOwner);
                independentPresenter.Finish();
            }
            Console.WriteLine("PASS: Competing cache producers cannot clear active progress.");

            // The MainWindow, not the job which first created the presenter,
            // must own its lifetime until every borrower releases it.
            var acquirePresenter = typeof(PCasa.App.MainWindow).GetMethod(
                "AcquireProgressPresenter",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("One progress presenter owner missing.");
            var releasePresenter = typeof(PCasa.App.MainWindow).GetMethod(
                "ReleaseProgressPresenter",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Progress presenter release missing.");
            var presenterHandle = typeof(PCasa.App.MainWindow).GetField(
                "_browseProgressWindow",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Shared progress presenter field missing.");
            var firstBorrower = new object();
            var secondBorrower = new object();
            var firstPresenter = acquirePresenter.Invoke(window, [firstBorrower]);
            var secondPresenter = acquirePresenter.Invoke(window, [secondBorrower]);
            if (!ReferenceEquals(firstPresenter, secondPresenter))
                throw new Exception("Background scan and cache got different presenters.");
            releasePresenter.Invoke(window, [firstBorrower]);
            if (!ReferenceEquals(presenterHandle.GetValue(window), secondPresenter))
                throw new Exception("First completed producer closed the still-owned presenter.");
            releasePresenter.Invoke(window, [secondBorrower]);
            Console.WriteLine("PASS: Shared progress presenter remains alive until last owner releases it.");

            var multiSelectionField = typeof(PCasa.App.MainWindow).GetField(
                "_gpsSelectionCount",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var googleButton = (Button?)window.FindName("GpsGoogleButton")
                ?? throw new Exception("Google Maps button is missing.");
            var coordField = typeof(PCasa.App.MainWindow).GetField(
                "_gpsCurrentCoordinates",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var updateGoogle = typeof(PCasa.App.MainWindow).GetMethod(
                "UpdateGpsGoogleButton",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var oldCount = multiSelectionField.GetValue(window);
            var oldCoords = coordField.GetValue(window);
            try
            {
                coordField.SetValue(window, (53.553333, 10.017222));
                multiSelectionField.SetValue(window, 2);
                updateGoogle.Invoke(window, null);
                if (googleButton.IsEnabled)
                    throw new Exception("Google Maps misleadingly accepts multiple photo markers.");
                multiSelectionField.SetValue(window, 1);
                updateGoogle.Invoke(window, null);
                if (!googleButton.IsEnabled)
                    throw new Exception("Single selected GPS photo cannot open Google Maps.");
            }
            finally
            {
                multiSelectionField.SetValue(window, oldCount);
                coordField.SetValue(window, oldCoords);
                updateGoogle.Invoke(window, null);
            }
            Console.WriteLine("PASS: Stable Google coordinate search; multi selection stays in PCasa.");

            // A previous run may have saved GPS in expanded mode.
            if (Equals(gpsExpand.Tag, "expanded")) Click(gpsExpand);
            window.UpdateLayout();
            if (gpsColumn.Width.GridUnitType != GridUnitType.Star ||
                galleryColumn.Width.GridUnitType != GridUnitType.Star ||
                gpsExpand.Visibility != Visibility.Visible ||
                !folderPane.IsVisible)
                throw new Exception("GPS normal layout is not relative with folders visible.");
            var normalGpsWidth = gpsColumn.ActualWidth;
            var gpsControls = (Grid?)window.FindName("GpsControlsPanel")
                ?? throw new Exception("GPS controls grid missing.");
            var gpsHeader = (Grid?)window.FindName("InfoSidebarHeader")
                ?? throw new Exception("GPS header grid missing.");
            var gpsMapHost = (Grid?)window.FindName("GpsMapHost")
                ?? throw new Exception("GPS map host missing.");
            var gpsSearchField = (TextBox?)window.FindName("GpsPlaceSearchBox")
                ?? throw new Exception("GPS search missing.");
            var gpsAction = (System.Windows.Controls.Primitives.ToggleButton?)window.FindName("GpsPickPositionToggle")
                ?? throw new Exception("GPS Pick missing.");
            var gpsCopy = (Button?)window.FindName("GpsCopyButton")
                ?? throw new Exception("GPS Copy missing.");
            var gpsTitle = (TextBlock?)window.FindName("SidePanelHeading")
                ?? throw new Exception("GPS title missing.");
            var gpsProvider = (Button?)window.FindName("GpsProviderButton")
                ?? throw new Exception("GPS map selector missing.");
            Point RelativePosition(FrameworkElement control)
                => control.TranslatePoint(new Point(0, 0), window);
            var originalTitle = RelativePosition(gpsTitle);
            var originalSearch = RelativePosition(gpsSearchField);
            var originalAction = RelativePosition(gpsAction);
            var originalCopy = RelativePosition(gpsCopy);
            var originalProvider = RelativePosition(gpsProvider);
            var originalMap = RelativePosition(gpsMapHost);
            var normalMapWidth = gpsMapHost.ActualWidth;
            var normalControlWidth = gpsControls.ActualWidth;
            var normalHeaderWidth = gpsHeader.ActualWidth;
            if (gpsControls.ActualWidth < 200 || gpsHeader.ActualWidth < 200)
                throw new Exception("GPS controls do not have usable width.");
            void CheckFixedRows()
            {
                foreach (var captionName in new[] {
                    "GpsPickPositionToggleCaption", "GpsApplyMissingButtonCaption",
                    "GpsReplaceButtonCaption", "GpsRemoveButtonCaption",
                    "GpsShowAreaCaption", "GpsClearAreaCaption",
                    "GpsCopyButtonCaption", "GpsGoogleButtonCaption" })
                    if (window.FindName(captionName) is not TextBlock caption ||
                        caption.Visibility != Visibility.Visible ||
                        string.IsNullOrWhiteSpace(caption.Text))
                        throw new Exception("GPS caption disappeared: " + captionName);
                if (Math.Abs(RelativePosition(gpsTitle).X - originalTitle.X) > 2 ||
                    Math.Abs(RelativePosition(gpsTitle).Y - originalTitle.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsProvider).X - originalProvider.X) > 2 ||
                    Math.Abs(RelativePosition(gpsProvider).Y - originalProvider.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsSearchField).Y - originalSearch.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsAction).Y - originalAction.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsCopy).Y - originalCopy.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsMapHost).Y - originalMap.Y) > 2 ||
                    Math.Abs(RelativePosition(gpsSearchField).X - originalSearch.X) > 2 ||
                    Math.Abs(RelativePosition(gpsAction).X - originalAction.X) > 2 ||
                    Math.Abs(gpsControls.ActualWidth - normalControlWidth) > 2 ||
                    Math.Abs(gpsHeader.ActualWidth - normalHeaderWidth) > 2 ||
                    gpsControls.HorizontalAlignment != HorizontalAlignment.Right ||
                    gpsHeader.HorizontalAlignment != HorizontalAlignment.Right)
                    throw new Exception("GPS controls changed position between normal and expanded modes.");
            }
            foreach (var captionName in new[] {
                "GpsPickPositionToggleCaption", "GpsApplyMissingButtonCaption",
                "GpsReplaceButtonCaption", "GpsRemoveButtonCaption",
                "GpsShowAreaCaption", "GpsClearAreaCaption",
                "GpsCopyButtonCaption", "GpsGoogleButtonCaption" })
                if (window.FindName(captionName) is not TextBlock text ||
                    text.Visibility != Visibility.Visible ||
                    string.IsNullOrWhiteSpace(text.Text))
                    throw new Exception("GPS button caption missing: " + captionName);
            Click(gpsExpand);
            window.UpdateLayout();
            if (folderPane.Visibility != Visibility.Collapsed ||
                folderColumn.ActualWidth != 0 ||
                gpsColumn.ActualWidth <= normalGpsWidth ||
                !galleryColumn.Width.IsStar ||
                !selected.SetEquals(selectionBeforeLayout) ||
                gpsMapHost.ActualWidth <= normalMapWidth)
                throw new Exception("GPS expansion changed selection or did not enlarge the map.");
            CheckFixedRows();
            Click(gpsFullscreen);
            window.UpdateLayout();
            if (Grid.GetColumn(sidebarPanel) != 0 ||
                Grid.GetColumnSpan(sidebarPanel) != workspace.ColumnDefinitions.Count ||
                gpsMapHost.ActualWidth <= normalMapWidth)
                throw new Exception("GPS fullscreen is not an in-place workspace overlay.");
            CheckFixedRows();
            Console.WriteLine("PASS: GPS search, action and footer rows retain their positions in both map modes.");

            // The full-map GPS photo remains a floating overlay ABOVE the
            // map, never forces the map back to an enlarged side panel, and
            // never changes the gallery selection.
            var gpsOverlay = (Grid?)window.FindName("GpsFullscreenImageOverlay")
                ?? throw new Exception("Fullscreen photo image overlay missing.");
            var gpsImage = (System.Windows.Controls.Image?)
                window.FindName("GpsFullscreenImage")
                ?? throw new Exception("Fullscreen photo surface missing.");
            var gpsFrame = (Border?)window.FindName("GpsFullscreenImageFrame")
                ?? throw new Exception("Floating photo frame missing.");
            var projectedType = projected[0].GetType();
            var pointArray = Array.CreateInstance(projectedType, projected.Length);
            for (var i = 0; i < projected.Length; i++)
                pointArray.SetValue(projected[i], i);
            typeof(PCasa.App.MainWindow).GetField("_gpsPoints",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!
                .SetValue(window, pointArray);
            var showGpsPhoto = typeof(PCasa.App.MainWindow).GetMethod(
                "ShowGpsFullscreenImage",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            showGpsPhoto.Invoke(window, [gpsA]);
            window.UpdateLayout();
            if (gpsOverlay.Visibility != Visibility.Visible ||
                !selected.SetEquals(selectionBeforeLayout) ||
                !gpsFullscreen.IsVisible ||
                !ReferenceEquals(gpsOverlay.Parent, workspace) ||
                Grid.GetColumn(gpsOverlay) != 0 ||
                Grid.GetColumnSpan(gpsOverlay) != workspace.ColumnDefinitions.Count ||
                Grid.GetColumn(sidebarPanel) != 0 ||
                Math.Abs(gpsFrame.ActualWidth - workspace.ActualWidth) > 2 ||
                Math.Abs(gpsFrame.ActualHeight - workspace.ActualHeight) > 2)
                throw new Exception("GPS fullscreen photo must cover the entire map and filmstrip.");
            gpsOverlay.RaiseEvent(new MouseButtonEventArgs(
                InputManager.Current.PrimaryMouseDevice,
                Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            if (gpsOverlay.Visibility != Visibility.Collapsed ||
                Grid.GetColumn(sidebarPanel) != 0 ||
                !selected.SetEquals(selectionBeforeLayout))
                throw new Exception("Closing the photo overlay must keep GPS fullscreen.");
            Click(gpsFullscreen);
            if (Grid.GetColumn(sidebarPanel) != 4 ||
                Grid.GetColumnSpan(sidebarPanel) != 1)
                throw new Exception("Leaving GPS fullscreen did not restore the panel.");

            // Regression: double-clicking a thumbnail used to enter the normal
            // gallery viewer and SelectViewerImage cleared all other selected
            // IDs. The GPS photo view now owns only its overlay image: a
            // subsequent SINGLE click on a different thumbnail swaps photos.
            // Neither action may alter the catalog selection or map viewport.
            var multiProjection = ((System.Collections.IEnumerable)readGps.Invoke(
                window, [new long[] { gpsA, gpsB }, b.Id, CancellationToken.None, null])!)
                .Cast<object>().ToArray();
            var multiArray = Array.CreateInstance(projectedType, multiProjection.Length);
            for (var i = 0; i < multiProjection.Length; i++)
                multiArray.SetValue(multiProjection[i], i);
            var gpsPointsField = typeof(PCasa.App.MainWindow).GetField("_gpsPoints",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var viewerOpenField = typeof(PCasa.App.MainWindow).GetField("_viewerOpen",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var filmList = (ListBox?)window.FindName("GpsFilmstripList")
                ?? throw new Exception("GPS filmstrip image list is missing.");
            var filmDoubleClick = typeof(PCasa.App.MainWindow).GetMethod(
                "GpsFilmstrip_MouseDoubleClick",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var previousPoints = gpsPointsField.GetValue(window);
            var previousSelection = selected.ToHashSet();
            try
            {
                gpsPointsField.SetValue(window, multiArray);
                selected.Clear();
                selected.Add(gpsA);
                selected.Add(gpsB);
                filmToggle.IsChecked = true;
                var entries = new List<PCasa.App.MainWindow.GpsFilmstripEntry>
                {
                    new() { Id = gpsA, Name = "first GPS photo" },
                    new() { Id = gpsB, Name = "second GPS photo" }
                };
                filmList.ItemsSource = entries;
                filmList.SelectedItem = entries[0];
                var keepSelection = selected.ToHashSet();
                var doubleClick = new MouseButtonEventArgs(
                    InputManager.Current.PrimaryMouseDevice,
                    Environment.TickCount, MouseButton.Left);
                filmDoubleClick.Invoke(window, [filmList, doubleClick]);
                if (gpsOverlay.Visibility != Visibility.Visible ||
                    gpsOverlay.Tag is not long openedId || openedId != gpsA ||
                    !ReferenceEquals(gpsOverlay.Parent, workspace) ||
                    Grid.GetColumn(gpsOverlay) != 2 ||
                    Grid.GetColumnSpan(gpsOverlay) != 1 ||
                    Grid.GetColumn(sidebarPanel) != 4 ||
                    (bool)viewerOpenField.GetValue(window)! ||
                    !selected.SetEquals(keepSelection))
                    throw new Exception("Filmstrip double click opened normal viewer, hid strip or lost multi-selection.");

                filmList.SelectedItem = entries[1]; // exactly ONE click
                if (gpsOverlay.Visibility != Visibility.Visible ||
                    gpsOverlay.Tag is not long switchedId || switchedId != gpsB ||
                    !selected.SetEquals(keepSelection) ||
                    (bool)viewerOpenField.GetValue(window)!)
                    throw new Exception("Filmstrip single click did not switch the OPEN photo without changing selection.");

                gpsOverlay.RaiseEvent(new MouseButtonEventArgs(
                    InputManager.Current.PrimaryMouseDevice,
                    Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                if (gpsOverlay.Visibility != Visibility.Collapsed ||
                    !selected.SetEquals(keepSelection) ||
                    (bool)viewerOpenField.GetValue(window)!)
                    throw new Exception("Closing GPS photo changed the multi-selection or opened the gallery viewer.");
            }
            finally
            {
                filmToggle.IsChecked = false;
                gpsPointsField.SetValue(window, previousPoints);
                selected.Clear();
                foreach (var id in previousSelection) selected.Add(id);
            }
            if (!selected.SetEquals(selectionBeforeLayout))
                throw new Exception("GPS photo regression test failed to restore original selection.");
            Console.WriteLine("PASS: Photo in gallery, map and filmstrip visible; single-click swaps preserve multi-selection.");

            Click(gpsExpand);
            window.UpdateLayout();
            if (!folderPane.IsVisible || gpsColumn.ActualWidth >=
                workspace.ActualWidth * 0.5)
                throw new Exception("Collapsing GPS did not restore the folder layout.");

            // A folder pane that was already closed must remain closed
            // when GPS later leaves expanded mode.
            Click(folderToggle);
            if (folderPane.Visibility != Visibility.Collapsed)
                throw new Exception("Folder toggle did not hide the folder list.");
            Click(gpsExpand);
            Click(gpsExpand);
            if (folderPane.Visibility != Visibility.Collapsed)
                throw new Exception("GPS resize overwrote the user's hidden-folder preference.");
            Click(folderToggle);
            Click(gpsToggle);
            if (!folderPane.IsVisible ||
                gpsColumn.Width.Value != 0 ||
                !selected.SetEquals(selectionBeforeLayout))
                throw new Exception("Switching sidebar did not restore the workspace layout.");
            Console.WriteLine(
                "PASS: Relative GPS split, expand/fullscreen, folder preference restoration and gallery selection.");

            // Creating an empty target must go through the scheduler, and the
            // result must exist on the filesystem before it is used for moves.
            var create = typeof(PCasa.App.MainWindow).GetMethod("CreateGallerySubfolderAsync",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            var org = Path.Combine(albumB, "ORG");
            var task = (Task)create.Invoke(window,
                [org, $"test:tag-flow-create:{Guid.NewGuid():N}"])!;
            Until(window, () => task.IsCompleted, "Scheduled creation of ORG never completed.");
            task.GetAwaiter().GetResult();
            if (!Directory.Exists(org))
                throw new Exception("Scheduled ORG folder was not created.");
            Console.WriteLine("PASS: Create ORG on monitored filesystem through shared scheduler.");

            // Real Topaz workflow: with filename/current-folder search still
            // active, invert results to show originals, move those originals
            // to ORG, then click a different folder. Its images MUST appear
            // without clearing the filter first.
            fileMode.IsChecked = true;
            albumBItem.IsSelected = true;
            current.IsChecked = true;
            search.Text = "topaz";
            invertFilter.IsChecked = true;
            Until(window, () => ((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    filteredIndex.GetValue(window)!).Count == 6 &&
                status.Text.Contains("Bilder ohne Dateiname: topaz"),
                "Unable to stage six visible originals for relocation.");
            var originals = Enumerable.Range(6, 6)
                .Select(i => Path.Combine(albumB, $"b-{i:00}.jpg")).ToArray();
            var transfer = typeof(PCasa.App.MainWindow).GetMethod(
                "TransferGalleryFilesAsync",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Shared gallery transfer method missing.");
            // Keep the last source file locked until all previous images
            // have moved. During this incomplete P0 manual package, the user
            // must still be able to click an unrelated image folder, even
            // with filename search + inverted current-folder scope enabled.
            Task moveTask;
            lock (SourceFileLocks.ForPath(originals[^1]))
            {
                moveTask = (Task)transfer.Invoke(window, [originals, org, true])!;
                Until(window, () =>
                    originals.Take(originals.Length - 1).All(path =>
                        !File.Exists(path) &&
                        File.Exists(Path.Combine(org, Path.GetFileName(path)))) &&
                    File.Exists(originals[^1]) && !moveTask.IsCompleted,
                    "Manual move did not pause after its first five file commits.");

                var otherFolder = Walk(tree).FirstOrDefault(item =>
                    string.Equals(ReadPath(item), albumA,
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception("Unrelated photo folder unavailable.");
                otherFolder.IsSelected = true;
                Until(window, () => ReferenceEquals(tree.SelectedItem, otherFolder) &&
                    surface.GetFolderOffset(a.Id) is not null &&
                    ((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                        filteredIndex.GetValue(window)!).Count == 8,
                    "Unrelated gallery folder temporarily emptied during manual move.");

                // Return to source scope while the file job is still paused,
                // so completion can publish the normal empty-source result.
                albumBItem.IsSelected = true;
                Until(window, () => ReferenceEquals(tree.SelectedItem, albumBItem),
                    "Folder focus did not return while transfer was pending.");
            }
            Until(window, () => moveTask.IsCompleted,
                "Moving originals to ORG did not complete.");
            moveTask.GetAwaiter().GetResult();
            if (originals.Any(File.Exists) ||
                originals.Any(path => !File.Exists(Path.Combine(org, Path.GetFileName(path)))))
                throw new Exception("Topaz originals were not moved into ORG.");
            if (((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    filteredIndex.GetValue(window)!).Count != 0 ||
                surface.GetFolderOffset(b.Id) is not null)
                throw new Exception(
                    "Move finished in filesystem/catalog but left the old source gallery visible.");
            Console.WriteLine("PASS: Active inverted filename filter immediately removes moved originals.");

            var albumAItem = Walk(tree).FirstOrDefault(item =>
                string.Equals(ReadPath(item), albumA,
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception("Album A missing after manual move.");
            albumAItem.IsSelected = true;
            Until(window, () => ReferenceEquals(tree.SelectedItem, albumAItem) &&
                surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is null &&
                ((IReadOnlyList<PCasa.App.Models.BrowseThumbnailIndexEntry>)
                    filteredIndex.GetValue(window)!).Count == 8,
                "After moving originals, clicking another folder did not navigate.");
            Console.WriteLine("PASS: Folder tree remains navigable with current-folder inverted filter after move.");
            invertFilter.IsChecked = false;
            search.Clear();

            tagMode.IsChecked = true;

            // Real end-to-end metadata editing: the prior search tests seed
            // SQLite directly and therefore cannot prove the actual UI writer.
            // Select two original JPEGs, write one Unicode tag through the
            // sidebar and verify BOTH originals and the catalog index.
            var editorIds = new[] { "a-00-topaz.jpg", "a-01-topaz.jpg" }
                .Select(name =>
                {
                    using var connection = db.OpenConnection();
                    using var lookup = connection.CreateCommand();
                    lookup.CommandText =
                        "SELECT id FROM images WHERE local_path = $path COLLATE NOCASE;";
                    lookup.Parameters.AddWithValue("$path", Path.Combine(albumA, name));
                    return Convert.ToInt64(lookup.ExecuteScalar()
                        ?? throw new Exception("JPEG missing for tag-writing UI test: " + name));
                }).ToArray();
            var sidebarButton = (Button?)window.FindName("TagSidebarButton")
                ?? throw new Exception("Tag sidebar toggle missing.");
            var tagInput = (TextBox?)window.FindName("TagEntryBox")
                ?? throw new Exception("Tag entry missing.");
            var addTag = (Button?)window.FindName("TagAddButton")
                ?? throw new Exception("Tag addition button missing.");
            var tagRows = (StackPanel?)window.FindName("TagRowsPanel")
                ?? throw new Exception("Tag rows panel missing.");
            var quickTags = (WrapPanel?)window.FindName("TagQuickPanel")
                ?? throw new Exception("Quick tags panel missing.");
            var tagAnd = (RadioButton?)window.FindName("TagLogicAnd")
                ?? throw new Exception("Tag AND selector missing.");
            var tagOr = (RadioButton?)window.FindName("TagLogicOr")
                ?? throw new Exception("Tag OR selector missing.");
            const string editableTag = "PCasa UI München";
            selected.Clear();
            foreach (var imageId in editorIds) selected.Add(imageId);
            surface.SetSelection(selected);
            sidebarButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, sidebarButton));
            Until(window, () => window.FindName("TagSidebar") is Grid sidebar &&
                sidebar.IsVisible, "Tag sidebar did not open.");
            tagInput.Text = editableTag;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            var tagOk = (Button?)window.FindName("TagDraftOkButton")
                ?? throw new Exception("Tag OK button missing.");
            var tagCancel = (Button?)window.FindName("TagDraftCancelButton")
                ?? throw new Exception("Tag cancel button missing.");
            if (!tagOk.IsEnabled) throw new Exception("Staged tag did not enable OK.");
            // Cancel must leave both the tag catalogue and originals untouched
            // and restore the original tag list in the sidebar.
            tagCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagCancel));
            Until(window, () => !tagOk.IsEnabled &&
                editorIds.All(id => !(new ImageTagRepository(db).GetKeywords(id) ?? "")
                    .Contains(editableTag, StringComparison.Ordinal)) &&
                !tagRows.Children.OfType<DockPanel>()
                    .Any(row => row.Children.OfType<TextBlock>()
                        .Any(label => label.Text.Contains(editableTag,
                            StringComparison.Ordinal))),
                "Cancel did not restore the original tag list.");
            // A staged batch is not tied to the currently selected folder.
            // Different photos receive different tags. Only ONE OK produces
            // a durable package; switching photos, folders or clearing the
            // selection must never discard the unconfirmed session.
            var applyGallerySelection = typeof(PCasa.App.MainWindow).GetMethod(
                "ApplyGallerySelection",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            long otherImageId;
            string otherImagePath;
            using (var connection = db.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, local_path FROM images
                    WHERE photo_folder_id=$folder ORDER BY file_name LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$folder", b.Id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new Exception("Second folder has no available image.");
                otherImageId = reader.GetInt64(0);
                otherImagePath = reader.GetString(1);
            }
            const string stagedA = "CrossFolder Draft A";
            const string stagedB = "CrossFolder Draft B";
            var draftSummary = (TextBlock?)window.FindName("TagDraftSummaryText")
                ?? throw new Exception("Pending-tag summary is missing.");
            void SelectTagImages(params long[] ids)
            {
                selected.Clear();
                foreach (var id in ids) selected.Add(id);
                applyGallerySelection.Invoke(window, null);
            }
            SelectTagImages(editorIds[0]);
            tagInput.Text = stagedA;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            SelectTagImages(otherImageId);
            tagInput.Text = stagedB;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            Until(window, () => tagOk.IsEnabled &&
                draftSummary.Text.Contains("2 Bilder") &&
                draftSummary.Text.Contains("2 Änderungen") &&
                tagRows.Children.OfType<DockPanel>().Any(row =>
                    row.Children.OfType<TextBlock>().Any(label =>
                        label.Text.Contains(stagedB, StringComparison.Ordinal))),
                "Changing folders dropped staged Tag changes or the per-image projection.");
            SelectTagImages(editorIds[0]);
            Until(window, () =>
                tagRows.Children.OfType<DockPanel>().Any(row =>
                    row.Children.OfType<TextBlock>().Any(label =>
                        label.Text.Contains(stagedA, StringComparison.Ordinal))) &&
                !tagRows.Children.OfType<DockPanel>().Any(row =>
                    row.Children.OfType<TextBlock>().Any(label =>
                        label.Text.Contains(stagedB, StringComparison.Ordinal))),
                "Returning to a previous photo failed to show its staged tags.");
            SelectTagImages();
            if (!tagOk.IsEnabled || !tagCancel.IsEnabled ||
                !draftSummary.Text.Contains("2 Bilder"))
                throw new Exception("Clearing selection prematurely discarded draft.");
            if (editorIds.All(id => (new ImageTagRepository(db).GetKeywords(id) ?? "")
                    .Contains(stagedA)) ||
                (new ImageTagRepository(db).GetKeywords(otherImageId) ?? "")
                    .Contains(stagedB))
                throw new Exception("Unconfirmed Tag session wrote SQLite prematurely.");
            tagCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagCancel));
            if (tagOk.IsEnabled || draftSummary.Visibility != Visibility.Collapsed ||
                (new ImageTagRepository(db).GetKeywords(editorIds[0]) ?? "")
                    .Contains(stagedA) ||
                (new ImageTagRepository(db).GetKeywords(otherImageId) ?? "")
                    .Contains(stagedB))
                throw new Exception("Cancel did not discard every cross-folder Tag edit.");

            // Repeat with a single confirmation, proving different original
            // files get their own words in one new durable tag-edit-v2 package.
            SelectTagImages(editorIds[0]);
            tagInput.Text = stagedA;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            SelectTagImages(otherImageId);
            tagInput.Text = stagedB;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            var activeScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var beforePackages = activeScheduler.Snapshot().Count(job =>
                job.Key.StartsWith("tags:package:", StringComparison.Ordinal));
            tagOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagOk));
            Until(window, () =>
                (ImageTagRepository.ReadKeywords(Path.Combine(albumA,
                    "a-00-topaz.jpg")) ?? "").Contains(stagedA) &&
                (ImageTagRepository.ReadKeywords(otherImagePath) ?? "")
                    .Contains(stagedB) &&
                activeScheduler.Snapshot().Count(job =>
                    job.Key.StartsWith("tags:package:", StringComparison.Ordinal))
                    == beforePackages + 1,
                "Cross-folder Tag session was not confirmed as ONE durable package.");
            if ((new ImageTagRepository(db).GetKeywords(editorIds[0]) ?? "")
                    .Contains(stagedB) ||
                (new ImageTagRepository(db).GetKeywords(otherImageId) ?? "")
                    .Contains(stagedA))
                throw new Exception("Cross-folder Tag batch applied words to wrong images.");
            Console.WriteLine(
                "PASS: One Tag draft survives folder/selection changes, Cancel discards it, one OK writes distinct originals.");

            SelectTagImages(editorIds);
            tagInput.Text = editableTag;
            addTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, addTag));
            if (!tagOk.IsEnabled) throw new Exception("Re-staged tag did not enable OK.");
            tagOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagOk));
            Until(window, () => editorIds.All(id =>
                (new ImageTagRepository(db).GetKeywords(id) ?? "")
                    .Contains(editableTag, StringComparison.Ordinal)) &&
                new[] { "a-00-topaz.jpg", "a-01-topaz.jpg" }.All(name =>
                    (ImageTagRepository.ReadKeywords(Path.Combine(
                        albumA, name)) ?? "")
                    .Contains(editableTag, StringComparison.Ordinal)),
                "Tag UI did not persist Unicode keyword to both ORIGINAL JPEG files.");
            // Image information reads the same catalog tags in its existing
            // SQLite query; no extra EXIF/NAS request is made.
            var infoGetter = typeof(PCasa.App.MainWindow).GetMethod(
                "ReadImageInfo", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Image info reader missing.");
            var imageInfo = infoGetter.Invoke(window, [editorIds[0], "Info"]) as string
                ?? throw new Exception("Image info was empty.");
            if (!imageInfo.Contains("Tags:", StringComparison.Ordinal) ||
                !imageInfo.Contains(editableTag, StringComparison.Ordinal))
                throw new Exception("Image information does not include stored tags.");
            Console.WriteLine("PASS: Image info displays current SQLite keywords.");

            Until(window, () => tagRows.Children.OfType<DockPanel>()
                    .Any(row => row.Children.OfType<TextBlock>().Any(label =>
                        label.Text.Contains(editableTag, StringComparison.Ordinal) &&
                        label.Text.Contains("[2]", StringComparison.Ordinal))) &&
                quickTags.Children.OfType<Button>().Any(button =>
                    string.Equals(button.Content?.ToString(), editableTag,
                        StringComparison.Ordinal)),
                "New tag was not immediately shown with multi-image frequency and quick tag.");
            // Existing keywords are suggested from the local DB and never
            // require another original JPEG/NAS read. A choice stages the tag
            // immediately, but it is not committed until the user presses OK.
            if (!new ImageTagRepository(db).SuggestExistingTags("PCasa UI M")
                .Contains(editableTag, StringComparer.Ordinal))
                throw new Exception("Existing Unicode tag was absent from suggestions.");
            var suggestionsPopup = (System.Windows.Controls.Primitives.Popup?)
                window.FindName("TagSuggestionsPopup")
                ?? throw new Exception("Tag suggestions popup missing.");
            var suggestions = (ListBox?)window.FindName("TagSuggestionsList")
                ?? throw new Exception("Tag suggestions list missing.");
            tagInput.Focus();
            Keyboard.Focus(tagInput);
            tagInput.Text = "PCasa UI M";
            Until(window, () => suggestionsPopup.IsOpen &&
                suggestions.Items.OfType<string>().Contains(editableTag),
                "Typing a known Tag did not open matching DB suggestions.");
            suggestions.SelectedItem = editableTag;
            var pressEnter = typeof(PCasa.App.MainWindow).GetMethod(
                "TagEntry_KeyDown", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            KeyEventArgs SimulateEnter() => new(
                Keyboard.PrimaryDevice, PresentationSource.FromVisual(tagInput)!, 0,
                Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            pressEnter.Invoke(window, [tagInput, SimulateEnter()]);
            if (tagInput.Text.Length != 0 || suggestionsPopup.IsOpen ||
                !tagOk.IsEnabled)
                throw new Exception("First Enter did not stage the selected Tag.");
            tagCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagCancel));
            tagInput.Focus();
            Keyboard.Focus(tagInput);
            tagInput.Text = "PCasa UI M";
            Until(window, () => suggestionsPopup.IsOpen &&
                suggestions.Items.OfType<string>().Contains(editableTag),
                "Tag matching did not reopen after a previous selection.");
            suggestions.SelectedItem = editableTag;
            var chooseByMouse = typeof(PCasa.App.MainWindow).GetMethod(
                "TagSuggestion_MouseUp", System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            chooseByMouse.Invoke(window, [suggestions,
                new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonUpEvent
                }]);
            if (tagInput.Text.Length != 0 || suggestionsPopup.IsOpen ||
                !tagOk.IsEnabled)
                throw new Exception("Mouse click did not stage the selected Tag.");
            tagCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagCancel));
            Console.WriteLine("PASS: Tag suggestions stage on one Enter or mouse click, without early writes.");

            // The editable TextBox consumes ordinary bubbling Up/Down
            // KeyDown events for caret navigation. Raise the REAL WPF
            // tunneling event to prove keyboard navigation reaches the UI;
            // directly invoking TagEntry_KeyDown would miss this regression.
            using (var connection = db.OpenConnection())
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT OR REPLACE INTO recent_tags(tag,last_used_utc)
                    VALUES ('PCasa UI M Keyboard Alpha', '2026-10-28T20:00:01Z'),
                           ('PCasa UI M Keyboard Beta', '2026-10-28T20:00:02Z');
                    """;
                seed.ExecuteNonQuery();
            }
            tagInput.Focus();
            Keyboard.Focus(tagInput);
            tagInput.Text = "PCasa UI M";
            Until(window, () => suggestionsPopup.IsOpen &&
                suggestions.Items.Count >= 3 && suggestions.SelectedIndex == 0,
                "Arrow-key test has no multiple selectable Tag suggestions.");
            void PressSuggestionKey(UIElement target, Key key)
            {
                var eventArgs = new KeyEventArgs(
                    Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                target.RaiseEvent(eventArgs);
                if (!eventArgs.Handled)
                    throw new Exception(
                        $"Tag suggestion did not intercept {key} before native TextBox navigation.");
            }
            PressSuggestionKey(tagInput, Key.Down);
            if (suggestions.SelectedIndex != 1 || tagInput.Text != "PCasa UI M")
                throw new Exception("Down key failed to select second suggestion.");
            PressSuggestionKey(tagInput, Key.Down);
            if (suggestions.SelectedIndex != 2)
                throw new Exception("Second Down key did not advance suggestion.");
            PressSuggestionKey(tagInput, Key.Up);
            if (suggestions.SelectedIndex != 1)
                throw new Exception("Up key did not move back one suggestion.");
            var keyboardChoice = (string)suggestions.SelectedItem!;
            PressSuggestionKey(tagInput, Key.Enter);
            if (tagInput.Text.Length != 0 || suggestionsPopup.IsOpen ||
                !tagOk.IsEnabled || !tagRows.Children.OfType<DockPanel>()
                    .Any(row => row.Children.OfType<TextBlock>()
                        .Any(label => label.Text.Contains(keyboardChoice,
                            StringComparison.Ordinal))))
                throw new Exception("Enter did not stage the keyboard-highlighted Tag.");
            tagCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagCancel));

            tagInput.Focus();
            Keyboard.Focus(tagInput);
            tagInput.Text = "PCasa UI M";
            Until(window, () => suggestionsPopup.IsOpen && suggestions.Items.Count >= 3,
                "Suggestions did not reopen for Escape key test.");
            PressSuggestionKey(tagInput, Key.Escape);
            if (suggestionsPopup.IsOpen || tagInput.Text != "PCasa UI M")
                throw new Exception("Escape did not dismiss suggestions without editing input.");
            tagInput.Clear();
            // Keep the later Tag-sidebar smoke cases free of test-only
            // recents; no changes to originals or the pending write journal.
            using (var connection = db.OpenConnection())
            using (var cleanup = connection.CreateCommand())
            {
                cleanup.CommandText = """
                    DELETE FROM recent_tags WHERE tag IN
                      ('PCasa UI M Keyboard Alpha', 'PCasa UI M Keyboard Beta');
                    """;
                cleanup.ExecuteNonQuery();
            }
            Console.WriteLine(
                "PASS: Routed WPF Up/Down changes highlighted Tag, Enter stages it, Escape closes popup.");


            tagAnd.IsChecked = true;
            Until(window, () => tagRows.Children.OfType<DockPanel>()
                .Any(row => row.Children.OfType<TextBlock>().Any(label =>
                    label.Text.Contains(editableTag, StringComparison.Ordinal))),
                "Tag AND failed to show a tag shared by both selected images.");

            tagOr.IsChecked = true;
            // RefreshTagSidebarAsync clears the panel before its asynchronous
            // scheduler read. Wait for the OR projection to be republished
            // rather than clicking an ephemeral empty panel.
            Until(window, () => tagRows.Children.OfType<DockPanel>()
                .Any(row => row.Children.OfType<TextBlock>().Any(label =>
                    label.Text.Contains(editableTag, StringComparison.Ordinal))),
                "Tag OR refresh did not republish the common keyword.");

            // Remove through the actual RED-X row button; verify that BOTH
            // originals lost the keyword and the SQLite search is refreshed.
            var keywordRow = tagRows.Children.OfType<DockPanel>()
                .Single(row => row.Children.OfType<TextBlock>()
                    .Any(label => label.Text.Contains(editableTag,
                        StringComparison.Ordinal)));
            var removeTag = keywordRow.Children.OfType<Button>().Single();
            removeTag.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, removeTag));
            if (!tagOk.IsEnabled) throw new Exception("Staged tag removal did not enable OK.");
            tagOk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tagOk));
            Until(window, () => editorIds.All(id =>
                    !(new ImageTagRepository(db).GetKeywords(id) ?? "")
                        .Contains(editableTag, StringComparison.Ordinal)) &&
                editorIds.Select((_, index) => Path.Combine(
                        albumA, $"a-{index:00}-topaz.jpg"))
                    .All(path => !(ImageTagRepository.ReadKeywords(path) ?? "")
                        .Contains(editableTag, StringComparison.Ordinal)) &&
                !tagRows.Children.OfType<DockPanel>()
                    .Any(row => row.Children.OfType<TextBlock>().Any(label =>
                        label.Text.Contains(editableTag, StringComparison.Ordinal))),
                "Red-X tag removal did not update original files and visible tag list.");
            // This test next injects pending edits directly into SQLite, outside
            // the user's normal tag-edit package. Await the preceding real WPF
            // edit's UI completion as well as its journal completion first.
            // Otherwise its late ObserveTagPackageAsync callback can launch an
            // automatic recovery for our synthetic backlog and race the exact
            // recovery package we intend to test below.
            var visibleTagOperationsField = typeof(PCasa.App.MainWindow).GetField(
                "_visibleManualOperations",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Shared manual progress projection missing.");
            var visibleTagOperations =
                (System.Collections.IDictionary)visibleTagOperationsField.GetValue(window)!;
            Until(window, () =>
                !visibleTagOperations.Keys.Cast<string>()
                    .Any(key => key.StartsWith("tags:", StringComparison.Ordinal)) &&
                !new DurablePackageJournal(db).Pending().Any(record =>
                    record.Type.StartsWith("tag-edit-", StringComparison.Ordinal) ||
                    record.Type.StartsWith("tag-recovery-", StringComparison.Ordinal)) &&
                new ImageTagRepository(db).PendingEdits(1).Count == 0,
                "Previous WPF tag package or its recovery observer did not finish.");
            // Durable intent is committed locally before touching the image,
            // and restarted repositories can replay several tags in ONE write.
            var restartedTags = new ImageTagRepository(db);
            const string pendingA = "Durable A";
            const string pendingB = "Durable B";
            if (restartedTags.QueueTagEdit(new[] { editorIds[0] }, pendingA, false) != 1 ||
                restartedTags.QueueTagEdit(new[] { editorIds[0] }, pendingB, false) != 1)
                throw new Exception("Durable tag intents were not inserted.");
            var durableEdits = new ImageTagRepository(db).PendingEdits()
                .Where(edit => edit.ImageId == editorIds[0]).ToArray();
            if (durableEdits.Length != 2)
                throw new Exception("Restart-safe tag backlog lost one of two intents.");
            using (var durableWriter = new ExifToolKeywordWriter.Session())
                new ImageTagRepository(db).ApplyPendingImageEdits(
                    editorIds[0], durableEdits, durableWriter, CancellationToken.None);
            var firstOriginal = Path.Combine(albumA, "a-00-topaz.jpg");
            var persisted = ImageTagRepository.ReadKeywords(firstOriginal) ?? "";
            using (var verifier = new ExifToolKeywordWriter.Session())
            {
                var snapshot = verifier.ReadSnapshot(firstOriginal);
                foreach (var field in new[] { snapshot.Xmp, snapshot.Iptc })
                    if (!field.Contains(pendingA, StringComparer.OrdinalIgnoreCase) ||
                        !field.Contains(pendingB, StringComparer.OrdinalIgnoreCase))
                        throw new Exception("Original JPEG is missing a keyword in XMP or IPTC.");
            }
            if (!persisted.Contains(pendingA, StringComparison.Ordinal) ||
                !persisted.Contains(pendingB, StringComparison.Ordinal) ||
                new ImageTagRepository(db).PendingEdits()
                    .Any(edit => edit.ImageId == editorIds[0]))
                throw new Exception("Grouped durable keyword replay did not finish.");
            Console.WriteLine("PASS: Two durable intents replay once per image and clear after acknowledgment.");
            // A real WPF recovery must not mark the package complete merely
            // because one original was temporarily unavailable. Successful
            // files are acknowledged independently in pending_tag_edits; the
            // same persisted scheduler identity resumes the remaining photo.
            const string recoveredTag = "Recovered P0 Tag";
            const string unavailableTag = "Offline P0 Tag";
            var unavailableImage = Path.Combine(albumA, "a-01-topaz.jpg");
            var hiddenOriginal = unavailableImage + ".unreachable";
            var repoForRecovery = new ImageTagRepository(db);
            if (repoForRecovery.QueueTagEdit(
                    new[] { editorIds[0] }, recoveredTag, false) != 1 ||
                repoForRecovery.QueueTagEdit(
                    new[] { editorIds[1] }, unavailableTag, false) != 1)
                throw new Exception("Cannot seed the durable tag recovery test.");
            File.Move(unavailableImage, hiddenOriginal);
            // Red folder markers are driven ONLY by pending_tag_edits.
            // Recompute once for our synthetic test setup; all later
            // transitions must refresh themselves from scheduler callbacks.
            var dots = typeof(PCasa.App.MainWindow).GetMethod(
                "RefreshPendingTagDotsAsync",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Pending tag-dot projection missing.");
            _ = (Task)dots.Invoke(window, null)!;
            bool FolderShowsPending()
            {
                var item = Walk(tree).FirstOrDefault(node =>
                    string.Equals(ReadPath(node), albumA,
                        StringComparison.OrdinalIgnoreCase));
                return item?.Tag?.GetType().GetProperty("HasPendingTagWrites")
                    ?.GetValue(item.Tag) is true;
            }
            Until(window, FolderShowsPending,
                "Pending tag writes were not marked on the affected folder.");
            try
            {
                var scheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                    .GetField("_scheduler",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(window)!;
                var factory = typeof(PCasa.App.MainWindow).GetMethod(
                    "BuildTagRecoveryPackageV2",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("Durable tag recovery V2 factory missing.");
                var recoveryKey = "tags:recover:test-" +
                    Guid.NewGuid().ToString("N");
                const string recoveryPayload = "{}";
                SchedulerTaskPackage Package() =>
                    (SchedulerTaskPackage?)factory.Invoke(
                        window, [recoveryKey, recoveryPayload])
                    ?? throw new Exception("Unable to build tag recovery package.");
                var firstReplay = scheduler.EnqueueDurablePackageAsync(
                    Package(), "tag-recovery-v2", recoveryPayload,
                    SchedulerPriority.ManualAction);
                Until(window, () => firstReplay.IsCompleted,
                    "Unavailable original blocked the durable recovery forever.");
                var deferred = false;
                try { firstReplay.GetAwaiter().GetResult(); }
                catch (DurablePackageRetryException) { deferred = true; }
                var firstProgress = scheduler.Snapshot()
                    .LastOrDefault(snapshot => snapshot.Key == recoveryKey);
                if (firstProgress is null ||
                    firstProgress.Total != 2 || firstProgress.Processed != 1)
                    throw new Exception(
                        "Incorrect Tag recovery progress: " +
                        $"Processed={firstProgress?.Processed}; Total={firstProgress?.Total}; " +
                        $"Remaining={string.Join(",", new ImageTagRepository(db).PendingEdits().Select(e => e.ImageId))}.");
                if (!deferred ||
                    !new ImageTagRepository(db).PendingEdits().Any(edit =>
                        edit.ImageId == editorIds[1]) ||
                    new ImageTagRepository(db).PendingEdits().Any(edit =>
                        edit.ImageId == editorIds[0]) ||
                    !(ImageTagRepository.ReadKeywords(firstOriginal) ?? "")
                        .Contains(recoveredTag, StringComparison.Ordinal) ||
                    !new DurablePackageJournal(db).Pending().Any(record =>
                        record.Key == recoveryKey &&
                        record.Priority == SchedulerPriority.ManualAction &&
                        record.CompletedSteps == 0))
                    throw new Exception(
                        "Offline tag recovery lost priority or treated an unsaved image as finished.");
                if (!FolderShowsPending())
                    throw new Exception(
                        "Red pending-tag marker disappeared although one original was still offline.");

                File.Move(hiddenOriginal, unavailableImage);
                // Recreate the exact same versioned recipe. Persisted DB
                // revisions, not a newly staged tag edit, are replayed.
                var resumed = scheduler.EnqueueDurablePackageAsync(
                    Package(), "tag-recovery-v2", recoveryPayload,
                    SchedulerPriority.ManualAction);
                Until(window, () => resumed.IsCompleted,
                    "Tag recovery did not resume after the file returned.");
                resumed.GetAwaiter().GetResult();
                // The production UI observes normal manual completions and
                // reconciles the same durable backlog. This synthetic direct
                // replay bypasses that observer, so invoke its real
                // reconciliation path rather than creating a test-only queue.
                var reconcile = typeof(PCasa.App.MainWindow).GetMethod(
                    "StartPendingTagDrain", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("Tag recovery reconciliation missing.");
                reconcile.Invoke(window, null);
                if (new DurablePackageJournal(db).Pending().Any(record =>
                        record.Type == "tag-recovery-v2"))
                    throw new Exception(
                        "A deferred recovery remained P0 after all of its original writes were satisfied.");
                if (new ImageTagRepository(db).PendingEdits().Any(edit =>
                        edit.ImageId == editorIds[0] || edit.ImageId == editorIds[1]) ||
                    !(ImageTagRepository.ReadKeywords(unavailableImage) ?? "")
                        .Contains(unavailableTag, StringComparison.Ordinal) ||
                    new DurablePackageJournal(db).Pending().Any(record =>
                        record.Key == recoveryKey))
                    throw new Exception(
                        "Recovered original did not receive durable pending keywords.");
                Until(window, () => !FolderShowsPending(),
                    "Red pending-tag marker remained after all originals were written.");
                Console.WriteLine(
                    "PASS: Red tag marker persists while a file is offline, then disappears automatically.");
                Console.WriteLine(
                    "PASS: Offline original stays pending at P0 and resumes without re-tagging saved files.");
            }
            finally
            {
                if (File.Exists(hiddenOriginal) && !File.Exists(unavailableImage))
                    File.Move(hiddenOriginal, unavailableImage);
            }


            // Real UI projection: a running P0 metadata write must display
            // the exact original and pending count, not the inactive cache
            // dialog's default "Einlesen 0/0".
            const string visibleTag = "Visible Durable P0 Tag";
            var scheduleRecovery = typeof(PCasa.App.MainWindow).GetMethod(
                "StartPendingTagDrain",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Tag recovery scheduler entry missing.");
            var presenterField = typeof(PCasa.App.MainWindow).GetField(
                "_visibleManualWindow",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Unified manual progress presenter missing.");
            lock (SourceFileLocks.ForPath(firstOriginal))
            {
                // Hold this original BEFORE staging the visible intent.
                // Otherwise the automatic post-package recovery may already
                // have written it before the test can observe progress.
                if (new ImageTagRepository(db).QueueTagEdit(
                        new[] { editorIds[0] }, visibleTag, false) != 1)
                    throw new Exception("Cannot seed visible tag recovery.");
                scheduleRecovery.Invoke(window, null);
                try
                {
                Until(window, () =>
                {
                    var presenter = presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow;
                    var phase = (presenter?.FindName("PhaseText") as TextBlock)?.Text;
                    var current = (presenter?.FindName("CurrentFileText") as TextBlock)?.Text;
                    var count = (presenter?.FindName("CountText") as TextBlock)?.Text;
                    var folder = (presenter?.FindName("FolderPathText") as TextBlock)?.Text;
                    var preview = (presenter?.FindName("CurrentThumbnail") as Image)?.Source;
                    var hide = (presenter?.FindName("HideButton") as Button)?.Content?.ToString();
                    return presenter?.IsVisible == true &&
                        presenter.Width == 348 && presenter.Height == 76 &&
                        phase?.Contains("Tags", StringComparison.OrdinalIgnoreCase) == true &&
                        current == Path.GetFileName(firstOriginal) &&
                        folder == Path.GetDirectoryName(firstOriginal) &&
                        count != "0 / 0" &&
                        hide == "Hide" &&
                        preview is not null &&
                        status.Text == presenter.ProgressStatusText &&
                        Application.Current.Windows
                            .OfType<PCasa.App.BrowseCacheProgressWindow>()
                            .Count(w => w.IsVisible) == 1;
                }, "Tag progress did not show compact count, folder, Hide and cached image in one unchanged-size window.");
                }
                catch (TimeoutException ex)
                {
                    var presenter = presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow;
                    var diagnosticFile = (presenter?.FindName("CurrentFileText") as TextBlock)?.Text;
                    var phase = (presenter?.FindName("PhaseText") as TextBlock)?.Text;
                    var count = (presenter?.FindName("CountText") as TextBlock)?.Text;
                    var folder = (presenter?.FindName("FolderPathText") as TextBlock)?.Text;
                    var preview = (presenter?.FindName("CurrentThumbnail") as Image)?.Source;
                    var scheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                        .GetField("_scheduler", System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                    var journalState = string.Join(";", new DurablePackageJournal(db)
                        .Pending().Where(x => x.Key.StartsWith("tags:")).Select(x =>
                            $"{x.Key}:{x.Type}:{x.State}:{x.CompletedSteps}:{x.Error}"));
                    var schedulerState = string.Join(";", scheduler.Snapshot()
                        .Where(x => x.Key.StartsWith("tags:")).TakeLast(8)
                        .Select(x => $"{x.Key}:{x.State}:{x.Phase}:{x.Error}"));
                    throw new TimeoutException(
                        $"{ex.Message} Phase={phase}, File={diagnosticFile}, Folder={folder}, " +
                        $"Count={count}, Preview={(preview is not null)}, " +
                        $"Visible={presenter?.IsVisible}, ActiveTags={journalState}, " +
                        $"Scheduler={schedulerState}", ex);
                }
            }
            Until(window, () =>
                new ImageTagRepository(db).PendingEdits()
                    .All(edit => edit.ImageId != editorIds[0]) &&
                (ImageTagRepository.ReadKeywords(firstOriginal) ?? "")
                    .Contains(visibleTag, StringComparison.Ordinal),
                "Visible tag recovery did not finish writing its original.");
            Until(window, () => !FolderShowsPending(),
                "Red tag marker remained after the visible tag write completed.");
            // The preceding step is a REAL durable tag job. Pending DB
            // revisions may be committed before the UI receives the final
            // completed/progress callbacks. Do not mix that real burst into
            // the isolated synthetic series-wide counter assertions below.
            var tagSeriesField = typeof(PCasa.App.MainWindow).GetField(
                "_visibleTagSeries", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Tag series projection missing.");
            Until(window, () =>
                tagSeriesField.GetValue(window) is System.Collections.ICollection
                    { Count: 0 },
                "Real Tag progress did not finish before independent synthetic counter test.");

            // An offline NAS volume must not trap reachable local tag
            // writes behind the old fixed-list retry cursor. This runs the
            // actual V2 package through PCasa's existing scheduler and native
            // ExifTool writer; all sources remain part of the same DB intent
            // table, never a second queue.
            var unavailableA = Path.Combine(albumA, "a-09.jpg");
            var unavailableB = Path.Combine(albumA, "a-10.jpg");
            var reachable = firstOriginal; // known indexed and present after earlier tag tests
            var unavailableAId = catalog.GetImageId(unavailableA)
                ?? throw new Exception("First offline test photo missing.");
            var unavailableBId = catalog.GetImageId(unavailableB)
                ?? throw new Exception("Second offline test photo missing.");
            var reachableId = catalog.GetImageId(reachable)
                ?? throw new Exception("Local test photo missing.");
            const string sourceOfflineTag = "PCasa-offline-root-resume";
            if (Directory.Exists(@"Z:\\"))
                throw new Exception("Test machine unexpectedly has the isolated Z drive.");
            var testRepo = new ImageTagRepository(db);
            if (testRepo.QueueTagEdit(
                    new[] { unavailableAId, unavailableBId, reachableId },
                    sourceOfflineTag, false) != 3)
                throw new Exception("Failed to stage two offline and one local tag.");
            using (var connection = db.OpenConnection())
            using (var update = connection.CreateCommand())
            {
                update.CommandText =
                    "UPDATE images SET local_path=$path WHERE id=$id;";
                update.Parameters.Add("$path", SqliteType.Text);
                update.Parameters.Add("$id", SqliteType.Integer);
                for (var i = 0; i < 2; i++)
                {
                    update.Parameters["$path"].Value = @"Z:\\PCasa-unavailable-test\\" +
                        (i == 0 ? "a.jpg" : "b.jpg");
                    update.Parameters["$id"].Value =
                        i == 0 ? unavailableAId : unavailableBId;
                    update.ExecuteNonQuery();
                }
            }
            var createV2 = typeof(PCasa.App.MainWindow).GetMethod(
                "BuildTagRecoveryPackageV2",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("V2 recovery factory missing.");
            const string sourceRecoveryKey = "tags:smoke:offline-root-v2";
            var sourceRecovery = (SchedulerTaskPackage?)createV2.Invoke(
                window, [sourceRecoveryKey, "{}"])
                ?? throw new Exception("Failed to construct the V2 recovery.");
            var sourceScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            try
            {
                var sourceRecoveryTask = sourceScheduler.EnqueuePackageAsync(
                    sourceRecovery, SchedulerPriority.ManualAction);
                Until(window, () => sourceRecoveryTask.IsCompleted,
                    "Offline-source recovery never finished its bounded pass.");
                if (sourceRecoveryTask.Exception is null ||
                    !sourceRecoveryTask.Exception.Flatten().InnerExceptions
                        .Any(ex => ex is DurablePackageRetryException))
                    throw new Exception(
                        "Offline originals were incorrectly marked as fully written.");
                if (testRepo.PendingEditsForImage(reachableId).Count != 0 ||
                    !(ImageTagRepository.ReadKeywords(reachable) ?? "")
                        .Contains(sourceOfflineTag, StringComparison.Ordinal))
                    throw new Exception(
                        "Offline NAS originals blocked the reachable local Tag write.");
                if (testRepo.PendingEditsForImage(unavailableAId).Count != 1 ||
                    testRepo.PendingEditsForImage(unavailableBId).Count != 1)
                    throw new Exception(
                        "Unreachable NAS edits were lost instead of remaining red/pending.");
            }
            finally
            {
                // Test-only cleanup for ephemeral WindowSmoke DB. Real PCasa
                // NEVER drops pending user intents on offline source errors.
                using var connection = db.OpenConnection();
                using var tx = connection.BeginTransaction();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = tx;
                    delete.CommandText =
                        "DELETE FROM pending_tag_edits WHERE tag=$tag;";
                    delete.Parameters.AddWithValue("$tag", sourceOfflineTag);
                    delete.ExecuteNonQuery();
                }
                using (var restorePaths = connection.CreateCommand())
                {
                    restorePaths.Transaction = tx;
                    restorePaths.CommandText =
                        "UPDATE images SET local_path=$path WHERE id=$id;";
                    restorePaths.Parameters.Add("$path", SqliteType.Text);
                    restorePaths.Parameters.Add("$id", SqliteType.Integer);
                    restorePaths.Parameters["$path"].Value = unavailableA;
                    restorePaths.Parameters["$id"].Value = unavailableAId;
                    restorePaths.ExecuteNonQuery();
                    restorePaths.Parameters["$path"].Value = unavailableB;
                    restorePaths.Parameters["$id"].Value = unavailableBId;
                    restorePaths.ExecuteNonQuery();
                }
                tx.Commit();
            }
            Console.WriteLine(
                "PASS: A single V2 pass writes local C: tags despite two offline NAS originals.");

            // Several manual Tag packets must share ONE series-wide counter
            // even when an older package progresses behind the newest one.
            // The UI is a projection; no fake durable jobs or DB intents are
            // created by this smoke test.
            var beginManual = typeof(PCasa.App.MainWindow).GetMethod(
                "BeginVisibleManualOperation",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var reportManual = typeof(PCasa.App.MainWindow).GetMethod(
                "VisibleManualProgressChanged",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var endManual = typeof(PCasa.App.MainWindow).GetMethod(
                "EndVisibleManualOperation",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            const string olderTag = "tags:smoke:older";
            const string newerTag = "tags:smoke:newer";
            void FakeProgress(string key, int completed, int total)
            {
                var snapshot = new SchedulerJobSnapshot(
                    0, key, SchedulerPriority.ManualAction,
                    SchedulerJobState.Running, DateTimeOffset.UtcNow, null)
                {
                    Phase = "Tags in Originaldateien schreiben",
                    Processed = completed, Total = total,
                    CurrentPath = firstOriginal
                };
                reportManual.Invoke(window, [snapshot]);
            }
            beginManual.Invoke(window, [olderTag,
                "Tags in Originaldateien schreiben", 4]);
            beginManual.Invoke(window, [newerTag,
                "Tags in Originaldateien schreiben", 6]);
            FakeProgress(olderTag, 3, 4);
            FakeProgress(newerTag, 2, 6);
            try
            {
                Until(window, () =>
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CountText") is TextBlock number &&
                    number.Text == "5 / 10" &&
                    status.Text ==
                        (presenterField.GetValue(window)
                            as PCasa.App.BrowseCacheProgressWindow)?.ProgressStatusText,
                    "Status bar and floating dialog disagree on aggregate Tag progress.");
            }
            catch (TimeoutException ex)
            {
                var presenter = presenterField.GetValue(window)
                    as PCasa.App.BrowseCacheProgressWindow;
                var actual = (presenter?.FindName("CountText") as TextBlock)?.Text;
                var statusValue = status.Text;
                var presenterValue = presenter?.ProgressStatusText;
                var seriesField = typeof(PCasa.App.MainWindow).GetField(
                    "_visibleTagSeries", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var series = seriesField?.GetValue(window);
                throw new TimeoutException(
                    $"{ex.Message} Count='{actual}', Status='{statusValue}', " +
                    $"Presenter='{presenterValue}', Series='{series}'", ex);
            }
            endManual.Invoke(window, [olderTag]);
            FakeProgress(newerTag, 5, 6);
            Until(window, () =>
                (presenterField.GetValue(window)
                    as PCasa.App.BrowseCacheProgressWindow)?
                    .FindName("CountText") is TextBlock number &&
                number.Text == "8 / 10",
                "Finished Tag job was dropped from the series-wide count.");
            beginManual.Invoke(window, ["tags:smoke:third",
                "Tags in Originaldateien schreiben", 7]);
            Until(window, () =>
                (presenterField.GetValue(window)
                    as PCasa.App.BrowseCacheProgressWindow)?
                    .FindName("CountText") is TextBlock number &&
                number.Text == "8 / 17",
                "A new Tag packet did not grow the shared total.");
            endManual.Invoke(window, [newerTag]);
            endManual.Invoke(window, ["tags:smoke:third"]);
            Console.WriteLine(
                "PASS: Tag series count stays cumulative across older, newer and newly added packages.");

            // The last displayed tag can still be queued while a prior
            // non-interruptible write is running. Its unchanged "wait" text
            // must not conceal the real file currently being processed.
            const string realWorkerKey = "tags:smoke:running-original";
            const string waitingUiKey = "tags:smoke:waiting-newer";
            var workerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseWorker = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var actualScheduler = (PCasaScheduler)typeof(PCasa.App.MainWindow)
                .GetField("_scheduler", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
            var runningTag = actualScheduler.EnqueueAsync(realWorkerKey,
                SchedulerPriority.ManualAction, async token =>
                {
                    workerStarted.TrySetResult();
                    await releaseWorker.Task.WaitAsync(token);
                });
            try
            {
                Until(window, () => workerStarted.Task.IsCompleted,
                    "Tag scheduler worker did not begin.");
                beginManual.Invoke(window, [realWorkerKey,
                    "Tags in Originaldateien schreiben", 4]);
                beginManual.Invoke(window, [waitingUiKey,
                    "Tags in Originaldateien schreiben", 6]);
                const string actualPath = @"C:\Tests\actual-original.jpg";
                actualScheduler.ReportProgress(realWorkerKey,
                    "Tags in Originaldateien schreiben", 1, 4, actualPath);
                Until(window, () =>
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CurrentFileText") is TextBlock file &&
                    file.Text == "actual-original.jpg" &&
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CountText") is TextBlock count &&
                    count.Text == "1 / 10",
                    "A queued newer Tag package hid the executing original's path.");
            }
            finally
            {
                releaseWorker.TrySetResult();
                Until(window, () => runningTag.IsCompleted,
                    "Tag presenter test scheduler worker failed to finish.");
                endManual.Invoke(window, [realWorkerKey]);
                endManual.Invoke(window, [waitingUiKey]);
            }
            Console.WriteLine(
                "PASS: Running original path overrides newer queued tag while series total stays cumulative.");
            // A queued tag series must yield its CAPTION, not its priority,
            // to actual running NAS inventory in the one shared presenter.
            const string waitingNasKey = "tags:smoke:waiting-for-nas";
            beginManual.Invoke(window, [waitingNasKey,
                "Tags in Originaldateien schreiben", 7]);
            var scanPresenter = presenterField.GetValue(window)
                as PCasa.App.BrowseCacheProgressWindow
                ?? throw new Exception("Shared scan presenter missing.");
            var mayShowScan = typeof(PCasa.App.MainWindow).GetMethod(
                "ScanMayReplaceWaitingManualPresentation",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Shared scheduler scan visibility decision missing.");
            var scanAllowed = (bool)mayShowScan.Invoke(window, null)!;
            if (!scanAllowed)
                throw new Exception("Queued Tag job incorrectly suppresses NAS progress.");
            scanPresenter.UpdateScan(new ScanProgress(5, 3, 2, 0,
                @"S:\album\new-photo.jpg", Added: 3),
                showWhileManualWaiting: scanAllowed);
            var repaint = typeof(PCasa.App.MainWindow).GetMethod(
                "RenderVisibleTagSeries",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Shared tag aggregate projection missing.");
            repaint.Invoke(window, null);
            if ((scanPresenter.FindName("PhaseText") as TextBlock)?.Text !=
                    "Bilder werden eingelesen" ||
                (scanPresenter.FindName("CurrentFileText") as TextBlock)?.Text !=
                    "new-photo.jpg" ||
                (scanPresenter.FindName("CountText") as TextBlock)?.Text
                    .StartsWith("Neu: 3", StringComparison.Ordinal) != true ||
                status.Text != scanPresenter.ProgressStatusText)
                throw new Exception(
                    "Queued tag overlay concealed running NAS scan or diverged from status bar.");
            var restorePending = typeof(PCasa.App.MainWindow).GetMethod(
                "RestoreWaitingManualPresentationAfterScan",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Pending Tag presenter restoration missing.");
            restorePending.Invoke(window, null);
            if (scanPresenter.ShowingScanDuringManualWait ||
                (scanPresenter.FindName("CountText") as TextBlock)?.Text != "0 / 7")
                throw new Exception("Completed NAS scan did not restore queued Tag series.");
            // The scan dialog must render a committed LOCAL thumbnail even
            // while an older Tag package is queued. Do this before ending
            // the shared presentation lifetime.
            string? localPreview;
            using (var connection = db.OpenConnection())
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = """
                    SELECT thumbnail_path FROM images
                    WHERE local_path=$path COLLATE NOCASE LIMIT 1;
                    """;
                lookup.Parameters.AddWithValue("$path", firstOriginal);
                localPreview = lookup.ExecuteScalar() as string;
            }
            if (string.IsNullOrWhiteSpace(localPreview) || !File.Exists(localPreview))
                throw new Exception("Smoke image has no committed local scan thumbnail.");
            scanPresenter.UpdateScan(new ScanProgress(6, 4, 2, 0,
                firstOriginal, CurrentThumbnailPath: localPreview),
                showWhileManualWaiting: true);
            Until(window, () =>
                (scanPresenter.FindName("CurrentThumbnail") as Image)?.Source
                is ImageSource previewImage && previewImage.Width > 0 && previewImage.Height > 0,
                "Scan progress did not display the committed local image.");
            restorePending.Invoke(window, null);
            endManual.Invoke(window, [waitingNasKey]);
            Console.WriteLine("PASS: Live scan progress shows its local cached picture.");
            Console.WriteLine(
                "PASS: Active NAS scan replaces queued Tag caption and restores pending series.");

            // Recovered P0 manual scans must appear in the same progress
            // window as recovered Tags. Otherwise a new scan may correctly
            // precede an older Tag under LIFO, but the only visible caption
            // remains a stale 314/828 "Warte auf Scheduler".
            var scanJournal = new DurablePackageJournal(db);
            var recoveredScanKey =
                "catalog:complete-scan:smoke:" + Guid.NewGuid().ToString("N");
            scanJournal.Put(recoveredScanKey, "manual-folder-scan-v1",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    Directory = albumA,
                    MonitoredRootId = (long?)root.Id
                }),
                SchedulerPriority.ManualAction, SchedulerWorkKind.CatalogScan);
            var restoreScanProjection = typeof(PCasa.App.MainWindow).GetMethod(
                "RestoreVisiblePendingManualScans",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Recovered P0 scan presenter missing.");
            restoreScanProjection.Invoke(window, null);
            var restoredScanEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRestoredScan = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var displayingScan = actualScheduler.EnqueueAsync(recoveredScanKey,
                SchedulerPriority.ManualAction, async token =>
                {
                    restoredScanEntered.TrySetResult();
                    await releaseRestoredScan.Task.WaitAsync(token);
                }, workKind: SchedulerWorkKind.CatalogScan);
            try
            {
                Until(window, () => restoredScanEntered.Task.IsCompleted,
                    "Recovered manual scan never reached scheduler worker.");
                actualScheduler.ReportProgress(recoveredScanKey,
                    "Bilder einlesen", 7, 0, firstOriginal);
                Until(window, () =>
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("PhaseText") is TextBlock phase &&
                    phase.Text == "Bilder einlesen" &&
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CurrentFileText") is TextBlock file &&
                    file.Text == Path.GetFileName(firstOriginal) &&
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CurrentThumbnail") is Image image &&
                    image.Source is ImageSource,
                    "Restored manual scan did not show its real cached photo.");
            }
            finally
            {
                releaseRestoredScan.TrySetResult();
                Until(window, () => displayingScan.IsCompleted,
                    "Synthetic recovered manual scan did not complete.");
                scanJournal.SetState(recoveredScanKey, "completed");
                endManual.Invoke(window, [recoveredScanKey]);
            }
            Console.WriteLine(
                "PASS: Restored P0 manual scan is visible with its real active path.");

            // Recovery must use each immutable package's own image count.
            // Previously the UI assigned the ENTIRE pending-tag image count
            // to every recovered package and multiplied the grand total.
            // Two separate requests for the SAME photo are still two file
            // steps, not one "unique photo".
            long[] fiveImageIds;
            using (var connection = db.OpenConnection())
            using (var query = connection.CreateCommand())
            {
                query.CommandText = "SELECT id FROM images ORDER BY id LIMIT 5;";
                using var reader = query.ExecuteReader();
                var ids = new List<long>();
                while (reader.Read()) ids.Add(reader.GetInt64(0));
                fiveImageIds = ids.ToArray();
            }
            if (new ImageTagRepository(db).QueueTagEdit(
                    fiveImageIds, "recovery-count-smoke", false) != 5)
                throw new Exception("Could not stage five distinct recovery targets.");
            var journal = new DurablePackageJournal(db);
            var sameImagePayload = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    ImageIds = new[] { fiveImageIds[0] },
                    Changes = new[] { new { Tag = "recovery-count-smoke", Remove = false } }
                });
            var firstKey = "tags:package:smoke-first-" + Guid.NewGuid().ToString("N");
            var secondKey = "tags:package:smoke-second-" + Guid.NewGuid().ToString("N");
            journal.Put(firstKey, "tag-edit-v1", sameImagePayload,
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            journal.Put(secondKey, "tag-edit-v1", sameImagePayload,
                SchedulerPriority.ManualAction, SchedulerWorkKind.Catalog);
            var restore = typeof(PCasa.App.MainWindow).GetMethod(
                "RestoreVisiblePendingTagPackages",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?? throw new Exception("Recovered Tag progress projection missing.");
            restore.Invoke(window, null);
            Until(window, () =>
                (presenterField.GetValue(window)
                    as PCasa.App.BrowseCacheProgressWindow)?
                    .FindName("CountText") is TextBlock number &&
                number.Text == "0 / 2",
                "Recovered Tag progress multiplied the five pending images by two packages.");
            journal.SetState(firstKey, "completed");
            journal.SetState(secondKey, "completed");
            endManual.Invoke(window, [firstKey]);
            endManual.Invoke(window, [secondKey]);
            // These five rows were generated ONLY to exercise aggregate
            // presentation. They were not real EXIF writes, and must not leak
            // into the next independent WPF smoke test through shared
            // %LOCALAPPDATA%: that would create an unavailable manual P0
            // recovery and legitimately block all background work.
            using (var cleanup = db.OpenConnection())
            using (var tx = cleanup.BeginTransaction())
            {
                using var dropIntent = cleanup.CreateCommand();
                dropIntent.Transaction = tx;
                dropIntent.CommandText =
                    "DELETE FROM pending_tag_edits WHERE tag='recovery-count-smoke';";
                dropIntent.ExecuteNonQuery();
                using var restoreProjection = cleanup.CreateCommand();
                restoreProjection.Transaction = tx;
                restoreProjection.CommandText = """
                    UPDATE image_tags
                    SET keywords = TRIM(
                        REPLACE(keywords, 'recovery-count-smoke', ''), char(10))
                    WHERE keywords LIKE '%recovery-count-smoke%';
                    """;
                restoreProjection.ExecuteNonQuery();
                tx.Commit();
            }
            Console.WriteLine(
                "PASS: Two separate Tag packages for the same photo count twice, without duplicating the recovery backlog.");

            // A Tag job may target a catalogued photo before its browse-pack
            // tiers are ready (common during a long initial NAS scan). Its
            // previously committed LOCAL thumbnail must still be shown even
            // while the ORIGINAL is temporarily unreachable. No source read
            // or extra preview scheduler is allowed.
            string legacyThumbnail;
            using (var connection = db.OpenConnection())
            using (var lookup = connection.CreateCommand())
            {
                lookup.CommandText = """
                    SELECT thumbnail_path FROM images
                    WHERE local_path=$path COLLATE NOCASE LIMIT 1;
                    """;
                lookup.Parameters.AddWithValue("$path", firstOriginal);
                legacyThumbnail = lookup.ExecuteScalar() as string
                    ?? throw new Exception("Legacy local thumbnail path missing.");
                if (!File.Exists(legacyThumbnail))
                    throw new Exception("Legacy local thumbnail file missing.");
            }
            using (var connection = db.OpenConnection())
            using (var removePacked = connection.CreateCommand())
            {
                removePacked.CommandText = """
                    DELETE FROM thumbnail_entries
                    WHERE image_id=(SELECT id FROM images
                                    WHERE local_path=$path COLLATE NOCASE);
                    """;
                removePacked.Parameters.AddWithValue("$path", firstOriginal);
                if (removePacked.ExecuteNonQuery() == 0)
                    throw new Exception("Preview regression lacks an existing packed tier.");
            }
            const string legacyPreviewJob = "tags:smoke:legacy-thumbnail";
            var movedOriginal = firstOriginal + ".offline-for-preview-test";
            File.Move(firstOriginal, movedOriginal);
            try
            {
                beginManual.Invoke(window, [legacyPreviewJob,
                    "Tags in Originaldateien schreiben", 1]);
                var missingPackStatus = new SchedulerJobSnapshot(
                    0, legacyPreviewJob, SchedulerPriority.ManualAction,
                    SchedulerJobState.Running, DateTimeOffset.UtcNow, null)
                {
                    Phase = "Tags in Originaldateien schreiben",
                    CurrentPath = firstOriginal,
                    Total = 1, Processed = 0
                };
                reportManual.Invoke(window, [missingPackStatus]);
                Until(window, () =>
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CurrentThumbnail") is Image preview &&
                    preview.Source is BitmapSource &&
                    (presenterField.GetValue(window)
                        as PCasa.App.BrowseCacheProgressWindow)?
                        .FindName("CurrentFileText") is TextBlock file &&
                    file.Text == Path.GetFileName(firstOriginal),
                    "Tag progress could not load the LOCAL thumbnail when " +
                    "the packed tier and original were unavailable.");
            }
            finally
            {
                endManual.Invoke(window, [legacyPreviewJob]);
                File.Move(movedOriginal, firstOriginal);
            }
            Console.WriteLine(
                "PASS: Tag progress uses local cached picture with no packed tier or accessible original.");

            Console.WriteLine(
                "PASS: Tag-write P0 progress shows one real file and red marker clears after success.");
            Console.WriteLine(
                "PASS: WPF two-image Unicode tag add/remove updates original JPEGs, counts and recent tags.");
            // Regression: selecting ALL 5,200+ photos must send ALL stored
            // GPS coordinates, including records whose cached EXIF source
            // fingerprint temporarily differs after a metadata-only write.
            // Context markers are capped; SELECTED photo markers never are.
            const int selectedGpsCount = 5201;
            var largeGpsPrefix = Path.Combine(sourceRoot, "gps-load-fixture-");
            using (var connection = db.OpenConnection())
            {
                using var transaction = connection.BeginTransaction();
                for (var first = 1; first <= selectedGpsCount; first += 500)
                {
                    using var insert = connection.CreateCommand();
                    insert.Transaction = transaction;
                    insert.CommandText = """
                        WITH RECURSIVE seq(n) AS (
                            SELECT $first
                            UNION ALL SELECT n+1 FROM seq WHERE n<$last)
                        INSERT INTO images (
                            folder_id,photo_folder_id,local_path,canonical_path,
                            file_name,extension,file_size,modified_utc_ticks,
                            width,height,thumbnail_path,indexed_utc)
                        SELECT $root,$photoFolder,
                               $prefix||n||'.jpg',$prefix||n||'.jpg',
                               'gps-load-'||n||'.jpg','.jpg',111,111,
                               64,64,'',strftime('%Y-%m-%dT%H:%M:%fZ','now')
                        FROM seq;
                        """;
                    insert.Parameters.AddWithValue("$first",first);
                    insert.Parameters.AddWithValue("$last",
                        Math.Min(selectedGpsCount,first+499));
                    insert.Parameters.AddWithValue("$root",root.Id);
                    insert.Parameters.AddWithValue("$photoFolder",a.Id);
                    insert.Parameters.AddWithValue("$prefix",largeGpsPrefix);
                    insert.ExecuteNonQuery();
                }
                using var gps = connection.CreateCommand();
                gps.Transaction = transaction;
                gps.CommandText = """
                    INSERT INTO image_exif (
                        image_id,source_modified_utc_ticks,source_file_size,
                        exif_json,gps_latitude,gps_longitude)
                    SELECT id,222,222,'{}',40.0+(id%25)*0.01,-3.7+(id%25)*0.01
                      FROM images WHERE local_path LIKE $prefix||'%';
                    """;
                gps.Parameters.AddWithValue("$prefix",largeGpsPrefix);
                gps.ExecuteNonQuery();
                transaction.Commit();
            }
            try
            {
                long[] loadIds;
                using (var connection = db.OpenConnection())
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT id FROM images WHERE local_path LIKE $prefix||'%' ORDER BY id;";
                    cmd.Parameters.AddWithValue("$prefix",largeGpsPrefix);
                    using var reader = cmd.ExecuteReader();
                    var selectedLoadIds = new List<long>();
                    while (reader.Read()) selectedLoadIds.Add(reader.GetInt64(0));
                    loadIds = selectedLoadIds.ToArray();
                }
                if (loadIds.Length != selectedGpsCount)
                    throw new Exception("Large selected GPS test fixture was incomplete.");
                var allGps = ((System.Collections.IEnumerable)readGps.Invoke(
                    window,[loadIds,a.Id,CancellationToken.None,null])!).Cast<object>().ToArray();
                var selectedAll = allGps.Where(item =>
                    (bool)item.GetType().GetProperty("Selected")!.GetValue(item)!)
                    .Select(item => (long)item.GetType().GetProperty("Id")!.GetValue(item)!)
                    .ToHashSet();
                if (selectedAll.Count != selectedGpsCount ||
                    !loadIds.All(selectedAll.Contains))
                    throw new Exception(
                        "GPS map omitted selected positions above 5,000 or rejected stale EXIF fingerprints.");
            }
            finally
            {
                using var connection = db.OpenConnection();
                using var delete = connection.CreateCommand();
                delete.CommandText =
                    "DELETE FROM images WHERE local_path LIKE $prefix||'%';";
                delete.Parameters.AddWithValue("$prefix",largeGpsPrefix);
                delete.ExecuteNonQuery();
            }
            Console.WriteLine("PASS: GPS map receives all 5,201 selected positions despite EXIF cache drift.");

            // Error review: the FIRST failed attempt is visible immediately.
            // An internal ExifTool/reader cancellation with the scheduler
            // token still active is a real failure; a scheduler yield is not.
            var tryOriginal = typeof(PCasa.App.MainWindow).GetMethod(
                "TryWriteOriginal",System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!;
            var writeErrors = new OriginalWriteFailureRepository(db);
            const string testWriteKey = "gps:synthetic:reader-timeout";
            try
            {
                using (var schedulerYield = new CancellationTokenSource())
                {
                    schedulerYield.Cancel();
                    try
                    {
                        tryOriginal.Invoke(window, [
                            OriginalWriteFailureRepository.Gps,testWriteKey,
                            gpsA,null,schedulerYield.Token,
                            (Action)(() => throw new OperationCanceledException(
                                "Normal scheduler priority yield"))
                        ]);
                        throw new Exception("Cancelled scheduler work unexpectedly completed.");
                    }
                    catch (System.Reflection.TargetInvocationException ex)
                        when (ex.InnerException is OperationCanceledException) { }
                    if (writeErrors.Unresolved(includeRetrying:true)
                        .Any(entry => entry.PackageKey == testWriteKey))
                        throw new Exception("Normal scheduler yield was recorded as file error.");
                }

                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var done = (bool)tryOriginal.Invoke(window, [
                            OriginalWriteFailureRepository.Gps,testWriteKey,
                            gpsA,null,CancellationToken.None,
                            (Action)(() => throw new OperationCanceledException(
                                "ExifTool reader stopped without scheduler cancellation"))
                        ])!;
                        if (attempt != 3 || done)
                            throw new Exception("Faulty GPS original was never parked.");
                    }
                    catch (System.Reflection.TargetInvocationException ex)
                        when (ex.InnerException is DurablePackageRetryException &&
                              attempt < 3) { }
                    var live = writeErrors.Unresolved(includeRetrying:true)
                        .SingleOrDefault(entry => entry.PackageKey == testWriteKey)
                        ?? throw new Exception("First failed GPS write did not appear in review.");
                    if (live.Attempts != attempt)
                        throw new Exception("GPS review showed the wrong current attempt number.");
                    if (attempt < 3 && writeErrors.Unresolved()
                            .Any(entry => entry.PackageKey == testWriteKey))
                        throw new Exception("GPS failure parked before three attempts.");
                }
                if (writeErrors.Unresolved().All(entry => entry.PackageKey != testWriteKey))
                    throw new Exception("Third failure was not permanently parked.");
                Console.WriteLine("PASS: First internal write abort is visible; yields are not errors; three attempts park file.");
            }
            finally
            {
                writeErrors.Clear(OriginalWriteFailureRepository.Gps,gpsA);
            }

            // Real modal error review: BOTH actions must be available for
            // a selected three-attempt failure, and not for a transient one.
            // This guards the previously unusable "Write errors" dialog.
            var sharedErrors = new OriginalWriteFailureRepository(db);
            const string reviewKey = "gps:test:review-buttons";
            sharedErrors.RecordFailure(OriginalWriteFailureRepository.Gps,
                reviewKey, gpsA, new TimeoutException("Synthetic GPS write timed out"),
                permanent: true);
            Exception? reviewFailure = null;
            try
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        var review = Application.Current.Windows.Cast<Window>()
                            .FirstOrDefault(w => w.Title == UiLanguage.T("Schreibfehler"));
                        try
                        {
                            if (review?.Content is not Grid reviewRoot)
                                throw new Exception("Write errors modal is not open.");
                            var table = reviewRoot.Children.OfType<DataGrid>().Single();
                            var row = table.Items.Cast<OriginalWriteFailureRepository.FailedFile>()
                                .FirstOrDefault(item => item.PackageKey == reviewKey)
                                ?? throw new Exception("Parked GPS original is missing from error table.");
                            var templates = table.Columns.OfType<DataGridTemplateColumn>().ToArray();
                            if (templates.Length != 2)
                                throw new Exception("Write errors has no explicit row Retry/Discard actions.");
                            // Attach the template to a real WPF visual tree:
                            // detached buttons have no reliable DataContext
                            // binding and previously produced a false failure.
                            var probeHost = new StackPanel();
                            Grid.SetRow(probeHost,0);
                            reviewRoot.Children.Add(probeHost);
                            try
                            {
                                foreach (var template in templates)
                                {
                                    var button = (Button?)template.CellTemplate.LoadContent()
                                        ?? throw new Exception("Review action button missing.");
                                    probeHost.Children.Add(button);
                                    button.DataContext = row;
                                    probeHost.UpdateLayout();
                                    System.Windows.Data.BindingOperations.GetBindingExpression(
                                        button, Button.IsEnabledProperty)?.UpdateTarget();
                                    if (!button.IsEnabled)
                                        throw new Exception("A three-attempt error cannot be decided.");
                                    button.DataContext = row with { Attempts = 1 };
                                    probeHost.UpdateLayout();
                                    System.Windows.Data.BindingOperations.GetBindingExpression(
                                        button, Button.IsEnabledProperty)?.UpdateTarget();
                                    if (button.IsEnabled)
                                        throw new Exception("A transient failure allows premature discard.");
                                }
                            }
                            finally { reviewRoot.Children.Remove(probeHost); }
                        }
                        catch (Exception ex) { reviewFailure = ex; }
                        finally { review?.Close(); }
                    }));
                typeof(PCasa.App.MainWindow).GetMethod("ShowWriteErrorsDialog",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance)!.Invoke(window,null);
                if (reviewFailure is not null) throw reviewFailure;
            }
            finally { sharedErrors.Clear(OriginalWriteFailureRepository.Gps,gpsA); }
            Console.WriteLine("PASS: Write errors modal offers working per-file decisions after third attempt.");

            // Reproduce a truly large selected set through the SAME WPF
            // catalog projection, without creating thousands of source files.
            // A hard 5000 cap or a first-chunk-only query must fail this test.
            long[] worldwideIds;
            using (var bulkDb = db.OpenConnection())
            {
                using (var insert = bulkDb.CreateCommand())
                {
                    insert.CommandText = """
                        WITH RECURSIVE numbers(n) AS (
                            SELECT 0 UNION ALL SELECT n+1 FROM numbers WHERE n < 6099
                        )
                        INSERT INTO images (folder_id, photo_folder_id,
                            local_path, canonical_path, file_name, extension,
                            file_size, modified_utc_ticks, width, height,
                            thumbnail_path, indexed_utc)
                        SELECT $root, $album,
                            $prefix || printf('bulk-gps-%04d.jpg', n),
                            $prefix || printf('bulk-gps-%04d.jpg', n),
                            printf('bulk-gps-%04d.jpg', n), '.jpg',
                            1, 1, 10, 10, 'synthetic-test-thumbnail', '2026-01-01'
                        FROM numbers;
                        """;
                    insert.Parameters.AddWithValue("$root", root.Id);
                    insert.Parameters.AddWithValue("$album", b.Id);
                    insert.Parameters.AddWithValue("$prefix", albumB + Path.DirectorySeparatorChar);
                    insert.ExecuteNonQuery();
                }
                using (var exif = bulkDb.CreateCommand())
                {
                    exif.CommandText = """
                        INSERT INTO image_exif(image_id, source_modified_utc_ticks,
                            source_file_size, exif_json, gps_latitude, gps_longitude)
                        SELECT id, modified_utc_ticks, file_size,
                            json_object('SourceFile',local_path),
                            CASE id % 3 WHEN 0 THEN 53.55 WHEN 1 THEN 26.11
                                ELSE -25.27 END,
                            CASE id % 3 WHEN 0 THEN 10.2 WHEN 1 THEN -80.15
                                ELSE 133.77 END
                        FROM images WHERE file_name LIKE 'bulk-gps-%';
                        """;
                    exif.ExecuteNonQuery();
                }
                using var idsCommand = bulkDb.CreateCommand();
                idsCommand.CommandText = """
                    SELECT id FROM images WHERE file_name LIKE 'bulk-gps-%'
                    ORDER BY id;
                    """;
                using var idsReader = idsCommand.ExecuteReader();
                var ids = new List<long>();
                while (idsReader.Read()) ids.Add(idsReader.GetInt64(0));
                worldwideIds = ids.ToArray();
            }
            if (worldwideIds.Length != 6100)
                throw new Exception("Worldwide GPS fixture is incomplete.");
            var worldwidePoints = ((System.Collections.IEnumerable)readGps.Invoke(
                window, [worldwideIds, null, CancellationToken.None, null])!)
                .Cast<object>().ToArray();
            var locations = worldwidePoints.Select(p => (
                Lat: (double)p.GetType().GetProperty("Lat")!.GetValue(p)!,
                Lon: (double)p.GetType().GetProperty("Lon")!.GetValue(p)!,
                Selected: (bool)p.GetType().GetProperty("Selected")!.GetValue(p)!))
                .ToArray();
            if (locations.Length != worldwideIds.Length ||
                locations.Any(p => !p.Selected) ||
                locations.Select(p => (p.Lat, p.Lon)).Distinct().Count() != 3)
                throw new Exception("Large GPS selection dropped images or continents.");
            Console.WriteLine("PASS: 6100 selected GPS photos from Europe, America and Australia survive the catalog projection.");

            // Explicitly leave an EXIF reader and keyword writer session
            // alive (not disposed) when closing. The app MUST kill both
            // owned native children after the scheduler's journal finishes.
            using var lingeringReader = new ImageExifReader();
            lingeringReader.Read(firstOriginal);
            using var lingeringWriter = new ExifToolKeywordWriter.Session();
            lingeringWriter.ReadSnapshot(firstOriginal);
            var registryType = typeof(ImageExifReader).Assembly.GetType(
                "PCasa.App.Services.ExifToolProcessRegistry")
                ?? throw new Exception("ExifTool process owner missing.");
            var liveCount = registryType.GetProperty("LiveCount",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Native process tracking missing.");
            int ActiveChildren() => (int)liveCount.GetValue(null)!;
            if (ActiveChildren() < 2)
                throw new Exception("ExifTool shutdown test did not start two children.");
            // The normal shutdown test only checks a graceful exit. Assert
            // Windows also owns the kernel job which kills ExifTool children
            // when PCasa is terminated before WPF can dispose its readers.
            var jobField = registryType.GetField("_processJob",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)
                ?? throw new Exception("Crash-safe ExifTool process job missing.");
            var processJob = jobField.GetValue(null) as System.Runtime.InteropServices.SafeHandle;
            if (OperatingSystem.IsWindows() &&
                (processJob is null || processJob.IsInvalid || processJob.IsClosed))
                throw new Exception("PCasa did not create its ExifTool kill-on-close job.");
            window.Close();
            Until(window, () => !window.IsVisible,
                "PCasa did not finish shutdown after writing tags.");
            if (ExifToolKeywordWriter.HasLiveSessions || ActiveChildren() != 0)
                throw new Exception(
                    "Closing PCasa left a reader or writer ExifTool process running.");
            window = null;
            Console.WriteLine("RESULT: Live WPF tag-search round trip passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: WPF tag-search round trip: " + ex);
            return 1;
        }
        finally
        {
            try { window?.Close(); }
            catch (Exception closeEx) { Console.Error.WriteLine(closeEx); }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(sourceRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CreatePhoto(string path)
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 75; pixels[i + 1] = 125;
            pixels[i + 2] = 180; pixels[i + 3] = 255;
        }
        var bitmap = BitmapSource.Create(64, 64, 96, 96,
            PixelFormats.Bgr32, null, pixels, 64 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<TreeViewItem> Walk(ItemsControl parent)
    {
        foreach (var item in parent.Items.OfType<TreeViewItem>())
        {
            yield return item;
            foreach (var descendant in Walk(item))
                yield return descendant;
        }
    }

    private static string? ReadPath(TreeViewItem item) =>
        item.Tag?.GetType().GetProperty("FullPath")?.GetValue(item.Tag) as string;

    private static void Until(Window window, Func<bool> predicate, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            window.UpdateLayout();
            var frame = new DispatcherFrame();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (predicate()) return;
            Thread.Sleep(40);
        }
        throw new TimeoutException(message);
    }
}
