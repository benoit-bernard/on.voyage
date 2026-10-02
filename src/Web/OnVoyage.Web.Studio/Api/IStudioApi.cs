using OnVoyage.Creators.Contracts;

namespace OnVoyage.Web.Studio.Api;

/// <summary>
/// What the creator space asks of the Creators service (through the Gateway, with the creator's own token): sign-up, profile, publication,
/// tips, contents and the places they point to. Every call concerns the creator of the token, never another one.
/// </summary>
public interface IStudioApi
{
    Task<StudioRegistrationDto> GetRegistrationAsync(CancellationToken cancellationToken = default);

    Task<StudioRegistrationDto> SignUpAsync(string handle, string displayName, string acceptedTermsVersion, CancellationToken cancellationToken = default);

    Task<StudioRegistrationDto> AcceptTermsAsync(string acceptedTermsVersion, CancellationToken cancellationToken = default);

    Task<StudioProfileDto> GetProfileAsync(CancellationToken cancellationToken = default);

    Task<StudioProfileDto> UpdateProfileAsync(CreatorProfileRequest profile, CancellationToken cancellationToken = default);

    Task<StudioProfileDto> PublishAsync(CancellationToken cancellationToken = default);

    Task<StudioProfileDto> UnpublishAsync(CancellationToken cancellationToken = default);

    Task<AdminContentDto> AddContentAsync(AddContentRequest content, CancellationToken cancellationToken = default);

    Task<AdminContentDto> UpdateContentAsync(Guid contentId, UpdateContentRequest content, CancellationToken cancellationToken = default);

    Task RemoveContentAsync(Guid contentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PoiSearchResultDto>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default);

    Task<AdminPlaceLinkDto> AddPlaceLinkAsync(AddPlaceLinkRequest link, CancellationToken cancellationToken = default);

    Task<AdminPlaceLinkDto> SetPlaceLinkStatusAsync(Guid linkId, string status, CancellationToken cancellationToken = default);

    Task RemovePlaceLinkAsync(Guid linkId, CancellationToken cancellationToken = default);

    Task<ConnectionsDto> GetConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The address of the platform's authorization page: the browser is sent there (F-27).</summary>
    Task<ConnectionStartDto> StartConnectionAsync(string platform, CancellationToken cancellationToken = default);

    /// <summary>Hands the <c>code</c> and <c>state</c> of the return address to the Creators service, which exchanges them for the tokens it keeps.</summary>
    Task<ConnectedAccountDto> CompleteConnectionAsync(string platform, string code, string state, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string platform, bool deleteContents, CancellationToken cancellationToken = default);

    Task<SyncRequestedDto> RequestSyncAsync(CancellationToken cancellationToken = default);

    Task<AdminTipDto> SetTipAsync(Guid poiId, string text, CancellationToken cancellationToken = default);

    Task RemoveTipAsync(Guid poiId, CancellationToken cancellationToken = default);
}
