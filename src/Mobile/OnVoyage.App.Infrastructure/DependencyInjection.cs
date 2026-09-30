using Microsoft.Extensions.DependencyInjection;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Infrastructure.Http;

namespace OnVoyage.App.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the catalog HTTP client. Only the Gateway is called (single entry point, §9.1).</summary>
    public static IServiceCollection AddAppInfrastructure(this IServiceCollection services, Uri gatewayBaseAddress)
    {
        services.AddHttpClient<ICatalogClient, HttpCatalogClient>(client => client.BaseAddress = gatewayBaseAddress);
        return services;
    }
}
