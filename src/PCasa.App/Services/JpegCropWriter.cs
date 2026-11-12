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
    {
        var extension = Path.GetExtension(originalPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(UiLanguage.T("Nur JPEG-Fotos können zugeschnitten werden."));
        if (normalized.Width <= .01 || normalized.Height <= .01 ||
            normalized.X < 0 || normalized.Y < 0 ||
            normalized.Right > 1.000001 || normalized.Bottom > 1.000001)
            throw new InvalidDataException(UiLanguage.T("Der Zuschneidebereich ist ungültig."));
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
            var left = Math.Clamp((int)Math.Floor(normalized.X * pixels.PixelWidth),
                0, pixels.PixelWidth - 1);
            var top = Math.Clamp((int)Math.Floor(normalized.Y * pixels.PixelHeight),
                0, pixels.PixelHeight - 1);
            var width = Math.Clamp((int)Math.Round(normalized.Width * pixels.PixelWidth),
                1, pixels.PixelWidth - left);
            var height = Math.Clamp((int)Math.Round(normalized.Height * pixels.PixelHeight),
                1, pixels.PixelHeight - top);
            if (width < 16 || height < 16)
                throw new InvalidDataException(UiLanguage.T("Der Ausschnitt ist zu klein."));
            var cropped = new CroppedBitmap(pixels, new Int32Rect(left, top, width, height));
            cropped.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var output = new FileStream(staged, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
                output.Flush(flushToDisk: true);
            }
            // ExifTool is the existing release dependency. Preserve all
            // writable original EXIF, IPTC, XMP, GPS and ICC. The crop pixels
            // were baked in display orientation: reset EXIF Orientation.
            CopyMetadata(fullPath, staged, width, height, token);
            using (var verify = new FileStream(staged, FileMode.Open,
                       FileAccess.Read, FileShare.Read))
            {
                var candidate = BitmapDecoder.Create(verify,
                    BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (candidate.Frames.Count != 1 ||
                    candidate.Frames[0].PixelWidth != width ||
                    candidate.Frames[0].PixelHeight != height)
                    throw new InvalidDataException(UiLanguage.T("Die Zuschneidedatei ist nicht vollständig."));
                verify.Position = 0;
                _ = SHA256.HashData(verify);
            }
            token.ThrowIfCancellationRequested();
            // Never replace the original if the Undo capture fails.
            new UndoHistoryService(UndoOptions.Load()).CaptureBeforeSave(fullPath, token);
            token.ThrowIfCancellationRequested();
            File.Replace(staged, fullPath, null);
            // At this point the crop is committed. A failure to restore the
            // file timestamp must never turn a successful edit into an error.
            try { File.SetLastWriteTimeUtc(fullPath, originalWriteTime); }
            catch (IOException ex) { PCasaDiagnostics.Write("Image.Crop.Timestamp", ex); }
            catch (UnauthorizedAccessException ex)
            { PCasaDiagnostics.Write("Image.Crop.Timestamp", ex); }
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
