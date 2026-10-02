using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Infrastructure.Persistence;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Creators.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "onvoyage";

    public static IServiceCollection AddCreatorsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is missing.");

        services.AddDbContextWithWolverineIntegration<CreatorsDbContext>(options => CreatorsPersistence.Configure(options, connectionString));
        services.AddScoped<ICreatorsUnitOfWork, CreatorsUnitOfWork>();
        services.AddScoped<ICreatorRepository, CreatorRepository>();
        services.AddScoped<IContentRepository, ContentRepository>();
        services.AddScoped<IModerationRepository, ModerationRepository>();
        services.AddScoped<IFollowRepository, FollowRepository>();
        services.AddScoped<ICreatorQueries, CreatorQueries>();
        services.AddScoped<PoiDirectory>();
        services.AddScoped<IPoiDirectory>(provider => provider.GetRequiredService<PoiDirectory>());
        services.AddScoped<IPoiDirectoryWriter>(provider => provider.GetRequiredService<PoiDirectory>());
        services.AddScoped<IDataRightsStore, DataRightsStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks().AddCheck<CreatorsDatabaseHealthCheck>("creators-db", tags: ["ready"]);
        return services;
    }

    public static async Task InitializeCreatorsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Creators:Migrate", true))
        {
            await scope.ServiceProvider.GetRequiredService<CreatorsDbContext>().Database.MigrateAsync(cancellationToken);
        }
    }

    private sealed class CreatorsDatabaseHealthCheck(CreatorsDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Creators database unreachable.");
    }
}
