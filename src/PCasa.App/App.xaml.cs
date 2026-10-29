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
