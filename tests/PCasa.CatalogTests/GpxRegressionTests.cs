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
                    <trkpt lat="53.6" lon="10.1"><time>2026-11-12T10:01:00Z</time></trkpt>
                  </trkseg><trkseg>
                    <trkpt lat="54.5" lon="11.0"><time>2026-11-12T10:02:00Z</time></trkpt>
                    <trkpt lat="54.6" lon="11.1"><time>2026-11-12T10:03:00Z</time></trkpt>
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
}
