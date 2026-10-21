using System.Windows;
using System.Windows.Threading;
using PCasa.App.Services;

namespace PCasa.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
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
