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
            var copy = ExternalEditTransaction.Commit(copied, export,
                ExternalEditDecision.SaveCopy, undoOptions: undoOptions);
            if (copy is null || !File.Exists(copy) ||
                !copy.EndsWith("-Kopie.jpg", StringComparison.OrdinalIgnoreCase) ||
                File.ReadAllBytes(copy).SequenceEqual(original) ||
                !File.ReadAllBytes(photo).SequenceEqual(original) ||
                Directory.Exists(copied.DirectoryPath))
                throw new Exception("Save as Copy did not import a unique output and preserve original.");

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
            if (File.Exists(Path.Combine(orphan, "stale.txt")))
                throw new Exception("Completed session trash was not cleared before reuse.");
            ExternalEditTransaction.Commit(newSession, null,
                ExternalEditDecision.Cancel);

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

            foreach (var extension in new[] { ".arw", ".nef", ".nrw", ".cr2",
                ".cr3", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw" })
            {
                try
                {
                    ExternalEditTransaction.Begin(Path.Combine(root, "raw" + extension));
                    throw new Exception("RAW reached external editing.");
                }
                catch (NotSupportedException) { }
            }
            Console.WriteLine(
                "PASS: Alpha.34 isolated external sessions, locked Cancel/Save, deferred cleanup, crash-safe commit, Copy, Undo, RAW.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
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
