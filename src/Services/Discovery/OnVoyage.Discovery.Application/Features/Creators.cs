using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Discovery.Application.Features;

public sealed record GetCreatorsForMeQuery(Guid TravelerId, string Destination, int Limit);

public static class GetCreatorsForMeHandler
{
    public const int MaxLimit = 50;

    /// <summary>
    /// "Pour vos goûts" of the creators screen (§6.15): the published creators who validated a place of the destination, by affinity
    /// <c>A(u, c)</c> with the traveler's vector, then by number of places, then by handle. The control cohort (F-03) gets no personal order:
    /// number of places, then handle. Affinity is a percentage; the vector <c>c</c> itself is never returned.
    /// </summary>
    public static async Task<Result<CreatorsForMeDto>> Handle(
        GetCreatorsForMeQuery query, IDiscoveryStore store, ITravelerReader travelers, ICreatorReader creators, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit || string.IsNullOrWhiteSpace(query.Destination))
        {
            return Result.Failure<CreatorsForMeDto>("validation", $"limit 1–{MaxLimit}, destination required");
        }

        var traveler = await travelers.GetAsync(query.TravelerId, cancellationToken);
        var control = (traveler?.Cohort ?? (ControlCohort.Contains(query.TravelerId) ? "control" : "personalized")) == "control";
        var vector = control ? new Dictionary<string, double>() : (await store.GetProfileAsync(query.TravelerId, cancellationToken))?.Learned.Vector ?? new Dictionary<string, double>();
        var followed = control ? new HashSet<Guid>() : await creators.FollowedAsync(query.TravelerId, cancellationToken);

        var items = (await creators.CreatorsAsync(query.Destination, cancellationToken))
            .Select(c => (Creator: c, Affinity: CreatorAffinity.Affinity(vector, c.Vector)))
            .OrderByDescending(x => x.Affinity)
            .ThenByDescending(x => x.Creator.PlaceCount)
            .ThenBy(x => x.Creator.Handle, StringComparer.Ordinal)
            .Take(query.Limit)
            .Select(x => new CreatorForMeDto(
                x.Creator.CreatorId, x.Creator.Handle, x.Creator.DisplayName, x.Creator.AvatarPath, x.Creator.Specialties, x.Creator.PlaceCount,
                (int)Math.Round(100d * x.Affinity, MidpointRounding.AwayFromZero), followed.Contains(x.Creator.CreatorId)))
            .ToArray();

        return Result.Success(new CreatorsForMeDto(query.Destination, items, control ? "control" : "personalized"));
    }
}

public sealed record GetCfScoresQuery(Guid TravelerId, string Destination);

public static class GetCfScoresHandler
{
    /// <summary>
    /// What the device downloads with a pack for offline ranking (§6.12, §6.15): per place, the collaborative score and the creator signal.
    /// <c>cf</c> is the neighbours' score <c>CF</c> in [−1, 1] and <c>support</c> the number of neighbours behind it, so the device can apply
    /// <c>w_cf · min(1, support / 10) · (cf + 1) / 2</c> offline; both are null for a place no one rated enough (fewer than the minimum number
    /// of neighbours), and for the control cohort. No neighbour is ever identified.
    /// </summary>
    public static async Task<Result<CfScoresDto>> Handle(
        GetCfScoresQuery query, IDiscoveryStore store, ITravelerReader travelers, IPlaceReader places, ICreatorReader creators, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await places.DestinationAsync(query.Destination, cancellationToken) is null)
        {
            return Result.Failure<CfScoresDto>("destination_not_found", "Unknown destination.");
        }

        var context = await RankingContext.LoadAsync(query.TravelerId, query.Destination, store, travelers, places, creators, clock, cancellationToken);
        var items = context.Eligible(null, null, null)
            .OrderBy(p => p.PoiId)
            .Select(p => context.CfScores.TryGetValue(p.PoiId, out var cf)
                ? new CfScoreDto(p.PoiId, Math.Round(cf.Score, 4), Math.Round(context.EndorsementOf(p)?.Signal ?? 0d, 4), cf.Support)
                : new CfScoreDto(p.PoiId, null, Math.Round(context.EndorsementOf(p)?.Signal ?? 0d, 4)))
            .ToArray();
        return Result.Success(new CfScoresDto(items, context.Traveler.Cohort, clock.GetUtcNow()));
    }
}
