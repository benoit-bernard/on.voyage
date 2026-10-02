using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Creators.Application.Features;

/// <summary>Builds the integration events of §13 from the domain state. Events carry a v7 id and the time they were decided.</summary>
public static class CreatorEvents
{
    public const string Service = "creators";

    public static CreatorPublishedV1 Published(Creator creator, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), now, creator.Id, creator.Handle, creator.Profile.DisplayName, creator.Profile.AvatarPath, creator.Profile.Specialties);

    public static CreatorUnpublishedV1 Unpublished(Creator creator, string reason, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), now, creator.Id, creator.Handle, reason);

    public static CreatorTermsAcceptedV1? TermsAccepted(Creator creator, DateTimeOffset now) =>
        creator.AccountId is { } account && creator.TermsAccepted ? new CreatorTermsAcceptedV1(Guid.CreateVersion7(), now, account, creator.Id, creator.TermsVersion!) : null;

    public static CreatorPlaceLinkChangedV1 LinkChanged(PlaceLink link, ContentItem? content, bool validated, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), now, link.CreatorId, link.PoiId, link.ContentId, content?.Kind ?? "tip", content?.IsCommercial ?? false, validated ? PlaceLinkStatuses.Validated : "removed");

    public static FollowChangedV1 Followed(Guid travelerId, Guid creatorId, bool following, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), now, travelerId, creatorId, following);

    /// <summary>Every admin write is journaled by Platform (SEC-10). Saved with the change itself, so the journal cannot miss it.</summary>
    public static AdminActionRecordedV1 Audit(Guid actor, string action, string target, string? summary, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), now, Service, actor.ToString(), action, target, 200, summary is { Length: > 500 } text ? text[..500] : summary);
}
