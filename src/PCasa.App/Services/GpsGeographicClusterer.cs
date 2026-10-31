using System.Threading;

namespace PCasa.App.Services;

// Single provider-independent GPS grouping authority. Originals remain
// untouched: a cluster is a temporary UI projection of image identities.
public sealed record GpsPhotoPosition(long ImageId, double Latitude, double Longitude);
public sealed record GpsPhotoCluster(
    long Id, double Latitude, double Longitude, IReadOnlyList<long> ImageIds)
{
    public int Count => ImageIds.Count;
}

public static class GpsGeographicClusterer
{
    private const double EarthRadiusKm = 6371.0088;
    private const double Radians = Math.PI / 180d;

    public static double RadiusKm(double zoom) =>
        Math.Max(0.001, 280d * Math.Pow(2d, 2d - Math.Clamp(zoom, 0, 23)));

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var latDelta = (lat2 - lat1) * Radians;
        // Smallest angular difference also handles ±180° antimeridian.
        var lonDelta = ((lon2 - lon1 + 540d) % 360d - 180d) * Radians;
        var h = Math.Pow(Math.Sin(latDelta / 2), 2) +
            Math.Cos(lat1 * Radians) * Math.Cos(lat2 * Radians) *
            Math.Pow(Math.Sin(lonDelta / 2), 2);
        return 2d * EarthRadiusKm * Math.Asin(Math.Min(1d, Math.Sqrt(Math.Max(0d, h))));
    }

    private sealed class Group(GpsPhotoPosition first)
    {
        public long Id { get; } = first.ImageId;
        public List<long> ImageIds { get; } = [];
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        private double _latitudeSum;
        private double _longitudeSin;
        private double _longitudeCos;

        public void Add(GpsPhotoPosition point)
        {
            ImageIds.Add(point.ImageId);
            _latitudeSum += point.Latitude;
            _longitudeSin += Math.Sin(point.Longitude * Radians);
            _longitudeCos += Math.Cos(point.Longitude * Radians);
            Latitude = _latitudeSum / ImageIds.Count;
            Longitude = Math.Atan2(_longitudeSin, _longitudeCos) / Radians;
        }

        public GpsPhotoCluster Snapshot() =>
            new(Id, Latitude, Longitude, ImageIds.ToArray());
    }

    public static IReadOnlyList<GpsPhotoCluster> Cluster(
        IReadOnlyList<GpsPhotoPosition> positions, double zoom,
        CancellationToken cancellationToken = default)
    {
        var radiusKm = RadiusKm(zoom);
        var maxLatitudeDifference = radiusKm / 110.5d;
        var groups = new List<Group>();
        var active = new List<Group>();

        // Stable geographical order is important. Input order must never
        // change a group's identity or make a camera move produce a grid.
        var ordered = positions
            .Where(p => double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude) &&
                p.Latitude is >= -90 and <= 90 &&
                p.Longitude is >= -180 and <= 180)
            .OrderBy(p => p.Latitude).ThenBy(p => p.Longitude)
            .ThenBy(p => p.ImageId);

        foreach (var point in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // As the photos are latitude-ordered, groups too far south will
            // never be candidates again. This bounds the search to nearby
            // geographic groups rather than all historical image positions.
            active.RemoveAll(g => g.Latitude < point.Latitude - maxLatitudeDifference);
            Group? nearest = null;
            var best = radiusKm;
            foreach (var group in active)
            {
                if (Math.Abs(group.Latitude - point.Latitude) > maxLatitudeDifference)
                    continue;
                var km = DistanceKm(group.Latitude, group.Longitude,
                    point.Latitude, point.Longitude);
                if (km > best) continue;
                best = km;
                nearest = group;
            }

            if (nearest is null)
            {
                nearest = new Group(point);
                groups.Add(nearest);
                active.Add(nearest);
            }
            nearest.Add(point);
        }

        var result = groups.Select(g => g.Snapshot()).ToArray();
        // Every input identity belongs to one and only one cluster. A
        // drawn count is a count of PHOTOS, never rounded GPS locations.
        return result;
    }
}
