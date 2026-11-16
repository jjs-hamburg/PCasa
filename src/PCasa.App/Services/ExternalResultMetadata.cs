using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PCasa.App.Services;

/// <summary>
/// Copy camera/capture and descriptive metadata from a source image to an
/// already-rendered external result. Never convert layered PSD pixels.
/// ExifTool's -TagsFromFile is restricted to metadata groups; not Photoshop
/// image resources, previews, ICC color profiles or the layer/mask payload.
/// </summary>
public static class ExternalResultMetadata
{
    public static void CopyAll(string original, string result,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(original) || !File.Exists(result))
            throw new FileNotFoundException("Metadatenquelle oder Ergebnis fehlt.");
        if (Path.GetFullPath(original).Equals(Path.GetFullPath(result),
            StringComparison.OrdinalIgnoreCase))
            return;
        var program = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(program))
            program = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(program))
            throw new FileNotFoundException("ExifTool für Panorama-/PSD-Metadaten fehlt.", program);

        var originalData = new ImageExifReader();
        ImageExifData before;
        using (originalData) before = originalData.Read(original, token);

        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[]
        {
            "-overwrite_original", "-P",
            "-TagsFromFile", original,
            "-EXIF:All", "-IPTC:All", "-XMP:All",
            // Some PSD writers represent these fields only as XMP. Preserve
            // the original camera capture even when native EXIF is unavailable.
            "-XMP-exif:DateTimeOriginal<EXIF:DateTimeOriginal",
            "-XMP-tiff:Make<EXIF:Make",
            "-XMP-tiff:Model<EXIF:Model",
            "-XMP-exif:GPSLatitude<Composite:GPSLatitude",
            "-XMP-exif:GPSLongitude<Composite:GPSLongitude",
            result
        })
            start.ArgumentList.Add(arg);
        using var process = ExifToolProcessRegistry.Start(start);
        using var cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            if (!process.WaitForExit(120000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Metadatenübernahme überschreitet 120 Sekunden.");
            }
            token.ThrowIfCancellationRequested();
            var output = stdout.GetAwaiter().GetResult();
            var errors = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new IOException("ExifTool: " + errors + " " + output);
        }
        finally { ExifToolProcessRegistry.Retire(process); }

        using var resultReader = new ImageExifReader();
        var after = resultReader.Read(result, token);
        if (!string.IsNullOrWhiteSpace(before.DateTimeOriginal) &&
            !string.Equals(before.DateTimeOriginal, after.DateTimeOriginal,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Originales Aufnahmedatum wurde nicht vollständig übertragen.");
        if (!string.IsNullOrWhiteSpace(before.CameraMake) &&
            !string.Equals(before.CameraMake, after.CameraMake,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kamerahersteller wurde nicht übertragen.");
        if (!string.IsNullOrWhiteSpace(before.CameraModel) &&
            !string.Equals(before.CameraModel, after.CameraModel,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kameramodell wurde nicht übertragen.");
        if (before.GpsLatitude is double latitude &&
            before.GpsLongitude is double longitude &&
            (after.GpsLatitude is not double resultLat ||
             after.GpsLongitude is not double resultLon ||
             Math.Abs(latitude - resultLat) > .00001 ||
             Math.Abs(longitude - resultLon) > .00001))
            throw new InvalidDataException("GPS-Koordinaten wurden nicht übertragen.");
    }
}
