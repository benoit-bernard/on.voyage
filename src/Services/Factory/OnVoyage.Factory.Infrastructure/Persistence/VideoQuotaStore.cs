using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Features.Videos;

namespace OnVoyage.Factory.Infrastructure.Persistence;

/// <summary>Counts the YouTube quota units of the day and keeps the answers of recent searches (see <see cref="VideoQuotaOptions"/>).</summary>
internal sealed class VideoQuotaStore(FactoryDbContext db) : IVideoQuotaStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> UnitsUsedAsync(DateOnly day, CancellationToken cancellationToken) =>
        await db.YouTubeUsage.AsNoTracking().Where(row => row.Day == day).Select(row => (int?)row.Units).FirstOrDefaultAsync(cancellationToken) ?? 0;

    public async Task AddUnitsAsync(DateOnly day, int units, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"insert into factory.youtube_usage (day, units) values ({day}, {units}) on conflict (day) do update set units = factory.youtube_usage.units + excluded.units", cancellationToken);

    public async Task FillDayAsync(DateOnly day, int dailyUnits, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"insert into factory.youtube_usage (day, units) values ({day}, {dailyUnits}) on conflict (day) do update set units = greatest(factory.youtube_usage.units, excluded.units)", cancellationToken);

    public async Task<IReadOnlyList<VideoCandidate>?> FindSearchAsync(string key, DateTimeOffset notBefore, CancellationToken cancellationToken)
    {
        var row = await db.YouTubeSearches.AsNoTracking().FirstOrDefaultAsync(item => item.QueryKey == key && item.FetchedAt >= notBefore, cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<List<VideoCandidate>>(row.Results, Json);
    }

    public async Task SaveSearchAsync(string key, IReadOnlyList<VideoCandidate> results, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(results, Json);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"insert into factory.youtube_search (query_key, results, fetched_at) values ({key}, {json}::jsonb, {at}) on conflict (query_key) do update set results = excluded.results, fetched_at = excluded.fetched_at", cancellationToken);
    }
}
