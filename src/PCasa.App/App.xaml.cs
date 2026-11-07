using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class App : Application
{
    private static bool _tooltipsEnabled = true;

    // A single application-wide gate, including dynamically created folder
    // tree elements, context menus and independently owned dialogs.
    public static void SetTooltipsEnabled(bool enabled)
        => _tooltipsEnabled = enabled;

    private static void GlobalToolTipOpening(object sender, RoutedEventArgs e)
    {
        if (!_tooltipsEnabled)
            e.Handled = true;
    }

    public App()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement),
            ToolTipService.ToolTipOpeningEvent,
            new RoutedEventHandler(GlobalToolTipOpening), true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement),
            ToolTipService.ToolTipOpeningEvent,
            new RoutedEventHandler(GlobalToolTipOpening), true);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Always log the ACTUAL loaded executable and assembly before any
        // window, network or scheduler work. A copied/deleted installation
        // folder is NOT sufficient evidence of which process is running.
        // The CI build embeds its exact Git commit in InformationalVersion.
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "(unknown)";
        PCasaDiagnostics.WriteMessage("App.Build",
            "Version=" + version +
            "; ModuleVersionId=" + assembly.ManifestModule.ModuleVersionId +
            "; ProcessId=" + Environment.ProcessId +
            "; Executable=" + Environment.ProcessPath +
            "; AppAssembly=" + assembly.Location);
    }

    // An isolated user-invoked benchmark mode; it never creates the
    // ordinary MainWindow, scheduler or monitored-folder background jobs.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 3 &&
            string.Equals(e.Args[0], "--database-maintenance",
                StringComparison.OrdinalIgnoreCase))
        {
            // No MainWindow, scheduler, native GPS browser or thumbnail writer
            // exists in this process. Wait for the old process to fully exit
            // BEFORE opening or replacing its WAL-backed catalog.
            // Maintenance starts without a startup window. WPF forbids
            // assigning null to Application.StartupUri in .NET 10.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            var path = e.Args[1];
            var parentPid = int.TryParse(e.Args[2], out var parsed) ? parsed : 0;
            // This is a fresh process without MainWindow.InitializeAppearance.
            // Apply the same persisted language before creating any dialog.
            try
            {
                var settings = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PCasa", "appearance.json");
                if (File.Exists(settings))
                {
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(settings));
                    if (json.RootElement.TryGetProperty("Language", out var language))
                        UiLanguage.Current.Select(language.GetString() ?? "de");
                }
            }
            catch (Exception ex) { PCasaDiagnostics.Write("Database.MaintenanceLanguage", ex); }
            // This separate maintenance process owns a real visible window.
            // The previous PCasa process can exit safely while the window
            // stays alive and reports errors, progress and completion.
            var maintenanceWindow = new DatabaseMaintenanceProgressWindow(
                DatabaseMaintenance.ReadRequestOperation(path));
            MainWindow = maintenanceWindow;
            // ContentRendered, not process creation or Loaded, is the
            // observable handoff boundary. The original PCasa only closes
            // after the user has a rendered maintenance window to watch.
            var readySignaled = false;
            maintenanceWindow.ContentRendered += (_, _) =>
            {
                if (readySignaled) return;
                readySignaled = true;
                try
                {
                    File.WriteAllText(path + ".ready", Environment.ProcessId.ToString(
                        CultureInfo.InvariantCulture));
                    maintenanceWindow.Start(path, parentPid);
                }
                catch (Exception ex)
                {
                    PCasaDiagnostics.Write("Database.MaintenanceReady", ex);
                    maintenanceWindow.ShowStartupFailure(ex);
                }
            };
            maintenanceWindow.Show();
            return;
        }
        if (e.Args.Length == 0 || !string.Equals(e.Args[0],
            "--jpeg-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            // Only the interactive launch creates MainWindow. Setting a
            // valid URI here, rather than in App.xaml, keeps the isolated
            // maintenance process window-free until restore/reset finishes.
            StartupUri = new Uri("MainWindow.xaml", UriKind.Relative);
            base.OnStartup(e);
            return;
        }

        try
        {
            var sources = e.Args.Length > 1
                ? e.Args.Skip(1).ToArray()
                : Array.Empty<string>();
            if (sources.Length == 0)
            {
                var picker = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "PCasa JPEG-Benchmark – 1 bis 20 JPGs auswählen (Tags + GPS)",
                    Filter = "JPEG-Bilder (*.jpg;*.jpeg)|*.jpg;*.jpeg",
                    Multiselect = true
                };
                if (picker.ShowDialog() != true) return;
                sources = picker.FileNames;
            }

            var result = JpegTagBenchmarkRunner.RunMany(sources);
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "PCasa");
            Directory.CreateDirectory(folder);
            var report = Path.Combine(folder, "JPEG-Benchmark.txt");
            File.WriteAllText(report, result + Environment.NewLine,
                System.Text.Encoding.UTF8);
            PcasaDialog.Show(result + Environment.NewLine + Environment.NewLine +
                "Bericht gespeichert unter:" + Environment.NewLine + report,
                "PCasa – JPEG-Benchmark", MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PcasaDialog.Show(
                "Der JPEG-Benchmark konnte nicht abgeschlossen werden." +
                Environment.NewLine + ex.Message +
                Environment.NewLine +
                "Das Original wurde nicht verändert.",
                "PCasa – JPEG-Benchmark", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // WPF can exit through a path other than MainWindow.Close().
        // Always invoke the SAME ExifTool owner cleanup on application exit;
        // the Windows kill-on-close job is the final crash-safe fallback.
        try
        {
            ExifToolProcessRegistry.StopAll();
            // GPS uses the open-source CEF engine only if its panel was opened.
            // Shutdown after WPF closes the owned map browser, not on every
            // incidental sidebar switch.
            if (CefSharp.Cef.IsInitialized is true) CefSharp.Cef.Shutdown();
        }
        finally { base.OnExit(e); }
    }

    // The folder tree has no hover highlighting. A full-path tooltip is
    // allowed ONLY when the displayed name is actually clipped.
    private void FolderNameText_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not TextBlock label || label.ActualWidth <= 0)
        {
            e.Handled = true;
            return;
        }
        var typeface = new Typeface(label.FontFamily, label.FontStyle,
            label.FontWeight, label.FontStretch);
        var measured = new FormattedText(label.Text ?? string.Empty,
            CultureInfo.CurrentUICulture, label.FlowDirection, typeface,
            label.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(label).PixelsPerDip);
        if (measured.WidthIncludingTrailingWhitespace <= label.ActualWidth + 1)
            e.Handled = true;
    }

    private static void App_DispatcherUnhandledException(
        object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Do not hide an unknown failure or force a damaged application state.
        // Record the actual exception so an offline/reconnect crash can be diagnosed.
        PCasaDiagnostics.Write("DispatcherUnhandledException", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(
        object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            PCasaDiagnostics.Write("AppDomain.UnhandledException", exception);
        }
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender, UnobservedTaskExceptionEventArgs e)
    {
        PCasaDiagnostics.Write("TaskScheduler.UnobservedTaskException", e.Exception);
    }
}
