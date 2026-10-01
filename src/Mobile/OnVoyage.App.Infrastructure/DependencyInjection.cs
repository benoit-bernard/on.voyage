using Microsoft.Extensions.DependencyInjection;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Infrastructure.Auth;
using OnVoyage.App.Infrastructure.Http;

namespace OnVoyage.App.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers the HTTP clients. Only the Gateway is called (single entry point, §9.1).</summary>
    public static IServiceCollection AddAppInfrastructure(this IServiceCollection services, Uri gatewayBaseAddress, string appVersion)
    {
        services.AddSingleton(new AppVersion(appVersion));
        services.AddTransient<BearerTokenHandler>();

        // The identity client must not go through the bearer handler: the handler depends on it to refresh tokens.
        services.AddHttpClient<IAuthClient, HttpAuthClient>(client => client.BaseAddress = gatewayBaseAddress);
        services.AddHttpClient<ICatalogClient, HttpCatalogClient>(client => client.BaseAddress = gatewayBaseAddress)
            .AddHttpMessageHandler<BearerTokenHandler>();
        services.AddHttpClient<IDiscoveryClient, HttpDiscoveryClient>(client => client.BaseAddress = gatewayBaseAddress)
            .AddHttpMessageHandler<BearerTokenHandler>();
        return services;
    }
}
