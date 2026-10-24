using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            var sortDirection = (TextBlock?)window.FindName("GallerySortDirectionGlyph")
                ?? throw new Exception("Current sorting direction not visible.");
            var sortLabel = (TextBlock?)window.FindName("GallerySortSelectedFieldLabel")
                ?? throw new Exception("Current sorting criterion not visible.");
            if (!sortName.IsChecked || !sortAscending.IsChecked ||
                sortDescending.IsChecked || sortDirection.Text != "↓" ||
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
                    sortDirection.Text == "↑" && sortLabel.Text == "Name" &&
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
                    sortDirection.Text == "↓" && names.Length == 12 &&
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
            window.Close();
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
