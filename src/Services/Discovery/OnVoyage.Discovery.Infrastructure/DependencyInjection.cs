using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        services.AddScoped<IPlaceReader, PlaceReader>();
        services.AddScoped<ITravelerReader, TravelerReader>();
        services.AddScoped<IAffinityStore, AffinityStore>();
        services.AddHostedService<CategoryAffinityJob>();
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

    /// <summary>The nightly job of §6.10: recomputes the co-appreciation matrix once a day (03:00 UTC by default, <c>Discovery:CategoryAffinityHourUtc</c>).</summary>
    private sealed class CategoryAffinityJob(IServiceProvider services, IConfiguration configuration, TimeProvider clock, Microsoft.Extensions.Logging.ILogger<CategoryAffinityJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var hour = configuration.GetValue("Discovery:CategoryAffinityHourUtc", 3);
            DateOnly? lastRun = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = clock.GetUtcNow();
                if (now.Hour < hour || lastRun == DateOnly.FromDateTime(now.UtcDateTime))
                {
                    continue;
                }

                try
                {
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<IAffinityStore>().RecomputeAsync(now, stoppingToken);
                    lastRun = DateOnly.FromDateTime(now.UtcDateTime);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The category_affinity job failed; it will try again at the next tick.");
                }
            }
        }
    }

    private sealed class DiscoveryDatabaseHealthCheck(DiscoveryDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Discovery database unreachable.");
    }
}
