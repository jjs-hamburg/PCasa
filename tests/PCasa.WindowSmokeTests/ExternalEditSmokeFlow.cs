using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class ExternalEditSmokeFlow
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "pcasa-external-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "photo.jpg");
            WriteJpeg(path, 36);
            var original = File.ReadAllBytes(path);
            // Crash recovery: a snapshot must be findable by source identity.
            var cancel = ExternalEditTransaction.Begin(path);
            if (ExternalEditTransaction.FindPending(path)?.Id != cancel.Id)
                throw new Exception("External edit backup cannot be recovered.");
            WriteJpeg(path, 175);
            if (ExternalEditTransaction.DetectResult(cancel) != path)
                throw new Exception("In-place edit was not detected.");
            if (ExternalEditTransaction.Commit(cancel, path,
                    ExternalEditDecision.Cancel) is not null ||
                !File.ReadAllBytes(path).SequenceEqual(original) ||
                Directory.Exists(cancel.DirectoryPath) ||
                ExternalEditTransaction.FindPending(path) is not null)
                throw new Exception("Cancel did not restore original and remove backup.");

            var copySession = ExternalEditTransaction.Begin(path);
            WriteJpeg(path, 175);
            var copy = ExternalEditTransaction.Commit(copySession, path,
                ExternalEditDecision.SaveCopy);
            if (copy is null || !File.Exists(copy) ||
                !Path.GetFileName(copy).Contains("-Kopie", StringComparison.OrdinalIgnoreCase) ||
                File.ReadAllBytes(copy).SequenceEqual(original) || !File.ReadAllBytes(path).SequenceEqual(original) ||
                Directory.Exists(copySession.DirectoryPath))
                throw new Exception("Save as Copy did not keep result and restore original.");

            var save = ExternalEditTransaction.Begin(path);
            WriteJpeg(path, 225);
            var savedPixels = File.ReadAllBytes(path);
            var saved = ExternalEditTransaction.Commit(save, path,
                ExternalEditDecision.Save);
            if (saved != path || !File.ReadAllBytes(path).SequenceEqual(savedPixels) ||
                Directory.Exists(save.DirectoryPath))
                throw new Exception("Save did not keep edited original and delete backup.");
            if (!new UndoHistoryService(UndoOptions.Load()).GetVersions(path)
                    .Any(v => v.Id == save.Id && v.Sha256 == save.OriginalSha256))
                throw new Exception("External Save did not preserve JPEG undo history.");

            var unchanged = ExternalEditTransaction.Begin(path);
            if (ExternalEditTransaction.DetectResult(unchanged) is not null)
                throw new Exception("Unedited file was mistaken for a saved result.");
            ExternalEditTransaction.Commit(unchanged, null, ExternalEditDecision.Cancel);

            foreach (var extension in new[] { ".arw", ".nef", ".nrw", ".cr2",
                ".cr3", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw", ".crw" })
            {
                try
                {
                    ExternalEditTransaction.Begin(Path.Combine(root,
                        "raw" + extension));
                    throw new Exception("RAW was sent to the external transaction.");
                }
                catch (NotSupportedException) { }
            }
            Console.WriteLine("PASS: External edits Cancel/Save Copy/Save, undo, backup cleanup and RAW rejection.");
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
        var bitmap = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 94 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None);
        encoder.Save(stream);
        stream.Flush(true);
    }

}
