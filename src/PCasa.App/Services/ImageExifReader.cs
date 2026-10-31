using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PCasa.App.Services;

/// <summary>
/// One native metadata reader per scan. The raw tag dictionary is retained,
/// while camera and GPS are also materialized for quick catalog queries.
/// No source image, preview or sidecar is modified.
/// </summary>
public sealed record ImageExifData(string Json, string? CameraMake,
    string? CameraModel, string? DateTimeOriginal,
    double? GpsLatitude, double? GpsLongitude, double? GpsAltitude);

public sealed class ImageExifReader : IDisposable
{
    private Process? _process;
    private int _nextRequestId;
    private bool _disposed;

    public static void StopAll() => ExifToolProcessRegistry.StopAll();

    public ImageExifData Read(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (path.Contains('\n') || path.Contains('\r'))
            throw new ArgumentException("EXIF-Dateipfad enthält Zeilenumbrüche.", nameof(path));

        StartIfNeeded();
        var process = _process!;
        var request = ++_nextRequestId;
        try
        {
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            // Every response carries the request identity. A timeout may
            // leave an outstanding ReadLineAsync and a delayed ExifTool reply;
            // NEVER allow that reply to be stored under the next photo ID.
            foreach (var arg in new[] { "-j", "-G1", "-n", "-EXIF:All",
                "-GPS:All", "-XMP:GPSLatitude", "-XMP:GPSLongitude",
                "-XMP:Make", "-XMP:Model", path, "-execute" + request })
                process.StandardInput.WriteLine(arg);
            process.StandardInput.Flush();

            var buffer = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = process.StandardOutput.ReadLineAsync(cancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                    .GetAwaiter().GetResult();
                if (line is null)
                    throw new IOException("ExifTool: Der Metadatenprozess wurde beendet.");
                if (line.Trim() == "{ready" + request + "}") break;
                if (line.TrimStart().StartsWith("{ready", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "ExifTool-Antwort gehört zu einer anderen Bildanfrage.");
                buffer.AppendLine(line);
                if (buffer.Length > 4_000_000)
                    throw new InvalidDataException("Zu große EXIF-Ausgabe.");
            }
            var response = buffer.ToString();
            if (!SourceFileMatches(response, path, requireSource: true))
                throw new InvalidDataException(
                    "ExifTool lieferte EXIF-Daten für eine andere Originaldatei: " + path);
            return Parse(response);
        }
        catch
        {
            // A failed/misframed request poisons the stay-open byte stream.
            // Kill it; the NEXT photo starts with a fresh child process.
            // Retiring a process also cancels any outstanding ReadLineAsync
            // that otherwise could consume the next request's response.
            ResetFailedProcess(process);
            throw;
        }
    }

    // Both the live reader and the catalog integrity check use the exact
    // SAME source-file identity rule. Legacy EXIF blobs lacking SourceFile are
    // tolerated only by the catalog check, never by a new read.
    public static bool SourceFileMatches(string exifJson, string requestedPath,
        bool requireSource = false)
    {
        try
        {
            using var json = JsonDocument.Parse(exifJson);
            var entry = json.RootElement;
            if (entry.ValueKind == JsonValueKind.Array)
            {
                if (entry.GetArrayLength() != 1) return false;
                entry = entry[0];
            }
            if (entry.ValueKind != JsonValueKind.Object) return false;
            if (!entry.TryGetProperty("SourceFile", out var source) ||
                source.ValueKind != JsonValueKind.String)
                return !requireSource; // unchanged older catalogs
            return IsSameSourcePath(source.GetString(), requestedPath);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException
            or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsSameSourcePath(string? sourcePath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        try
        {
            static string Normalize(string p) =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))
                    .Replace('/', '\\');
            return string.Equals(Normalize(sourcePath), Normalize(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private void ResetFailedProcess(Process failed)
    {
        if (ReferenceEquals(_process, failed)) _process = null;
        try
        {
            if (!failed.HasExited)
            {
                failed.Kill(entireProcessTree: true);
                failed.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { ExifToolProcessRegistry.Retire(failed); }
    }

    public static ImageExifData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        // Live ExifTool returns a one-object JSON array. The existing
        // catalog persists the unwrapped object (ImageExifData.Json).
        // Both must use exactly the SAME GPS parsing and reference-sign rules.
        var obj = document.RootElement.ValueKind switch
        {
            JsonValueKind.Object => document.RootElement,
            JsonValueKind.Array when document.RootElement.GetArrayLength() == 1 &&
                document.RootElement[0].ValueKind == JsonValueKind.Object =>
                document.RootElement[0],
            _ => throw new InvalidDataException("Unerwartete ExifTool-Antwort.")
        };
        var latitude = NumericTag(obj, "GPSLatitude");
        var longitude = NumericTag(obj, "GPSLongitude");
        if (latitude is double lat && longitude is double lon)
        {
            if (TextTag(obj, "GPSLatitudeRef")?.StartsWith("S",
                StringComparison.OrdinalIgnoreCase) == true)
                latitude = -Math.Abs(lat);
            if (TextTag(obj, "GPSLongitudeRef")?.StartsWith("W",
                StringComparison.OrdinalIgnoreCase) == true)
                longitude = -Math.Abs(lon);
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
                (latitude, longitude) = (null, null);
        }
        else (latitude, longitude) = (null, null);

        return new ImageExifData(obj.GetRawText(), TextTag(obj, "Make"),
            TextTag(obj, "Model"), TextTag(obj, "DateTimeOriginal") ??
            TextTag(obj, "CreateDate"), latitude, longitude,
            NumericTag(obj, "GPSAltitude"));
    }

    private static string? TextTag(JsonElement obj, string name)
    {
        // With -G1 ExifTool JSON uses e.g. "IFD0:Model" and
        // "GPS:GPSLatitude"; bracket-prefixed names may appear in older
        // serialized catalogs, so accept both without flattening groups.
        foreach (var prefix in new[] { "ExifIFD:", "IFD0:", "GPS:",
            "XMP-exif:", "XMP-tiff:", "[ExifIFD]", "[IFD0]", "[GPS]" })
        {
            if (obj.TryGetProperty(prefix + name, out var val))
                return AsText(val);
        }
        foreach (var prop in obj.EnumerateObject())
            if (prop.Name.EndsWith(":" + name, StringComparison.Ordinal) ||
                prop.Name.EndsWith("]" + name, StringComparison.Ordinal))
                return AsText(prop.Value);
        return null;
    }

    private static string? AsText(JsonElement val) => val.ValueKind switch
    {
        JsonValueKind.String => val.GetString(),
        JsonValueKind.Number => val.GetRawText(),
        _ => null
    };

    private static double? NumericTag(JsonElement obj, string name)
    {
        var value = TextTag(obj, name);
        return double.TryParse(value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed : null;
    }

    private void StartIfNeeded()
    {
        if (_process is not null) return;
        var executable = Environment.GetEnvironmentVariable("PCASA_EXIFTOOL_PATH");
        if (string.IsNullOrWhiteSpace(executable))
            executable = Path.Combine(AppContext.BaseDirectory, "exiftool", "exiftool.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("ExifTool für den EXIF-Katalog fehlt.", executable);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-charset", "filename=UTF8",
            "-stay_open", "True", "-@", "-" })
            start.ArgumentList.Add(arg);
        _process = ExifToolProcessRegistry.Start(start);
        // Drain warnings asynchronously to avoid a blocked stderr pipe.
        _ = _process.StandardError.ReadToEndAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var process = _process;
        _process = null;
        // Reads have finished before normal Dispose. There is no benefit in
        // waiting for a native -stay_open handshake; terminate the owned child
        // through the single registry instead.
        if (process is not null)
            ExifToolProcessRegistry.Retire(process);
    }

}
