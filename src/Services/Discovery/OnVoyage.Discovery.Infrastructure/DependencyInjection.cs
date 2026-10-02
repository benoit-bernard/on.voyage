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
        services.AddScoped<ICreatorProjectionWriter, CreatorProjectionWriter>();
        services.AddScoped<ICreatorReader, CreatorReader>();
        services.AddScoped<OnVoyage.Discovery.Application.Features.IDataRightsStore, DataRightsStore>();
        services.AddScoped<ITravelerReader, TravelerReader>();
        services.AddScoped<IAffinityStore, AffinityStore>();
        services.AddHostedService<CategoryAffinityJob>();
        services.AddScoped<OnVoyage.Discovery.Application.Features.ICollaborativeStore, CollaborativeStore>();
        services.AddScoped<OnVoyage.Discovery.Application.Features.INeighborSearch, ExactCosineNeighborSearch>();
        services.AddSingleton(new OnVoyage.Discovery.Application.Features.CfOptions(
            Neighbors: configuration.GetValue("Discovery:Cf:Neighbors", 50),
            MinNeighborDepth: configuration.GetValue("Discovery:Cf:MinNeighborDepth", 10),
            ActiveDays: configuration.GetValue("Discovery:Cf:ActiveDays", 365),
            Lambda: configuration.GetValue("Discovery:Cf:Lambda", 5d),
            MaxScoresPerTraveler: configuration.GetValue("Discovery:Cf:MaxScoresPerTraveler", 200),
            MinSupport: configuration.GetValue("Discovery:Cf:MinSupport", 3),
            MinPool: configuration.GetValue("Discovery:Cf:MinPool", 20),
            TargetMinDepth: configuration.GetValue("Discovery:Cf:TargetMinDepth", 5)));
        services.AddHostedService<CollaborativeScoresJob>();
        services.AddSingleton<IMediaUrls, ConfiguredMediaUrls>();
        services.AddSingleton(new OnVoyage.Discovery.Application.DiscoveryOptions(configuration.GetValue("Discovery:AllowTextOnlyStories", true)));
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

    /// <summary>
    /// The job of §6.5: recomputes the collaborative scores every <c>Discovery:Cf:IntervalHours</c> hours (6), the first time
    /// <c>Discovery:Cf:StartupDelayMinutes</c> minutes (2) after the start. <c>Discovery:Cf:Enabled</c> = false stops it (the endpoint still works).
    /// It goes through the Wolverine bus like any other command.
    /// </summary>
    private sealed class CollaborativeScoresJob(IServiceProvider services, IConfiguration configuration, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<CollaborativeScoresJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!configuration.GetValue("Discovery:Cf:Enabled", true))
            {
                return;
            }

            var interval = TimeSpan.FromHours(configuration.GetValue("Discovery:Cf:IntervalHours", 6d));
            var delay = TimeSpan.FromMinutes(configuration.GetValue("Discovery:Cf:StartupDelayMinutes", 2d));
            try
            {
                // The message bus is not usable before every hosted service has started, whatever the delay.
                var started = new TaskCompletionSource();
                await using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
                await using (stoppingToken.Register(() => started.TrySetCanceled(stoppingToken)))
                {
                    await started.Task;
                }

                await Task.Delay(delay, clock, stoppingToken);
                using var timer = new PeriodicTimer(interval, clock);
                do
                {
                    await RunOnceAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }

        private async Task RunOnceAsync(CancellationToken stoppingToken)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<Wolverine.IMessageBus>();
                await bus.InvokeAsync<OnVoyage.Discovery.Application.Result<OnVoyage.Discovery.Application.Features.CfRunSummary>>(new OnVoyage.Discovery.Application.Features.RecomputeCfScoresCommand(), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The collaborative filtering job failed; it will try again at the next interval.");
            }
        }
    }

    private sealed class DiscoveryDatabaseHealthCheck(DiscoveryDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Discovery database unreachable.");
    }
}
