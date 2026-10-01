namespace OnVoyage.Insights.Infrastructure.Persistence;

/// <summary>
/// A usage event (§11.4). The table is partitioned by month on <see cref="OccurredAt"/>, so the key includes it; <see cref="Id"/> is the id
/// chosen by the client, which is what makes a resend idempotent. <b>No column can hold a position</b> (a test keeps it that way): the
/// properties are the ones of the catalogue.
/// </summary>
internal sealed class EventRow
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid TravelerRef { get; set; }
    public Guid SessionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Props { get; set; } = "{}";
    public string AppVersion { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
}

/// <summary>Projection of <c>ConsentChangedV1</c> (kind <c>analytics</c>). No row means refused (§16.3).</summary>
internal sealed class ConsentProjectionRow
{
    public Guid TravelerId { get; set; }
    public bool Analytics { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A daily component of the KPIs (anonymous, kept). <see cref="DestinationId"/> is empty for "all destinations".</summary>
internal sealed class DailyKpiRow
{
    public DateOnly Date { get; set; }
    public string DestinationId { get; set; } = string.Empty;
    public string Cohort { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double Value { get; set; }
}

internal sealed class ConfigSnapshotRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = "{}";
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
