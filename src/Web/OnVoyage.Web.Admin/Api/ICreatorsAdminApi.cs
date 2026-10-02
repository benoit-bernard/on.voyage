using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Admin.Api;

/// <summary>
/// The creators' side of the back-office (T-1202, F-26, F-33): founding creators, their contents by URL, associations and tips, publication,
/// and the moderation queue. Separate from <see cref="IAdminApi"/> because it is another service behind the Gateway.
/// </summary>
public interface ICreatorsAdminApi
{
    Task<IReadOnlyList<AdminCreatorSummaryDto>> ListCreatorsAsync(string? status, string? search, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> GetCreatorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> CreateFounderAsync(CreatorProfileRequest profile, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> UpdateCreatorAsync(Guid id, CreatorProfileRequest profile, CancellationToken cancellationToken = default);

    /// <summary>Records the founder's signed consent (<c>terms_version = fondateur</c>) with the reference of the document.</summary>
    Task<AdminCreatorDetailDto> RecordConsentAsync(Guid id, string documentRef, DateTimeOffset? acceptedAt, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> LinkAccountAsync(Guid id, Guid accountId, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> PublishAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> UnpublishAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    Task<AdminCreatorDetailDto> SuspendAsync(Guid id, string reason, CancellationToken cancellationToken = default);

    /// <summary>The creator in the path claims <paramref name="handle"/>; whoever holds it is renamed and withdrawn.</summary>
    Task<AdminCreatorDetailDto> ClaimHandleAsync(Guid id, string handle, string reason, CancellationToken cancellationToken = default);

    Task<AdminContentDto> AddContentAsync(Guid id, AddContentRequest content, CancellationToken cancellationToken = default);

    Task RemoveContentAsync(Guid id, Guid contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoiSearchResultDto>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default);

    Task<AdminPlaceLinkDto> AddPlaceLinkAsync(Guid id, AddPlaceLinkRequest link, CancellationToken cancellationToken = default);

    Task<AdminPlaceLinkDto> SetPlaceLinkStatusAsync(Guid id, Guid linkId, string status, CancellationToken cancellationToken = default);

    Task RemovePlaceLinkAsync(Guid id, Guid linkId, CancellationToken cancellationToken = default);

    Task<AdminTipDto> SetTipAsync(Guid id, Guid poiId, string text, CancellationToken cancellationToken = default);

    Task RemoveTipAsync(Guid id, Guid poiId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModerationCaseDto>> ListModerationAsync(string? status, CancellationToken cancellationToken = default);

    Task<ModerationCaseDto> DecideAsync(Guid caseId, string decision, string? statementOfReasons, CancellationToken cancellationToken = default);
}
