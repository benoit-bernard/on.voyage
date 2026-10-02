using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.Recommendation.Engine.Planning;
using OnVoyage.Taxonomy;
using EngineMode = OnVoyage.Recommendation.Engine.TravelMode;

namespace OnVoyage.Discovery.Application.Features;

internal static class Modes
{
    public static EngineMode Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "bike" or "velo" => EngineMode.Bike,
        "car" or "voiture" => EngineMode.Car,
        _ => EngineMode.Walk,
    };

    public static bool IsKnown(string? value) => value is null || value.ToLowerInvariant() is "walk" or "bike" or "car" or "velo" or "voiture";
}

public sealed record GetRecommendationsQuery(Guid TravelerId, double? Latitude, double? Longitude, int RadiusMeters, int Limit, string? Context, string? Surface, string Destination = "marseille");

public static class GetRecommendationsHandler
{
    public static async Task<Result<RecommendationsDto>> Handle(
        GetRecommendationsQuery query, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, IAffinityStore affinity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > 50 || query.RadiusMeters is < 50 or > 200_000 || !Modes.IsKnown(query.Context)
            || (query.Latitude is null != query.Longitude is null) || query.Latitude is < -90 or > 90 || query.Longitude is < -180 or > 180)
        {
            return Result.Failure<RecommendationsDto>("validation", "limit 1–50, radius 50–200000, context walk|bike|car, lat and lng together");
        }

        var context = await RankingContext.LoadAsync(query.TravelerId, null, store, travelers, places, creators, clock, cancellationToken);
        var eligible = context.Eligible(query.Latitude, query.Longitude, query.RadiusMeters);
        var ranked = context.Rank(eligible.Select(p => context.ToCandidate(p, query.Latitude, query.Longitude, remote: false)), Modes.Parse(query.Context));
        var (lifts, travelersInMatrix) = await affinity.LoadAsync(cancellationToken);
        var chosen = context.Select(ranked, Math.Min(query.Limit, ranked.Count), lifts, travelersInMatrix);

        var destination = (await places.DestinationAsync(query.Destination, cancellationToken))?.Name ?? "Marseille";
        return Result.Success(new RecommendationsDto(
            [.. chosen.Select(c => context.Item(c.Scored, c.Place, destination, c.Exploration))],
            context.Traveler.Cohort,
            context.Options.WeightsVersion,
            clock.GetUtcNow()));
    }
}

public sealed record GetDestinationForMeQuery(Guid TravelerId, string Slug, double? Latitude, double? Longitude, int? Days, string? Mobility);

public static class GetDestinationForMeHandler
{
    public const int PlaceCount = 9;
    public const double ZoneRadiusMeters = 50_000d;

    public static async Task<Result<DestinationForMeDto>> Handle(
        GetDestinationForMeQuery query, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, IAffinityStore affinity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (query.Days is < 1 or > 4 || !Modes.IsKnown(query.Mobility) || (query.Latitude is null != query.Longitude is null))
        {
            return Result.Failure<DestinationForMeDto>("validation", "days 1–4, mobility walk|bike|car, lat and lng together");
        }

        var destination = await places.DestinationAsync(query.Slug, cancellationToken);
        if (destination is null)
        {
            return Result.Failure<DestinationForMeDto>("destination_not_found", "Unknown destination.");
        }

        var context = await RankingContext.LoadAsync(query.TravelerId, query.Slug, store, travelers, places, creators, clock, cancellationToken);
        var remote = query.Latitude is null || query.Longitude is null
            || VisitPlanner.Distance(query.Latitude.Value, query.Longitude.Value, destination.Value.Latitude, destination.Value.Longitude) > ZoneRadiusMeters;
        var mode = Modes.Parse(query.Mobility);
        var ranked = context.Rank(context.Eligible(null, null, null).Select(p => context.ToCandidate(p, query.Latitude, query.Longitude, remote)), mode);
        var (lifts, matrix) = await affinity.LoadAsync(cancellationToken);

        var chosen = context.Select(ranked, Math.Min(PlaceCount, ranked.Count), lifts, matrix);
        var items = chosen.Select(c => context.Item(c.Scored, c.Place, destination.Value.Name, c.Exploration)).ToArray();

        IReadOnlyList<PlanDayDto>? plan = null;
        if (query.Days is { } days)
        {
            var byId = context.Places.ToDictionary(p => p.PoiId.ToString("N"));
            var planned = VisitPlanner.Plan(
                [.. ranked.Select(s => new PlannerPlace(s.Candidate.Id, byId[s.Candidate.Id].Latitude, byId[s.Candidate.Id].Longitude, s.Score, Recommendation.Engine.Learning.InterestLearning.DominantCategory(s.Candidate.Weights)))],
                days, mode, (destination.Value.Latitude, destination.Value.Longitude), query.TravelerId);
            var scored = ranked.ToDictionary(s => s.Candidate.Id);
            plan = [.. planned.Select(day => new PlanDayDto(
                day.Day,
                [.. day.Places.Select((p, i) => new PlanPlaceDto(
                    context.Item(scored[p.Id], byId[p.Id], destination.Value.Name),
                    i == 0 ? 0 : (int)Math.Round(VisitPlanner.Distance(day.Places[i - 1].Latitude, day.Places[i - 1].Longitude, p.Latitude, p.Longitude))))],
                (int)Math.Round(VisitPlanner.PathLength(day.Places))))];
        }

        var bars = Interests.LevelOne.Select(code => new AffinityDto(code, context.Vector.GetValueOrDefault(code))).ToArray();
        return Result.Success(new DestinationForMeDto(
            query.Slug,
            destination.Value.Name,
            remote,
            [.. bars.Where(b => b.Value > 0).OrderByDescending(b => b.Value).ThenBy(b => b.Code, StringComparer.Ordinal).Take(5)],
            [.. bars.Where(b => b.Value < 0).OrderBy(b => b.Value).ThenBy(b => b.Code, StringComparer.Ordinal).Take(2)],
            items,
            plan,
            context.Traveler.Cohort,
            context.Options.WeightsVersion));
    }
}

public sealed record GetSurpriseQuery(Guid TravelerId, double? Latitude, double? Longitude, int RadiusMeters, string Destination = "marseille");

public static class GetSurpriseHandler
{
    public const int Pool = 10;
    public const int MemoryOfProposed = 20;

    /// <summary>
    /// "Surprenez-moi" (§6.10): one place drawn with a probability proportional to its score among the ten best exploration candidates, never a
    /// fragile, regulated or crowded (level 4+) place, nor one of the last twenty proposed. The draw is seeded by the traveler and the number of
    /// places already proposed, so a repeated call moves on and a test can replay it.
    /// </summary>
    public static async Task<Result<RecommendationItemDto>> Handle(
        GetSurpriseQuery query, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, IAffinityStore affinity, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (query.RadiusMeters is < 50 or > 200_000 || (query.Latitude is null != query.Longitude is null))
        {
            return Result.Failure<RecommendationItemDto>("validation", "radius 50–200000, lat and lng together");
        }

        var context = await RankingContext.LoadAsync(query.TravelerId, null, store, travelers, places, creators, clock, cancellationToken);
        var recent = await travelers.RecentSurprisesAsync(query.TravelerId, MemoryOfProposed, cancellationToken);
        var eligible = context.Eligible(query.Latitude, query.Longitude, query.RadiusMeters)
            .Where(p => !p.Fragile && !p.AccessRegulated && p.CrowdLevel < 4 && !recent.Contains(p.PoiId))
            .ToArray();
        if (eligible.Length == 0)
        {
            return Result.Failure<RecommendationItemDto>("surprise_not_found", "Nothing left to propose here.");
        }

        var ranked = context.Rank(eligible.Select(p => context.ToCandidate(p, query.Latitude, query.Longitude, remote: query.Latitude is null)), EngineMode.Walk);
        var (lifts, matrix) = await affinity.LoadAsync(cancellationToken);
        var adjacent = Exploration.AdjacentCategories(context.Vector, lifts, matrix);
        var exploration = ranked.Where(s => Exploration.DominantLeaf(s.Candidate.Weights) is { } leaf && adjacent.Contains(leaf)).Take(Pool).ToList();
        var candidates = exploration.Count > 0 ? exploration : [.. ranked.Take(Pool)];

        var random = new Random(HashCode.Combine(query.TravelerId.GetHashCode(), recent.Count));
        var floor = candidates.Min(c => c.Score);
        var weights = candidates.Select(c => Math.Max(1e-6, c.Score - floor + 0.05)).ToArray();
        var ticket = random.NextDouble() * weights.Sum();
        var pick = candidates[0];
        for (var i = 0; i < candidates.Count; i++)
        {
            ticket -= weights[i];
            if (ticket <= 0)
            {
                pick = candidates[i];
                break;
            }
        }

        var place = context.Places.First(p => p.PoiId.ToString("N") == pick.Candidate.Id);
        await store.ExclusiveAsync(query.TravelerId, async session =>
        {
            await session.RecordSurpriseAsync(place.PoiId, clock.GetUtcNow(), cancellationToken);
            return true;
        }, cancellationToken);

        var name = (await places.DestinationAsync(query.Destination, cancellationToken))?.Name ?? "Marseille";
        return Result.Success(context.Item(pick, place, name, exploration: exploration.Count > 0));
    }
}

public sealed record GetCandidatesQuery(Guid TravelerId, string Destination, string BaseUrl);

public static class GetCandidatesHandler
{
    /// <summary>
    /// What the discovery mode caches (§12.4): every place of the destination with a standard story, its flags and a <c>baseScore</c> that leaves
    /// out Distance, Context and CrowdPenalty (the device knows the position, the hour and the mode). Premium stories are left out. A place whose
    /// story has no <c>main</c> audio part (published without a TTS voice) is proposed too, flagged <c>TextOnly</c>, while
    /// <see cref="DiscoveryOptions.AllowTextOnlyStories"/> is on (MVP-0: on): the device reads its text aloud.
    /// </summary>
    public static async Task<Result<CandidatesDto>> Handle(
        GetCandidatesQuery query, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, IMediaUrls media, DiscoveryOptions options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var destination = await places.DestinationAsync(query.Destination, cancellationToken);
        if (destination is null)
        {
            return Result.Failure<CandidatesDto>("destination_not_found", "Unknown destination.");
        }

        var context = await RankingContext.LoadAsync(query.TravelerId, query.Destination, store, travelers, places, creators, clock, cancellationToken);
        var stories = context.Stories.Where(s => !s.IsPremium).ToLookup(s => s.PoiId);
        var items = context.Eligible(null, null, null)
            .Where(p => stories[p.PoiId].Any(s => s.Kind == "standard" && (options.AllowTextOnlyStories || s.AudioParts.ContainsKey("main"))))
            .Select(p =>
            {
                var candidate = context.ToCandidate(p, null, null, remote: true);
                var baseScore = context.Control ? p.Importance : Recommender.BaseScore(context.Taste, candidate, context.Options);
                return new CandidateDto(
                    p.PoiId, p.Slug, p.Name, p.Latitude, p.Longitude, p.Importance, p.Fragile, CarAccessible: false, VisibleFromRoad: false, p.CrowdLevel,
                    Math.Round(Math.Clamp(baseScore, 0d, 1d), 4),
                    [.. stories[p.PoiId].Select(s => new CandidateStoryDto(s.StoryId, s.Kind, s.DurationSeconds, s.AudioParts.ToDictionary(part => part.Key, part => media.Url(part.Value)), !s.AudioParts.ContainsKey("main")))]);
            })
            .OrderBy(c => c.PoiId)
            .ToArray();

        return Result.Success(new CandidatesDto(items, context.Traveler.Cohort, context.Options.WeightsVersion, clock.GetUtcNow()));
    }
}
