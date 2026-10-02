namespace OnVoyage.Creators.Contracts;

// ---- Traveler side (§12.9). Never carries a follower identity, a position, or anything about an unpublished creator. ----

public sealed record CreatorLinkDto(string Kind, string Url);

public sealed record CreatorSummaryDto(Guid Id, string Handle, string DisplayName, string? AvatarPath, IReadOnlyList<string> Specialties, int PlaceCount);

public sealed record CreatorListDto(IReadOnlyList<CreatorSummaryDto> Items, string? NextCursor);

/// <summary>A content stays on its platform: <c>Url</c> is the link out (timestamped for a chapter), never an embed (D-17).</summary>
public sealed record CreatorContentDto(Guid Id, string Platform, string Kind, string Title, string Url, int? StartSeconds, int? DurationSeconds, string? CoverPath, bool IsCommercial, DateTimeOffset? PublishedAt);

public sealed record CreatorPlaceDto(Guid PoiId, string Name, string? City, string? Tip, IReadOnlyList<CreatorContentDto> Contents);

/// <summary><c>FollowerCount</c> is null below the display threshold (20): the page then says "Nouveau créateur" (<c>IsNew</c>).</summary>
public sealed record CreatorPageDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarPath,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Specialties,
    IReadOnlyList<CreatorLinkDto> Links,
    int? FollowerCount,
    bool IsNew,
    int PlaceCount,
    int DestinationCount,
    bool IsFollowing,
    IReadOnlyList<CreatorPlaceDto> Places,
    IReadOnlyList<CreatorContentDto> RecentContents);

public sealed record PoiCreatorItemDto(CreatorSummaryDto Creator, string? Tip, CreatorContentDto? Content);

/// <summary>The "Vu par les créateurs" block of a place: <c>Total</c> counts every creator, <c>Items</c> is capped by the request.</summary>
public sealed record PoiCreatorsDto(Guid PoiId, int Total, IReadOnlyList<PoiCreatorItemDto> Items);

public sealed record FollowedCreatorDto(CreatorSummaryDto Creator, DateTimeOffset FollowedAt);

public sealed record FollowStateDto(Guid CreatorId, bool Following);

/// <summary><c>TargetType</c> is <c>creator</c>, <c>content</c>, <c>place_link</c> or <c>tip</c>; <c>Reason</c> is one of <see cref="ReportReasons"/>.</summary>
public sealed record ReportRequest(string TargetType, Guid TargetId, string Reason);

public sealed record ReportReceiptDto(Guid CaseId);

public static class ReportReasons
{
    public const string Inaccurate = "inaccurate";
    public const string Misleading = "misleading";
    public const string UndeclaredAd = "undeclared_ad";
    public const string Inappropriate = "inappropriate";
    public const string Impersonation = "impersonation";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Inaccurate, Misleading, UndeclaredAd, Inappropriate, Impersonation, Other];
}

// ---- Admin side (§12.9, F-26, F-33). ----

public sealed record CreatorProfileRequest(
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarPath,
    IReadOnlyList<string>? Languages,
    IReadOnlyList<string>? Specialties,
    IReadOnlyList<Guid>? DestinationIds,
    IReadOnlyList<CreatorLinkDto>? Links);

/// <summary>The founder's written consent (F-26): the reference of the signed document is mandatory.</summary>
public sealed record FounderConsentRequest(string DocumentRef, DateTimeOffset? AcceptedAt);

public sealed record LinkAccountRequest(Guid AccountId);

public sealed record ReasonRequest(string Reason);

/// <summary>The creator in the path claims <c>Handle</c>, held by someone else: the holder is renamed (F-33, impersonation).</summary>
public sealed record ClaimHandleRequest(string Handle, string Reason);

public sealed record ChapterDto(int StartSeconds, string Title);

public sealed record AddContentRequest(
    string Url,
    string Title,
    string? CaptionExcerpt,
    string? CoverPath,
    DateTimeOffset? PublishedAt,
    int? DurationSeconds,
    string? Kind,
    bool IsCommercial,
    IReadOnlyList<ChapterDto>? Chapters);

/// <summary><c>Status</c> is <c>imported</c> (online) or <c>hidden</c>.</summary>
public sealed record UpdateContentRequest(string Title, string? CaptionExcerpt, string? CoverPath, int? DurationSeconds, bool IsCommercial, IReadOnlyList<ChapterDto>? Chapters, string Status);

/// <summary><c>Status</c> defaults to <c>validated</c> (the administrator acts with the founder's consent); <c>proposed</c> and <c>rejected</c> are accepted.</summary>
public sealed record AddPlaceLinkRequest(Guid PoiId, Guid? ContentId, int? StartSeconds, string? Status);

public sealed record SetPlaceLinkStatusRequest(string Status);

public sealed record SetTipRequest(string Text);

public sealed record DecideCaseRequest(string Decision, string? StatementOfReasons);

/// <summary>A reason the creator cannot be published yet: <c>Code</c> is <c>terms_required</c> or <c>specialty_required</c>.</summary>
public sealed record PublishBlockDto(string Code, string Message);

public sealed record AdminCreatorSummaryDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string Status,
    bool Founding,
    string? TermsVersion,
    int SpecialtyCount,
    int ValidatedPlaceCount,
    bool HasAccount,
    PublishBlockDto? PublishBlock);

public sealed record AdminContentDto(
    Guid Id,
    string Platform,
    string Kind,
    string Title,
    string Permalink,
    string? CaptionExcerpt,
    string? CoverPath,
    int? DurationSeconds,
    DateTimeOffset? PublishedAt,
    bool IsCommercial,
    string Status,
    IReadOnlyList<ChapterDto> Chapters);

public sealed record AdminPlaceLinkDto(Guid Id, Guid PoiId, string PoiName, Guid? ContentId, string? ContentTitle, int? StartSeconds, double Confidence, string Status, DateTimeOffset? ValidatedAt);

public sealed record AdminTipDto(Guid Id, Guid PoiId, string PoiName, string Text, string Status, DateTimeOffset UpdatedAt);

public sealed record AdminCreatorDetailDto(
    Guid Id,
    Guid? AccountId,
    string Handle,
    string DisplayName,
    string? Bio,
    string? AvatarPath,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Specialties,
    IReadOnlyList<Guid> DestinationIds,
    IReadOnlyList<CreatorLinkDto> Links,
    string Status,
    bool Founding,
    string? TermsVersion,
    string? TermsDocumentRef,
    DateTimeOffset? TermsAcceptedAt,
    int FollowerCount,
    PublishBlockDto? PublishBlock,
    IReadOnlyList<AdminContentDto> Contents,
    IReadOnlyList<AdminPlaceLinkDto> PlaceLinks,
    IReadOnlyList<AdminTipDto> Tips);

public sealed record PoiSearchResultDto(Guid PoiId, string Name, string? City, string DestinationSlug, bool IsPublished);

/// <summary>The reporter is never part of the queue (F-33): the administrator sees what was reported, not who reported it.</summary>
public sealed record ModerationCaseDto(
    Guid Id,
    string TargetType,
    Guid TargetId,
    string TargetLabel,
    Guid? CreatorId,
    string? CreatorHandle,
    string Reason,
    string Status,
    string? Decision,
    string? StatementOfReasons,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public static class ModerationDecisions
{
    public const string Dismissed = "dismissed";
    public const string Upheld = "upheld";
}
