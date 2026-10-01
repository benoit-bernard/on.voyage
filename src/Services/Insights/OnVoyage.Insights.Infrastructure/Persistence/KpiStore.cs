using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Contracts;
using OnVoyage.Insights.Domain;

namespace OnVoyage.Insights.Infrastructure.Persistence;

/// <summary>
/// Builds <c>insights.daily_kpi</c> (§11.4, §26) from the stored events, one set-based pass per component, inside one transaction. The days
/// asked for are deleted and rebuilt, so the job can run as often as wanted and a late event just shows up at the next run. Only travelers
/// who accepted the statistics have non-essential events in the table, so the indicators count them and nobody else.
/// </summary>
internal sealed class KpiStore(InsightsDbContext db) : IKpiStore
{
    private const string AllDestinations = "";
    private const long AdvisoryLockKey = 7_482_011;

    // Cohort of a traveler: the one seen in their recommendation events (the app writes it there, §17.3). Unknown travelers are "unassigned".
    private const string CreateCohorts = """
        create temp table kpi_cohort on commit drop as
        select traveler_ref, (array_agg(props->>'cohort' order by occurred_at desc))[1] as cohort
        from insights.event
        where name in ('recommendation_viewed', 'recommendation_clicked') and props->>'cohort' in ('control', 'personalized')
        group by traveler_ref
        """;

    private const string CreateDayEvents = """
        create temp table kpi_event on commit drop as
        select e.name, e.session_id, e.props,
               (e.occurred_at at time zone 'UTC')::date as day,
               coalesce(e.props->>'cohort', c.cohort, 'unassigned') as cohort
        from insights.event e
        left join kpi_cohort c on c.traveler_ref = e.traveler_ref
        where e.occurred_at >= @start and e.occurred_at < @end
        """;

    // Installs: the first day a traveler produced a non-essential event, kept when it falls in the period.
    private const string CreateInstalls = """
        create temp table kpi_install on commit drop as
        select f.traveler_ref, f.first_day as day, coalesce(c.cohort, 'unassigned') as cohort
        from (select traveler_ref, min(occurred_at at time zone 'UTC')::date as first_day
              from insights.event where name <> all(@essential) group by traveler_ref) f
        left join kpi_cohort c on c.traveler_ref = f.traveler_ref
        where f.first_day >= @from and f.first_day <= @to
        """;

    private const string Insert = "insert into insights.daily_kpi (date, destination_id, cohort, metric, value) ";

    private static readonly string[] Components =
    [
        // Counts of events per day and cohort. A completed listen counts when it reached 80 % (§26).
        Insert + """
        select day, '', cohort, metric, count(*) from (
            select day, cohort, case name
                when 'recommendation_viewed' then 'rec_viewed'
                when 'recommendation_clicked' then 'rec_clicked'
                when 'poi_liked' then 'poi_liked'
                when 'poi_disliked' then 'poi_disliked'
                when 'audio_started' then 'audio_started'
                when 'audio_completed' then 'audio_completed_80' end as metric
            from kpi_event
            where name in ('recommendation_viewed', 'recommendation_clicked', 'poi_liked', 'poi_disliked', 'audio_started', 'audio_completed')
              and (name <> 'audio_completed' or coalesce((props->>'percent')::numeric, 100) >= 80)
        ) x group by day, cohort, metric
        """,

        // Click rate by ProfileDepth tranche (the strategic KPI): views and clicks per tranche.
        Insert + """
        select day, '', cohort, prefix || band, count(*) from (
            select day, cohort,
                   case name when 'recommendation_viewed' then 'rec_viewed_depth_' else 'rec_clicked_depth_' end as prefix,
                   case when (props->>'profile_depth')::numeric <= 9 then '0_9' when (props->>'profile_depth')::numeric <= 49 then '10_49' else '50_plus' end as band
            from kpi_event
            where name in ('recommendation_viewed', 'recommendation_clicked') and props->>'profile_depth' is not null
        ) x group by day, cohort, prefix, band
        """,

        // Sessions with at least one non-essential event (travelers who refused only send crashes, which would skew the rate).
        Insert + """
        select day, '', cohort, 'sessions', count(distinct session_id)
        from kpi_event where name <> all(@essential) group by day, cohort
        """,
        Insert + """
        select day, '', cohort, 'sessions_with_crash', count(distinct session_id)
        from kpi_event k
        where name = 'app_crash'
          and exists (select 1 from kpi_event s where s.session_id = k.session_id and s.day = k.day and s.name <> all(@essential))
        group by day, cohort
        """,

        // Activation: installs, and those with a first story listened within a week (the day of the install included).
        Insert + "select day, '', cohort, 'installs', count(*) from kpi_install group by day, cohort",
        Insert + """
        select i.day, '', i.cohort, 'activated', count(*)
        from kpi_install i
        where exists (select 1 from insights.event e
                      where e.traveler_ref = i.traveler_ref and e.name = 'audio_started'
                        and e.occurred_at >= (i.day::timestamp at time zone 'UTC')
                        and e.occurred_at < ((i.day + 8)::timestamp at time zone 'UTC'))
        group by i.day, i.cohort
        """,

        // ProfileDepth at day 7: the highest depth announced by the app in the first week, as a histogram (a median cannot be summed over days).
        Insert + """
        select day, '', cohort, 'profile_depth_d7_h' || depth::text, count(*) from (
            select i.day, i.cohort, least(d.depth, 100)::int as depth
            from kpi_install i
            join lateral (select max((e.props->>'profile_depth')::numeric) as depth
                          from insights.event e
                          where e.traveler_ref = i.traveler_ref
                            and e.name in ('recommendation_viewed', 'recommendation_clicked')
                            and e.props->>'profile_depth' is not null
                            and e.occurred_at < ((i.day + 8)::timestamp at time zone 'UTC')) d on d.depth is not null
            where i.day + 7 < @today
        ) x group by day, cohort, depth
        """,

        .. KpiMetrics.RetentionDays.SelectMany(RetentionComponents),
    ];

    private static IEnumerable<string> RetentionComponents(int days)
    {
        var offset = days.ToString(CultureInfo.InvariantCulture);
        var next = (days + 1).ToString(CultureInfo.InvariantCulture);

        // Retention of day N: installers of day D who produce a non-essential event on day D+N. Counted once D+N is over.
        yield return Insert + $"select i.day, '', i.cohort, '{KpiMetrics.RetentionBase(days)}', count(*) from kpi_install i where i.day + {offset} < @today group by i.day, i.cohort";
        yield return Insert + $"""
            select i.day, '', i.cohort, '{KpiMetrics.RetentionReturned(days)}', count(*)
            from kpi_install i
            where i.day + {offset} < @today
              and exists (select 1 from insights.event e
                          where e.traveler_ref = i.traveler_ref and e.name <> all(@essential)
                            and e.occurred_at >= ((i.day + {offset})::timestamp at time zone 'UTC')
                            and e.occurred_at < ((i.day + {next})::timestamp at time zone 'UTC'))
            group by i.day, i.cohort
            """;
    }

    public async Task ComputeAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Two instances rebuilding the same days would fight over the rows: the one that finds the lock taken has nothing to add.
        if (!await db.Database.SqlQuery<bool>($"""select pg_try_advisory_xact_lock({AdvisoryLockKey}) as "Value" """).SingleAsync(cancellationToken))
        {
            return;
        }

        NpgsqlParameter[] Parameters() =>
        [
            new("from", NpgsqlDbType.Date) { Value = from },
            new("to", NpgsqlDbType.Date) { Value = to },
            new("today", NpgsqlDbType.Date) { Value = today },
            new("start", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).UtcDateTime },
            new("end", NpgsqlDbType.TimestampTz) { Value = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).UtcDateTime },
            new("essential", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = EventCatalogue.EssentialNames.ToArray() },
        ];

        // Parameters unused by a statement are harmless to Npgsql; the temp tables vanish with the transaction.
#pragma warning disable EF1002 // Constant statements; the only interpolated parts are integers and constants of KpiMetrics.
        await db.Database.ExecuteSqlRawAsync("delete from insights.daily_kpi where date >= @from and date <= @to and destination_id = ''", Parameters(), cancellationToken);
        await db.Database.ExecuteSqlRawAsync(CreateCohorts, Parameters(), cancellationToken);
        await db.Database.ExecuteSqlRawAsync(CreateDayEvents, Parameters(), cancellationToken);
        await db.Database.ExecuteSqlRawAsync(CreateInstalls, Parameters(), cancellationToken);
        foreach (var component in Components)
        {
            await db.Database.ExecuteSqlRawAsync(component, Parameters(), cancellationToken);
        }
#pragma warning restore EF1002

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KpiRow>> ReadAsync(DateOnly from, DateOnly to, string? destination, string? cohort, CancellationToken cancellationToken)
    {
        var place = destination ?? AllDestinations;
        var rows = await db.DailyKpis.AsNoTracking()
            .Where(row => row.Date >= from && row.Date <= to && row.DestinationId == place && (cohort == null || row.Cohort == cohort))
            .ToListAsync(cancellationToken);
        return [.. rows.Select(row => new KpiRow(row.Date, row.DestinationId, row.Cohort, row.Metric, row.Value))];
    }
}
