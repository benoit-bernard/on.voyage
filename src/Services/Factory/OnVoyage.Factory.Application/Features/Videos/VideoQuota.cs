using OnVoyage.Factory.Application.Content;

namespace OnVoyage.Factory.Application.Features.Videos;

/// <summary>YouTube answered that the daily quota of the server key is used up. Retrying today is useless, unlike an outage.</summary>
public sealed class VideoQuotaExceededException(string message) : Exception(message);

/// <summary>
/// The YouTube Data API allows 10 000 units a day per key; a search costs 100 and a video lookup 1. The units are counted here, in
/// the day of the quota (it resets at midnight Pacific time), so the editor is told before the key is burned, and a repeated search is
/// answered from the cache without touching the quota.
/// </summary>
public sealed record VideoQuotaOptions(int DailyUnits = 10_000, int SearchCost = 100, int LookupCost = 1, int CacheHours = 24);

public sealed record VideoQuotaStatus(DateOnly Day, int UnitsUsed, int DailyUnits, int Remaining, int SearchesLeft, DateTimeOffset ResetsAt);

public sealed record SelectedVideo(PlaceVideo Video, string PlaceName, string Destination);

public interface IVideoQuotaStore
{
    Task<int> UnitsUsedAsync(DateOnly day, CancellationToken cancellationToken);

    Task AddUnitsAsync(DateOnly day, int units, CancellationToken cancellationToken);

    /// <summary>YouTube said "quota exceeded": whatever was counted, the day is full.</summary>
    Task FillDayAsync(DateOnly day, int dailyUnits, CancellationToken cancellationToken);

    Task<IReadOnlyList<VideoCandidate>?> FindSearchAsync(string key, DateTimeOffset notBefore, CancellationToken cancellationToken);

    Task SaveSearchAsync(string key, IReadOnlyList<VideoCandidate> results, DateTimeOffset at, CancellationToken cancellationToken);
}

public sealed record GetVideoQuotaQuery;

public sealed record ListSelectedVideosQuery(int Limit);

public static class VideoQuotaClock
{
    private static readonly TimeZoneInfo Pacific = FindPacific();

    /// <summary>The day the quota counts in: it resets at midnight Pacific time.</summary>
    public static DateOnly DayOf(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Pacific).Date);

    public static DateTimeOffset ResetOf(DateOnly day)
    {
        var local = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Pacific.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The same query typed differently ("Fort  Saint-Jean ", "fort saint-jean") is one search.</summary>
    public static string Key(string query) => string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    private static TimeZoneInfo FindPacific()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("pacific-fixed", TimeSpan.FromHours(-8), "Pacific", "Pacific");
        }
    }
}

public static class VideoQuotaHandler
{
    public static async Task<Result<VideoQuotaStatus>> Handle(GetVideoQuotaQuery query, IVideoQuotaStore quota, VideoQuotaOptions options, TimeProvider clock, CancellationToken cancellationToken) =>
        Result.Success(await StatusAsync(quota, options, clock, cancellationToken));

    public static async Task<Result<IReadOnlyList<SelectedVideo>>> Handle(ListSelectedVideosQuery query, IVideoStore videos, CancellationToken cancellationToken) =>
        Result.Success(await videos.ListAllAsync(Math.Clamp(query.Limit, 1, 500), cancellationToken));

    public static async Task<VideoQuotaStatus> StatusAsync(IVideoQuotaStore quota, VideoQuotaOptions options, TimeProvider clock, CancellationToken cancellationToken)
    {
        var day = VideoQuotaClock.DayOf(clock.GetUtcNow());
        var used = await quota.UnitsUsedAsync(day, cancellationToken);
        var remaining = Math.Max(0, options.DailyUnits - used);
        return new VideoQuotaStatus(day, used, options.DailyUnits, remaining, remaining / Math.Max(1, options.SearchCost), VideoQuotaClock.ResetOf(day));
    }

    public static string ExhaustedMessage(VideoQuotaStatus status) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The daily YouTube quota is used up ({status.UnitsUsed} of {status.DailyUnits} units). It resets at {status.ResetsAt:yyyy-MM-dd HH:mm} UTC.");
}
