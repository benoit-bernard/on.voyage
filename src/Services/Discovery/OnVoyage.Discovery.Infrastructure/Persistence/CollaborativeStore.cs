using Microsoft.EntityFrameworkCore;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Recommendation.Engine;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

/// <summary>Reads what the neighbour computation needs and stores its result in <c>discovery.cf_score</c>.</summary>
internal sealed class CollaborativeStore(DiscoveryDbContext db) : ICollaborativeStore
{
    public async Task<IReadOnlyList<CfTraveler>> ActiveTravelersAsync(DateTimeOffset activeSince, int minDepth, CancellationToken cancellationToken)
    {
        var rows = await (from vector in db.Vectors.AsNoTracking()
                          join traveler in db.Travelers.AsNoTracking() on vector.TravelerId equals traveler.Id
                          where traveler.ProfileDepth >= minDepth && traveler.LastActiveAt >= activeSince
                          select new { vector.TravelerId, vector.Vector, traveler.ProfileDepth }).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CfTraveler(row.TravelerId, row.Vector, row.ProfileDepth))];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, double>>> NeighborRatingsAsync(NeighborFilter filter, CancellationToken cancellationToken)
    {
        var rows = await (from rating in db.Ratings.AsNoTracking()
                          join traveler in db.Travelers.AsNoTracking() on rating.TravelerId equals traveler.Id
                          join place in db.Places.AsNoTracking() on rating.PoiId equals place.PoiId
                          where traveler.ProfileDepth >= filter.MinDepth && traveler.LastActiveAt >= filter.ActiveSince && place.IsPublished
                          select new { rating.TravelerId, rating.PoiId, rating.Rating, rating.Excluded }).ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.TravelerId).ToDictionary(
            group => group.Key,
            group => (IReadOnlyDictionary<Guid, double>)group.ToDictionary(row => row.PoiId, row => row.Excluded ? -1d : row.Rating));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> PlaceDestinationsAsync(CancellationToken cancellationToken) =>
        await db.Places.AsNoTracking().Where(place => place.IsPublished).ToDictionaryAsync(place => place.PoiId, place => place.Destination, cancellationToken);

    public async Task ReplaceScoresAsync(Guid travelerId, IReadOnlyList<StoredCfScore> scores, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.CfScores.Where(row => row.TravelerId == travelerId).ExecuteDeleteAsync(cancellationToken);
        db.CfScores.AddRange(scores.Select(score => new CfScoreRow
        {
            TravelerId = travelerId,
            PoiId = score.PoiId,
            Destination = score.Destination,
            Score = (float)score.Score,
            Support = score.Support,
            ComputedAt = at,
        }));
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> DeleteScoresExceptAsync(IReadOnlyCollection<Guid> keepTravelerIds, CancellationToken cancellationToken)
    {
        var keep = keepTravelerIds.ToArray();
        return await db.CfScores.Where(row => !keep.Contains(row.TravelerId)).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Exact neighbour search: the cosine similarity of the traveler to every eligible traveler, computed in memory over the stored
/// <c>real[]</c> vectors. The cahier asks for a pgvector HNSW index (§6.5), but the extension is in neither the local database image nor the
/// test database (ADR-0011, ADR-0020); this class sits behind <see cref="INeighborSearch"/> so an index-backed one replaces it without a change
/// elsewhere. The eligible pool is read once per instance (the service is scoped, so once per run), which makes a full run cost one pass over the vectors
/// per traveler instead of one query each.
/// </summary>
internal sealed class ExactCosineNeighborSearch(DiscoveryDbContext db) : INeighborSearch
{
    private NeighborFilter? _loadedFor;
    private NeighborCandidate[] _pool = [];

    public async Task<int> PoolSizeAsync(NeighborFilter filter, CancellationToken cancellationToken)
    {
        await LoadAsync(filter, cancellationToken);
        return _pool.Length;
    }

    public async Task<IReadOnlyList<Neighbor>> NearestAsync(Guid travelerId, float[] vector, NeighborFilter filter, int k, CancellationToken cancellationToken)
    {
        await LoadAsync(filter, cancellationToken);
        return CollaborativeFiltering.Nearest(travelerId, vector, _pool, k);
    }

    private async Task LoadAsync(NeighborFilter filter, CancellationToken cancellationToken)
    {
        if (_loadedFor == filter)
        {
            return;
        }

        var rows = await (from vector in db.Vectors.AsNoTracking()
                          join traveler in db.Travelers.AsNoTracking() on vector.TravelerId equals traveler.Id
                          where traveler.ProfileDepth >= filter.MinDepth && traveler.LastActiveAt >= filter.ActiveSince
                          select new { vector.TravelerId, vector.Vector }).ToListAsync(cancellationToken);
        _pool = [.. rows.Select(row => new NeighborCandidate(row.TravelerId, row.Vector))];
        _loadedFor = filter;
    }
}
