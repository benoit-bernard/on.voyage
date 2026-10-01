using OnVoyage.Insights.Application.Ports;
using OnVoyage.Insights.Contracts;
using OnVoyage.Insights.Domain;

namespace OnVoyage.Insights.Application.Features.Maintenance;

/// <summary>The daily housekeeping of the event table: create the monthly partitions that will be needed, apply the retention of §16.2.</summary>
public sealed record RunMaintenanceCommand;

public sealed record MaintenanceReport(int PartitionsCreated, int PartitionsDropped, int ExpiredEventsDeleted, int ExpiredTechnicalEventsDeleted);

public static class RunMaintenanceHandler
{
    /// <summary>
    /// Raw events are kept <c>retention.analytics_raw_months</c> (13) months: whole monthly partitions are dropped, then the rest of the oldest
    /// month is trimmed by date. Essential technical events are kept <c>retention.technical_events_days</c> (90) days. The daily aggregates
    /// (<c>insights.daily_kpi</c>) are anonymous and are never deleted.
    /// </summary>
    public static async Task<MaintenanceReport> Handle(RunMaintenanceCommand command, IMaintenanceStore store, IInsightsSettings settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var limits = await settings.GetAsync(cancellationToken);
        var now = clock.GetUtcNow();

        var created = await store.EnsurePartitionsAsync(EventPartitions.Required(now), cancellationToken);

        var rawCutoff = EventPartitions.RawCutoff(now, limits.RawMonths);
        var dropped = EventPartitions.Expired(await store.ListPartitionsAsync(cancellationToken), rawCutoff);
        foreach (var month in dropped)
        {
            await store.DropPartitionAsync(month, cancellationToken);
        }

        var trimmed = await store.DeleteOlderThanAsync(rawCutoff, null, cancellationToken);
        var technical = await store.DeleteOlderThanAsync(now.AddDays(-limits.TechnicalDays), EventCatalogue.EssentialNames, cancellationToken);
        return new MaintenanceReport(created.Count, dropped.Count, trimmed, technical);
    }
}
