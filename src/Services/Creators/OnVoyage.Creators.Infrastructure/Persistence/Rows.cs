namespace OnVoyage.Creators.Infrastructure.Persistence;

internal sealed class CreatorRow
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public string Handle { get; set; } = "";
    public string? TermsDocumentRef { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Bio { get; set; }
    public string? AvatarPath { get; set; }
    public string[] Languages { get; set; } = [];
    public string[] Specialties { get; set; } = [];
    public Guid[] DestinationIds { get; set; } = [];
    public string Links { get; set; } = "[]";
    public string Status { get; set; } = "draft";
    public string? TermsVersion { get; set; }
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    public bool Founding { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ContentRow
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string Platform { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Permalink { get; set; } = "";
    public string Title { get; set; } = "";
    public string? CaptionExcerpt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int? DurationS { get; set; }
    public string Kind { get; set; } = "video";
    public string? CoverPath { get; set; }
    public string Chapters { get; set; } = "[]";
    public bool IsCommercial { get; set; }
    public string Status { get; set; } = "imported";
    public Guid? ConnectedAccountId { get; set; }
    public DateTimeOffset? GeotaggedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A place a content mentions that the catalog does not have, already suggested to the editorial team (one per content and name).</summary>
internal sealed class UnmatchedMentionRow
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public Guid ContentId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? Excerpt { get; set; }
    public DateTimeOffset SuggestedAt { get; set; }
}

/// <summary>
/// A connected social account with its OAuth tokens. The tokens are encrypted with Data Protection (<see cref="Social.SocialConnector"/> is the only code that
/// reads or writes the two protected columns); they are never mapped to a domain type, an API or a log.
/// </summary>
internal sealed class ConnectedAccountRow
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public string Platform { get; set; } = "";
    public string ExternalUserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset? LastSyncAt { get; set; }
    public string Status { get; set; } = "active";
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PlaceLinkRow
{
    public Guid Id { get; set; }
    public Guid? ContentId { get; set; }
    public Guid CreatorId { get; set; }
    public Guid PoiId { get; set; }
    public int? StartS { get; set; }
    public float Confidence { get; set; }
    public string Signals { get; set; } = "{}";
    public string Status { get; set; } = "proposed";
    public DateTimeOffset? ValidatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class TipRow
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public Guid PoiId { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public string Status { get; set; } = "published";
}

/// <summary>Never exposed to the creator or to a third party (cahier §11.5).</summary>
internal sealed class FollowRow
{
    public Guid TravelerId { get; set; }
    public Guid CreatorId { get; set; }
    public DateTimeOffset FollowedAt { get; set; }
}

/// <summary>A place of the catalog, by name only: there is deliberately no column that can hold a position.</summary>
internal sealed class PoiDirectoryRow
{
    public Guid PoiId { get; set; }
    public Guid DestinationId { get; set; }
    public string DestinationSlug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Names { get; set; } = "{}";
    public string? City { get; set; }
    public short ImportanceScore { get; set; }
    public bool IsPublished { get; set; }
    public int Version { get; set; }
    public string SearchText { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ModerationCaseRow
{
    public Guid Id { get; set; }
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ReporterRef { get; set; }
    public string Status { get; set; } = "open";
    public string? Decision { get; set; }
    public string? StatementOfReasons { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
