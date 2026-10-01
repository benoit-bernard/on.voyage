using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;
using Wolverine;

namespace OnVoyage.Platform.Infrastructure.Persistence;

/// <summary>Loads annexe E (default configuration) and the §18 feature flags into an empty Platform.</summary>
internal static class PlatformSeeder
{
    public static async Task SeedAsync(PlatformDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!await db.RemoteConfig.AnyAsync(cancellationToken))
        {
            await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("default-config.json")
                ?? throw new InvalidOperationException("default-config.json is missing.");
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var now = clock.GetUtcNow();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var json = property.Value.GetRawText();
                db.RemoteConfig.Add(new RemoteConfigRow { Key = property.Name, Value = json, Version = 1, UpdatedBy = "seed", UpdatedAt = now });
                db.RemoteConfigHistory.Add(new RemoteConfigHistoryRow { Key = property.Name, Version = 1, Value = json, UpdatedBy = "seed", UpdatedAt = now });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        if (!await db.FeatureFlags.AnyAsync(cancellationToken))
        {
            db.FeatureFlags.AddRange(KnownFeatureFlags.Defaults.Select(flag => new FeatureFlagRow
            {
                Name = flag.Name,
                Enabled = flag.Enabled,
                RolloutPercent = (short)flag.RolloutPercent,
                Platforms = [.. flag.Platforms],
                MinAppVersion = flag.MinAppVersion,
            }));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Publishes the current version of every key so services that missed earlier events (first start, lost queue) catch up.
    /// Consumers apply only newer versions, so republishing is harmless.
    /// </summary>
    public static async Task RepublishAsync(PlatformDbContext db, IMessageBus bus, TimeProvider clock, CancellationToken cancellationToken)
    {
        foreach (var row in await db.RemoteConfig.AsNoTracking().ToListAsync(cancellationToken))
        {
            await bus.PublishAsync(new ConfigChangedV1(Guid.CreateVersion7(), clock.GetUtcNow(), row.Key, row.Value, row.Version));
        }
    }
}
