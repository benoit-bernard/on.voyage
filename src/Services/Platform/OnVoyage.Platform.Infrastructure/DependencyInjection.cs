using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Infrastructure.Identity;
using OnVoyage.Platform.Infrastructure.Persistence;
using Wolverine;
using OnVoyage.Platform.Application.Features.DataRights;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<PlatformDbContext>(options => PlatformPersistence.Configure(options, connectionString));
        services.AddScoped<IRemoteConfigStore, RemoteConfigStore>();
        services.AddScoped<IFeatureFlagStore, FeatureFlagStore>();
        services.AddScoped<IConsentStore, ConsentStore>();
        services.AddScoped<OnVoyage.Platform.Application.Features.DataRights.IDataRightsStore, DataRightsStore>();
        services.AddSingleton<OnVoyage.Platform.Application.Features.DataRights.IExportFiles, ExportFiles>();
        services.AddHostedService<DataRightsJob>();
        services.AddScoped<OnVoyage.Platform.Application.Features.Audit.IAdminAuditStore, AdminAuditStore>();
        services.AddScoped<IAccountStore, AccountStore>();
        services.AddScoped<IOtpStore, OtpStore>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddScoped<IAuthSettingsProvider, AuthSettingsProvider>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<ICredentialService, CredentialService>();
        AddEmailSender(services, configuration, environment);
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<PlatformDatabaseHealthCheck>("platform-db", tags: ["ready"]);
        return services;
    }

    private static void AddEmailSender(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        switch (configuration["Email:Provider"]?.ToLowerInvariant())
        {
            case "resend":
                services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
                {
                    client.BaseAddress = new Uri("https://api.resend.com/");
                    client.Timeout = TimeSpan.FromSeconds(10);
                });
                break;
            case "log" when environment.IsDevelopment():
                services.AddSingleton<IEmailSender, LogEmailSender>();
                break;
            case "log":
                throw new InvalidOperationException("Email:Provider=log prints sign-in codes and is only allowed in Development.");
            default:
                throw new InvalidOperationException("Email:Provider must be 'resend' (or 'log' in Development).");
        }
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

    /// <summary>Hourly housekeeping: expired exports go (rows and files), and anonymous accounts unused for 24 months start their deletion (§16.2).</summary>
    private sealed class DataRightsJob(IServiceProvider services, IConfiguration configuration, TimeProvider clock, ILogger<DataRightsJob> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!configuration.GetValue("Platform:DataRightsJob", true))
            {
                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(configuration.GetValue("Platform:DataRightsJobMinutes", 60)), clock);
            do
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                    await bus.InvokeAsync<int>(new CleanUpExportsCommand(), stoppingToken);
                    await bus.InvokeAsync<int>(new PurgeInactiveAnonymousAccountsCommand(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The data rights housekeeping failed; it will run again.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    private sealed class PlatformDatabaseHealthCheck(PlatformDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Platform database unreachable.");
    }
}
