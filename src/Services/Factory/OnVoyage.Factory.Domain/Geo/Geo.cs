namespace OnVoyage.Factory.Domain.Geo;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    private const double EarthRadiusMeters = 6_371_000d;

    public double DistanceTo(GeoPoint other)
    {
        var dLat = Radians(other.Latitude - Latitude);
        var dLon = Radians(other.Longitude - Longitude);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(Radians(Latitude)) * Math.Cos(Radians(other.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180d;
}

/// <summary>Outer rings of the polygons of a place's footprint (holes are ignored: visit detection only needs "inside the area").</summary>
public sealed record Footprint(IReadOnlyList<IReadOnlyList<GeoPoint>> Polygons)
{
    /// <summary>Ray casting point-in-polygon test on longitude/latitude; accurate enough at the scale of a monument or a park.</summary>
    public bool Contains(GeoPoint point) => Polygons.Any(ring => RingContains(ring, point));

    private static bool RingContains(IReadOnlyList<GeoPoint> ring, GeoPoint point)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            var crosses = (a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                && point.Longitude < ((b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude)) + a.Longitude;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
