using System.Globalization;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Application.Features;

public sealed record GetCreatorPageQuery(string Handle, Guid? Viewer);

public sealed record ListCreatorsQuery(string? Destination, string? Specialty, string? Cursor, int? Limit);

public sealed record GetPoiCreatorsQuery(Guid PoiId, int? Limit);

public sealed record ListFollowsQuery(Guid Traveler);

public sealed record SetFollowCommand(Guid Traveler, Guid CreatorId, bool Following);

/// <summary>
/// What a traveler may see of the creators (F-26, F-30): a published creator, its validated links to published places, contents online.
/// Nothing here takes a position, and a follow is only ever shown to the traveler who made it.
/// </summary>
public static class PublicCreatorHandler
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static async Task<Result<CreatorPageDto>> Handle(GetCreatorPageQuery query, ICreatorQueries queries, CancellationToken cancellationToken)
    {
        if (!Handles.TryNormalize(query.Handle, out var handle) || await queries.GetPageAsync(handle, query.Viewer, cancellationToken) is not { } page)
        {
            return Result.Failure<CreatorPageDto>("creator_not_found", "Créateur introuvable.");
        }

        return Result.Success(page);
    }

    public static async Task<Result<CreatorListDto>> Handle(ListCreatorsQuery query, ICreatorQueries queries, CancellationToken cancellationToken)
    {
        var offset = 0;
        if (!string.IsNullOrEmpty(query.Cursor) && (!int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100_000))
        {
            return Result.Failure<CreatorListDto>("validation", "Curseur invalide.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);
        return Result.Success(await queries.ListPublishedAsync(
            string.IsNullOrWhiteSpace(query.Destination) ? null : query.Destination.Trim(),
            string.IsNullOrWhiteSpace(query.Specialty) ? null : query.Specialty.Trim(),
            offset,
            limit,
            cancellationToken));
    }

    public static async Task<Result<PoiCreatorsDto>> Handle(GetPoiCreatorsQuery query, ICreatorQueries queries, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListForPoiAsync(query.PoiId, Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit), cancellationToken));

    public static async Task<Result<IReadOnlyList<FollowedCreatorDto>>> Handle(ListFollowsQuery query, ICreatorQueries queries, CancellationToken cancellationToken) =>
        Result.Success(await queries.ListFollowsAsync(query.Traveler, cancellationToken));

    /// <summary>Follow or unfollow, idempotent: repeating it changes nothing and publishes nothing. Only a published creator can be followed.</summary>
    public static async Task<Result<FollowStateDto>> Handle(SetFollowCommand command, ICreatorQueries queries, IFollowRepository follows, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (command.Following && !await queries.IsPublishedAsync(command.CreatorId, cancellationToken))
        {
            return Result.Failure<FollowStateDto>("creator_not_found", "Créateur introuvable.");
        }

        var now = clock.GetUtcNow();
        if (await follows.StageAsync(command.Traveler, command.CreatorId, command.Following, now, cancellationToken))
        {
            await unit.CommitAsync([CreatorEvents.Followed(command.Traveler, command.CreatorId, command.Following, now)], cancellationToken);
        }

        return Result.Success(new FollowStateDto(command.CreatorId, command.Following));
    }
}
