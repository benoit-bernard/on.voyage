using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Recommendation.Engine;
using OnVoyage.Recommendation.Engine.Learning;
using OnVoyage.Taxonomy;

namespace OnVoyage.Discovery.Infrastructure.Persistence;

internal sealed class DiscoveryStore(DiscoveryDbContext db, TimeProvider clock) : IDiscoveryStore
{
    private static readonly Dictionary<string, int> Index = Interests.All.Select((code, i) => (code, i)).ToDictionary(x => x.code, x => x.i);

    public async Task<T> ExclusiveAsync<T>(Guid travelerId, Func<ITravelerSession, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var cohort = ControlCohort.Contains(travelerId) ? "control" : "personalized";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO discovery.traveler (id, lang, ethical_mode, is_premium, profile_depth, cohort, last_active_at, created_at)
            VALUES ({travelerId}, 'fr', 'balanced', false, 0, {cohort}, {now}, {now})
            ON CONFLICT (id) DO UPDATE SET last_active_at = excluded.last_active_at
            """, cancellationToken);

        // One batch at a time per traveler: the vector is a replay of the history, so two interleaved batches would overwrite each other.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({travelerId.ToString("N")}, 0))", cancellationToken);

        var result = await work(new Session(db, travelerId, now));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<StoredProfile?> GetProfileAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var traveler = await db.Travelers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == travelerId, cancellationToken);
        if (traveler is null)
        {
            return null;
        }

        var row = await db.Vectors.AsNoTracking().FirstOrDefaultAsync(v => v.TravelerId == travelerId, cancellationToken);
        var excluded = await db.Ratings.AsNoTracking().Where(r => r.TravelerId == travelerId && r.Excluded).Select(r => r.PoiId.ToString()).ToListAsync(cancellationToken);
        var locks = row is null ? Locks.None : ParseLocks(row.Locks);
        var vector = row is null ? new Dictionary<string, double>() : Decode(row.Vector);
        return new StoredProfile(new LearnedProfile(vector, new HashSet<string>(excluded), new Dictionary<string, double>(), traveler.ProfileDepth, null), locks, traveler.Cohort, row?.TaxonomyVersion ?? Interests.Version);
    }

    internal static Dictionary<string, double> Decode(float[] vector)
    {
        var result = new Dictionary<string, double>();
        foreach (var (code, i) in Index)
        {
            if (i < vector.Length && vector[i] != 0f)
            {
                result[code] = vector[i];
            }
        }

        return result;
    }

    internal static float[] Encode(IReadOnlyDictionary<string, double> vector)
    {
        var result = new float[Interests.All.Count];
        foreach (var (code, value) in vector)
        {
            if (Index.TryGetValue(code, out var i))
            {
                result[i] = (float)value;
            }
        }

        return result;
    }

    private sealed record LockEntry(DateTimeOffset Until, double Value);

    internal static Locks ParseLocks(string json)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, LockEntry>>(json) ?? [];
        return new Locks(entries.ToDictionary(e => e.Key, e => e.Value.Until), entries.ToDictionary(e => e.Key, e => e.Value.Value));
    }

    internal static string WriteLocks(Locks locks)
    {
        var codes = locks.Until.Keys.Concat(locks.Pinned.Keys).Distinct();
        // A pinned value outlives its lock (unlocking keeps the value), so entries without a lock end in the past stay.
        return JsonSerializer.Serialize(codes.ToDictionary(c => c, c => new LockEntry(locks.Until.GetValueOrDefault(c, DateTimeOffset.UnixEpoch), locks.Pinned.GetValueOrDefault(c))));
    }

    private sealed class Session(DiscoveryDbContext db, Guid travelerId, DateTimeOffset now) : ITravelerSession
    {
        public async Task<int> AddAsync(IReadOnlyList<IncomingInteraction> items, CancellationToken cancellationToken)
        {
            var ids = items.Select(i => i.Interaction.ClientEventId).ToArray();
            var known = (await db.Interactions.Where(r => r.TravelerId == travelerId && ids.Contains(r.ClientEventId)).Select(r => r.ClientEventId).ToListAsync(cancellationToken))
                .Concat(await db.Impressions.Where(r => r.TravelerId == travelerId && ids.Contains(r.ClientEventId)).Select(r => r.ClientEventId).ToListAsync(cancellationToken))
                .ToHashSet();

            var added = 0;
            foreach (var item in items.Where(i => !known.Contains(i.Interaction.ClientEventId)))
            {
                var interaction = item.Interaction;
                var poi = interaction.PoiId is { } p ? Guid.Parse(p) : (Guid?)null;
                added++;
                if (interaction.Kind == InteractionKinds.Impression)
                {
                    db.Impressions.Add(new ImpressionRow { TravelerId = travelerId, ClientEventId = interaction.ClientEventId, PoiId = poi!.Value, Surface = item.Surface ?? "unknown", ShownAt = interaction.OccurredAt });
                    continue;
                }

                db.Interactions.Add(new InteractionRow
                {
                    TravelerId = travelerId,
                    ClientEventId = interaction.ClientEventId,
                    PoiId = poi,
                    StoryId = item.StoryId,
                    StoryVersion = item.StoryVersion,
                    Kind = interaction.Kind,
                    Value = interaction.Value,
                    CategoryCode = interaction.CategoryCode,
                    OccurredAt = interaction.OccurredAt,
                });

                if (interaction.Kind == InteractionKinds.Visit)
                {
                    db.Visits.Add(new VisitRow
                    {
                        TravelerId = travelerId,
                        ClientEventId = interaction.ClientEventId,
                        PoiId = poi!.Value,
                        VisitedOn = DateOnly.FromDateTime(interaction.OccurredAt.UtcDateTime),
                        DwellS = item.DwellSeconds ?? 0,
                        Confidence = interaction.Value,
                    });
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            return added;
        }

        public async Task<IReadOnlyList<Interaction>> HistoryAsync(CancellationToken cancellationToken)
        {
            var rows = await db.Interactions.AsNoTracking().Where(r => r.TravelerId == travelerId).OrderBy(r => r.OccurredAt).ThenBy(r => r.ClientEventId).ToListAsync(cancellationToken);
            return [.. rows.Select(r => new Interaction(r.ClientEventId, r.Kind, r.PoiId?.ToString("D"), r.OccurredAt, r.Value, r.CategoryCode))];
        }

        public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>> PlaceWeightsAsync(IEnumerable<string> poiIds, CancellationToken cancellationToken)
        {
            var ids = poiIds.Select(Guid.Parse).ToArray();
            var rows = await db.Places.AsNoTracking().Where(p => ids.Contains(p.PoiId)).Select(p => new { p.PoiId, p.Weights }).ToListAsync(cancellationToken);
            return rows.ToDictionary(
                r => r.PoiId.ToString("D"),
                r => (IReadOnlyDictionary<string, double>)(JsonSerializer.Deserialize<Dictionary<string, double>>(r.Weights) ?? []));
        }

        public async Task<Locks> LocksAsync(CancellationToken cancellationToken)
        {
            var row = await db.Vectors.AsNoTracking().FirstOrDefaultAsync(v => v.TravelerId == travelerId, cancellationToken);
            return row is null ? Locks.None : ParseLocks(row.Locks);
        }

        public async Task SaveAsync(LearnedProfile learned, Locks locks, CancellationToken cancellationToken)
        {
            var row = await db.Vectors.FirstOrDefaultAsync(v => v.TravelerId == travelerId, cancellationToken);
            if (row is null)
            {
                row = new InterestVectorRow { TravelerId = travelerId };
                db.Vectors.Add(row);
            }

            row.Vector = Encode(learned.Vector);
            row.TaxonomyVersion = Interests.Version;
            row.Locks = WriteLocks(locks);
            row.UpdatedAt = now;

            (await db.Travelers.FirstAsync(t => t.Id == travelerId, cancellationToken)).ProfileDepth = learned.ProfileDepth;

            var ratings = await db.Ratings.Where(r => r.TravelerId == travelerId).ToDictionaryAsync(r => r.PoiId, cancellationToken);
            foreach (var (poi, rating) in learned.Ratings)
            {
                var id = Guid.Parse(poi);
                if (!ratings.TryGetValue(id, out var rated))
                {
                    rated = new PoiRatingRow { TravelerId = travelerId, PoiId = id };
                    db.Ratings.Add(rated);
                }

                rated.Rating = rating;
                rated.Excluded = learned.Excluded.Contains(poi);
                rated.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
