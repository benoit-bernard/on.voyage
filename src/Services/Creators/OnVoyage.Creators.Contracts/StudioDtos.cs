namespace OnVoyage.Creators.Contracts;

// ---- Creator side: the self-service space (Web.Studio, F-26, T-1206). Same shapes as the admin's, minus what a creator must never see. ----

/// <summary>
/// Where a signed-in account stands: not yet a creator (<c>Registered</c> false), or registered with its handle and status. <c>CurrentTermsVersion</c>
/// is the version of the creator terms to accept; <c>TermsAccepted</c> is false when the creator has not accepted that version.
/// </summary>
public sealed record StudioRegistrationDto(bool Registered, string CurrentTermsVersion, Guid? CreatorId, string? Handle, string? Status, bool TermsAccepted);

/// <summary>The creator terms are accepted by naming their version, which must be the current one (F-26).</summary>
public sealed record StudioSignupRequest(string Handle, string DisplayName, string AcceptedTermsVersion);

/// <summary>Accepting a newer version of the creator terms.</summary>
public sealed record AcceptTermsRequest(string AcceptedTermsVersion);

/// <summary>
/// The creator's own sheet. <c>FollowerCount</c> follows the public threshold (null under 20, <c>IsNew</c> true): a creator never sees who follows
/// them, nor an exact count that the public page does not show.
/// </summary>
public sealed record StudioProfileDto(
    Guid Id,
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
    DateTimeOffset? TermsAcceptedAt,
    int? FollowerCount,
    bool IsNew,
    PublishBlockDto? PublishBlock,
    IReadOnlyList<AdminContentDto> Contents,
    IReadOnlyList<AdminPlaceLinkDto> PlaceLinks,
    IReadOnlyList<AdminTipDto> Tips);

// ---- Geo-association review (F-28, T-1209): nothing here is published until the creator validates it. ----

/// <summary>
/// A place the assistant found in a content and proposes. <c>Confidence</c> is 0–1; <c>Signals</c> says why (<c>text</c>, <c>chapter</c>, <c>context</c>,
/// <c>ambiguous</c>); <c>Evidence</c> is the sentence or the chapter it comes from; <c>ContentUrl</c> opens the content at the chapter.
/// </summary>
public sealed record PlaceProposalDto(
    Guid LinkId,
    Guid PoiId,
    string PoiName,
    string? City,
    string DestinationSlug,
    Guid? ContentId,
    string? ContentTitle,
    string? ContentUrl,
    int? StartSeconds,
    double Confidence,
    string? Evidence,
    IReadOnlyList<string> Signals);

public sealed record PlaceProposalGroupDto(string Destination, IReadOnlyList<PlaceProposalDto> Items);

/// <summary>
/// « Nous avons trouvé <c>Total</c> lieux dans vos contenus », by destination. <c>BulkThreshold</c> is the confidence from which « Tout valider » applies
/// (<c>ReadyCount</c> proposals); below it each proposal is reviewed one by one. <c>Pending</c> counts contents not analysed yet.
/// </summary>
public sealed record PlaceProposalsDto(int Total, int ReadyCount, double BulkThreshold, int Pending, IReadOnlyList<PlaceProposalGroupDto> Groups);

/// <summary>Either explicit <c>LinkIds</c> (reviewed one by one) or <c>MinConfidence</c> (« Tout valider »: never below the bulk threshold).</summary>
public sealed record ReviewPlaceLinksRequest(IReadOnlyList<Guid>? LinkIds, double? MinConfidence);

public sealed record ReviewResultDto(int Validated, int Rejected);

/// <summary>The creator corrects the place of a proposal: the proposal is rejected and the chosen place is validated in its stead.</summary>
public sealed record CorrectPlaceLinkRequest(Guid PoiId);

public sealed record AnalysisRequestedDto(int Contents);
