using System.Text.Json;

namespace OnVoyage.Insights.Contracts;

/// <summary>
/// One usage event sent by the app (§12.7, §14.8). <see cref="Id"/> is chosen by the client and makes a resend harmless.
/// <see cref="Props"/> only holds the properties the catalogue (<see cref="EventCatalogue"/>) lists for <see cref="Name"/>: never coordinates.
/// </summary>
public sealed record EventDto(
    Guid Id,
    string Name,
    DateTimeOffset OccurredAt,
    Guid SessionId,
    string AppVersion,
    string Platform,
    IReadOnlyDictionary<string, JsonElement>? Props = null);

public sealed record EventBatchRequest(IReadOnlyList<EventDto> Events);

/// <param name="Accepted">Events stored.</param>
/// <param name="Duplicates">Events already stored (same id): a resend.</param>
/// <param name="Ignored">Non-essential events not stored because the traveler has not accepted the statistics (§16.3), or too old to keep.</param>
public sealed record EventBatchResponse(int Accepted, int Duplicates, int Ignored);

public static class Platforms
{
    public const string Android = "android";
    public const string Ios = "ios";
    public const string Web = "web";

    public static IReadOnlyList<string> All { get; } = [Android, Ios, Web];
}

/// <summary>The two arms of the central hypothesis (§26): the control cohort sees the non-personalized ranking.</summary>
public static class Cohorts
{
    public const string Control = "control";
    public const string Personalized = "personalized";

    public static IReadOnlyList<string> All { get; } = [Control, Personalized];
}

/// <summary>What <c>GET /api/insights/v1/kpis</c> returns. The back office (<c>Web.Admin</c>) reads exactly this shape.</summary>
public sealed record KpiReportDto(DateOnly From, DateOnly To, string? Destination, string? Cohort, IReadOnlyDictionary<string, KpiReadingDto> Values);

public sealed record KpiReadingDto(double? Value, long Sample);
