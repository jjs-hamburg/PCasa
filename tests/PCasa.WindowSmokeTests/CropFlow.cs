using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class CropFlow
{
    internal static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCasa-Crop-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "croptest.jpg");
        var raw = Path.Combine(root, "untouched.arw");
        try
        {
            // Real JPEG, not a mocked bitmap, encoded on WPF's STA.
            var pixels = new byte[480 * 320 * 4];
            for (int y = 0; y < 320; y++)
            for (int x = 0; x < 480; x++)
            {
                int n = (y * 480 + x) * 4;
                pixels[n] = (byte)(x % 256);
                pixels[n + 1] = (byte)(y % 256);
                pixels[n + 2] = 160;
                pixels[n + 3] = 255;
            }
            var bitmap = BitmapSource.Create(480, 320, 96, 96,
                PixelFormats.Bgra32, null, pixels, 480 * 4);
            var encoder = new JpegBitmapEncoder { QualityLevel = 91 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
            var exif = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
            if (string.IsNullOrWhiteSpace(exif) || !File.Exists(exif))
                throw new Exception("Test requires installed ExifTool.");
            RunExif(exif, "-overwrite_original", "-GPSLatitude=53.5",
                "-GPSLatitudeRef=N", "-GPSLongitude=10.25",
                "-GPSLongitudeRef=E", "-Keywords=PCasaCropSmoke", path);
            var original = File.ReadAllBytes(path);
            File.WriteAllBytes(raw, [1, 2, 3, 4]);
            try
            {
                JpegCropWriter.Save(raw, new Rect(0, 0, .5, .5),
                    CancellationToken.None);
                throw new Exception("RAW image write was not rejected.");
            }
            catch (NotSupportedException) { }
            if (File.ReadAllBytes(raw).Length != 4)
                throw new Exception("RAW source changed.");

            // Exercise the real scheduler-worker apartment (MTA), not just
            // the WPF test's STA main thread.
            Task.Run(() => JpegCropWriter.Save(path,
                new Rect(.1, .1, .5, .5), CancellationToken.None))
                .GetAwaiter().GetResult();

            using (var input = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(input, BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad).Frames.Single();
                if (frame.PixelWidth != 240 || frame.PixelHeight != 160)
                    throw new Exception("Crop dimensions do not match requested pixels.");
            }
            var gps = ReadExif(exif, "-n", "-GPSLatitude", "-GPSLongitude",
                "-Keywords", path);
            if (!gps.Contains("53.5") || !gps.Contains("10.25") ||
                !gps.Contains("PCasaCropSmoke"))
                throw new Exception("JPEG crop lost GPS or keyword metadata: " + gps);

            var versions = new UndoHistoryService(UndoOptions.Load())
                .GetVersions(path);
            if (versions.Count < 1)
                throw new Exception("Central pre-save Undo version was not created.");
            if (!File.ReadAllBytes(versions[0].SnapshotPath).SequenceEqual(original))
                throw new Exception("Undo backup does not match the original bytes.");
            if (Directory.GetFiles(root, ".pcasa-crop-*").Length > 0)
                throw new Exception("Temporary crop files were not removed.");
            // Regression for the reported Windows file-lock sequence:
            // Crop -> restore -> crop the same path again, on MTA workers.
            var undo = new UndoHistoryService(UndoOptions.Load());
            Task.Run(() => undo.Restore(path, versions[0].Id,
                CancellationToken.None)).GetAwaiter().GetResult();
            using (var restoredInput = File.OpenRead(path))
            {
                var restoredFrame = BitmapDecoder.Create(restoredInput,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
                if (restoredFrame.PixelWidth != 480 || restoredFrame.PixelHeight != 320)
                    throw new Exception("Undo did not restore original image dimensions.");
            }
            Task.Run(() => JpegCropWriter.Save(path,
                new Rect(.2, .2, .5, .5), CancellationToken.None))
                .GetAwaiter().GetResult();
            using (var croppedAgain = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(croppedAgain,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
                if (frame.PixelWidth != 240 || frame.PixelHeight != 160)
                    throw new Exception("Second crop after restore failed.");
            }
            if (!undo.HasRestorableVersion(path))
                throw new Exception("Verified restore availability lost after second crop.");
            Console.WriteLine("PASS: Crop -> Restore -> Crop reuses same path without file lock.");
            Console.WriteLine("PASS: MTA JPEG crop, source RAW untouched, GPS/keywords preserved, byte-perfect central Undo.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static string ReadExif(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException("ExifTool not started.");
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0) throw new IOException(errors);
        return output;
    }

    private static void RunExif(string exe, params string[] args)
        => _ = ReadExif(exe, args);
}
