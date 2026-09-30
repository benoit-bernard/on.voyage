using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Infrastructure.Seed;

namespace OnVoyage.Catalog.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services) =>
        services.AddSingleton<IPoiReader, InMemoryPoiReader>();
}
