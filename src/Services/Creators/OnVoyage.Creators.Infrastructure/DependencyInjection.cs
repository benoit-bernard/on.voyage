using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OnVoyage.Creators.Application.Features;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;
using OnVoyage.Creators.Infrastructure.Persistence;
using OnVoyage.Creators.Infrastructure.Social;
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
        services.AddScoped<IConnectedAccountRepository, ConnectedAccountRepository>();
        services.AddScoped<ICreatorQueries, CreatorQueries>();
        services.AddScoped<PoiDirectory>();
        services.AddScoped<IPoiDirectory>(provider => provider.GetRequiredService<PoiDirectory>());
        services.AddScoped<IPoiDirectoryWriter>(provider => provider.GetRequiredService<PoiDirectory>());
        services.AddScoped<IDataRightsStore, DataRightsStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICreatorTerms>(new ConfiguredCreatorTerms(configuration["Creators:Terms:CurrentVersion"] is { Length: > 0 } version ? version : "2026-10"));
        AddSocial(services, configuration);
        services.AddHealthChecks().AddCheck<CreatorsDatabaseHealthCheck>("creators-db", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Connected accounts and imports (F-27): off until a platform is switched on (<c>Creators:Social:&lt;Platform&gt;:Enabled</c>, H-008). Tokens are encrypted
    /// by Data Protection; with a live platform the key ring must be persisted, otherwise a restart would make every stored token unreadable.
    /// </summary>
    private static void AddSocial(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SocialOptions.Section);
        var social = section.Get<SocialOptions>() ?? new SocialOptions();

        var keys = configuration["Creators:DataProtection:KeysDirectory"];
        var dataProtection = services.AddDataProtection().SetApplicationName("onvoyage-creators");
        if (!string.IsNullOrWhiteSpace(keys))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }
        else if (social.Provider == "live" && (social.Instagram.Enabled || social.YouTube.Enabled))
        {
            throw new InvalidOperationException("Creators:DataProtection:KeysDirectory is required to enable an import: the OAuth tokens are encrypted with keys that must survive a restart.");
        }

        if (configuration["Creators:DataProtection:CertificatePath"] is { Length: > 0 } certificate)
        {
            dataProtection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certificate, configuration["Creators:DataProtection:CertificatePassword"]));
        }

        services.AddSingleton<FakeSocialWorld>();
        services.AddHttpClient("social-instagram").RemoveAllLoggers(); // some URLs of the platform API carry a token: never log them
        services.AddHttpClient("social-youtube").RemoveAllLoggers();
        services.AddScoped<ISocialProvider>(provider => social.Provider == "fake"
            ? new FakeSocialProvider(ContentPlatforms.Instagram, social.Instagram, provider.GetRequiredService<FakeSocialWorld>(), provider.GetRequiredService<TimeProvider>())
            : new InstagramProvider(provider.GetRequiredService<IHttpClientFactory>().CreateClient("social-instagram"), social.Instagram, provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<ISocialProvider>(provider => social.Provider == "fake"
            ? new FakeSocialProvider(ContentPlatforms.YouTube, social.YouTube, provider.GetRequiredService<FakeSocialWorld>(), provider.GetRequiredService<TimeProvider>())
            : new YouTubeProvider(provider.GetRequiredService<IHttpClientFactory>().CreateClient("social-youtube"), social.YouTube, provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<ISocialConnector, SocialConnector>();
        services.AddHttpClient<IThumbnailStore, ThumbnailStore>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton(Options.Create(social));
    }

    public static async Task InitializeCreatorsAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue("Creators:Migrate", true))
        {
            await scope.ServiceProvider.GetRequiredService<CreatorsDbContext>().Database.MigrateAsync(cancellationToken);
        }
    }

    private sealed record ConfiguredCreatorTerms(string CurrentVersion) : ICreatorTerms;

    private sealed class CreatorsDatabaseHealthCheck(CreatorsDbContext db) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Creators database unreachable.");
    }
}
