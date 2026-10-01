using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Infrastructure.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<PlatformDbContext>(options => PlatformPersistence.Configure(options, connectionString));
        services.AddScoped<IRemoteConfigStore, RemoteConfigStore>();
        services.AddScoped<IFeatureFlagStore, FeatureFlagStore>();
        services.AddScoped<IConsentStore, ConsentStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<PlatformDatabaseHealthCheck>("platform-db", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Registers the startup task that migrates and seeds. Call it after <c>UseWolverine</c>: the seed publishes events through the
    /// outbox, so Wolverine must already be running, and hosted services start in registration order (before Kestrel accepts requests).
    /// </summary>
    public static IServiceCollection AddPlatformInitializer(this IServiceCollection services) =>
        services.AddHostedService<PlatformInitializer>();

    private sealed class PlatformInitializer(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<PlatformInitializer> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var scope = services.CreateAsyncScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();

            if (config.GetValue("Platform:Migrate", true))
            {
                await db.Database.MigrateAsync(cancellationToken);
            }

            if (config.GetValue("Platform:SeedDefaults", true))
            {
                await PlatformSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<TimeProvider>(), cancellationToken);
            }

            if (config.GetValue("Platform:RepublishOnStart", true))
            {
                // Once the host (and so Wolverine's sending agents) is fully up.
                lifetime.ApplicationStarted.Register(() => _ = RepublishAsync());
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private async Task RepublishAsync()
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await PlatformSeeder.RepublishAsync(
                    scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IMessageBus>(),
                    scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Republishing the configuration at startup failed; services will catch up on the next change.");
            }
        }
    }

    private sealed class PlatformDatabaseHealthCheck(PlatformDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Platform database unreachable.");
    }
}
