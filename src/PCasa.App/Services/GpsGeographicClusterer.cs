using System.Threading;

namespace PCasa.App.Services;

// One provider-neutral GPS grouping owner. Its transient results contain
// immutable image identities, not approximated or edited source coordinates.
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
    // MapLibre renders its DOM markers in Web Mercator screen pixels (512
    // world pixels at zoom 0). The 27px badge, outline and shadow require
    // at least this center-to-center separation to avoid black patches.
    // Do not infer marker spacing from kilometers at a given latitude.
    public const double MinimumMarkerSpacingPixels = 48d;
    private const double MaxMercatorLatitude = 85.0511287798066;

    public static double RadiusKm(double zoom) =>
        Math.Max(0.001, 280d * Math.Pow(2d, 2d - Math.Clamp(zoom, 0, 23)));

    private static (double X, double Y, double Z) Sphere(double lat, double lon)
    {
        var latitude = lat * Radians;
        var longitude = lon * Radians;
        var cos = Math.Cos(latitude);
        return (cos * Math.Cos(longitude), cos * Math.Sin(longitude),
            Math.Sin(latitude));
    }

    private static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var latDelta = (lat2 - lat1) * Radians;
        var lonDelta = ((lon2 - lon1 + 540d) % 360d - 180d) * Radians;
        var h = Math.Pow(Math.Sin(latDelta / 2), 2) +
            Math.Cos(lat1 * Radians) * Math.Cos(lat2 * Radians) *
            Math.Pow(Math.Sin(lonDelta / 2), 2);
        return 2d * EarthRadiusKm *
            Math.Asin(Math.Min(1d, Math.Sqrt(Math.Max(0d, h))));
    }

    // Cubic buckets in Earth-centered 3D coordinates are ONLY an index:
    // unlike latitude/longitude grids, they have no antimeridian seam or
    // increasing distortion toward the poles. They never determine a
    // displayed marker position or whether two images are close enough.
    private static (long X, long Y, long Z) Cell(
        double lat, double lon, double chord)
    {
        var p = Sphere(lat, lon);
        return ((long)Math.Floor((p.X + 1) / chord),
            (long)Math.Floor((p.Y + 1) / chord),
            (long)Math.Floor((p.Z + 1) / chord));
    }

    private sealed class Group(GpsPhotoPosition first)
    {
        public long Id { get; private set; } = first.ImageId;
        public List<long> ImageIds { get; } = [];
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        private double _x, _y, _z;

        public void Add(GpsPhotoPosition point)
        {
            ImageIds.Add(point.ImageId);
            if (ImageIds.Count == 1)
            {
                Latitude = point.Latitude;
                Longitude = point.Longitude; // singleton stays EXACT
            }
            var p = Sphere(point.Latitude, point.Longitude);
            _x += p.X; _y += p.Y; _z += p.Z;
            if (ImageIds.Count > 1)
            {
                Latitude = Math.Atan2(_z, Math.Sqrt(_x * _x + _y * _y)) / Radians;
                Longitude = Math.Atan2(_y, _x) / Radians;
            }
        }

        public void Merge(Group other)
        {
            // All memberships remain real image IDs. A merging group
            // contributes its entire spherical vector, never an estimated
            // number of points at a synthetic position.
            ImageIds.AddRange(other.ImageIds);
            Id = Math.Min(Id, other.Id);
            _x += other._x;
            _y += other._y;
            _z += other._z;
            Latitude = Math.Atan2(_z, Math.Sqrt(_x * _x + _y * _y)) / Radians;
            Longitude = Math.Atan2(_y, _x) / Radians;
        }

        public GpsPhotoCluster Snapshot() =>
            new(Id, Latitude, Longitude, ImageIds.ToArray());
    }

    private static (double X, double Y) ScreenPoint(
        double latitude, double longitude, double worldPixels)
    {
        // Exactly the projection used by MapLibre. Clamp ONLY the projected
        // latitude, never the original GPS coordinate stored in a photo.
        var phi = Math.Clamp(latitude, -MaxMercatorLatitude,
            MaxMercatorLatitude) * Radians;
        var sine = Math.Sin(phi);
        return (((longitude + 180d) / 360d) * worldPixels,
            (0.5d - Math.Log((1d + sine) / (1d - sine)) /
                (4d * Math.PI)) * worldPixels);
    }

    private static List<Group> SeparateVisibleMarkers(
        List<Group> input, double zoom, CancellationToken cancellationToken)
    {
        var worldPixels = 512d * Math.Pow(2d, Math.Clamp(zoom, 0, 23));
        var columns = (long)Math.Ceiling(worldPixels / MinimumMarkerSpacingPixels);
        var spacingSquared = MinimumMarkerSpacingPixels * MinimumMarkerSpacingPixels;

        (long X, long Y) Cell(Group group)
        {
            var (x, y) = ScreenPoint(group.Latitude, group.Longitude, worldPixels);
            var col = ((long)Math.Floor(x / MinimumMarkerSpacingPixels) %
                columns + columns) % columns;
            return (col, (long)Math.Floor(y / MinimumMarkerSpacingPixels));
        }

        double DistanceSquared(Group a, Group b)
        {
            var p = ScreenPoint(a.Latitude, a.Longitude, worldPixels);
            var q = ScreenPoint(b.Latitude, b.Longitude, worldPixels);
            var dx = Math.Abs(p.X - q.X);
            dx = Math.Min(dx, worldPixels - dx); // International Date Line
            var dy = p.Y - q.Y;
            return dx * dx + dy * dy;
        }

        var groups = input;
        bool changed;
        do
        {
            changed = false;
            var survivors = new List<Group>(groups.Count);
            var buckets = new Dictionary<(long X, long Y), HashSet<Group>>();
            var locations = new Dictionary<Group, (long X, long Y)>();

            void Add(Group group)
            {
                var key = Cell(group);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets.Add(key, bucket);
                }
                bucket.Add(group);
                locations[group] = key;
            }

            foreach (var group in groups.OrderBy(g => g.Latitude)
                         .ThenBy(g => g.Longitude).ThenBy(g => g.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = Cell(group);
                Group? closest = null;
                var best = spacingSquared;
                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                {
                    var x = ((key.X + dx) % columns + columns) % columns;
                    if (!buckets.TryGetValue((x, key.Y + dy),
                            out var bucket)) continue;
                    foreach (var candidate in bucket)
                    {
                        var distance = DistanceSquared(group, candidate);
                        if (distance >= spacingSquared) continue;
                        if (closest is null || distance < best ||
                            (distance == best && candidate.Id < closest.Id))
                        {
                            closest = candidate;
                            best = distance;
                        }
                    }
                }

                if (closest is null)
                {
                    survivors.Add(group);
                    Add(group);
                    continue;
                }

                // A centroid can move into another marker after merging.
                // Re-index it and repeat complete passes until no pair of
                // visible marker centers overlaps, including at world zoom.
                var previous = locations[closest];
                var previousBucket = buckets[previous];
                previousBucket.Remove(closest);
                if (previousBucket.Count == 0) buckets.Remove(previous);
                closest.Merge(group);
                Add(closest);
                changed = true;
            }
            groups = survivors;
        } while (changed);

        return groups;
    }

    public static IReadOnlyList<GpsPhotoCluster> Cluster(
        IReadOnlyList<GpsPhotoPosition> positions, double zoom,
        CancellationToken cancellationToken = default)
    {
        var radiusKm = RadiusKm(zoom);
        // A spherical chord has exactly the same geographic maximum
        // distance as the user-visible radius, independently of latitude.
        var chord = 2d * Math.Sin(radiusKm / (2d * EarthRadiusKm));
        var groups = new List<Group>();
        var buckets = new Dictionary<(long X, long Y, long Z), HashSet<Group>>();
        void Insert((long X, long Y, long Z) key, Group group)
        {
            if (!buckets.TryGetValue(key, out var set))
            {
                set = [];
                buckets.Add(key, set);
            }
            set.Add(group);
        }

        // Sorting by true GPS values, then stable photo identity makes the
        // projection independent of arbitrary gallery thumbnail order.
        var ordered = positions
            .Where(p => double.IsFinite(p.Latitude) && double.IsFinite(p.Longitude) &&
                p.Latitude is >= -90 and <= 90 &&
                p.Longitude is >= -180 and <= 180)
            .OrderBy(p => p.Latitude).ThenBy(p => p.Longitude)
            .ThenBy(p => p.ImageId);

        foreach (var point in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Cell(point.Latitude, point.Longitude, chord);
            Group? nearest = null;
            var best = radiusKm;
            // If the true great-circle distance is <= radius, each
            // Cartesian component differs by <= chord, hence the group's
            // centroid MUST be in one of these 27 neighboring cells.
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                var neighbor = (key.X + dx, key.Y + dy, key.Z + dz);
                if (!buckets.TryGetValue(neighbor, out var candidates)) continue;
                foreach (var group in candidates)
                {
                    var km = DistanceKm(group.Latitude, group.Longitude,
                        point.Latitude, point.Longitude);
                    if (km > best) continue;
                    if (nearest is null || km < best ||
                        (km == best && group.Id < nearest.Id))
                    {
                        nearest = group;
                        best = km;
                    }
                }
            }
            if (nearest is null)
            {
                nearest = new Group(point);
                groups.Add(nearest);
                nearest.Add(point);
                Insert(Cell(nearest.Latitude, nearest.Longitude, chord), nearest);
            }
            else
            {
                var oldKey = Cell(nearest.Latitude, nearest.Longitude, chord);
                nearest.Add(point);
                var newKey = Cell(nearest.Latitude, nearest.Longitude, chord);
                if (oldKey != newKey)
                {
                    var oldBucket = buckets[oldKey];
                    oldBucket.Remove(nearest);
                    if (oldBucket.Count == 0) buckets.Remove(oldKey);
                    Insert(newKey, nearest);
                }
            }
        }

        // The geographic-radius pass preserves precise identities and
        // locality. A final projection-space pass enforces real marker
        // dimensions on the FIRST frame, not only after user zoom gestures.
        return SeparateVisibleMarkers(groups, zoom, cancellationToken)
            .Select(g => g.Snapshot()).ToArray();
    }
}
