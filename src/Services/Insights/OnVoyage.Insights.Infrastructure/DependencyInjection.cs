using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Domain;
using OnVoyage.Insights.Infrastructure.Persistence;
using OnVoyage.ServiceDefaults.Configuration;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Insights.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddInsightsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<InsightsDbContext>(options => InsightsPersistence.Configure(options, connectionString));
        services.AddMemoryCache();
        services.AddSingleton<KnownPartitions>();
        services.AddScoped<PartitionCatalog>();
        services.AddScoped<IEventStore, EventStore>();
        services.AddScoped<IConsentProjectionWriter, ConsentProjectionWriter>();
        services.AddScoped<IConfigSnapshotStore, ConfigSnapshotStore>();
        services.AddSingleton<IConfigSnapshot, DbConfigSnapshot>();
        services.AddScoped<IInsightsSettings, ConfiguredInsightsSettings>();
        services.AddScoped<IMaintenanceStore, MaintenanceStore>();
        services.AddScoped<IKpiStore, KpiStore>();
        services.AddScoped<ITravelerDataStore, TravelerDataStore>();
        services.AddSingleton<IExportPartWriter, FileExportPartWriter>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<InsightsDatabaseHealthCheck>("insights-db", tags: ["ready"]);
        return services;
    }

    /// <summary>Applies the migrations and creates the partitions of the coming months, so the first batch never waits for the nightly job.</summary>
    public static async Task InitializeInsightsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Insights:Migrate", true))
        {
            await scope.ServiceProvider.GetRequiredService<InsightsDbContext>().Database.MigrateAsync(cancellationToken);
            var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
            await scope.ServiceProvider.GetRequiredService<IMaintenanceStore>().EnsurePartitionsAsync(EventPartitions.Required(clock.GetUtcNow()), cancellationToken);
        }
    }

    private sealed class InsightsDatabaseHealthCheck(InsightsDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Insights database unreachable.");
    }
}
