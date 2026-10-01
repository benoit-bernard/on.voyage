using System.Globalization;
using OnVoyage.App.Core.Discovery;

namespace OnVoyage.App.Core.Wishes;

/// <summary>Values of F-08 (⚙️).</summary>
public sealed record ReminderSettings
{
    public double WalkRadiusMeters { get; init; } = 800d;
    public double MaxDetourMinutes { get; init; } = 12d;
    public double DetourFactor { get; init; } = 1.3d;
    public TimeSpan MinBetweenSamePlace { get; init; } = TimeSpan.FromDays(30);
    public int MaxPerDay { get; init; } = 3;

    /// <summary>Below this speed (km/h) the traveler is on foot; above, in a vehicle and the detour time counts.</summary>
    public double VehicleSpeedKmh { get; init; } = 30d;
}

public sealed record SavedPlace(Guid PoiId, string Name, string Slug, double Latitude, double Longitude, DateTimeOffset SavedAt);

public sealed record Reminder(Guid PoiId, string Slug, string Message, int DistanceMeters);

/// <summary>When each place was last reminded. Stays on the device and is never synchronised: it would reveal a passage nearby (F-08).</summary>
public interface IReminderStore
{
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken);

    Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed class InMemoryReminderStore : IReminderStore
{
    private readonly Dictionary<Guid, DateTimeOffset> _reminded = [];

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>(_reminded));

    public Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _reminded[poiId] = at;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The rule of F-08, pure and testable. On foot: a saved place within 800 m. In a vehicle: an estimated detour of at most 12 minutes, computed as
/// <c>2 × straight distance × 1.3 / smoothed speed</c> (no route calculation). At most one reminder per place every 30 days and three a day.
/// The message names the year when the place was saved in an earlier year.
/// </summary>
public static class ProximityReminderRule
{
    public static Reminder? Evaluate(
        double latitude,
        double longitude,
        double speedMetersPerSecond,
        IReadOnlyList<SavedPlace> saved,
        IReadOnlyDictionary<Guid, DateTimeOffset> lastReminded,
        DateTimeOffset now,
        ReminderSettings? settings = null)
    {
        settings ??= new ReminderSettings();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (lastReminded.Values.Count(at => DateOnly.FromDateTime(at.UtcDateTime) == today) >= settings.MaxPerDay)
        {
            return null;
        }

        var inVehicle = speedMetersPerSecond * 3.6d > settings.VehicleSpeedKmh;
        var best = saved
            .Where(place => !(lastReminded.TryGetValue(place.PoiId, out var last) && now - last < settings.MinBetweenSamePlace))
            .Select(place => (Place: place, Distance: GeoMath.DistanceMeters(latitude, longitude, place.Latitude, place.Longitude)))
            .Where(item => inVehicle ? DetourMinutes(item.Distance, speedMetersPerSecond, settings) <= settings.MaxDetourMinutes : item.Distance <= settings.WalkRadiusMeters)
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Place.PoiId)
            .Cast<(SavedPlace Place, double Distance)?>()
            .FirstOrDefault();
        if (best is not { } chosen)
        {
            return null;
        }

        var meters = (int)Math.Round(chosen.Distance / 10d) * 10;
        return new Reminder(chosen.Place.PoiId, chosen.Place.Slug, Message(chosen.Place, meters, inVehicle ? DetourMinutes(chosen.Distance, speedMetersPerSecond, settings) : null, now), meters);
    }

    public static double DetourMinutes(double straightMeters, double speedMetersPerSecond, ReminderSettings settings) =>
        speedMetersPerSecond <= 0.1d ? double.PositiveInfinity : 2d * straightMeters * settings.DetourFactor / speedMetersPerSecond / 60d;

    internal static string Message(SavedPlace place, int meters, double? detourMinutes, DateTimeOffset now)
    {
        var where = detourMinutes is { } minutes
            ? $"à environ {Math.Max(1, (int)Math.Round(minutes))} min de détour de {place.Name}"
            : $"à {Distance(meters)} de {place.Name}";
        var when = place.SavedAt.Year < now.Year
            ? $"que vous aviez ajouté lors de votre voyage de {place.SavedAt.Year.ToString(CultureInfo.InvariantCulture)}"
            : "que vous aviez enregistré";
        return $"Vous êtes {where} {when}. Un détour ?";
    }

    private static string Distance(int meters) => meters >= 1000
        ? string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{meters / 1000d:0.#} km")
        : $"{meters} m";
}
