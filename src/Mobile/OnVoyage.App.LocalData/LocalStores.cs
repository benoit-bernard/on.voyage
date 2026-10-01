using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Discovery;
using OnVoyage.App.LocalData.Sync;

namespace OnVoyage.App.LocalData;

/// <summary>The 30-day repeat delay survives a restart because the told dates are in <c>user.db</c>.</summary>
internal sealed class DbTellHistoryStore(IDbContextFactory<UserDbContext> factory) : ITellHistoryStore
{
    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Told.AsNoTracking().ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.PoiId, r => r.At);
    }

    public async Task MarkAsync(Guid poiId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Told.FindAsync([poiId], cancellationToken);
        if (row is null)
        {
            db.Told.Add(new TellEntry { PoiId = poiId, At = at });
        }
        else
        {
            row.At = at;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A detected stay is kept locally and queued for the profile's "visited" state. The event carries the place and the stay, no coordinates.</summary>
internal sealed class DbVisitSink(IDbContextFactory<UserDbContext> factory, SyncOutbox queue) : IVisitSink
{
    public async Task RecordAsync(Visit visit, CancellationToken cancellationToken)
    {
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            db.Visits.Add(new VisitEntry { PoiId = visit.PoiId, StartedAt = visit.StartedAt, DwellSeconds = (int)visit.Dwell.TotalSeconds, Confidence = visit.Confidence });
            await db.SaveChangesAsync(cancellationToken);
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            poi_id = visit.PoiId,
            started_at = visit.StartedAt,
            dwell_s = (int)visit.Dwell.TotalSeconds,
            confidence = Math.Round(visit.Confidence, 2),
        });
        await queue.EnqueueAsync("visit", payload, cancellationToken: cancellationToken);
    }
}

/// <summary>Booleans such as "the AI voice notice was heard", kept in <see cref="Setting"/>.</summary>
internal sealed class DbFlagStore(IDbContextFactory<UserDbContext> factory) : IFlagStore
{
    private static string KeyOf(string key) => "flag:" + key;

    public async Task<bool> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyOf(key), cancellationToken);
        return row is not null && bool.Parse(row.Value);
    }

    public async Task SetAsync(string key, bool value, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Settings.FindAsync([KeyOf(key)], cancellationToken);
        var text = value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = KeyOf(key), Value = text });
        }
        else
        {
            row.Value = text;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
