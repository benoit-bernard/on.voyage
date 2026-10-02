using System.Security.Cryptography;
using System.Text;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Recommendation.Engine.Learning;

namespace OnVoyage.Discovery.Application.IntegrationEvents;

/// <summary>
/// Discovery's projection of the creators (T-1205, §6.15). Each handler is idempotent and tolerates reordering: the newest <c>OccurredAt</c>
/// per creator, per link and per follow wins, so a redelivery or an older event changes nothing.
/// </summary>
public static class CreatorPublishedHandler
{
    public static Task Handle(CreatorPublishedV1 published, ICreatorProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyCreatorAsync(
            new CreatorProjection(published.CreatorId, published.Handle, published.DisplayName, published.AvatarPath, published.Specialties, IsPublished: true, published.OccurredAt),
            cancellationToken);
}

/// <summary>The creator disappears from every ranking at once (withdrawal rule of §13); their links are kept in case the page is published again.</summary>
public static class CreatorUnpublishedHandler
{
    public static Task Handle(CreatorUnpublishedV1 unpublished, ICreatorProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyCreatorAsync(
            new CreatorProjection(unpublished.CreatorId, unpublished.Handle, unpublished.Handle, null, [], IsPublished: false, unpublished.OccurredAt),
            cancellationToken);
}

public static class CreatorPlaceLinkChangedHandler
{
    public const string Validated = "validated";

    public static Task Handle(CreatorPlaceLinkChangedV1 changed, ICreatorProjectionWriter writer, CancellationToken cancellationToken) =>
        writer.ApplyLinkAsync(
            new CreatorLinkProjection(changed.CreatorId, changed.PoiId, changed.ContentId, changed.Kind, changed.IsCommercial, changed.Status == Validated, changed.OccurredAt),
            cancellationToken);
}

/// <summary>
/// Stores the follow and, the first time a traveler follows a creator, writes a <c>follow_creator</c> interaction that carries the creator
/// vector <c>c</c> as it is now (§6.15). The interaction id is derived from the pair, so the signal exists once per creator: unfollowing does
/// not undo it, following again does not repeat it. A creator without validated places has no vector yet and writes nothing.
/// </summary>
public static class FollowChangedHandler
{
    public static Task Handle(FollowChangedV1 changed, IDiscoveryStore store, CancellationToken cancellationToken) =>
        store.ExclusiveAsync(changed.TravelerId, async session =>
        {
            if (!await session.SetFollowAsync(changed.CreatorId, changed.Following, changed.OccurredAt, cancellationToken) || !changed.Following)
            {
                return false;
            }

            var vector = await session.CreatorVectorAsync(changed.CreatorId, cancellationToken);
            if (vector.Count == 0)
            {
                return false;
            }

            var signal = new Interaction(InteractionId(changed.TravelerId, changed.CreatorId), InteractionKinds.FollowCreator, null, changed.OccurredAt, Weights: vector);
            if (await session.AddAsync([new IncomingInteraction(signal, null, null, null, null)], cancellationToken) == 0)
            {
                return false;
            }

            await Features.IngestInteractionsHandler.Replay(session, cancellationToken);
            return true;
        }, cancellationToken);

    /// <summary>A stable GUID for the (traveler, creator) pair: the same follow replayed gives the same <c>client_event_id</c>.</summary>
    public static Guid InteractionId(Guid travelerId, Guid creatorId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"follow_creator:{travelerId:N}:{creatorId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
