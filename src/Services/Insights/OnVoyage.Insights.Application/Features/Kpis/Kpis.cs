using System.Globalization;
using System.Text;
using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Contracts;
using OnVoyage.Insights.Domain;

namespace OnVoyage.Insights.Application.Features.Kpis;

/// <summary>Rebuilds the daily components for a period (days, UTC). Used by the scheduler through <see cref="RefreshRecentKpisCommand"/> and by tests.</summary>
public sealed record ComputeKpisCommand(DateOnly From, DateOnly To);

/// <summary>Rebuilds the recent days (<see cref="InsightsSettings.KpiRecomputeDays"/>), enough for the 30-day retention window to close.</summary>
public sealed record RefreshRecentKpisCommand;

public sealed record GetKpisQuery(DateOnly From, DateOnly To, string? Destination, string? Cohort);

public sealed record ExportKpisQuery(DateOnly From, DateOnly To, string? Destination, string? Cohort);

public static class ComputeKpisHandler
{
    public static async Task Handle(ComputeKpisCommand command, IKpiStore store, TimeProvider clock, CancellationToken cancellationToken) =>
        await store.ComputeAsync(command.From, command.To, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), cancellationToken);

    public static async Task Handle(RefreshRecentKpisCommand command, IKpiStore store, IInsightsSettings settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var days = (await settings.GetAsync(cancellationToken)).KpiRecomputeDays;
        await store.ComputeAsync(today.AddDays(-(days - 1)), today, today, cancellationToken);
    }
}

public static class GetKpisHandler
{
    internal const int MaxDays = 400;

    public static async Task<Result<KpiReportDto>> Handle(GetKpisQuery query, IKpiStore store, CancellationToken cancellationToken)
    {
        if (Check(query.From, query.To, query.Destination, query.Cohort) is { } problem)
        {
            return Result.Failure<KpiReportDto>("validation", problem);
        }

        var destination = string.IsNullOrWhiteSpace(query.Destination) ? null : query.Destination;
        var cohort = string.IsNullOrWhiteSpace(query.Cohort) ? null : query.Cohort;
        var rows = await store.ReadAsync(query.From, query.To, destination, cohort, cancellationToken);
        var totals = rows.GroupBy(r => (r.Cohort, r.Metric)).Select(g => new KpiTotal(g.Key.Cohort, g.Key.Metric, g.Sum(r => r.Value))).ToList();
        var values = KpiCalculator.Compute(totals, cohort).ToDictionary(pair => pair.Key, pair => new KpiReadingDto(pair.Value.Value, pair.Value.Sample));
        return Result.Success(new KpiReportDto(query.From, query.To, destination, cohort, values));
    }

    internal static string? Check(DateOnly from, DateOnly to, string? destination, string? cohort)
    {
        if (to < from)
        {
            return "from must not be after to";
        }

        if (to.DayNumber - from.DayNumber >= MaxDays)
        {
            return $"a period holds {MaxDays} days at most";
        }

        if (!string.IsNullOrWhiteSpace(cohort) && !Cohorts.All.Contains(cohort))
        {
            return "cohort must be 'control' or 'personalized'";
        }

        return destination is { Length: > 64 } ? "destination is too long" : null;
    }
}

public static class ExportKpisHandler
{
    /// <summary>The stored components, one line each: date, destination (empty = all), cohort, metric, value. Invariant culture, dot decimals.</summary>
    public static async Task<Result<string>> Handle(ExportKpisQuery query, IKpiStore store, CancellationToken cancellationToken)
    {
        if (GetKpisHandler.Check(query.From, query.To, query.Destination, query.Cohort) is { } problem)
        {
            return Result.Failure<string>("validation", problem);
        }

        var rows = await store.ReadAsync(
            query.From,
            query.To,
            string.IsNullOrWhiteSpace(query.Destination) ? null : query.Destination,
            string.IsNullOrWhiteSpace(query.Cohort) ? null : query.Cohort,
            cancellationToken);

        var csv = new StringBuilder("date,destination,cohort,metric,value\n");
        foreach (var row in rows.OrderBy(r => r.Date).ThenBy(r => r.Destination, StringComparer.Ordinal).ThenBy(r => r.Cohort, StringComparer.Ordinal).ThenBy(r => r.Metric, StringComparer.Ordinal))
        {
            csv.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Destination).Append(',')
                .Append(row.Cohort).Append(',')
                .Append(row.Metric).Append(',')
                .Append(row.Value.ToString("0.####", CultureInfo.InvariantCulture)).Append('\n');
        }

        return Result.Success(csv.ToString());
    }
}
