using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Core.Creators;

/// <summary>
/// The Creators endpoints the app calls through the Gateway (F-30, F-33). No call takes a position: the block of a place is asked by its
/// identifier. Failures throw <see cref="HttpRequestException"/>; an unknown or unpublished creator is <c>null</c>.
/// </summary>
public interface ICreatorsClient
{
    Task<PoiCreatorsDto?> GetPoiCreatorsAsync(Guid poiId, int limit, CancellationToken cancellationToken);

    Task<CreatorPageDto?> GetCreatorAsync(string handle, CancellationToken cancellationToken);

    /// <summary>Idempotent; null when the creator is not published (or was removed meanwhile).</summary>
    Task<FollowStateDto?> SetFollowAsync(Guid creatorId, bool following, CancellationToken cancellationToken);

    Task<ReportReceiptDto> ReportAsync(ReportRequest request, CancellationToken cancellationToken);

    /// <summary>"Pour vos goûts" (Discovery, §6.15): the creators of the destination by affinity with the traveler.</summary>
    Task<CreatorsForMeDto?> GetCreatorsForMeAsync(string destination, CancellationToken cancellationToken);
}

/// <summary>Turns the path a service returns for one of our own media (avatar, cover) into an address the app can load.</summary>
public sealed class MediaLocator(string baseUrl)
{
    private readonly string _base = baseUrl.TrimEnd('/');

    public string? Resolve(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : $"{_base}/{path.TrimStart('/')}";
}
