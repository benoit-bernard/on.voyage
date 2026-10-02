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
