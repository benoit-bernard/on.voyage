using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.LocalData.Sync;

namespace OnVoyage.App.LocalData;

public static class DependencyInjection
{
    /// <summary>
    /// Opens <c>user.db</c> at <paramref name="databasePath"/> and replaces the in-memory stores of the core with database-backed ones.
    /// The caller (the MAUI host, not the PWA) registers an <see cref="ISyncTransport"/>.
    /// </summary>
    public static IServiceCollection AddLocalData(this IServiceCollection services, string databasePath)
    {
        services.AddDbContextFactory<UserDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISyncTransport, HoldingSyncTransport>();
        services.AddSingleton<SyncOutbox>(provider => new SyncOutbox(provider.GetRequiredService<IDbContextFactory<UserDbContext>>(), provider.GetRequiredService<ISyncTransport>(), provider.GetRequiredService<TimeProvider>()));
        services.Replace(ServiceDescriptor.Singleton<ITellHistoryStore, DbTellHistoryStore>());
        services.Replace(ServiceDescriptor.Singleton<IVisitSink, DbVisitSink>());
        services.Replace(ServiceDescriptor.Singleton<IFlagStore, DbFlagStore>());
        return services;
    }

    /// <summary>Creates the file and tables when absent. Called once at start-up.</summary>
    public static async Task InitializeLocalDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<UserDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
