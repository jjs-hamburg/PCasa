using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class ExternalEditSmokeFlow
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "pcasa-alpha33-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var testDirectory = ExternalEditTransaction.UseTestDirectory(
            Path.Combine(root, "ExternalEdit"));
        var undoOptions = new UndoOptions
        {
            DirectoryPath = Path.Combine(root, "Undo"),
            MaximumVersionsPerImage = 5,
            MaximumBytes = 10 * 1024 * 1024
        };
        try
        {
            var photo = Path.Combine(root, "test-photo.jpg");
            var other = Path.Combine(root, "other.jpg");
            WriteJpeg(photo, 36);
            WriteJpeg(other, 52);
            var originalCreation = new DateTime(2024, 6, 15, 12, 0, 0,
                DateTimeKind.Utc);
            var externalEditTime = new DateTime(2026, 11, 17, 9, 10, 0,
                DateTimeKind.Utc);
            File.SetCreationTimeUtc(photo, originalCreation);
            var original = File.ReadAllBytes(photo);

            // One fixed folder, one fixed working name and one durable slot.
            var cancel = ExternalEditTransaction.Begin(photo);
            if (Path.GetFileName(cancel.BackupPath) != "photo.jpg" ||
                ExternalEditTransaction.FindPending()?.Id != cancel.Id ||
                !File.ReadAllBytes(cancel.BackupPath).SequenceEqual(original))
                throw new Exception("Fixed working image or durable session was not created.");
            try
            {
                ExternalEditTransaction.Begin(other);
                throw new Exception("A second external editing session was admitted.");
            }
            catch (InvalidOperationException) { }
            if (!File.Exists(cancel.BackupPath))
                throw new Exception("Blocked second editor destroyed the first session.");

            WriteJpeg(cancel.BackupPath, 175);
            if (!File.ReadAllBytes(photo).SequenceEqual(original) ||
                ExternalEditTransaction.DetectResult(cancel) != cancel.BackupPath)
                throw new Exception("External edit changed the original or lost its working result.");
            if (ExternalEditTransaction.Commit(cancel, cancel.BackupPath,
                    ExternalEditDecision.Cancel) is not null ||
                !File.ReadAllBytes(photo).SequenceEqual(original) ||
                Directory.Exists(cancel.DirectoryPath))
                throw new Exception("Cancel failed to preserve original or clear the workspace.");

            // A named export as used by Topaz is equally valid.
            var copied = ExternalEditTransaction.Begin(photo);
            var export = Path.Combine(copied.DirectoryPath, "photo-topaz.jpg");
            WriteJpeg(export, 170);
            File.SetLastWriteTimeUtc(export, externalEditTime);
            var copy = ExternalEditTransaction.Commit(copied, export,
                ExternalEditDecision.SaveCopy, undoOptions: undoOptions);
            if (copy is null || !File.Exists(copy) ||
                !copy.EndsWith("-Kopie.jpg", StringComparison.OrdinalIgnoreCase) ||
                File.ReadAllBytes(copy).SequenceEqual(original) ||
                !File.ReadAllBytes(photo).SequenceEqual(original) ||
                Directory.Exists(copied.DirectoryPath))
                throw new Exception("Save as Copy did not import a unique output and preserve original.");
            VerifyDates(copy, originalCreation, externalEditTime);

            // Format changes use the original stem, without a suffix when
            // free; only an actual collision triggers gallery '-Kopie'.
            var changedFormat = ExternalEditTransaction.Begin(photo);
            var png = Path.Combine(changedFormat.DirectoryPath, "photo.png");
            WritePng(png, 92);
            File.SetLastWriteTimeUtc(png, externalEditTime);
            try
            {
                ExternalEditTransaction.Commit(changedFormat, png,
                    ExternalEditDecision.Save, undoOptions: undoOptions);
                throw new Exception("Format change was silently treated as Save Copy.");
            }
            catch (NotSupportedException) { }
            if (ExternalEditTransaction.FindPending()?.Id != changedFormat.Id)
                throw new Exception("Rejected Save destroyed the editing session.");
            var firstPng = ExternalEditTransaction.Commit(changedFormat, png,
                ExternalEditDecision.SaveCopy, undoOptions: undoOptions);
            if (firstPng is null ||
                !firstPng.EndsWith("test-photo.png", StringComparison.OrdinalIgnoreCase))
                throw new Exception("Format change incorrectly added a copy suffix.");
            VerifyDates(firstPng, originalCreation, externalEditTime);

            var collision = ExternalEditTransaction.Begin(photo);
            var pngAgain = Path.Combine(collision.DirectoryPath, "second.png");
            WritePng(pngAgain, 105);
            File.SetLastWriteTimeUtc(pngAgain, externalEditTime);
            var secondPng = ExternalEditTransaction.Commit(collision, pngAgain,
                ExternalEditDecision.SaveCopy, undoOptions: undoOptions);
            if (secondPng is null ||
                !secondPng.EndsWith("test-photo-Kopie.png",
                    StringComparison.OrdinalIgnoreCase))
                throw new Exception("Format collision did not use gallery copy naming.");
            VerifyDates(secondPng, originalCreation, externalEditTime);

            var save = ExternalEditTransaction.Begin(photo);
            WriteJpeg(save.BackupPath, 225);
            var edited = File.ReadAllBytes(save.BackupPath);
            var saved = ExternalEditTransaction.Commit(save, save.BackupPath,
                ExternalEditDecision.Save, undoOptions: undoOptions);
            if (saved != photo || !File.ReadAllBytes(photo).SequenceEqual(edited) ||
                Directory.Exists(save.DirectoryPath))
                throw new Exception("Save failed to commit working pixels or clear the workspace.");
            var history = new UndoHistoryService(undoOptions).GetVersions(photo);
            if (!history.Any(v => v.Sha256 == save.OriginalSha256 &&
                    v.SourcePath.Equals(photo, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Save did not use the central Undo archive.");

            var unchanged = ExternalEditTransaction.Begin(photo);
            if (ExternalEditTransaction.DetectResult(unchanged) is not null)
                throw new Exception("Unchanged workspace was incorrectly accepted as edited.");
            ExternalEditTransaction.Commit(unchanged, null,
                ExternalEditDecision.Cancel);
            if (Directory.Exists(unchanged.DirectoryPath))
                throw new Exception("Unchanged workspace was not cleaned.");

            var orphan = ExternalEditTransaction.DirectoryPath;
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "stale.txt"), "stale");
            var newSession = ExternalEditTransaction.Begin(photo);
            // An unrelated abandoned export must not delay the P0 Begin.
            // It is collected by the lower-priority cleanup path instead.
            if (!File.Exists(Path.Combine(orphan, "stale.txt")))
                throw new Exception("P0 editor launch performed background cleanup.");
            ExternalEditTransaction.Commit(newSession, null,
                ExternalEditDecision.Cancel);
            ExternalEditTransaction.CleanupCompletedWorkspaces();
            if (File.Exists(Path.Combine(orphan, "stale.txt")))
                throw new Exception("Orphan cleanup did not remove stale work.");

            // Real editor behavior: Topaz/PhotoCraft may keep photo.jpg
            // open even when PCasa's Cancel/Save button is pressed. A locked
            // work image must not roll back a COMMITTED decision, remove the
            // manifest in an ambiguous way, crash focus activation, or block
            // creation of a completely new session.
            var lockedCancel = ExternalEditTransaction.Begin(photo);
            using (var held = new FileStream(lockedCancel.BackupPath,
                       FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (ExternalEditTransaction.Commit(lockedCancel, null,
                    ExternalEditDecision.Cancel) is not null)
                    throw new Exception("Locked Cancel unexpectedly returned a saved path.");
                if (ExternalEditTransaction.FindPending() is not null)
                    throw new Exception("Locked Cancel left an active manifest.");
                if (!Directory.Exists(lockedCancel.DirectoryPath))
                    throw new Exception("Locked image was unexpectedly deleted.");
                var overlapping = ExternalEditTransaction.Begin(other);
                if (overlapping.DirectoryPath == lockedCancel.DirectoryPath)
                    throw new Exception("Two editor launches used the same working folder.");
                ExternalEditTransaction.Commit(overlapping, null,
                    ExternalEditDecision.Cancel);
            }
            ExternalEditTransaction.CleanupCompletedWorkspaces();
            if (Directory.Exists(lockedCancel.DirectoryPath))
                throw new Exception("Released editor file could not be cleaned later.");

            // Saving may succeed while the editor still holds an ordinary
            // read handle without FILE_SHARE_DELETE. This is NOT a Save error.
            var lockedSave = ExternalEditTransaction.Begin(photo);
            WriteJpeg(lockedSave.BackupPath, 77);
            var expectedSaved = File.ReadAllBytes(lockedSave.BackupPath);
            using (var held = new FileStream(lockedSave.BackupPath,
                       FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var target = ExternalEditTransaction.Commit(lockedSave,
                    lockedSave.BackupPath, ExternalEditDecision.Save,
                    undoOptions: undoOptions);
                if (target != photo ||
                    !File.ReadAllBytes(photo).SequenceEqual(expectedSaved) ||
                    ExternalEditTransaction.FindPending() is not null)
                    throw new Exception("Locked cleanup incorrectly failed a completed Save.");
                var fresh = ExternalEditTransaction.Begin(other);
                ExternalEditTransaction.Commit(fresh, null, ExternalEditDecision.Cancel);
            }
            ExternalEditTransaction.CleanupCompletedWorkspaces();
            if (Directory.Exists(lockedSave.DirectoryPath))
                throw new Exception("Deferred locked Save workspace not cleaned.");

            // A RAW input is staged verbatim for an explicitly RAW-enabled
            // external application. Cancel must never alter the camera file.
            var cameraRaw = Path.Combine(root, "camera.arw");
            var cameraBytes = new byte[] { 1, 33, 76, 9, 28, 119, 20 };
            File.WriteAllBytes(cameraRaw, cameraBytes);
            var rawSession = ExternalEditTransaction.Begin(cameraRaw);
            if (!File.ReadAllBytes(rawSession.BackupPath).SequenceEqual(cameraBytes) ||
                !rawSession.BackupPath.EndsWith(".arw", StringComparison.OrdinalIgnoreCase))
                throw new Exception("RAW source was not staged byte-for-byte.");
            // A DNG is a RAW output, not a still-image encoder extension.
            // Detect it before trying any preview/ExifTool operation.
            var dng = Path.Combine(rawSession.DirectoryPath, "photo.dng");
            File.WriteAllBytes(dng, [0x49, 0x49, 0x2A, 0x00]);
            if (ExternalEditTransaction.DetectResult(rawSession) != dng)
                throw new Exception("DNG output was not detected for RAW editing.");
            ExternalEditTransaction.Commit(rawSession, null, ExternalEditDecision.Cancel);
            if (!File.ReadAllBytes(cameraRaw).SequenceEqual(cameraBytes) ||
                ExternalEditTransaction.FindPending() is not null)
                throw new Exception("Cancel modified camera RAW or left active session.");

            // Panorama and batch-edit program capabilities are independent.
            var panoramic = new ExternalEditorProgram("Pano", "pano.exe",
                "photo", Panorama: true);
            var batchEditor = new ExternalEditorProgram("Batch", "batch.exe",
                "photo", MultiEdit: true);
            var combined = new ExternalEditorProgram("Both", "both.exe",
                "photo", Panorama: true, MultiEdit: true);
            var selection = new[] { photo, other };
            var editorSettings = new ExternalEditorSettings
            {
                Programs = [panoramic, batchEditor, combined]
            };
            if (panoramic.MatchesMultiple(selection) ||
                !batchEditor.MatchesMultiple(selection) ||
                !combined.MatchesMultiple(selection) ||
                editorSettings.ForMultiple(selection).Count != 2 ||
                editorSettings.ForPanorama().Count != 2)
                throw new Exception("Panorama/multiple-edit program flags are conflated.");

            // Two staged images may have different output extensions or
            // editors may overwrite a staged file in place. Never map by
            // directory enumeration order; use staged/source identities.
            var batchRoot = Path.Combine(root, "Batch");
            var originals = new[] { File.ReadAllBytes(photo), File.ReadAllBytes(other) };
            var batch = PanoramaTransaction.Begin(
                [(1L, photo), (2L, other)], [], null, null,
                isPanorama: false, workspaceRoot: batchRoot);
            try
            {
                WriteJpeg(batch.Inputs[0], 205);
                var secondExport = Path.Combine(batch.Directory,
                    Path.GetFileNameWithoutExtension(batch.Inputs[1]) +
                    "-Enhanced.png");
                WritePng(secondExport, 120);
                var detected = PanoramaTransaction.FindMultipleResults(batch);
                if (detected.Length != 2 ||
                    detected.Count(x => x.SourceIndex == 0 &&
                        x.Path == batch.Inputs[0]) != 1 ||
                    detected.Count(x => x.SourceIndex == 1 &&
                        x.Path == secondExport) != 1)
                    throw new Exception("Multiple-edit output mapping lost a changed input/export.");
                if (PanoramaTransaction.CanSaveMultiple(batch, detected))
                    throw new Exception("Format-changing batch result was allowed to overwrite an original.");
                var sameFormat = new[]
                {
                    new ExternalBatchOutput(0, batch.Inputs[0]),
                    new ExternalBatchOutput(1, batch.Inputs[1])
                };
                if (!PanoramaTransaction.CanSaveMultiple(batch, sameFormat))
                    throw new Exception("Same-format one-to-one batch cannot use Save.");
                var ambiguous = new[]
                {
                    new ExternalBatchOutput(0, batch.Inputs[0]),
                    new ExternalBatchOutput(0, batch.Inputs[0])
                };
                if (PanoramaTransaction.CanSaveMultiple(batch, ambiguous))
                    throw new Exception("Two results for one original enabled destructive Save.");
                if (PanoramaTransaction.CanSaveMultiple(batch with
                    { IsPanorama = true }, sameFormat))
                    throw new Exception("Panorama was offered in-place Save.");
                try
                {
                    PanoramaTransaction.MatchSourceIndex(batch, "unrelated.png");
                    throw new Exception("Unrelated result was assigned to an input.");
                }
                catch (InvalidDataException) { }
                if (!File.ReadAllBytes(photo).SequenceEqual(originals[0]) ||
                    !File.ReadAllBytes(other).SequenceEqual(originals[1]))
                    throw new Exception("Batch staging altered an original.");

                var intended = Path.Combine(root, "collision.jpg");
                WriteJpeg(intended, 33);
                if (ExternalCopyNames.NextAvailable(intended) !=
                    Path.Combine(root, "collision-Kopie.jpg"))
                    throw new Exception("Gallery and batch copy names diverged.");
                File.WriteAllText(Path.Combine(root, "collision-Kopie.jpg"), "reserved");
                if (ExternalCopyNames.NextAvailable(intended) !=
                    Path.Combine(root, "collision-Kopie-2.jpg"))
                    throw new Exception("Second collision did not allocate -Kopie-2.");
            }
            finally { PanoramaTransaction.Cleanup(batch); }

            // A program may place the panorama in PCasa's already-known
            // ExternalEdit root, not inside its panorama session directory.
            // Discover exactly one NEW result there, never a historic file.
            var panorama = PanoramaTransaction.Begin(
                [(1L, photo), (2L, other)], [], null, null,
                workspaceRoot: Path.Combine(root, "PanoramaTests"));
            try
            {
                Directory.CreateDirectory(ExternalEditTransaction.DirectoryPath);
                var stale = Path.Combine(ExternalEditTransaction.DirectoryPath,
                    "historic-export.jpg");
                WriteJpeg(stale, 21);
                File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
                var output = Path.Combine(ExternalEditTransaction.DirectoryPath,
                    "topaz-panorama.jpg");
                WriteJpeg(output, 211);
                if (PanoramaTransaction.FindResult(panorama) != output)
                    throw new Exception(
                        "Fresh panorama in known ExternalEdit root was not found.");
                var competing = Path.Combine(ExternalEditTransaction.DirectoryPath,
                    "second-panorama.jpg");
                WriteJpeg(competing, 177);
                if (PanoramaTransaction.FindResult(panorama) is not null)
                    throw new Exception(
                        "Ambiguous ExternalEdit exports were automatically accepted.");
            }
            finally { PanoramaTransaction.Cleanup(panorama); }

            Console.WriteLine(
                "PASS: Isolated external sessions, timestamp/copy rules, DNG, batch mapping, and independent editor flags.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void VerifyDates(string path, DateTime creation,
        DateTime modified)
    {
        if (Math.Abs((File.GetCreationTimeUtc(path) - creation).TotalSeconds) > 2 ||
            Math.Abs((File.GetLastWriteTimeUtc(path) - modified).TotalSeconds) > 2)
            throw new Exception("Save Copy did not preserve creation/edit timestamps.");
    }

    private static void WritePng(string path, byte brightness)
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = brightness;
            pixels[i + 1] = (byte)(255 - brightness);
            pixels[i + 2] = brightness;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create,
            FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }

    private static void WriteJpeg(string path, byte brightness)
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = brightness;
            pixels[i + 1] = (byte)(255 - brightness);
            pixels[i + 2] = brightness;
            pixels[i + 3] = 255;
        }
        var image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 94 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create,
            FileAccess.Write, FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }
}
