using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml;

namespace PCasa.App.Services;

// GPX is read from disk on demand; never copied into the catalog or SQLite.
public sealed class GpxOptions
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PCasa", "gpx-options.json");

    public string DirectoryPath { get; set; } =
        Path.Combine(PcasaDataPaths.SharedDataRoot, "GPX");
    public double CameraOffsetSeconds { get; set; }
    public string CameraTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public int MaximumGapSeconds { get; set; } = 60;

    public static GpxOptions Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<GpxOptions>(File.ReadAllText(SettingsFile));
                if (settings is not null && settings.IsValid()) return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return new GpxOptions();
    }

    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath) ||
            !Path.IsPathFullyQualified(DirectoryPath) ||
            !double.IsFinite(CameraOffsetSeconds) ||
            Math.Abs(CameraOffsetSeconds) > 86400 ||
            MaximumGapSeconds is < 1 or > 86400) return false;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(CameraTimeZoneId); }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
        return true;
    }

    public void Save()
    {
        if (!IsValid()) throw new ArgumentException("Invalid GPX options.");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record GpxTrackPoint(DateTimeOffset UtcTime, double Latitude, double Longitude, int SegmentId = 0);
public sealed record GpxFileSummary(string FilePath, DateTimeOffset First, DateTimeOffset Last, int PointCount);

// Secure streaming reader: trackpoint coordinates and mandatory UTC time only.
// Sorting and maximum-gap checks happen before any position is assigned.
public static class GpxTrackReader
{
    public static IReadOnlyList<GpxTrackPoint> Read(string path)
    {
        var points = new List<GpxTrackPoint>();
        var segmentId = 0;
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 30_000_000,
            IgnoreWhitespace = true
        });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (reader.LocalName is "trkseg" or "rte")
            {
                segmentId++;
                continue;
            }
            if (reader.LocalName is not ("trkpt" or "rtept")) continue;
            var latText = reader.GetAttribute("lat");
            var lonText = reader.GetAttribute("lon");
            if (!double.TryParse(latText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(lonText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var lon) ||
                !double.IsFinite(lat) || !double.IsFinite(lon) ||
                lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                reader.Skip();
                continue;
            }
            using var subtree = reader.ReadSubtree();
            string? timestamp = null;
            subtree.Read();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "time")
                {
                    timestamp = subtree.ReadElementContentAsString();
                    break;
                }
            }
            if (timestamp is null ||
                !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var instant)) continue;
            points.Add(new GpxTrackPoint(instant.ToUniversalTime(), lat, lon, segmentId));
        }
        return points.OrderBy(x => x.UtcTime).DistinctBy(x => (x.SegmentId, x.UtcTime)).ToArray();
    }

    public static DateTimeOffset CameraToUtc(DateTime cameraLocal, GpxOptions options)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.CameraTimeZoneId);
        var adjusted = DateTime.SpecifyKind(cameraLocal, DateTimeKind.Unspecified)
            .AddSeconds(-options.CameraOffsetSeconds);
        if (zone.IsInvalidTime(adjusted) || zone.IsAmbiguousTime(adjusted))
            throw new InvalidOperationException("Ambiguous or invalid camera local time.");
        return new DateTimeOffset(adjusted, zone.GetUtcOffset(adjusted)).ToUniversalTime();
    }

    public static (double Latitude, double Longitude)? Match(
        IReadOnlyList<GpxTrackPoint> points, DateTimeOffset cameraUtc,
        int maximumGapSeconds)
    {
        if (points.Count == 0 || maximumGapSeconds <= 0) return null;
        int lo = 0, hi = points.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (points[mid].UtcTime < cameraUtc) lo = mid + 1;
            else hi = mid;
        }
        if (lo < points.Count && points[lo].UtcTime == cameraUtc)
            return (points[lo].Latitude, points[lo].Longitude);
        if (lo == 0 || lo == points.Count) return null;
        var first = points[lo - 1];
        var next = points[lo];
        if (first.SegmentId != next.SegmentId) return null;
        var gap = (next.UtcTime - first.UtcTime).TotalSeconds;
        if (gap <= 0 || gap > maximumGapSeconds) return null;
        var t = (cameraUtc - first.UtcTime).TotalSeconds / gap;
        // Keep longitude interpolation on the short side of the dateline.
        var dLon = (next.Longitude - first.Longitude + 540) % 360 - 180;
        var lon = first.Longitude + t * dLon;
        if (lon > 180) lon -= 360;
        if (lon < -180) lon += 360;
        return (first.Latitude + (next.Latitude - first.Latitude) * t, lon);
    }
}
