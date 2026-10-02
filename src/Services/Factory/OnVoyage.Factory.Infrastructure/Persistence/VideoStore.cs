using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Features.Videos;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class VideoStore(IDbContextOutbox<FactoryDbContext> outbox) : IVideoStore
{
    private FactoryDbContext Db => outbox.DbContext;

    public async Task<IReadOnlyList<PlaceVideo>> ListAsync(Guid placeId, CancellationToken cancellationToken) =>
        [.. (await Db.PlaceVideos.AsNoTracking().Where(row => row.PlaceId == placeId).OrderBy(row => row.SelectedAt).ToListAsync(cancellationToken))
            .Select(row => new PlaceVideo(row.PlaceId, row.VideoId, row.Title, row.Channel, row.ThumbnailPath, row.Url, row.SelectedAt))];

    public async Task AddAsync(PlaceVideo video, CancellationToken cancellationToken)
    {
        Db.PlaceVideos.Add(new PlaceVideoRow { PlaceId = video.PlaceId, VideoId = video.VideoId, Title = video.Title, Channel = video.Channel, ThumbnailPath = video.ThumbnailPath, Url = video.Url, SelectedAt = video.SelectedAt });
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SelectedVideo>> ListAllAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = await (from video in Db.PlaceVideos.AsNoTracking()
                          join place in Db.Places.AsNoTracking() on video.PlaceId equals place.Id
                          orderby video.SelectedAt descending
                          select new { Video = video, place.Name, place.DestinationSlug }).Take(limit).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new SelectedVideo(
            new PlaceVideo(row.Video.PlaceId, row.Video.VideoId, row.Video.Title, row.Video.Channel, row.Video.ThumbnailPath, row.Video.Url, row.Video.SelectedAt), row.Name, row.DestinationSlug))];
    }

    public async Task<PlaceVideo?> RemoveAsync(Guid placeId, string videoId, CancellationToken cancellationToken)
    {
        var row = await Db.PlaceVideos.FirstOrDefaultAsync(item => item.PlaceId == placeId && item.VideoId == videoId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        Db.PlaceVideos.Remove(row);
        await Db.SaveChangesAsync(cancellationToken);
        return new PlaceVideo(row.PlaceId, row.VideoId, row.Title, row.Channel, row.ThumbnailPath, row.Url, row.SelectedAt);
    }
}
