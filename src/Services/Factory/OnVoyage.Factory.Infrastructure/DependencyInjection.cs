using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Infrastructure.Osm;
using OnVoyage.Factory.Infrastructure.Persistence;
using OnVoyage.Factory.Infrastructure.Sources;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddFactoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<FactoryDbContext>(options => FactoryPersistence.Configure(options, connectionString));
        services.AddScoped<IPlaceStore, PlaceStore>();
        services.AddSingleton<IDestinationCatalog, ConfiguredDestinationCatalog>();
        services.AddSingleton<IClassificationRuleProvider, JsonClassificationRuleProvider>();
        services.AddSingleton<IPlaceModelClassifier, NoModelClassifier>();
        services.AddSingleton(provider => configuration.GetSection("Factory:Heritage").Get<HeritageClasses>() ?? HeritageClasses.Defaults);
        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient<IOsmImporter, OsmImporter>(client => client.Timeout = TimeSpan.FromHours(2));
        services.AddHttpClient<WikimediaRequester>(client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<IWikidataClient, WikidataSparqlClient>();
        services.AddScoped<IPageviewsClient, WikimediaPageviewsClient>();

        services.AddHealthChecks().AddCheck<FactoryDatabaseHealthCheck>("factory-db", tags: ["ready"]);
        return services;
    }

    /// <summary>Registers the migration task. Call it after <c>UseWolverine</c>; it runs before the host accepts requests.</summary>
    public static IServiceCollection AddFactoryInitializer(this IServiceCollection services) => services.AddHostedService<FactoryInitializer>();

    private sealed class FactoryInitializer(IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Factory:Migrate", true))
            {
                await scope.ServiceProvider.GetRequiredService<FactoryDbContext>().Database.MigrateAsync(cancellationToken);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FactoryDatabaseHealthCheck(FactoryDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Factory database unreachable.");
    }
}
