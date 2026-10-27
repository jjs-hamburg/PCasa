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
            var sortAscending = SortItem("Ascending");
            var sortDescending = SortItem("Descending");
            var sortDirection = (System.Windows.Shapes.Path?)window.FindName(
                "GallerySortDirectionIcon")
                ?? throw new Exception("LAP-style sort direction icon not found.");
            var downArrow = Geometry.Parse(
                "M 20,2 L 20,17 M 16,13 L 20,17 L 24,13").ToString();
            var upArrow = Geometry.Parse(
                "M 20,17 L 20,2 M 16,6 L 20,2 L 24,6").ToString();
            var sortLabel = (TextBlock?)window.FindName("GallerySortSelectedFieldLabel")
                ?? throw new Exception("Current sorting criterion not visible.");
            if (!sortName.IsChecked || !sortAscending.IsChecked ||
                sortDescending.IsChecked || sortDirection.Data?.ToString() != downArrow ||
                sortLabel.Text != "Name")
                throw new Exception("PCasa did not show Name ascending in the search bar.");

            var sortIndexField = typeof(PCasa.App.MainWindow).GetField(
                "_displayedBrowseIndex",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)!;
            IReadOnlyList<BrowseThumbnailIndexEntry> SortedGallery()
                => (IReadOnlyList<BrowseThumbnailIndexEntry>)
                    sortIndexField.GetValue(window)!;
            sortDescending.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Until(window, () =>
            {
                var images = SortedGallery();
                var bImages = images.Where(image => image.PhotoFolderId == b.Id)
                    .Select(image => image.FileName).ToArray();
                return sortDescending.IsChecked && sortName.IsChecked &&
                    sortDirection.Data?.ToString() == upArrow && sortLabel.Text == "Name" &&
                    images.Count == 24 && bImages.Length == 12 &&
                    bImages.SequenceEqual(bImages.OrderByDescending(
                        name => name, StringComparer.OrdinalIgnoreCase)) &&
                    images.Take(12).All(image => image.PhotoFolderId == a.Id) &&
                    images.Skip(12).All(image => image.PhotoFolderId == b.Id);
            }, "Descending Name sort changed folder order or did not sort within each folder.");
            sortAscending.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Until(window, () =>
            {
                var images = SortedGallery();
                var names = images.Where(image => image.PhotoFolderId == b.Id)
                    .Select(image => image.FileName).ToArray();
                return sortAscending.IsChecked &&
                    sortDirection.Data?.ToString() == downArrow && names.Length == 12 &&
                    names.SequenceEqual(names.OrderBy(
                        name => name, StringComparer.OrdinalIgnoreCase));
            }, "Ascending Name sort did not restore default order.");
            Console.WriteLine("PASS: Sort menu defaults to Name ascending; descending preserves distinct folders.");

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
            var skipCollapsed = (MenuItem?)window.FindName("HideCollapsedFoldersMenu")
                ?? throw new Exception("Skip-collapsed gallery option missing.");
            search.Clear();
            skipCollapsed.IsChecked = true;
            skipCollapsed.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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

            fileMode.IsChecked = true;
            search.Text = "topaz";
            Until(window, () => SortedGallery().Count == 10 &&
                surface.GetFolderOffset(a.Id) is not null &&
                surface.GetFolderOffset(b.Id) is not null &&
                surface.HiddenFolderIds.Count == 0,
                "Global filename search excluded hidden descendant folders.");
            Console.WriteLine(
                "PASS: Global filename search includes all collapsed descendants.");

            search.Clear();
            Until(window, () => surface.HiddenFolderIds.Contains(a.Id) &&
                surface.HiddenFolderIds.Contains(b.Id),
                "Clearing search failed to restore gallery-only collapsed-folder hiding.");
            monitoredRootItem.IsExpanded = true;
            skipCollapsed.IsChecked = false;
            skipCollapsed.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
                        "An offline original was falsely counted as a confirmed Tag write.");
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
                    var current = (presenter?.FindName("CurrentFileText") as TextBlock)?.Text;
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
                        $"{ex.Message} Phase={phase}, File={current}, Folder={folder}, " +
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
            endManual.Invoke(window, [waitingNasKey]);
            Console.WriteLine(
                "PASS: Active NAS scan replaces queued Tag caption and restores pending series.");

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

            Console.WriteLine(
                "PASS: Tag-write P0 progress shows one real file and red marker clears after success.");
            Console.WriteLine(
                "PASS: WPF two-image Unicode tag add/remove updates original JPEGs, counts and recent tags.");
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
