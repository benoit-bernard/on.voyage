using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Contracts;
using OnVoyage.Taxonomy;

namespace OnVoyage.Factory.Application.Features.Places;

public sealed record PublishPlaceCommand(Guid PlaceId);

public sealed record UnpublishPlaceCommand(Guid PlaceId, string Reason);

public sealed record SetPlaceEditorialCommand(Guid PlaceId, int? ImportanceOverride, bool? EditoriallySaturated);

public sealed record RejectPlaceCommand(Guid PlaceId);

public sealed record RevertMergeCommand(Guid LinkId);

public sealed record ConfirmMergeCommand(Guid LinkId);

public sealed record ListPlacesQuery(string DestinationSlug, PlaceStatus? Status, int Limit);

public sealed record GetPlaceQuery(Guid PlaceId);

public sealed record ListDedupProposalsQuery(string DestinationSlug);

public sealed record PlaceView(PlaceRecord Place, IReadOnlyList<(string Code, double Weight)> Interests);

public static class PublishPlaceHandler
{
    /// <summary>
    /// Publishes (or republishes) a place to the catalog. A place that still needs classification review cannot be published, nor can
    /// one that was never scored: the catalog's recommendations depend on the interest vector.
    /// </summary>
    public static async Task<Result<int>> Handle(
        PublishPlaceCommand command, IPlaceStore places, IDestinationCatalog destinations, TimeProvider clock, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return Result.Failure<int>("place_not_found", "Place not found.");
        }

        if (place.Status is PlaceStatus.NeedsReview or PlaceStatus.Rejected or PlaceStatus.Merged)
        {
            return Result.Failure<int>("place_not_publishable", $"A place in state {place.Status} cannot be published.");
        }

        var interests = await places.GetInterestsAsync(place.Id, cancellationToken);
        if (interests.Count == 0)
        {
            return Result.Failure<int>("place_not_scored", "The place has not been classified yet.");
        }

        var destination = await destinations.FindAsync(place.DestinationSlug, cancellationToken);
        if (destination is null)
        {
            return Result.Failure<int>("destination_not_found", "Unknown destination.");
        }

        var detail = await places.GetScoreDetailAsync(place.Id, cancellationToken);
        var version = place.PublishedVersion + 1;
        var now = clock.GetUtcNow();
        var published = new PoiPublishedV1(
            Guid.CreateVersion7(),
            now,
            place.Id,
            version,
            new PoiDestinationV1(destination.Slug, destination.Name, destination.Center.Latitude, destination.Center.Longitude),
            place.Slug,
            place.Enrichment?.LabelFr ?? place.Name,
            place.Enrichment?.LabelEn ?? place.NameEn,
            place.Location.Latitude,
            place.Location.Longitude,
            detail.Importance,
            detail.Percentile,
            detail.HiddenGem,
            0.8f,
            Interests.Version,
            [.. interests.Select(item => new PoiInterestV1(item.Code, (float)item.Weight))],
            new PoiCrowdProfileV1(detail.Offpeak, detail.Shoulder, detail.Peak),
            detail.Fragile,
            detail.AccessRegulated);

        await places.PublishAsync(place.Id, version, published, cancellationToken);
        return Result.Success(version);
    }
}

public static class UnpublishPlaceHandler
{
    public static async Task<Result<int>> Handle(UnpublishPlaceCommand command, IPlaceStore places, TimeProvider clock, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return Result.Failure<int>("place_not_found", "Place not found.");
        }

        if (place.Status != PlaceStatus.Published)
        {
            return Result.Failure<int>("place_not_published", "The place is not published.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure<int>("validation", "A reason is required.");
        }

        var version = place.PublishedVersion + 1;
        await places.UnpublishAsync(place.Id, version, new PoiUnpublishedV1(Guid.CreateVersion7(), clock.GetUtcNow(), place.Id, version, command.Reason.Trim()), cancellationToken);
        return Result.Success(version);
    }
}

public static class PlaceAdminHandler
{
    public static async Task<Result<bool>> Handle(SetPlaceEditorialCommand command, IPlaceStore places, CancellationToken cancellationToken)
    {
        if (command.ImportanceOverride is < 0 or > 100)
        {
            return Result.Failure<bool>("validation", "Importance must be between 0 and 100.");
        }

        if (await places.FindAsync(command.PlaceId, cancellationToken) is null)
        {
            return Result.Failure<bool>("place_not_found", "Place not found.");
        }

        await places.SetEditorialAsync(command.PlaceId, command.ImportanceOverride, command.EditoriallySaturated, cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<bool>> Handle(RejectPlaceCommand command, IPlaceStore places, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return Result.Failure<bool>("place_not_found", "Place not found.");
        }

        if (place.Status == PlaceStatus.Published)
        {
            return Result.Failure<bool>("place_published", "Unpublish the place before rejecting it.");
        }

        await places.SetStatusAsync(command.PlaceId, PlaceStatus.Rejected, cancellationToken);
        return Result.Success(true);
    }

    public static async Task<Result<bool>> Handle(RevertMergeCommand command, IPlaceStore places, CancellationToken cancellationToken) =>
        await places.RevertMergeAsync(command.LinkId, cancellationToken)
            ? Result.Success(true)
            : Result.Failure<bool>("link_not_found", "No active merge with this id.");

    public static async Task<Result<bool>> Handle(ConfirmMergeCommand command, IPlaceStore places, TimeProvider clock, CancellationToken cancellationToken)
    {
        var proposal = (await places.ListAllDedupLinksAsync(cancellationToken)).FirstOrDefault(link => link.Id == command.LinkId && !link.Automatic && link.RevertedAt is null);
        if (proposal is null)
        {
            return Result.Failure<bool>("link_not_found", "No pending proposal with this id.");
        }

        await places.MergeAsync(proposal.KeptPlaceId, proposal.OtherPlaceId, proposal with { CreatedAt = clock.GetUtcNow() }, cancellationToken);
        return Result.Success(true);
    }
}

public static class PlaceQueryHandler
{
    public static async Task<Result<IReadOnlyList<PlaceRecord>>> Handle(ListPlacesQuery query, IPlaceStore places, CancellationToken cancellationToken) =>
        Result.Success(await places.ListAsync(query.DestinationSlug, query.Status, Math.Clamp(query.Limit, 1, 200), cancellationToken));

    public static async Task<Result<PlaceView>> Handle(GetPlaceQuery query, IPlaceStore places, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(query.PlaceId, cancellationToken);
        return place is null
            ? Result.Failure<PlaceView>("place_not_found", "Place not found.")
            : Result.Success(new PlaceView(place, await places.GetInterestsAsync(place.Id, cancellationToken)));
    }

    public static async Task<Result<IReadOnlyList<DedupLink>>> Handle(ListDedupProposalsQuery query, IPlaceStore places, CancellationToken cancellationToken) =>
        Result.Success(await places.ListDedupLinksAsync(query.DestinationSlug, true, cancellationToken));
}
