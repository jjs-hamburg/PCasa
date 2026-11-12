using System.IO;
using PCasa.App.Services;

internal static class GpxRegressionTests
{
    internal static void SegmentBoundariesAreRespected()
    {
        var filename = Path.Combine(Path.GetTempPath(),
            "pcasa-gpx-regression-" + Guid.NewGuid().ToString("N") + ".gpx");
        try
        {
            File.WriteAllText(filename, """
                <?xml version="1.0" encoding="utf-8"?>
                <gpx version="1.1" creator="PCasa Regression" xmlns="http://www.topografix.com/GPX/1/1">
                  <trk><trkseg>
                    <trkpt lat="53.5" lon="10.0"><time>2026-11-12T10:00:00Z</time></trkpt>
                    <trkpt lat="53.5005" lon="10.0005"><time>2026-11-12T10:01:00Z</time></trkpt>
                  </trkseg><trkseg>
                    <trkpt lat="54.5" lon="11.0"><time>2026-11-12T10:02:00Z</time></trkpt>
                    <trkpt lat="54.5005" lon="11.0005"><time>2026-11-12T10:03:00Z</time></trkpt>
                  </trkseg></trk>
                </gpx>
                """);
            var points = GpxTrackReader.Read(filename);
            if (points.Count != 4 || points[0].SegmentId == points[2].SegmentId)
                throw new Exception("GPX track segment identities were lost.");

            if (GpxTrackReader.Match(points, DateTimeOffset.Parse("2026-11-12T10:00:30Z"), 120) is null)
                throw new Exception("Interpolation within a segment failed.");

            if (GpxTrackReader.Match(points, DateTimeOffset.Parse("2026-11-12T10:01:30Z"), 120) is not null)
                throw new Exception("Interpolation incorrectly bridged two GPX segments.");
        }
        finally
        {
            if (File.Exists(filename)) File.Delete(filename);
        }
    }

    internal static void GapSpeedAndClockOffset()
    {
        var start = DateTimeOffset.Parse("2026-11-12T10:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var regular = new[]
        {
            new GpxTrackPoint(start, 53.5, 10),
            new GpxTrackPoint(start.AddSeconds(30), 53.5003, 10.0003)
        };
        var midpoint = GpxTrackReader.MatchDetailed(regular,
            start.AddSeconds(15), 120);
        if (midpoint.Position is null ||
            Math.Abs(midpoint.Position.Value.Latitude - 53.50015) > 0.0000001)
            throw new Exception("Timestamp interpolation failed.");

        var sparse = new[]
        {
            regular[0],
            regular[1] with { UtcTime = start.AddMinutes(5) }
        };
        if (GpxTrackReader.MatchDetailed(sparse, start.AddMinutes(2), 120)
                .FailureReason != "Zeitabstand zu groß")
            throw new Exception("Large GPX recording gap was incorrectly interpolated.");

        var teleported = new[]
        {
            regular[0],
            new GpxTrackPoint(start.AddSeconds(30), 55, 13)
        };
        if (GpxTrackReader.MatchDetailed(teleported, start.AddSeconds(15), 120)
                .FailureReason != "Unplausibler Positionssprung")
            throw new Exception("Implausible GPX speed was not rejected.");

        var options = new GpxOptions { CameraTimeZoneId = TimeZoneInfo.Utc.Id };
        var camera = new DateTime(2026, 11, 12, 10, 1, 0);
        var corrected = GpxTrackReader.CameraToUtc(camera, options, 60);
        if (corrected != start)
            throw new Exception("Positive camera clock offset was not subtracted.");
        if (GpxTrackReader.CameraToUtc(camera, options, -60) !=
            start.AddMinutes(2))
            throw new Exception("Negative camera clock offset was not applied.");
    }
}
