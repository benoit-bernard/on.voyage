using Microsoft.EntityFrameworkCore;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class RemoteConfigStore(IDbContextOutbox<PlatformDbContext> outbox) : IRemoteConfigStore
{
    private PlatformDbContext Db => outbox.DbContext;

    public async Task<IReadOnlyList<RemoteConfigEntry>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await Db.RemoteConfig.AsNoTracking().ToListAsync(cancellationToken)).Select(ToDomain)];

    public async Task<RemoteConfigEntry?> FindAsync(string key, CancellationToken cancellationToken)
    {
        var row = await Db.RemoteConfig.AsNoTracking().FirstOrDefaultAsync(entry => entry.Key == key, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<RemoteConfigEntry>> HistoryAsync(string key, CancellationToken cancellationToken) =>
        [.. (await Db.RemoteConfigHistory.AsNoTracking().Where(item => item.Key == key).OrderByDescending(item => item.Version).ToListAsync(cancellationToken))
            .Select(item => new RemoteConfigEntry(item.Key, item.Value, item.Version, item.UpdatedBy, item.UpdatedAt))];

    public async Task SaveAsync(RemoteConfigEntry entry, ConfigChangedV1 changed, CancellationToken cancellationToken)
    {
        var row = await Db.RemoteConfig.FirstOrDefaultAsync(existing => existing.Key == entry.Key, cancellationToken);
        if (row is null)
        {
            row = new RemoteConfigRow { Key = entry.Key };
            Db.RemoteConfig.Add(row);
        }

        row.Value = entry.ValueJson;
        row.Version = entry.Version;
        row.UpdatedBy = entry.UpdatedBy;
        row.UpdatedAt = entry.UpdatedAt;
        Db.RemoteConfigHistory.Add(new RemoteConfigHistoryRow { Key = entry.Key, Version = entry.Version, Value = entry.ValueJson, UpdatedBy = entry.UpdatedBy, UpdatedAt = entry.UpdatedAt });

        await outbox.PublishAsync(changed);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private static RemoteConfigEntry ToDomain(RemoteConfigRow row) => new(row.Key, row.Value, row.Version, row.UpdatedBy, row.UpdatedAt);
}

internal sealed class FeatureFlagStore(PlatformDbContext db) : IFeatureFlagStore
{
    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await db.FeatureFlags.AsNoTracking().ToListAsync(cancellationToken)).Select(row => new FeatureFlag(row.Name, row.Enabled, row.RolloutPercent, row.Platforms, row.MinAppVersion))];

    public async Task SaveAsync(FeatureFlag flag, CancellationToken cancellationToken)
    {
        var row = await db.FeatureFlags.FirstOrDefaultAsync(existing => existing.Name == flag.Name, cancellationToken);
        if (row is null)
        {
            row = new FeatureFlagRow { Name = flag.Name };
            db.FeatureFlags.Add(row);
        }

        row.Enabled = flag.Enabled;
        row.RolloutPercent = (short)flag.RolloutPercent;
        row.Platforms = [.. flag.Platforms];
        row.MinAppVersion = flag.MinAppVersion;
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class ConsentStore(IDbContextOutbox<PlatformDbContext> outbox) : IConsentStore
{
    private PlatformDbContext Db => outbox.DbContext;

    public async Task<IReadOnlyList<Consent>> ListAsync(Guid travelerId, CancellationToken cancellationToken) =>
        [.. (await Db.Consents.AsNoTracking().Where(row => row.TravelerId == travelerId).ToListAsync(cancellationToken))
            .Select(row => new Consent(row.TravelerId, row.Kind, row.Granted, row.TextVersion, row.UpdatedAt))];

    public async Task SaveAsync(Consent consent, ConsentChangedV1 changed, CancellationToken cancellationToken)
    {
        var row = await Db.Consents.FirstOrDefaultAsync(existing => existing.TravelerId == consent.TravelerId && existing.Kind == consent.Kind, cancellationToken);
        if (row is null)
        {
            row = new ConsentRow { TravelerId = consent.TravelerId, Kind = consent.Kind };
            Db.Consents.Add(row);
        }

        row.Granted = consent.Granted;
        row.TextVersion = consent.TextVersion;
        row.UpdatedAt = consent.UpdatedAt;

        await outbox.PublishAsync(changed);
        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }
}
