using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Domain;
using OnVoyage.Platform.Contracts;
using OnVoyage.ServiceDefaults.Configuration;
using OnVoyage.ServiceDefaults.Exports;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Insights.Infrastructure.Persistence;

internal sealed class ConsentProjectionWriter(InsightsDbContext db) : IConsentProjectionWriter
{
    public async Task ApplyAsync(Guid travelerId, bool analytics, DateTimeOffset changedAt, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into insights.consent_projection (traveler_id, analytics, updated_at)
            values ({travelerId}, {analytics}, {changedAt.ToUniversalTime()})
            on conflict (traveler_id) do update
                set analytics = excluded.analytics, updated_at = excluded.updated_at
                where insights.consent_projection.updated_at < excluded.updated_at
            """, cancellationToken);
}

internal sealed class ConfigSnapshotStore(InsightsDbContext db, IMemoryCache cache, TimeProvider clock) : IConfigSnapshotStore
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

/// <summary>Read side of the configuration projection, cached for a short time and invalidated when a change is applied in this process.</summary>
internal sealed class DbConfigSnapshot(IServiceScopeFactory scopes, IMemoryCache cache) : IConfigSnapshot
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static string CacheKey(string key) => $"insights-config-snapshot:{key}";

    public async ValueTask<string?> GetJsonAsync(string key, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(CacheKey(key), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Ttl;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<InsightsDbContext>();
            return await db.ConfigSnapshot.AsNoTracking().Where(row => row.Key == key).Select(row => row.Value).FirstOrDefaultAsync(cancellationToken);
        });
}

/// <summary>The ⚙️ values of this service (annexe E), with the annexe's defaults until Platform's projection has them.</summary>
internal sealed class ConfiguredInsightsSettings(IConfigSnapshot snapshot, IConfiguration configuration) : IInsightsSettings
{
    public async Task<InsightsSettings> GetAsync(CancellationToken cancellationToken)
    {
        var defaults = new InsightsSettings();
        return new InsightsSettings(
            await IntAsync("retention.analytics_raw_months", defaults.RawMonths, cancellationToken),
            await IntAsync("retention.technical_events_days", defaults.TechnicalDays, cancellationToken),
            await IntAsync("sync.max_events_per_batch", defaults.MaxEventsPerBatch, cancellationToken),
            Math.Max(1, configuration.GetValue("Insights:Kpi:RecomputeDays", defaults.KpiRecomputeDays)));
    }

    private async Task<int> IntAsync(string path, int fallback, CancellationToken cancellationToken) =>
        int.TryParse(await snapshot.GetStringAsync(path, cancellationToken), out var value) && value > 0 ? value : fallback;
}

internal sealed class MaintenanceStore(InsightsDbContext db, PartitionCatalog partitions) : IMaintenanceStore
{
    public Task<IReadOnlyList<PartitionMonth>> ListPartitionsAsync(CancellationToken cancellationToken) => partitions.ListAsync(cancellationToken);

    public Task<IReadOnlyList<PartitionMonth>> EnsurePartitionsAsync(IReadOnlyList<PartitionMonth> months, CancellationToken cancellationToken) =>
        partitions.EnsureAsync(months, cancellationToken);

    public Task DropPartitionAsync(PartitionMonth month, CancellationToken cancellationToken) => partitions.DropAsync(month, cancellationToken);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, IReadOnlyCollection<string>? onlyNames, CancellationToken cancellationToken)
    {
        var moment = cutoff.ToUniversalTime();
        var events = db.Events.Where(row => row.OccurredAt < moment);
        if (onlyNames is not null)
        {
            var names = onlyNames.ToArray();
            events = events.Where(row => names.Contains(row.Name));
        }

        return events.ExecuteDeleteAsync(cancellationToken);
    }
}

internal sealed class TravelerDataStore(IDbContextOutbox<InsightsDbContext> outbox) : ITravelerDataStore
{
    private InsightsDbContext Db => outbox.DbContext;

    public async Task DeleteAsync(Guid travelerId, TravelerDataDeletedV1 confirmation, CancellationToken cancellationToken)
    {
        // The deletion and the confirmation to Platform commit together, so a confirmation is never sent for data that is still there.
        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        await Db.Events.Where(row => row.TravelerRef == travelerId).ExecuteDeleteAsync(cancellationToken);
        await Db.Consents.Where(row => row.TravelerId == travelerId).ExecuteDeleteAsync(cancellationToken);
        await outbox.PublishAsync(confirmation);

        // Commits the transaction opened above (the outbox row is written in it) and then sends the message.
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task<TravelerData> ReadAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var consent = await Db.Consents.AsNoTracking().FirstOrDefaultAsync(row => row.TravelerId == travelerId, cancellationToken);
        var events = await Db.Events.AsNoTracking()
            .Where(row => row.TravelerRef == travelerId)
            .OrderBy(row => row.OccurredAt)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

        return new TravelerData(
            consent?.Analytics,
            consent?.UpdatedAt,
            [.. events.Select(row => new StoredEvent(row.Id, row.SessionId, row.Name, row.Props, row.AppVersion, row.Platform, row.OccurredAt))]);
    }
}

/// <summary>Writes export parts in the shared private exports area (<see cref="ExportStorage"/>).</summary>
internal sealed class FileExportPartWriter(ExportStorage storage) : IExportPartWriter
{
    public Task<string> WriteAsync(Guid exportId, string json, CancellationToken cancellationToken) =>
        storage.WriteAsync(exportId, "insights", json, cancellationToken);
}
