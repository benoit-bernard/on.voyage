using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Catalog.Application.Ports;
using OnVoyage.ServiceDefaults.Configuration;

namespace OnVoyage.Catalog.Infrastructure.Persistence;

internal sealed class ConfigSnapshotStore(CatalogDbContext db, IMemoryCache cache, TimeProvider clock) : IConfigSnapshotStore
{
    public async Task<bool> ApplyAsync(string key, string valueJson, int version, CancellationToken cancellationToken)
    {
        var row = await db.ConfigSnapshot.FirstOrDefaultAsync(entry => entry.Key == key, cancellationToken);
        if (row is not null && row.Version >= version)
        {
            return false;
        }

        if (row is null)
        {
            row = new ConfigSnapshotRow { Key = key };
            db.ConfigSnapshot.Add(row);
        }

        row.Value = valueJson;
        row.Version = version;
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(DbConfigSnapshot.CacheKey(key));
        return true;
    }
}

/// <summary>Read side used by the version gate. Cached for a short time and invalidated when a change is applied in this process.</summary>
internal sealed class DbConfigSnapshot(IServiceScopeFactory scopes, IMemoryCache cache) : IConfigSnapshot
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static string CacheKey(string key) => $"config-snapshot:{key}";

    public async ValueTask<string?> GetJsonAsync(string key, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(CacheKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            return await db.ConfigSnapshot.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).FirstOrDefaultAsync(cancellationToken);
        });
}
