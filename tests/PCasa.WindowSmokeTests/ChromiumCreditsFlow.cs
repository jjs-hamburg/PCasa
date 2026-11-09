using System.IO;
using System.Windows;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf;

// Extract Chromium's own generated third-party credits from the EXACT CEF
// binary used by PCasa. Do not copy credits from an unrelated Chrome release.
internal static class ChromiumCreditsFlow
{
    public static int Run(string output)
    {
        var app = new PCasa.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (Cef.IsInitialized is not true &&
            Cef.Initialize(new CefSettings
            {
                CachePath = Path.Combine(Path.GetTempPath(), "PCasa-License-CefCache")
            }) is not true)
            throw new InvalidOperationException("Failed to initialize packaged CEF.");

        var credits = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new ChromiumWebBrowser();
        browser.LoadingStateChanged += async (_, args) =>
        {
            if (args.IsLoading || credits.Task.IsCompleted)
                return;
            try
            {
                var html = await browser.GetSourceAsync();
                if (html.Length < 50000 ||
                    html.IndexOf("license", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException(
                        "CEF credits page did not contain substantial license information.");
                credits.TrySetResult(html);
            }
            catch (Exception ex)
            {
                credits.TrySetException(ex);
            }
        };
        var window = new Window
        {
            Title = "PCasa CI Chromium credits extraction",
            Width = 1000,
            Height = 700,
            Content = browser,
            ShowInTaskbar = false
        };
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var ticks = 0;
        timeout.Tick += (_, _) =>
        {
            if (credits.Task.IsCompleted || ++ticks >= 60)
            {
                timeout.Stop();
                frame.Continue = false;
            }
        };
        window.Show();
        browser.Load("chrome://credits/");
        timeout.Start();
        Dispatcher.PushFrame(frame);
        try
        {
            if (!credits.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException(
                    "Could not extract Chromium credits from the packaged CEF within 60 seconds.",
                    credits.Task.Exception);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, credits.Task.Result, System.Text.Encoding.UTF8);
            Console.WriteLine("PASS: Extracted Chromium credits from packaged CEF: " +
                              credits.Task.Result.Length + " characters.");
            return 0;
        }
        finally
        {
            window.Content = null;
            browser.Dispose();
            window.Close();
            Cef.Shutdown();
            app.Shutdown();
        }
    }
}
