using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Infrastructure.Persistence;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Discovery.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddDiscoveryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<DiscoveryDbContext>(options => DiscoveryPersistence.Configure(options, connectionString));
        services.AddScoped<IDiscoveryStore, DiscoveryStore>();
        services.AddScoped<IOnboardingStore, OnboardingStore>();
        services.AddScoped<IProjectionWriter, ProjectionWriter>();
        services.AddSingleton<IMediaUrls, ConfiguredMediaUrls>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<DiscoveryDatabaseHealthCheck>("discovery-db", tags: ["ready"]);
        return services;
    }

    public static async Task InitializeDiscoveryAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Discovery:Migrate", true))
        {
            await scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>().Database.MigrateAsync(cancellationToken);
        }
    }

    private sealed class DiscoveryDatabaseHealthCheck(DiscoveryDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Discovery database unreachable.");
    }
}
