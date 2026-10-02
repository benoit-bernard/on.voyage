using Microsoft.Extensions.Logging;
using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Discovery.Application.Features;

/// <summary>
/// Settings of the collaborative filtering of §6.5 (<c>Discovery:Cf:*</c>). <see cref="MinSupport"/> and <see cref="MinPool"/> are the privacy
/// guards: a score made of fewer than <see cref="MinSupport"/> neighbours, or computed in a population smaller than <see cref="MinPool"/>
/// travelers, would let a reader guess what one person rated, so it is not produced (same spirit as the k = 20 of the statistics).
/// </summary>
/// <param name="Neighbors"><c>K</c>: how many neighbours are consulted.</param>
/// <param name="MinNeighborDepth">A neighbour has a profile of at least this <c>profile_depth</c>.</param>
/// <param name="ActiveDays">A neighbour has been active in this many days.</param>
/// <param name="Lambda">The smoothing of <c>CF = Σ sim·r / (Σ |sim| + λ)</c>.</param>
/// <param name="MaxScoresPerTraveler">The best scores kept per traveler and destination.</param>
/// <param name="TargetMinDepth">Travelers below this depth are in cold start (§6.7): their <c>w_cf</c> is 0, so nothing is computed for them.</param>
public sealed record CfOptions(
    int Neighbors = 50,
    int MinNeighborDepth = 10,
    int ActiveDays = 365,
    double Lambda = 5d,
    int MaxScoresPerTraveler = 200,
    int MinSupport = 3,
    int MinPool = 20,
    int TargetMinDepth = 5)
{
    public NeighborFilter Filter(DateTimeOffset now) => new(MinNeighborDepth, now.AddDays(-ActiveDays));
}

/// <summary>Who may be a neighbour.</summary>
public sealed record NeighborFilter(int MinDepth, DateTimeOffset ActiveSince);

/// <summary>A traveler and the interest vector <c>u</c> (one value per dimension of the taxonomy).</summary>
public sealed record CfTraveler(Guid Id, float[] Vector, int ProfileDepth);

public sealed record StoredCfScore(Guid PoiId, string Destination, double Score, int Support);

/// <summary>What a nightly (or six-hourly) run did. <see cref="Outcome"/> is <c>computed</c> or <c>pool_too_small</c>.</summary>
public sealed record CfRunSummary(string Outcome, int Pool, int Travelers, int Scored, int Rows, int Removed);

/// <summary>
/// The nearest neighbours of a traveler. The shipped implementation is an exact cosine search over the stored vectors; an HNSW index (pgvector,
/// <c>vector_cosine_ops</c>, §6.5) answers the same question and replaces it without touching the rest (ADR-0020).
/// </summary>
public interface INeighborSearch
{
    Task<int> PoolSizeAsync(NeighborFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<Neighbor>> NearestAsync(Guid travelerId, float[] vector, NeighborFilter filter, int k, CancellationToken cancellationToken);
}

public interface ICollaborativeStore
{
    /// <summary>Travelers with a vector, at least this deep and active since the date.</summary>
    Task<IReadOnlyList<CfTraveler>> ActiveTravelersAsync(DateTimeOffset activeSince, int minDepth, CancellationToken cancellationToken);

    /// <summary>The ratings (−1…1, §6.2) of the travelers the filter accepts, for published places only: <c>r_v(poi)</c> of §6.5.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, double>>> NeighborRatingsAsync(NeighborFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> PlaceDestinationsAsync(CancellationToken cancellationToken);

    /// <summary>Replaces all the stored scores of one traveler in one transaction.</summary>
    Task ReplaceScoresAsync(Guid travelerId, IReadOnlyList<StoredCfScore> scores, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Drops the scores of every traveler not in the list (no longer active, or no longer deep enough); returns how many rows went.</summary>
    Task<int> DeleteScoresExceptAsync(IReadOnlyCollection<Guid> keepTravelerIds, CancellationToken cancellationToken);
}

public sealed record RecomputeCfScoresCommand;

public static class RecomputeCfScoresHandler
{
    /// <summary>
    /// The job of §6.5 (every 6 hours by default): for each active traveler past cold start, find the K nearest neighbours, score the places they
    /// rated and keep the 200 best per destination in <c>discovery.cf_score</c>. The online ranking only reads that cache. Deterministic: the same
    /// data gives the same rows. Nothing about a neighbour is stored: only a score and how many neighbours stand behind it.
    /// </summary>
    public static async Task<Result<CfRunSummary>> Handle(
        RecomputeCfScoresCommand command, ICollaborativeStore store, INeighborSearch neighbors, CfOptions options, TimeProvider clock, ILogger<RecomputeCfScoresCommand> logger, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var filter = options.Filter(now);
        var pool = await neighbors.PoolSizeAsync(filter, cancellationToken);
        var targets = await store.ActiveTravelersAsync(filter.ActiveSince, options.TargetMinDepth, cancellationToken);

        if (pool < options.MinPool)
        {
            // Too few travelers to hide anyone in: no collaborative score at all, and what an earlier run stored goes away.
            var removedAll = await store.DeleteScoresExceptAsync([], cancellationToken);
            logger.LogInformation("Collaborative filtering skipped: {Pool} eligible neighbours, {Min} needed.", pool, options.MinPool);
            return Result.Success(new CfRunSummary("pool_too_small", pool, targets.Count, 0, 0, removedAll));
        }

        var ratings = await store.NeighborRatingsAsync(filter, cancellationToken);
        var destinations = await store.PlaceDestinationsAsync(cancellationToken);
        var scored = 0;
        var rows = 0;
        List<Guid> kept = [];
        foreach (var target in targets.OrderBy(traveler => traveler.Id))
        {
            var nearest = await neighbors.NearestAsync(target.Id, target.Vector, filter, options.Neighbors, cancellationToken);
            var scores = CollaborativeFiltering.Scores(nearest, ratings, options.Lambda, options.MinSupport, int.MaxValue)
                .Where(score => destinations.ContainsKey(score.PoiId))
                .GroupBy(score => destinations[score.PoiId])
                .SelectMany(group => group.Take(options.MaxScoresPerTraveler).Select(score => new StoredCfScore(score.PoiId, group.Key, Math.Round(score.Score, 5), score.Support)))
                .ToList();

            await store.ReplaceScoresAsync(target.Id, scores, now, cancellationToken);
            kept.Add(target.Id);
            if (scores.Count > 0)
            {
                scored++;
                rows += scores.Count;
            }
        }

        var removed = await store.DeleteScoresExceptAsync(kept, cancellationToken);
        logger.LogInformation("Collaborative filtering: {Travelers} travelers, {Scored} with scores, {Rows} rows, {Removed} stale rows removed (pool {Pool}).", targets.Count, scored, rows, removed, pool);
        return Result.Success(new CfRunSummary("computed", pool, targets.Count, scored, rows, removed));
    }
}
