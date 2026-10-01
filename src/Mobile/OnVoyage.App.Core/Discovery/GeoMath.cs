namespace OnVoyage.App.Core.Discovery;

/// <summary>Distances and bearings on the sphere; at the scale of a walk the error is far below GPS noise.</summary>
public static class GeoMath
{
    private const double EarthRadiusMeters = 6_371_008.8;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Initial bearing from the first point to the second, 0° = north, clockwise, in [0, 360).</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var dLon = ToRadians(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(phi2);
        var x = (Math.Cos(phi1) * Math.Sin(phi2)) - (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon));
        return (ToDegrees(Math.Atan2(y, x)) + 360d) % 360d;
    }

    /// <summary>Signed smallest angle from <paramref name="from"/> to <paramref name="to"/>, in (-180, 180]; positive means clockwise (to the right).</summary>
    public static double SignedAngleDegrees(double from, double to)
    {
        var difference = (to - from + 540d) % 360d - 180d;
        return difference <= -180d ? 180d : difference;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0d;
        }

        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
