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
                    "Ordner", "Optionen", "Hilfe" }))
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
            if (gaps.Count != 4 || gaps.Any(gap => gap < 0 || gap > 80) ||
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
