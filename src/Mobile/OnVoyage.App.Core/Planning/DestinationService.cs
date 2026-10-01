using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.Core.Home;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Recommendation.Engine.Planning;
using OnVoyage.Taxonomy;
using TravelMode = OnVoyage.Recommendation.Engine.TravelMode;

namespace OnVoyage.App.Core.Planning;

public sealed record AffinityBar(string Code, string Label, double Value);

public sealed record DestinationPlace(PlaceCard Card, int DistanceMeters, bool FromCenter);

/// <summary>The "[Destination] pour vous" page (F-11): the summary of the taste profile, then nine places with their compatibility and a "Pourquoi".</summary>
public sealed record DestinationView(
    string Name,
    bool Remote,
    bool IsControl,
    IReadOnlyList<AffinityBar> Strongest,
    IReadOnlyList<AffinityBar> Weakest,
    IReadOnlyList<DestinationPlace> Places);

public sealed record PlannedPlace(PlaceCard Card, int LegMeters);

public sealed record PlannedDayView(int Day, IReadOnlyList<PlannedPlace> Places, int TotalMeters);

/// <summary>
/// Builds the destination page and "Que visiter ?" on the device, from the catalogue and the local profile (same ranking as the home screen,
/// so the taste profile stays on the phone). A traveler outside the destination is in "remote" mode: distances count from the centre and the
/// distance term of the score is neutral (§6.6).
/// </summary>
public sealed class DestinationService(ICatalogClient catalog, IProfileStore profiles, ISessionProvider sessions)
{
    public const int PlaceCount = 9;
    public const double ZoneRadiusMeters = 50_000d;
    private const int StrongestCount = 5;
    private const int WeakestCount = 2;

    public async Task<DestinationView> BuildAsync(double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var context = await LoadAsync(latitude, longitude, cancellationToken);
        var ranked = context.Rank(TravelMode.Walk);

        var chosen = VisitPlanner.Diversify(
            [.. ranked.Select(r => ToPlanner(r.Poi, r.Score, r.Reason))],
            PlaceCount,
            new PlannerOptions());
        var byId = ranked.ToDictionary(r => r.Poi.Id.ToString("N"));
        var places = chosen.Select(p => byId[p.Id]).Select(r => new DestinationPlace(context.CardFor(r), context.DistanceOf(r.Poi), context.Remote)).ToArray();

        var (strongest, weakest) = Summary(context.Profile);
        return new DestinationView(context.Name, context.Remote, context.Control, strongest, weakest, places);
    }

    /// <summary>"Que visiter ?": <paramref name="days"/> days (1–4) for the given mobility, ordered geographically.</summary>
    public async Task<IReadOnlyList<PlannedDayView>> PlanAsync(int days, TravelMode mobility, double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var context = await LoadAsync(latitude, longitude, cancellationToken);
        var ranked = context.Rank(mobility);
        var byId = ranked.ToDictionary(r => r.Poi.Id.ToString("N"));
        var plan = VisitPlanner.Plan(
            [.. ranked.Select(r => ToPlanner(r.Poi, r.Score, r.Reason))],
            days,
            mobility,
            (context.CenterLatitude, context.CenterLongitude),
            context.Profile.TravelerId);

        return [.. plan.Select(day => new PlannedDayView(
            day.Day,
            [.. day.Places.Select((place, index) => new PlannedPlace(
                context.CardFor(byId[place.Id]),
                index == 0 ? 0 : (int)Math.Round(VisitPlanner.Distance(day.Places[index - 1].Latitude, day.Places[index - 1].Longitude, place.Latitude, place.Longitude))))],
            (int)Math.Round(VisitPlanner.PathLength(day.Places))))];
    }

    /// <summary>The five strongest affinities and the two weakest, among the level-1 categories (the readable level of the profile).</summary>
    public static (IReadOnlyList<AffinityBar> Strongest, IReadOnlyList<AffinityBar> Weakest) Summary(LocalProfile profile)
    {
        var bars = Interests.LevelOne
            .Select(code => new AffinityBar(code, HomeFeedService.Label(code), profile.Affinities.GetValueOrDefault(code)))
            .ToArray();
        var strongest = bars.Where(b => b.Value > 0d).OrderByDescending(b => b.Value).ThenBy(b => b.Code, StringComparer.Ordinal).Take(StrongestCount).ToArray();
        var weakest = bars.Where(b => b.Value < 0d).OrderBy(b => b.Value).ThenBy(b => b.Code, StringComparer.Ordinal).Take(WeakestCount).ToArray();
        return (strongest, weakest);
    }

    private static PlannerPlace ToPlanner(PoiSummaryDto poi, double score, Reason reason) =>
        new(poi.Id.ToString("N"), poi.Latitude, poi.Longitude, score, InterestLearning.DominantCategory(poi.Weights));

    private async Task<Context> LoadAsync(double? latitude, double? longitude, CancellationToken cancellationToken)
    {
        var profile = await profiles.LoadAsync(cancellationToken);
        var session = await sessions.EnsureSessionAsync(cancellationToken);
        if (profile.TravelerId != session.TravelerId)
        {
            profile = profile with { TravelerId = session.TravelerId };
            await profiles.SaveAsync(profile, cancellationToken);
        }

        var destination = await catalog.GetDestinationAsync(profile.Destination, cancellationToken);
        var centerLat = destination?.Latitude ?? latitude ?? 0d;
        var centerLng = destination?.Longitude ?? longitude ?? 0d;
        var remote = latitude is null || longitude is null || destination is null
            || GeoMath.DistanceMeters(latitude.Value, longitude.Value, destination.Latitude, destination.Longitude) > ZoneRadiusMeters;

        // Away from the destination the catalogue is not asked for distances from the traveler's position.
        var pois = await catalog.GetPoisAsync(profile.Destination, remote ? null : latitude, remote ? null : longitude, cancellationToken);
        pois = [.. pois.Where(poi => !profile.Excluded.Contains(poi.Id))];
        return new Context(destination?.Name ?? "Marseille", profile, pois, remote, centerLat, centerLng, ControlCohort.Contains(profile.TravelerId));
    }

    private sealed record Ranked(PoiSummaryDto Poi, double Score, int? Compatibility, Reason Reason);

    private sealed record Context(string Name, LocalProfile Profile, IReadOnlyList<PoiSummaryDto> Pois, bool Remote, double CenterLatitude, double CenterLongitude, bool Control)
    {
        public IReadOnlyList<Ranked> Rank(TravelMode mode)
        {
            var byId = Pois.ToDictionary(poi => poi.Id.ToString("N"));
            if (Control)
            {
                return [.. Recommender.RankControl(Pois.Select(poi => HomeFeedService.ToCandidate(poi) with { DistanceMeters = Remote ? null : poi.DistanceMeters }), mode)
                    .Select((candidate, index) => new Ranked(byId[candidate.Id], 1d - (index * 0.001), null, new Reason(ReasonCode.ColdStart, [])))];
            }

            var candidates = Pois.Select(poi => HomeFeedService.ToCandidate(poi) with { DistanceMeters = Remote ? null : poi.DistanceMeters });
            return [.. Recommender.Rank(HomeFeedService.ToTaste(Profile), candidates, mode)
                .Select(scored => new Ranked(byId[scored.Candidate.Id], scored.Score, scored.CompatibilityPercent, scored.Reason))];
        }

        public PlaceCard CardFor(Ranked ranked)
        {
            var poi = ranked.Poi;
            var badge = ranked.Compatibility is { } percent ? $"{percent} %" : poi.HiddenGem ? "Pépite" : "Populaire";
            return new PlaceCard(poi, ranked.Compatibility, badge, HomeFeedService.Explain(ranked.Reason), Profile.Saved.Contains(poi.Id));
        }

        public int DistanceOf(PoiSummaryDto poi) => Remote
            ? (int)Math.Round(GeoMath.DistanceMeters(CenterLatitude, CenterLongitude, poi.Latitude, poi.Longitude))
            : poi.DistanceMeters ?? 0;
    }
}
