namespace OnVoyage.Creators.Contracts;

// Integration events published by Creators (cahier des charges §13). Immutable, versioned by name; consumers are idempotent (EventId).

/// <summary>A creator page is public (or its public data changed: handle, name, avatar, specialties).</summary>
public sealed record CreatorPublishedV1(Guid EventId, DateTimeOffset OccurredAt, Guid CreatorId, string Handle, string DisplayName, string? AvatarPath, IReadOnlyList<string> Specialties);

/// <summary>The creator page is no longer public. <c>Reason</c> is the administrator's reason or a stable code (<c>handle_claimed</c>, <c>moderation</c>, <c>account_deleted</c>).</summary>
public sealed record CreatorUnpublishedV1(Guid EventId, DateTimeOffset OccurredAt, Guid CreatorId, string Handle, string Reason);

/// <summary>
/// A creator now recommends (<c>validated</c>) or no longer recommends (<c>removed</c>) a place. <c>Kind</c> is the kind of content
/// (<c>video</c>, <c>photo</c>, <c>carousel</c>, <c>article</c>) or <c>tip</c> when the link has no content.
/// </summary>
public sealed record CreatorPlaceLinkChangedV1(Guid EventId, DateTimeOffset OccurredAt, Guid CreatorId, Guid PoiId, Guid? ContentId, string Kind, bool IsCommercial, string Status);

/// <summary>The creator accepted the creator terms (or the founder consent was recorded): Platform adds the <c>creator</c> role to the account.</summary>
public sealed record CreatorTermsAcceptedV1(Guid EventId, DateTimeOffset OccurredAt, Guid AccountId, Guid CreatorId, string TermsVersion);

/// <summary>A traveler follows or stops following a creator. Never exposed to creators.</summary>
public sealed record FollowChangedV1(Guid EventId, DateTimeOffset OccurredAt, Guid TravelerId, Guid CreatorId, bool Following);

/// <summary>
/// A creator's content mentions a place that the catalog does not have (F-28): the editorial team may want it. <c>Name</c> is the mention,
/// <c>Excerpt</c> the few words around it, <c>DestinationSlug</c> the destination the creator is most likely talking about (when known).
/// Only the place and the creator's public content are sent, never anything about a traveler.
/// </summary>
public sealed record PlaceSuggestedV1(Guid EventId, DateTimeOffset OccurredAt, string Name, string? City, string? Excerpt, Guid CreatorId, Guid ContentId, string? DestinationSlug = null);
