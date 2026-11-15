using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class CropFlow
{
    private static bool PixelsEqual(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        var stride = a.PixelWidth * 4;
        var aa = new byte[stride * a.PixelHeight];
        var bb = new byte[aa.Length];
        new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0)
            .CopyPixels(aa, stride, 0);
        new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0)
            .CopyPixels(bb, stride, 0);
        return aa.AsSpan().SequenceEqual(bb);
    }

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

            // Alpha.28: editor previews must use full-resolution zoom
            // geometry even when filters replace a smaller preview buffer.
            var physical = ViewerImageGeometry.LogicalEditPixels(
                6000, 4000, 1500, 1000, 1500, 1000);
            if (physical != (6000d, 4000d))
                throw new Exception("An unchanged edit preview reset logical zoom.");
            var croppedPhysical = ViewerImageGeometry.LogicalEditPixels(
                6000, 4000, 1500, 1000, 750, 500);
            if (croppedPhysical != (3000d, 2000d))
                throw new Exception("Zoom geometry lost physical crop dimensions.");

            // Alpha.30: a persisted square crop must never be forced into
            // the original landscape rectangle while catalog rescan is pending.
            var corrected = ViewerImageGeometry.CorrectStaleAspect(
                6000, 4000, 1200, 1200);
            if (Math.Abs(corrected.Width - corrected.Height) > .001 ||
                ViewerImageGeometry.NeedsAspectCorrection(
                    (int)corrected.Width, (int)corrected.Height, 1200, 1200))
                throw new Exception("Square saved crop stretches into a portrait.");
            var originalAspect = ViewerImageGeometry.CorrectStaleAspect(
                6000, 4000, 1200, 800);
            if (originalAspect != (6000d, 4000d))
                throw new Exception("Original uncropped zoom geometry changed.");

            // The exact SAME masked step is used in real-time preview and
            // the durable full-resolution write; no mask sidecar is saved.
            var maskPixels = new byte[16 * 16 * 4];
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var index = (y * 16 + x) * 4;
                maskPixels[index] = y < 8 ? (byte)245 : (byte)48;
                maskPixels[index + 1] = y < 8 ? (byte)175 : (byte)95;
                maskPixels[index + 2] = y < 8 ? (byte)100 : (byte)155;
                maskPixels[index + 3] = 255;
            }
            var skyMask = PhotoEditMasks.Compute(maskPixels, 16, 16, "sky");
            if (skyMask[2 * 16 + 8] < 200 || skyMask[14 * 16 + 8] != 0)
                throw new Exception("Automatic sky mask leaked into the foreground.");
            var highlights = PhotoEditMasks.Compute(maskPixels, 16, 16, "mask-highlights");
            var shadows = PhotoEditMasks.Compute(maskPixels, 16, 16, "mask-shadows");
            if (highlights.Length != 256 || shadows.Length != 256)
                throw new Exception("Mask dimension is not image dimension.");
            var maskSource = BitmapSource.Create(16, 16, 96, 96,
                PixelFormats.Bgra32, null, maskPixels, 16 * 4);
            var maskedTone = PhotoEditSession.Render(maskSource,
                [new PhotoEditStep("saturation", Amount: 1, Mask: "sky")]);
            var maskedOutput = new byte[maskPixels.Length];
            maskedTone.CopyPixels(maskedOutput, 16 * 4, 0);
            var below = (14 * 16 + 8) * 4;
            var above = (2 * 16 + 8) * 4;
            if (!maskedOutput.AsSpan(below, 4)
                    .SequenceEqual(maskPixels.AsSpan(below, 4)))
                throw new Exception("Sky-only adjustment modified unselected foreground.");
            if (maskedOutput.AsSpan(above, 3)
                    .SequenceEqual(maskPixels.AsSpan(above, 3)))
                throw new Exception("Sky mask did not affect selected pixels.");
            Console.WriteLine("PASS: Zoom geometry, automatic sky/luminance masks and selective edits.");

            // Subject selection needs explicit painted marks; background
            // still has a conservative local automatic suggestion.
            var subjectPixels = new byte[16 * 16 * 4];
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                var p = (y * 16 + x) * 4;
                subjectPixels[p] = 95; subjectPixels[p + 1] = 95;
                subjectPixels[p + 2] = 95; subjectPixels[p + 3] = 255;
                if (x >= 4 && x <= 11 && y >= 4 && y <= 11)
                {
                    subjectPixels[p] = 35;
                    subjectPixels[p + 1] = 55;
                    subjectPixels[p + 2] = 240;
                }
            }
            var subject = PhotoEditMasks.Compute(subjectPixels, 16, 16, "subject");
            var background = PhotoEditMasks.Compute(subjectPixels, 16, 16, "background");
            if (subject.Any(value => value != 0) ||
                background.Any(value => value != 0))
                throw new Exception("Automatic foreground/background cannot guess a person without hints.");
            // One loose stroke inside a flat-color person should grow to its
            // actual boundaries rather than remain a round paint stamp.
            var coarse = new[] { new PhotoMaskStroke(.5, .5, .16) };
            var inferred = PhotoEditMasks.Compute(subjectPixels, 16, 16,
                "subject", strokes: coarse);
            var protectedBackground = PhotoEditMasks.Compute(subjectPixels,
                16, 16, "background", strokes: coarse);
            if (inferred[6 * 16 + 6] < 160 ||
                inferred[1 * 16 + 1] > 30 ||
                protectedBackground[6 * 16 + 6] > 95 ||
                protectedBackground[1 * 16 + 1] < 200)
                throw new Exception("Seeded subject segmentation did not follow the color contour.");
            // A sea can have almost the same blue as the sky. The
            // continuous horizontal horizon must stop a top-edge flood.
            const int sceneWidth = 40, sceneHeight = 30;
            var coast = new byte[sceneWidth * sceneHeight * 4];
            for (var y = 0; y < sceneHeight; y++)
            for (var x = 0; x < sceneWidth; x++)
            {
                var p = (y * sceneWidth + x) * 4;
                coast[p] = y < 14 ? (byte)225 : (byte)174;
                coast[p + 1] = y < 14 ? (byte)174 : (byte)135;
                coast[p + 2] = y < 14 ? (byte)110 : (byte)91;
                coast[p + 3] = 255;
            }
            var coastSky = PhotoEditMasks.Compute(coast, sceneWidth,
                sceneHeight, "sky");
            if (coastSky[5 * sceneWidth + 20] < 200 ||
                coastSky[24 * sceneWidth + 20] > 15)
                throw new Exception("Horizon detection included the sea in the sky mask.");

            // Alpha.29: a user-painted subject is independent of preview
            // resolution and persists in the same immutable P0 recipe.
            var painted = new[] { new PhotoMaskStroke(.5, .5, .15) };
            var selected = PhotoEditMasks.Compute(maskPixels, 16, 16,
                "subject", strokes: painted);
            var inverse = PhotoEditMasks.Compute(maskPixels, 16, 16,
                "background", strokes: painted);
            if (selected[8 * 16 + 8] != 255 || selected[0] != 0 ||
                inverse[8 * 16 + 8] != 0 || inverse[0] != 255)
                throw new Exception("Manual subject/background masks are not complementary.");
            var paintedStep = new PhotoEditStep("saturation", Amount: .7,
                Mask: "subject", MaskStrokes: painted);
            var paintedResult = PhotoEditSession.Render(maskSource, [paintedStep]);
            var paintedBytes = new byte[maskPixels.Length];
            paintedResult.CopyPixels(paintedBytes, 16 * 4, 0);
            var center = (8 * 16 + 8) * 4;
            if (paintedBytes.AsSpan(center, 3).SequenceEqual(maskPixels.AsSpan(center, 3)) ||
                !paintedBytes.AsSpan(0, 4).SequenceEqual(maskPixels.AsSpan(0, 4)))
                throw new Exception("Manual subject adjustment was not selective.");
            var selectedPreview = PhotoEditMasks.Preview(maskSource, "subject",
                strokes: painted);
            if (selectedPreview.PixelWidth != 16 || selectedPreview.PixelHeight != 16)
                throw new Exception("Manual mask preview has wrong geometry.");
            var manualUndo = new PhotoEditSession(456, maskSource);
            manualUndo.Apply(paintedStep);
            manualUndo.Undo();
            if (!PixelsEqual(manualUndo.Render(), maskSource))
                throw new Exception("Undo failed to restore a painted subject.");
            manualUndo.Redo();
            if (manualUndo.AppliedSteps.Single().MaskStrokes?.Length != 1)
                throw new Exception("Redo lost the manual subject mask recipe.");
            var once = new PhotoEditSession(457, maskSource);
            once.Apply(new PhotoEditStep("auto-fix"));
            var onceRendered = once.Render();
            once.Apply(new PhotoEditStep("auto-fix"));
            if (once.AppliedSteps.Count != 1 || !PixelsEqual(once.Render(), onceRendered))
                throw new Exception("Automatic image correction compounded on a second click.");
            // The highlights tool must actually affect a selected light pixel,
            // unlike Alpha.28's accidentally persistent shadow-mask setting.
            var fullHighlights = PhotoEditSession.Render(maskSource,
                [new PhotoEditStep("highlights", Amount: .8, Mask: "all")]);
            var highlightsBytes = new byte[maskPixels.Length];
            fullHighlights.CopyPixels(highlightsBytes, 16 * 4, 0);
            if (highlightsBytes.AsSpan(above, 3)
                .SequenceEqual(maskPixels.AsSpan(above, 3)))
                throw new Exception("Highlights adjustment had no effect.");
            Console.WriteLine("PASS: Alpha.29 manual subject masks, Undo, auto idempotence and highlights.");

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

            // Alpha.27: all supported still formats, not RAW, videos or
            // animated/multipage images. No silent JPEG conversion.
            foreach (var suffix in new[] {
                ".jpg", ".jpeg", ".png", ".tif", ".tiff",
                ".bmp", ".gif", ".webp", ".heic", ".heif",
                ".avif", ".jxl" })
                if (!EditableImageFormats.Supports(Path.Combine(root, "still" + suffix)))
                    throw new Exception("Static still-image edit format missing: " + suffix);
            foreach (var suffix in new[] { ".arw", ".nef", ".cr3", ".mp4", ".mov" })
                if (EditableImageFormats.Supports(Path.Combine(root, "readonly" + suffix)))
                    throw new Exception("RAW/video must remain read-only: " + suffix);

            // Full-resolution preview and writer replay the SAME step kinds.
            // Each slider operation may be undone independently, even if the
            // final decoded source originated from a modern libvips codec.
            var toneSteps = new[] {
                new PhotoEditStep("denoise-luma", Amount: .65),
                new PhotoEditStep("denoise-chroma", Amount: .7),
                new PhotoEditStep("sharpen", Amount: .6, Radius: 2, Detail: .8),
                new PhotoEditStep("highlights", Amount: -.35),
                new PhotoEditStep("shadows", Amount: .35),
                new PhotoEditStep("contrast", Amount: .2),
                new PhotoEditStep("temperature", Amount: .2),
                new PhotoEditStep("tint", Amount: -.2),
                new PhotoEditStep("saturation", Amount: .2),
                new PhotoEditStep("auto-sharpen")
            };
            var edits = new PhotoEditSession(901, bitmap);
            foreach (var step in toneSteps) edits.Apply(step);
            if (edits.AppliedSteps.Count != toneSteps.Length ||
                !edits.CanUndo) throw new Exception("Advanced tools do not share Undo.");
            edits.Undo();
            if (edits.AppliedSteps.Count != toneSteps.Length - 1 || !edits.CanRedo)
                throw new Exception("Advanced editor Undo/Redo failed.");
            edits.Redo();
            var tonePreview = Task.Run(() => edits.Render())
                .GetAwaiter().GetResult();
            if (tonePreview.PixelWidth != 480 || tonePreview.PixelHeight != 320)
                throw new Exception("Advanced corrections changed pixel dimensions.");
            Console.WriteLine(
                "PASS: Alpha.27 sharpening/denoising/tone adjustments share preview, MTA and Undo history.");

            // Transparency must not be flattened or saved as JPEG. The format
            // is a real PNG on disk and is validated after the durable writer.
            var pngPath = Path.Combine(root, "alpha.png");
            var alphaPixels = new byte[64 * 48 * 4];
            for (var p = 0; p < alphaPixels.Length; p += 4)
            {
                alphaPixels[p] = 80;
                alphaPixels[p + 1] = 150;
                alphaPixels[p + 2] = 200;
                alphaPixels[p + 3] = (byte)((p / 4) % 64 < 32 ? 255 : 96);
            }
            var alphaBitmap = BitmapSource.Create(64, 48, 96, 96,
                PixelFormats.Bgra32, null, alphaPixels, 64 * 4);
            var pngEncoder = new PngBitmapEncoder();
            pngEncoder.Frames.Add(BitmapFrame.Create(alphaBitmap));
            using (var pngOutput = File.Create(pngPath)) pngEncoder.Save(pngOutput);
            var pngBefore = File.ReadAllBytes(pngPath);
            var pngCopy = JpegCropWriter.GetAvailableCopyPath(pngPath);
            Task.Run(() => JpegCropWriter.SaveAsCopy(pngPath, pngCopy,
                [new PhotoEditStep("contrast", Amount: .15)],
                CancellationToken.None)).GetAwaiter().GetResult();
            if (!File.ReadAllBytes(pngPath).SequenceEqual(pngBefore))
                throw new Exception("PNG save-as-copy changed original bytes.");
            using (var pngInput = File.OpenRead(pngCopy))
            {
                var frame = BitmapDecoder.Create(pngInput, BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad).Frames.Single();
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var resulting = new byte[64 * 48 * 4];
                rgba.CopyPixels(resulting, 64 * 4, 0);
                if (resulting[3] != 255 ||
                    resulting[(40 * 4) + 3] != 96)
                    throw new Exception("PNG alpha channel was lost in edit copy.");
            }
            Console.WriteLine("PASS: PNG edits preserve extension, transparency and original source bytes.");

            // An animation is one media item but NOT a still-photo edit
            // target. Never reduce a multi-frame GIF to a single image.
            var animatedPath = Path.Combine(root, "animated.gif");
            var gifEncoder = new GifBitmapEncoder();
            gifEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            gifEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var gifOutput = File.Create(animatedPath))
                gifEncoder.Save(gifOutput);
            try
            {
                EditableImageFormats.ValidateStillImage(animatedPath);
                throw new Exception("Animated GIF was silently accepted for editing.");
            }
            catch (NotSupportedException) { }
            Console.WriteLine("PASS: Animated images are never flattened by editor.");


            // Alpha.23: "Save as copy" is a full-resolution durable JPEG
            // write. It must never alter the source, overwrite an existing
            // sibling, strip GPS/tags or leave any temp/sidecar files.
            var firstCopy = JpegCropWriter.GetAvailableCopyPath(path);
            if (!string.Equals(firstCopy,
                    Path.Combine(root, "croptest-Kopie.jpg"),
                    StringComparison.OrdinalIgnoreCase))
                throw new Exception("First copy has the wrong sibling filename.");
            var copySteps = new[] { new PhotoEditStep("crop", .1, .1, .5, .5) };
            Task.Run(() => JpegCropWriter.SaveAsCopy(path, firstCopy,
                copySteps, CancellationToken.None)).GetAwaiter().GetResult();
            if (!File.ReadAllBytes(path).SequenceEqual(original))
                throw new Exception("Save as copy modified the original bytes.");
            using (var copyInput = File.OpenRead(firstCopy))
            {
                var copyFrame = BitmapDecoder.Create(copyInput,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.Single();
                if (copyFrame.PixelWidth != 240 ||
                    copyFrame.PixelHeight != 160)
                    throw new Exception("Save as copy did not apply the edit.");
            }
            var copyExif = ReadExif(exif, "-n", "-GPSLatitude",
                "-GPSLongitude", "-Keywords", firstCopy);
            if (!copyExif.Contains("53.5") || !copyExif.Contains("10.25") ||
                !copyExif.Contains("PCasaCropSmoke"))
                throw new Exception("Save as copy lost embedded GPS or IPTC tags.");
            var firstCopyBytes = File.ReadAllBytes(firstCopy);
            var secondCopy = JpegCropWriter.GetAvailableCopyPath(path);
            if (!string.Equals(secondCopy,
                    Path.Combine(root, "croptest-Kopie-2.jpg"),
                    StringComparison.OrdinalIgnoreCase))
                throw new Exception("Existing copy was not numbered safely.");
            Task.Run(() => JpegCropWriter.SaveAsCopy(path, secondCopy,
                copySteps, CancellationToken.None)).GetAwaiter().GetResult();
            if (!File.ReadAllBytes(path).SequenceEqual(original) ||
                !File.ReadAllBytes(firstCopy).SequenceEqual(firstCopyBytes))
                throw new Exception("Second copy modified original or first copy.");
            try
            {
                Task.Run(() => JpegCropWriter.SaveAsCopy(path, firstCopy,
                    copySteps, CancellationToken.None)).GetAwaiter().GetResult();
                throw new Exception("Duplicate durable save overwrote a copy.");
            }
            catch (IOException) { }
            if (!File.ReadAllBytes(firstCopy).SequenceEqual(firstCopyBytes) ||
                Directory.GetFiles(root, ".pcasa-edit-*").Length != 0)
                throw new Exception("Duplicate copy save damaged a previous result or leaked temp files.");

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
            if (Directory.GetFiles(root, ".pcasa-edit-*").Length > 0)
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
            if (session.Original is BitmapFrame || !session.Original.IsFrozen)
                throw new Exception("Editor source remains coupled to an STA decoder.");
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
                session.Original, session.AppliedSteps)).GetAwaiter().GetResult();
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
            if (Directory.GetFiles(root, ".pcasa-edit-*").Length != 0)
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
