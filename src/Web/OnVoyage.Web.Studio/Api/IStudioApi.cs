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

    Task<AdminTipDto> SetTipAsync(Guid poiId, string text, CancellationToken cancellationToken = default);

    Task RemoveTipAsync(Guid poiId, CancellationToken cancellationToken = default);
}
