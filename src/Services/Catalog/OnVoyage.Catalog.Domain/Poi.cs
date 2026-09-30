namespace OnVoyage.Catalog.Domain;

public sealed record GeoPoint(double Latitude, double Longitude)
{
    private const double EarthRadiusMeters = 6_371_000d;

    public double DistanceTo(GeoPoint other)
    {
        var dLat = ToRadians(other.Latitude - Latitude);
        var dLon = ToRadians(other.Longitude - Longitude);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(ToRadians(Latitude)) * Math.Cos(ToRadians(other.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return EarthRadiusMeters * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;
}

public sealed record Story(Guid Id, string Language, string Title, string Text, int DurationSeconds, string? AudioUrl, bool AiGenerated);

public sealed record Poi(
    Guid Id,
    string Slug,
    string Name,
    string Category,
    GeoPoint Location,
    double Importance,
    double Quality,
    int CrowdLevel,
    bool HiddenGem,
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyList<Story> Stories);

public sealed record Destination(string Slug, string Name, GeoPoint Center);
