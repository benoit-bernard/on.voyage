using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.Discovery.Application.Features;

public sealed record SetSavedCommand(Guid TravelerId, Guid PoiId, bool Saved);

public static class SetSavedHandler
{
    public static async Task<Result<bool>> Handle(SetSavedCommand command, IDiscoveryStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        await store.ExclusiveAsync(command.TravelerId, async session =>
        {
            await session.SetWishAsync(command.PoiId, command.Saved, clock.GetUtcNow(), cancellationToken);
            return true;
        }, cancellationToken);
        return Result.Success(command.Saved);
    }
}

public sealed record GetSavedQuery(Guid TravelerId);

public static class GetSavedHandler
{
    /// <summary>Wishes grouped by destination, newest first. Places no longer published are left out.</summary>
    public static async Task<Result<IReadOnlyList<SavedGroupDto>>> Handle(GetSavedQuery query, ITravelerReader travelers, IPlaceReader places, CancellationToken cancellationToken)
    {
        var saved = await travelers.SavedAsync(query.TravelerId, cancellationToken);
        var byId = (await places.PlacesAsync(null, cancellationToken)).ToDictionary(p => p.PoiId);
        var groups = saved
            .Where(s => byId.ContainsKey(s.PoiId))
            .OrderByDescending(s => s.SavedAt)
            .GroupBy(s => byId[s.PoiId].Destination)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SavedGroupDto(g.Key, [.. g.Select(s => new SavedItemDto(s.PoiId, byId[s.PoiId].Slug, byId[s.PoiId].Name, s.SavedAt))]))
            .ToArray();
        return Result.Success<IReadOnlyList<SavedGroupDto>>(groups);
    }
}

public sealed record GetHistoryQuery(Guid TravelerId);

public static class GetHistoryHandler
{
    public static async Task<Result<IReadOnlyList<HistoryItemDto>>> Handle(GetHistoryQuery query, ITravelerReader travelers, IPlaceReader places, CancellationToken cancellationToken)
    {
        var byId = (await places.PlacesAsync(null, cancellationToken)).ToDictionary(p => p.PoiId);
        var items = (await travelers.HistoryAsync(query.TravelerId, cancellationToken))
            .Where(h => byId.ContainsKey(h.PoiId))
            .OrderByDescending(h => h.LastAt)
            .Select(h => new HistoryItemDto(h.PoiId, byId[h.PoiId].Slug, byId[h.PoiId].Name, h.LastAt, h.Listened, h.Visited))
            .ToArray();
        return Result.Success<IReadOnlyList<HistoryItemDto>>(items);
    }
}

public sealed record DeleteHistoryCommand(Guid TravelerId, Guid PoiId);

public static class DeleteHistoryHandler
{
    /// <summary>Forgets one place (interactions, visits, impressions, rating) and recomputes the vector without it.</summary>
    public static async Task<Result<InteractionBatchResponse>> Handle(DeleteHistoryCommand command, IDiscoveryStore store, CancellationToken cancellationToken) =>
        Result.Success(await store.ExclusiveAsync(command.TravelerId, async session =>
        {
            await session.DeletePlaceAsync(command.PoiId, cancellationToken);
            var learned = await IngestInteractionsHandler.Replay(session, cancellationToken);
            return new InteractionBatchResponse(learned.Vector, learned.ProfileDepth, Taxonomy.Interests.Version, 0, 0, [.. learned.Excluded.Select(Guid.Parse)]);
        }, cancellationToken));
}

public sealed record RecomputeCategoryAffinityCommand;

public static class RecomputeCategoryAffinityHandler
{
    public static async Task<int> Handle(RecomputeCategoryAffinityCommand command, IAffinityStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        await store.RecomputeAsync(clock.GetUtcNow(), cancellationToken);
}
