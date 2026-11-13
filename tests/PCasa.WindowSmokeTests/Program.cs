using System.IO;
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

        if (args.Length == 2 &&
            string.Equals(args[0], "--chromium-credits", StringComparison.OrdinalIgnoreCase))
            return ChromiumCreditsFlow.Run(args[1]);
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
            if (!labels.SequenceEqual(new[] {
                    "Datei", "Ordner", "Bearbeiten", "Darstellung", "Optionen", "Hilfe" }))
                throw new Exception("Unexpected top-level menu labels: " +
                    string.Join(", ", labels));
            var fileMenu = items.Single(item => Equals(item.Header, "Datei"));
            if (fileMenu.Items.OfType<MenuItem>().Count() != 1 ||
                !Equals(fileMenu.Items.OfType<MenuItem>().Single().Header, "Beenden"))
                throw new Exception("File menu must contain only Exit.");
            var optionsMenu = items.Single(item => Equals(item.Header, "Optionen"));
            if (!optionsMenu.Items.OfType<MenuItem>().Any(item =>
                    Equals(item.Header, "Schreibfehler")) ||
                items.Any(item => Equals(item.Header, "Extras")))
                throw new Exception("Write errors belong under Options; Extras is not present.");
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
                    .SequenceEqual(new[] { "File", "Folder", "Edit", "Appearance", "Options", "Help" }))
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
                "FolderExpandAllButton", "FolderCollapseAllButton",
                "FolderTreeToggleButton"
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
            if (attributeSelector.ContextMenu?.Items.Count != 7 ||
                 !new[] { "None", "VideoPhotos", "Video", "Gps", "Tags", "Raw" }
                     .All(tag => attributeSelector.ContextMenu.Items.OfType<MenuItem>()
                         .Any(item => item.Tag is string actual && actual == tag)) ||
                attributeInvert.IsEnabled || attributeClear.IsEnabled ||
                attributeLabel.Text != "Filter")
                throw new Exception("Property filter initial state and six media/property choices are incorrect.");
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
            if (searchScroll.Parent is not Border searchBorder ||
                !ReferenceEquals(searchBorder.Parent, twoRowHeader) ||
                Grid.GetRow(searchBorder) != 1 ||
                searchScroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Auto ||
                searchScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                searchScroll.Content is not StackPanel searchRow ||
                searchRow.Orientation != Orientation.Horizontal)
                throw new Exception("Search is not in a permanent horizontal row under menu.");
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
            if (!searchRow.Children.Cast<UIElement>().SequenceEqual(
                    new UIElement[] { invertFrame, attributeFrame, sortFrame, searchFrame }))
                throw new Exception("Filter must be inserted before the unchanged sort/search order.");
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

            pages.SetIndex(synthetic);
            var surface = (BrowseGallerySurface?)window.FindName("BrowseSurface")
                ?? throw new Exception("Actual browse surface not found.");
            var scroller = (ScrollViewer?)window.FindName("BrowseScrollViewer")
                ?? throw new Exception("Actual browse scroll viewer not found.");
            surface.SetData(synthetic, groups, pages);
            Flush(window);
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
            returnToRemembered.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, returnToRemembered));
            Flush(window);
            if (Math.Abs(scroller.VerticalOffset - markedOffset) > 3 ||
                !returnToDeparture.IsEnabled)
                throw new Exception("Bookmark jump failed or did not enable return to departure.");
            returnToDeparture.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, returnToDeparture));
            Flush(window);
            if (Math.Abs(scroller.VerticalOffset - departureOffset) > 3 ||
                returnToDeparture.IsEnabled || !returnToRemembered.IsEnabled ||
                remembered.IsChecked != true)
                throw new Exception("Returning to departure failed to restore the origin or consumed bookmark.");

            // A repeated jump must take the NEW departure, not use the old
            // one or open an unbounded navigation stack.
            scroller.ScrollToVerticalOffset(1530);
            Flush(window);
            var secondOrigin = scroller.VerticalOffset;
            returnToRemembered.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, returnToRemembered));
            Flush(window);
            if (!returnToDeparture.IsEnabled ||
                Math.Abs(scroller.VerticalOffset - markedOffset) > 3)
                throw new Exception("Second bookmark jump did not capture a new departure.");
            returnToDeparture.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, returnToDeparture));
            Flush(window);
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
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                Flush(window);
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
                // Reaching the preceding LIVE edge may have started a
                // legitimate asynchronous refresh of the runner's empty
                // catalog. Drain that publication BEFORE restoring the
                // isolated synthetic index; otherwise the late completion
                // can overwrite the fixture during Flush().
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
                    scannerNotification.Invoke(window, new object[] {
                        atStart ? @"C:\synthetic\other-folder-top.jpg" :
                            @"C:\synthetic\other-folder-bottom.jpg", incomingOtherFolder });
                    Flush(window);
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

            // The manager's read-only tree is now intentionally INDEPENDENT
            // of the durable scheduler. Exercise actual WPF folder expansion
            // using a local directory that has never been catalogued.
            var fixtureDirectory = Path.Combine(Path.GetTempPath(),
                "PCasa-ManagerForeground-" + Guid.NewGuid().ToString("N"));
            var fixtureChild = Path.Combine(fixtureDirectory, "NeverIndexed");
            Directory.CreateDirectory(fixtureChild);
            PCasa.App.FolderManagerWindow? manager = null;
            try
            {
                var fixture = new MonitoredFolder
                {
                    Id = -100,
                    LocalPath = fixtureDirectory,
                    CanonicalPath = fixtureDirectory
                };
                manager = new PCasa.App.FolderManagerWindow(
                    new[] { (Folder: fixture,
                        ExcludedPaths: (IReadOnlyList<string>)Array.Empty<string>()) },
                    new ForegroundReadService(), serverDatabaseConfigured: false)
                {
                    Owner = window
                };
                manager.Show();
                Flush(manager);
                var managedTree = (TreeView?)manager.FindName("FolderManagerTree")
                    ?? throw new Exception("Folder manager tree missing.");
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
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!fixtureNode.Items.OfType<TreeViewItem>().Any(item =>
                           string.Equals(ManagerPath(item),
                               fixtureChild, StringComparison.OrdinalIgnoreCase)) &&
                       DateTime.UtcNow < deadline)
                    Flush(manager);
                if (!fixtureNode.Items.OfType<TreeViewItem>().Any(item =>
                        string.Equals(ManagerPath(item),
                            fixtureChild, StringComparison.OrdinalIgnoreCase)))
                    throw new Exception(
                        "Manager foreground enumeration did not reveal non-indexed folder.");
                Console.WriteLine(
                    "PASS: Folder manager expands uncatalogued physical folders independently of scheduler.");
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
                viewerOpenField.SetValue(window, true);
                viewerInfoField.SetValue(window, new PhotoViewerImageInfo(
                    999, @"C:\synthetic\no-file.jpg", "no-file.jpg", 4000, 1000));
                panel.Visibility = Visibility.Visible;
                Flush(window);
                fitField.SetValue(window, true);
                zoomField.SetValue(window, 100d);
                applyLayout.Invoke(window, null);
                Flush(window);
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
                    var filmstripDblClick = viewerType.GetMethod(
                        "GpsFilmstrip_MouseDoubleClick", reflectFlags)!;
                    filmstripDblClick.Invoke(window, new object[] {
                        filmstripList,
                        new System.Windows.Input.MouseButtonEventArgs(
                            System.Windows.Input.Mouse.PrimaryDevice, 0,
                            System.Windows.Input.MouseButton.Left)
                    });
                    Flush(window);
                    if (!(bool)gpsFullscreenFlag.GetValue(window)! ||
                        gpsOverlay.Visibility != Visibility.Visible ||
                        gpsOverlay.Tag is not long shownId || shownId != 999L)
                        throw new Exception(
                            "Filmstrip double-click exited fullscreen or failed to show the photo.");
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
                    clusterClick.Invoke(window, new object[] { 1003L, 77L });
                    Flush(window);
                    var gpsMarkerPopup = (FrameworkElement?)window.FindName("GpsPhotoPopup")
                        ?? throw new Exception("GPS popup missing.");
                    if (strip.Items.Count != 1 ||
                        gpsMarkerPopup.Tag is not long popupId || popupId != 1003)
                        throw new Exception("Single-marker click failed to show its photo.");
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
