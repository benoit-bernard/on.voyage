using System.Globalization;

namespace OnVoyage.Insights.Domain;

/// <summary>A monthly partition of <c>insights.event</c> (§11.4). The name is derived from the month, never from input.</summary>
public readonly record struct PartitionMonth(int Year, int Month)
{
    private const string Prefix = "event_y";

    public string TableName => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Year:0000}m{Month:00}");

    public DateTimeOffset Start => new(Year, Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Exclusive upper bound.</summary>
    public DateTimeOffset End => Start.AddMonths(1);

    public static PartitionMonth Of(DateTimeOffset moment)
    {
        var utc = moment.UtcDateTime;
        return new PartitionMonth(utc.Year, utc.Month);
    }

    public PartitionMonth AddMonths(int months) => Of(Start.AddMonths(months));

    public static bool TryParse(string tableName, out PartitionMonth month)
    {
        month = default;
        if (!tableName.StartsWith(Prefix, StringComparison.Ordinal) || tableName.Length != Prefix.Length + 7 || tableName[Prefix.Length + 4] != 'm')
        {
            return false;
        }

        if (!int.TryParse(tableName.AsSpan(Prefix.Length, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(tableName.AsSpan(Prefix.Length + 5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 12)
        {
            return false;
        }

        month = new PartitionMonth(year, number);
        return true;
    }
}

/// <summary>Which partitions must exist, and which have aged out (retention of the raw events, §16.2).</summary>
public static class EventPartitions
{
    /// <summary>The previous month (events arrive late from offline devices), the current one and <paramref name="monthsAhead"/> more.</summary>
    public static IReadOnlyList<PartitionMonth> Required(DateTimeOffset now, int monthsAhead = 2)
    {
        var current = PartitionMonth.Of(now);
        return [.. Enumerable.Range(-1, monthsAhead + 2).Select(current.AddMonths)];
    }

    /// <summary>Partitions whose newest possible event is older than the cutoff: they can be dropped as a whole.</summary>
    public static IReadOnlyList<PartitionMonth> Expired(IEnumerable<PartitionMonth> existing, DateTimeOffset cutoff) =>
        [.. existing.Where(month => month.End <= cutoff).OrderBy(month => month.Start)];

    /// <summary>The oldest moment worth keeping for a given retention in months.</summary>
    public static DateTimeOffset RawCutoff(DateTimeOffset now, int retentionMonths) => now.AddMonths(-retentionMonths);
}
