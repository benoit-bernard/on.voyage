using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Catalog;

public interface ICatalogClient
{
    Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Lists published places. The position, when given, is only sent as query parameters of this call.</summary>
    Task<IReadOnlyList<PoiSummaryDto>> GetPoisAsync(string destination, double? latitude, double? longitude, CancellationToken cancellationToken);

    /// <summary>
    /// Full-text search over the published places (F-14, <c>GET /api/catalog/v1/search</c>). The typed text is only the <c>q</c> parameter of this call.
    /// Failures throw <see cref="HttpRequestException"/>.
    /// </summary>
    Task<IReadOnlyList<PoiSummaryDto>> SearchAsync(string destination, string text, int limit, CancellationToken cancellationToken);

    Task<PoiDetailDto?> GetPoiAsync(string slug, CancellationToken cancellationToken);
}
