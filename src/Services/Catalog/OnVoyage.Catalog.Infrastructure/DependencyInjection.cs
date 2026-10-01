using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.Catalog.Infrastructure.Persistence;
using OnVoyage.ServiceDefaults.Configuration;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Catalog.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<CatalogDbContext>(options => CatalogPersistence.Configure(options, connectionString));
        services.AddMemoryCache();
        services.AddScoped<IConfigSnapshotStore, ConfigSnapshotStore>();
        services.AddSingleton<IConfigSnapshot, DbConfigSnapshot>();
        services.AddScoped<IPoiReader, PostgisPoiReader>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<CatalogDatabaseHealthCheck>("catalog-db", tags: ["ready"]);
        return services;
    }

    /// <summary>Applies pending migrations, then seeds the taxonomy (and the Marseille demo data when <c>Catalog:SeedDemoData</c> is true).</summary>
    public static async Task InitializeCatalogAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        if (config.GetValue("Catalog:Migrate", true))
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        await CatalogSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>(), config.GetValue("Catalog:SeedDemoData", false), cancellationToken);
    }

    private sealed class CatalogDatabaseHealthCheck(CatalogDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Catalog database unreachable.");
    }
}
