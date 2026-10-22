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
    private bool _disposed;

    public ImageExifData Read(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (path.Contains('\n') || path.Contains('\r'))
            throw new ArgumentException("EXIF-Dateipfad enthält Zeilenumbrüche.", nameof(path));

        StartIfNeeded();
        var process = _process!;
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        // In stay-open mode -execute delimits one request/response. Group
        // names disambiguate duplicate EXIF fields from distinct IFDs.
        foreach (var arg in new[] { "-j", "-G1", "-n", "-EXIF:All",
            "-GPS:All", "-XMP:GPSLatitude", "-XMP:GPSLongitude",
            "-XMP:Make", "-XMP:Model", path, "-execute" })
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
            if (line.Trim() == "{ready}") break;
            buffer.AppendLine(line);
            if (buffer.Length > 4_000_000)
                throw new InvalidDataException("Zu große EXIF-Ausgabe.");
        }
        return Parse(buffer.ToString());
    }

    public static ImageExifData Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() != 1 ||
            document.RootElement[0].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unerwartete ExifTool-Antwort.");
        var obj = document.RootElement[0];
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
        _process = Process.Start(start)
            ?? throw new IOException("ExifTool für den Metadatenindex konnte nicht starten.");
        // Drain warnings asynchronously to avoid a blocked stderr pipe.
        _ = _process.StandardError.ReadToEndAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.WriteLine("-stay_open");
                _process.StandardInput.WriteLine("False");
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(3000))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (IOException) { }
        finally { _process.Dispose(); }
    }
}
