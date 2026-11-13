using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PCasa.App.Services;

/// <summary>
/// Optional second Windows decoder path when the Shell has no video frame.
/// Uses the same built-in WPF media stack as PCasa's video player; there is
/// no ffmpeg dependency, network process, sidecar or file modification.
/// Only called by an explicit MANUAL thumbnail refresh (not bulk scans).
/// All media work lives on a short-lived background STA dispatcher.
/// </summary>
internal static class VideoFrameFallback
{
    public static BitmapSource? TryCapture(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var finished = new TaskCompletionSource<BitmapSource?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? workerDispatcher = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Interlocked.Exchange(ref workerDispatcher, dispatcher);
            var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
            DispatcherTimer? captureTimer = null;
            var completed = false;
            void Complete(BitmapSource? bitmap)
            {
                if (completed) return;
                completed = true;
                captureTimer?.Stop();
                try { player.Close(); }
                catch (Exception ex) { PCasaDiagnostics.Write("Video.MediaFallback.Close", ex); }
                finished.TrySetResult(bitmap);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }

            try
            {
                player.MediaFailed += (_, _) => Complete(null);
                player.MediaOpened += (_, _) =>
                {
                    try
                    {
                        if (player.NaturalVideoWidth < 16 ||
                            player.NaturalVideoHeight < 16)
                        {
                            Complete(null);
                            return;
                        }
                        player.Play();
                        player.Pause();
                        var seek = player.NaturalDuration.HasTimeSpan
                            ? Math.Min(2d,
                                Math.Max(.15,
                                    player.NaturalDuration.TimeSpan.TotalSeconds * .1))
                            : .5;
                        player.Position = TimeSpan.FromSeconds(seek);
                        captureTimer = new DispatcherTimer(
                            DispatcherPriority.Background, dispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(430)
                        };
                        captureTimer.Tick += (_, _) =>
                        {
                            try
                            {
                                var w = player.NaturalVideoWidth;
                                var h = player.NaturalVideoHeight;
                                var scale = Math.Min(1d, 512d / Math.Max(w, h));
                                var width = Math.Max(16, (int)Math.Round(w * scale));
                                var height = Math.Max(16, (int)Math.Round(h * scale));
                                var visual = new DrawingVisual();
                                using (var dc = visual.RenderOpen())
                                    dc.DrawVideo(player, new Rect(0, 0, width, height));
                                var bitmap = new RenderTargetBitmap(width, height,
                                    96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(visual);
                                bitmap.Freeze();
                                Complete(bitmap);
                            }
                            catch (Exception ex)
                            {
                                PCasaDiagnostics.Write("Video.MediaFallback.Capture", ex);
                                Complete(null);
                            }
                        };
                        captureTimer.Start();
                    }
                    catch (Exception ex)
                    {
                        PCasaDiagnostics.Write("Video.MediaFallback.Seek", ex);
                        Complete(null);
                    }
                };
                player.Open(new Uri(Path.GetFullPath(path), UriKind.Absolute));
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                PCasaDiagnostics.Write("Video.MediaFallback.Open", ex);
                finished.TrySetResult(null);
            }
            finally
            {
                try { player.Close(); }
                catch (Exception) { }
                finished.TrySetResult(null);
            }
        })
        {
            IsBackground = true,
            Name = "PCasa Video Frame"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // A disconnected NAS or broken codec must never stall a manual P0
        // package indefinitely. Stop the media dispatcher on timeout/cancel.
        try
        {
            if (finished.Task.Wait(TimeSpan.FromSeconds(7), token))
                return finished.Task.GetAwaiter().GetResult();
            return null;
        }
        finally
        {
            var dispatcher = Interlocked.CompareExchange(
                ref workerDispatcher, null, null);
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                catch (TaskCanceledException) { }
                catch (InvalidOperationException) { }
        }
    }
}
