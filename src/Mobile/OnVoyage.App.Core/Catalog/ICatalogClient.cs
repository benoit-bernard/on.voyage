using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Core.Catalog;

public interface ICatalogClient
{
    Task<DestinationDto?> GetDestinationAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Lists published places. The position, when given, is only sent as query parameters of this call.</summary>
    Task<IReadOnlyList<PoiSummaryDto>> GetPoisAsync(string destination, double? latitude, double? longitude, CancellationToken cancellationToken);

    Task<PoiDetailDto?> GetPoiAsync(string slug, CancellationToken cancellationToken);
}
