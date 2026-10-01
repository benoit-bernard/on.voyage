using OnVoyage.Insights.Domain;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Insights.Application.Ports;

/// <summary>Values of Platform's remote configuration this service uses (§18, annexe E), read from its local projection with the defaults of the annexe.</summary>
public sealed record InsightsSettings(int RawMonths = 13, int TechnicalDays = 90, int MaxEventsPerBatch = 500, int KpiRecomputeDays = 35);

public interface IInsightsSettings
{
    Task<InsightsSettings> GetAsync(CancellationToken cancellationToken);
}

/// <summary>An event ready to store. <see cref="PropsJson"/> only holds catalogue properties.</summary>
public sealed record NewEvent(Guid Id, Guid TravelerId, Guid SessionId, string Name, string PropsJson, string AppVersion, string Platform, DateTimeOffset OccurredAt);

public interface IEventStore
{
    /// <summary>The absence of a row in <c>insights.consent_projection</c> means refused (§16.3).</summary>
    Task<bool> HasAnalyticsConsentAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Stores the events that are new (by id and moment). Returns how many were.</summary>
    Task<int> InsertAsync(IReadOnlyList<NewEvent> events, CancellationToken cancellationToken);
}

public interface IConsentProjectionWriter
{
    /// <summary>Applies a consent change only if it is newer than what is stored, so redelivery and reordering change nothing.</summary>
    Task ApplyAsync(Guid travelerId, bool analytics, DateTimeOffset changedAt, CancellationToken cancellationToken);
}

public interface IConfigSnapshotStore
{
    /// <summary>Applies a change only if its version is newer; returns false for a duplicate or stale event.</summary>
    Task<bool> ApplyAsync(string key, string valueJson, int version, CancellationToken cancellationToken);
}

public interface IMaintenanceStore
{
    Task<IReadOnlyList<PartitionMonth>> ListPartitionsAsync(CancellationToken cancellationToken);

    /// <returns>The partitions that did not exist.</returns>
    Task<IReadOnlyList<PartitionMonth>> EnsurePartitionsAsync(IReadOnlyList<PartitionMonth> months, CancellationToken cancellationToken);

    Task DropPartitionAsync(PartitionMonth month, CancellationToken cancellationToken);

    /// <summary>Deletes the events older than the cutoff; <paramref name="onlyNames"/> limits it to those event names.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, IReadOnlyCollection<string>? onlyNames, CancellationToken cancellationToken);
}

public sealed record KpiRow(DateOnly Date, string Destination, string Cohort, string Metric, double Value);

public interface IKpiStore
{
    /// <summary>Rebuilds the daily components for the days from..to (inclusive, UTC) from the stored events. <paramref name="today"/> bounds the retention windows that are complete.</summary>
    Task ComputeAsync(DateOnly from, DateOnly to, DateOnly today, CancellationToken cancellationToken);

    /// <param name="destination">Null for every destination.</param>
    /// <param name="cohort">Null for every cohort.</param>
    Task<IReadOnlyList<KpiRow>> ReadAsync(DateOnly from, DateOnly to, string? destination, string? cohort, CancellationToken cancellationToken);
}

public sealed record StoredEvent(Guid Id, Guid SessionId, string Name, string PropsJson, string AppVersion, string Platform, DateTimeOffset OccurredAt);

public sealed record TravelerData(bool? AnalyticsConsent, DateTimeOffset? ConsentUpdatedAt, IReadOnlyList<StoredEvent> Events);

public interface ITravelerDataStore
{
    /// <summary>Deletes everything held for the traveler and records the confirmation to send, in one transaction.</summary>
    Task DeleteAsync(Guid travelerId, TravelerDataDeletedV1 confirmation, CancellationToken cancellationToken);

    Task<TravelerData> ReadAsync(Guid travelerId, CancellationToken cancellationToken);
}

/// <summary>Writes a part of a traveler export (a JSON file) where Platform collects it, and returns its path.</summary>
public interface IExportPartWriter
{
    Task<string> WriteAsync(Guid exportId, string json, CancellationToken cancellationToken);
}
