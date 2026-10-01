using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Domain;

namespace OnVoyage.Insights.Infrastructure.Persistence;

/// <summary>Partitions this process knows exist, so the common path does not ask the catalog at every batch.</summary>
internal sealed class KnownPartitions
{
    private readonly ConcurrentDictionary<PartitionMonth, byte> _known = new();

    public bool Contains(PartitionMonth month) => _known.ContainsKey(month);

    public void Add(PartitionMonth month) => _known[month] = 0;

    public void Remove(PartitionMonth month) => _known.TryRemove(month, out _);
}

/// <summary>Creates and lists the monthly partitions of <c>insights.event</c>. Names and bounds come from <see cref="PartitionMonth"/>, never from input.</summary>
internal sealed class PartitionCatalog(InsightsDbContext db, KnownPartitions known)
{
    private const string DuplicateTable = "42P07";

    public async Task<IReadOnlyList<PartitionMonth>> ListAsync(CancellationToken cancellationToken)
    {
        var names = await db.Database.SqlQuery<string>($"""
            select c.relname as "Value"
            from pg_inherits i
            join pg_class c on c.oid = i.inhrelid
            join pg_class p on p.oid = i.inhparent
            join pg_namespace n on n.oid = p.relnamespace
            where n.nspname = 'insights' and p.relname = 'event'
            """).ToListAsync(cancellationToken);
        return [.. names.Select(name => PartitionMonth.TryParse(name, out var month) ? (PartitionMonth?)month : null).OfType<PartitionMonth>()];
    }

    /// <returns>The partitions that were created by this call.</returns>
    public async Task<IReadOnlyList<PartitionMonth>> EnsureAsync(IEnumerable<PartitionMonth> months, CancellationToken cancellationToken)
    {
        var wanted = months.Distinct().Where(month => !known.Contains(month)).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var existing = (await ListAsync(cancellationToken)).ToHashSet();
        var created = new List<PartitionMonth>();
        foreach (var month in wanted)
        {
            if (!existing.Contains(month))
            {
                try
                {
#pragma warning disable EF1002 // The statement is built from integers only (see CreateStatement).
                    await db.Database.ExecuteSqlRawAsync(CreateStatement(month), cancellationToken);
#pragma warning restore EF1002
                    created.Add(month);
                }
                catch (PostgresException ex) when (ex.SqlState == DuplicateTable)
                {
                    // Another instance created it between our check and our statement.
                }
            }

            known.Add(month);
        }

        return created;
    }

    public async Task DropAsync(PartitionMonth month, CancellationToken cancellationToken)
    {
#pragma warning disable EF1002 // The name is built from two integers (PartitionMonth.TableName).
        await db.Database.ExecuteSqlRawAsync($"drop table if exists insights.{month.TableName}", cancellationToken);
#pragma warning restore EF1002
        known.Remove(month);
    }

    internal static string CreateStatement(PartitionMonth month) =>
        $"create table if not exists insights.{month.TableName} partition of insights.event for values from ('{month.Start:yyyy-MM-dd}T00:00:00Z') to ('{month.End:yyyy-MM-dd}T00:00:00Z')";
}

internal sealed class EventStore(InsightsDbContext db, PartitionCatalog partitions) : IEventStore
{
    private const string InsertSql = """
        insert into insights.event (id, occurred_at, traveler_ref, session_id, name, props, app_version, platform)
        select u.id, u.occurred_at, u.traveler_ref, u.session_id, u.name, u.props::jsonb, u.app_version, u.platform
        from unnest(@ids, @times, @travelers, @sessions, @names, @props, @versions, @platforms)
            as u(id, occurred_at, traveler_ref, session_id, name, props, app_version, platform)
        on conflict (id, occurred_at) do nothing
        """;

    public Task<bool> HasAnalyticsConsentAsync(Guid travelerId, CancellationToken cancellationToken) =>
        db.Consents.AsNoTracking().AnyAsync(row => row.TravelerId == travelerId && row.Analytics, cancellationToken);

    public async Task<int> InsertAsync(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken)
    {
        await partitions.EnsureAsync(events.Select(e => PartitionMonth.Of(e.OccurredAt)), cancellationToken);

        // One statement for the whole batch. A retry (same id, same moment) is a no-op, which makes the endpoint idempotent.
        return await db.Database.ExecuteSqlRawAsync(
            InsertSql,
            [
                Array("ids", NpgsqlDbType.Uuid, events.Select(e => e.Id)),
                Array("times", NpgsqlDbType.TimestampTz, events.Select(e => e.OccurredAt.UtcDateTime)),
                Array("travelers", NpgsqlDbType.Uuid, events.Select(e => e.TravelerId)),
                Array("sessions", NpgsqlDbType.Uuid, events.Select(e => e.SessionId)),
                Array("names", NpgsqlDbType.Text, events.Select(e => e.Name)),
                Array("props", NpgsqlDbType.Text, events.Select(e => e.PropsJson)),
                Array("versions", NpgsqlDbType.Text, events.Select(e => e.AppVersion)),
                Array("platforms", NpgsqlDbType.Text, events.Select(e => e.Platform)),
            ],
            cancellationToken);
    }

    internal static NpgsqlParameter Array<T>(string name, NpgsqlDbType element, IEnumerable<T> values) =>
        new(name, NpgsqlDbType.Array | element) { Value = values.ToArray() };
}
