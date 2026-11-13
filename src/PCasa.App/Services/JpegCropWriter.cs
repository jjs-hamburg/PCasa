using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// Full-resolution JPEG cropping, performed only by the durable manual/P0
/// scheduler worker. Original bytes remain untouched until the central Undo
/// archive is verified and the staged result passes full decoding/metadata
/// validation. RAW never enters this writer; no sidecar is retained.
/// </summary>
public static class JpegCropWriter
{
    public static void Save(string originalPath, Rect normalized, CancellationToken token)
        => Save(originalPath, [new PhotoEditStep("crop", normalized.X, normalized.Y,
            normalized.Width, normalized.Height)], token);

    /// <summary>
    /// Atomic multi-step editor commit: replay the unsaved UI history once,
    /// at full resolution, then preserve original metadata and capture Undo
    /// before replacing the JPEG. No intermediate JPEG writes or sidecars.
    /// </summary>
    public static void Save(string originalPath,
        IReadOnlyList<PhotoEditStep> operations, CancellationToken token)
    {
        var extension = Path.GetExtension(originalPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                UiLanguage.T("Nur JPEG-Fotos können zugeschnitten werden."));
        if (operations.Count == 0)
            throw new InvalidDataException("Keine Bildbearbeitung zu speichern.");
        using var lease = SourceFileLocks.Enter(originalPath, token);
        var fullPath = Path.GetFullPath(originalPath);
        var original = new FileInfo(fullPath);
        var originalWriteTime = original.LastWriteTimeUtc;
        var staged = Path.Combine(Path.GetDirectoryName(fullPath)!,
            ".pcasa-crop-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            token.ThrowIfCancellationRequested();
            BitmapSource pixels;
            using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, 131072, FileOptions.SequentialScan))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                image.StreamSource = input;
                image.EndInit();
                image.Freeze();
                pixels = ExifOrientation.Apply(image, input);
            }
            var result = PhotoEditSession.Render(pixels, operations);
            var width = result.PixelWidth;
            var height = result.PixelHeight;
            token.ThrowIfCancellationRequested();
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var output = new FileStream(staged, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
                output.Flush(flushToDisk: true);
            }
            // Same single metadata-copy code path for Crop and Straighten.
            // Pixels are already upright; EXIF Orientation must reset to 1.
            CopyMetadata(fullPath, staged, width, height, token);
            using (var verify = new FileStream(staged, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                var candidate = BitmapDecoder.Create(verify,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (candidate.Frames.Count != 1 ||
                    candidate.Frames[0].PixelWidth != width ||
                    candidate.Frames[0].PixelHeight != height)
                    throw new InvalidDataException(
                        UiLanguage.T("Die Zuschneidedatei ist nicht vollständig."));
                verify.Position = 0;
                _ = SHA256.HashData(verify);
            }
            token.ThrowIfCancellationRequested();
            new UndoHistoryService(UndoOptions.Load())
                .CaptureBeforeSave(fullPath, token);
            token.ThrowIfCancellationRequested();
            File.Replace(staged, fullPath, null);
            try { File.SetLastWriteTimeUtc(fullPath, originalWriteTime); }
            catch (IOException ex)
            { PCasaDiagnostics.Write("Image.Edit.Timestamp", ex); }
            catch (UnauthorizedAccessException ex)
            { PCasaDiagnostics.Write("Image.Edit.Timestamp", ex); }
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void CopyMetadata(string original, string candidate,
        int width, int height, CancellationToken token)
    {
        var exifTool = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(exifTool))
            exifTool = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(exifTool))
            throw new FileNotFoundException(UiLanguage.T("ExifTool fehlt – Original nicht verändert."),
                exifTool);
        var start = new ProcessStartInfo(exifTool)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var value in new[]
        {
            "-overwrite_original", "-P", "-q", "-q",
            "-TagsFromFile", original, "-all:all",
            "-Orientation#=1", "-ExifImageWidth=" + width,
            "-ExifImageHeight=" + height,
            candidate
        })
            start.ArgumentList.Add(value);
        using var child = ExifToolProcessRegistry.Start(start);
        try
        {
            using var cancelled = token.Register(() =>
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            var finished = child.WaitForExit(120000);
            if (!finished)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            token.ThrowIfCancellationRequested();
            var detail = errors.GetAwaiter().GetResult().Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = output.GetAwaiter().GetResult().Trim();
            if (!finished || child.ExitCode != 0)
                throw new IOException(
                    UiLanguage.T("ExifTool konnte die Metadaten nicht vollständig übertragen.") +
                    (string.IsNullOrWhiteSpace(detail) ? "" :
                        Environment.NewLine + detail[..Math.Min(detail.Length, 900)]));
        }
        finally { ExifToolProcessRegistry.Retire(child); }
    }
}
