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
            // The same checksummed availability logic feeds the Edit menu,
            // thumbnail context command and recycling glyph. An archive
            // record alone is not sufficient when its bytes are corrupt.
            var isolatedOptions = new UndoOptions
            {
                DirectoryPath = Path.Combine(root, "verified-undo"),
                MaximumVersionsPerImage = 3,
                MaximumBytes = 1024L * 1024 * 1024
            };
            var isolated = new UndoHistoryService(isolatedOptions);
            var isolatedVersion = isolated.CaptureBeforeSave(path, CancellationToken.None);
            if (!isolated.HasRestorableVersion(path) ||
                !isolated.GetVerifiedRestorablePaths(CancellationToken.None).Contains(
                    Path.GetFullPath(path)))
                throw new Exception("Valid local Undo snapshot must be restorable.");
            File.WriteAllBytes(isolatedVersion.SnapshotPath, [1, 2, 3]);
            if (isolated.HasRestorableVersion(path) ||
                isolated.GetVerifiedRestorablePaths(CancellationToken.None).Contains(
                    Path.GetFullPath(path)))
                throw new Exception("Corrupt Undo snapshot must never show Restore.");
            File.Delete(isolatedVersion.SnapshotPath);
            if (isolated.HasRestorableVersion(path))
                throw new Exception("Missing Undo snapshot must never show Restore.");
            // Alpha.21: Crop and straighten are ONE unsaved, globally
            // reversible edit session. The MTA writer replays all accepted
            // steps in exactly one verified commit, preserving embedded GPS.
            BitmapSource editorSource;
            using (var source = File.OpenRead(path))
                editorSource = BitmapDecoder.Create(source,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
            var session = new PhotoEditSession(1001, editorSource);
            if (session.HasChanges || session.CanUndo || session.CanRedo)
                throw new Exception("New edit session is not initially clean.");
            session.Apply(new PhotoEditStep("crop", .1, .1, .8, .8));
            var cropSize = (session.Render().PixelWidth, session.Render().PixelHeight);
            session.Apply(new PhotoEditStep("rotate", Degrees: 90));
            var turned = session.Render();
            if (turned.PixelWidth != cropSize.Item2 ||
                turned.PixelHeight != cropSize.Item1 || !session.CanUndo)
                throw new Exception("Quarter-turn did not swap cropped image axes.");
            session.Undo();
            if (!session.CanRedo || session.Render().PixelWidth != cropSize.Item1)
                throw new Exception("Global undo did not revert rotated crop.");
            session.Redo();
            if (!session.CanUndo || session.CanRedo ||
                session.Render().PixelWidth != cropSize.Item2)
                throw new Exception("Global redo did not restore rotation.");
            session.Apply(new PhotoEditStep("rotate", Degrees: 2.5));
            if (session.CanRedo)
                throw new Exception("A new edit after undo/redo kept a stale redo branch.");
            var expected = session.Render();
            // Arbitrary angles must work on the real MTA worker, not just in
            // a preview. WPF TransformedBitmap rejects non-90-degree angles.
            var mtaPreview = Task.Run(() => PhotoEditSession.Render(
                editorSource, session.AppliedSteps)).GetAwaiter().GetResult();
            if (mtaPreview.PixelWidth != expected.PixelWidth ||
                mtaPreview.PixelHeight != expected.PixelHeight)
                throw new Exception("MTA straightening disagrees with UI geometry.");
            // White corners after small-angle straightening must stay white:
            // the auto-crop must not leave black/transparent triangles.
            var white = new byte[320 * 200 * 4];
            Array.Fill(white, (byte)255);
            var whiteSource = BitmapSource.Create(320, 200, 96, 96,
                PixelFormats.Bgra32, null, white, 320 * 4);
            // Freeze at the origin before the MTA test worker receives it.
            whiteSource.Freeze();
            var whiteTurn = Task.Run(() => PhotoEditSession.Render(
                whiteSource, [new PhotoEditStep("rotate", Degrees: 2.5)]))
                .GetAwaiter().GetResult();
            var corners = new byte[checked(whiteTurn.PixelWidth * whiteTurn.PixelHeight * 4)];
            whiteTurn.CopyPixels(corners, whiteTurn.PixelWidth * 4, 0);
            foreach (var (cx, cy) in new[]
            {
                (0, 0), (whiteTurn.PixelWidth - 1, 0),
                (0, whiteTurn.PixelHeight - 1),
                (whiteTurn.PixelWidth - 1, whiteTurn.PixelHeight - 1)
            })
            {
                var offset = (cy * whiteTurn.PixelWidth + cx) * 4;
                if (corners[offset] != 255 || corners[offset + 1] != 255 ||
                    corners[offset + 2] != 255 || corners[offset + 3] != 255)
                    throw new Exception("Straightening left a dark corner.");
            }
            Task.Run(() => JpegCropWriter.Save(path, session.AppliedSteps,
                CancellationToken.None)).GetAwaiter().GetResult();
            using (var saved = File.OpenRead(path))
            {
                var result = BitmapDecoder.Create(saved, BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad).Frames.Single();
                if (result.PixelWidth != expected.PixelWidth ||
                    result.PixelHeight != expected.PixelHeight)
                    throw new Exception(
                        "Single-commit full-resolution edit differs from preview.");
            }
            var preserved = ReadExif(exif, "-n", "-GPSLatitude",
                "-GPSLongitude", "-Keywords", path);
            if (!preserved.Contains("53.5") || !preserved.Contains("10.25") ||
                !preserved.Contains("PCasaCropSmoke"))
                throw new Exception(
                    "Combined Crop/Rotate/straighten lost original metadata.");
            if (Directory.GetFiles(root, ".pcasa-crop-*").Length != 0)
                throw new Exception("Multi-step editor leaked a staged JPEG.");
            Console.WriteLine(
                "PASS: Global editor Undo/Redo, Crop+90°+straighten single P0 JPEG commit, embedded GPS preserved.");
            Console.WriteLine("PASS: Restore availability rejects corrupted and missing snapshots.");
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
