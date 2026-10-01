using System.Text.RegularExpressions;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Places;
using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Application.Features.Videos;

public sealed record VideoCandidate(string VideoId, string Title, string Channel, string ThumbnailUrl, DateTimeOffset? PublishedAt);

public sealed record PlaceVideo(Guid PlaceId, string VideoId, string Title, string Channel, string ThumbnailPath, string Url, DateTimeOffset SelectedAt);

/// <summary>
/// The YouTube Data API, called from the server only, with the server's key (the apps never talk to YouTube, F-19). Thumbnails are
/// downloaded here so the app shows our own copy.
/// </summary>
public interface IVideoSearch
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<VideoCandidate>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken);

    Task<VideoCandidate?> GetAsync(string videoId, CancellationToken cancellationToken);

    /// <summary>Downloads the thumbnail; null when the address is not a YouTube image host, is not an image or is too big.</summary>
    Task<byte[]?> DownloadThumbnailAsync(string url, CancellationToken cancellationToken);
}

public interface IVideoStore
{
    Task<IReadOnlyList<PlaceVideo>> ListAsync(Guid placeId, CancellationToken cancellationToken);

    Task AddAsync(PlaceVideo video, CancellationToken cancellationToken);

    Task<PlaceVideo?> RemoveAsync(Guid placeId, string videoId, CancellationToken cancellationToken);
}

public sealed record SearchVideosQuery(string Query);

public sealed record ListPlaceVideosQuery(Guid PlaceId);

public sealed record SelectVideoCommand(Guid PlaceId, string VideoId);

public sealed record RemoveVideoCommand(Guid PlaceId, string VideoId);

public static partial class VideoRules
{
    /// <summary>At most two videos per place: a pointer to learn more, not a playlist.</summary>
    public const int MaxPerPlace = 2;

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Pattern();

    public static bool IsVideoId(string value) => Pattern().IsMatch(value);

    public static string WatchUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";
}

public static class VideoHandler
{
    public static async Task<Result<IReadOnlyList<VideoCandidate>>> Handle(SearchVideosQuery query, IVideoSearch youtube, CancellationToken cancellationToken)
    {
        var text = query.Query?.Trim() ?? string.Empty;
        if (text.Length is < 2 or > 100)
        {
            return Result.Failure<IReadOnlyList<VideoCandidate>>("validation", "Search with 2 to 100 characters.");
        }

        if (!youtube.IsConfigured)
        {
            return Result.Failure<IReadOnlyList<VideoCandidate>>("youtube_not_configured", "No YouTube key is configured on the server (YouTube:ApiKey).");
        }

        try
        {
            return Result.Success(await youtube.SearchAsync(text, 8, cancellationToken));
        }
        catch (ExternalServiceException)
        {
            return Result.Failure<IReadOnlyList<VideoCandidate>>("youtube_unavailable", "YouTube did not answer (quota reached or service down). Try again later.");
        }
    }

    public static async Task<Result<IReadOnlyList<PlaceVideo>>> Handle(ListPlaceVideosQuery query, IVideoStore videos, CancellationToken cancellationToken) =>
        Result.Success(await videos.ListAsync(query.PlaceId, cancellationToken));

    /// <summary>
    /// The editor picks a video by its id; the title, channel and thumbnail are read again from YouTube, never taken from the request.
    /// A published place is published again so the catalog gets the link.
    /// </summary>
    public static async Task<Result<PlaceVideo>> Handle(
        SelectVideoCommand command, IPlaceStore places, IVideoStore videos, IVideoSearch youtube, IMediaStorage storage,
        IDestinationCatalog destinations, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!VideoRules.IsVideoId(command.VideoId))
        {
            return Result.Failure<PlaceVideo>("validation", "That is not a YouTube video id.");
        }

        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null || place.Status is PlaceStatus.Merged or PlaceStatus.Rejected)
        {
            return Result.Failure<PlaceVideo>("place_not_found", "Place not found.");
        }

        var current = await videos.ListAsync(place.Id, cancellationToken);
        if (current.Any(video => video.VideoId == command.VideoId))
        {
            return Result.Failure<PlaceVideo>("video_already_selected", "This video is already selected for the place.");
        }

        if (current.Count >= VideoRules.MaxPerPlace)
        {
            return Result.Failure<PlaceVideo>("too_many_videos", $"A place has at most {VideoRules.MaxPerPlace} videos: remove one first.");
        }

        if (!youtube.IsConfigured)
        {
            return Result.Failure<PlaceVideo>("youtube_not_configured", "No YouTube key is configured on the server (YouTube:ApiKey).");
        }

        VideoCandidate? candidate;
        byte[]? image;
        try
        {
            candidate = await youtube.GetAsync(command.VideoId, cancellationToken);
            if (candidate is null)
            {
                return Result.Failure<PlaceVideo>("video_not_found", "YouTube does not know this video.");
            }

            image = await youtube.DownloadThumbnailAsync(candidate.ThumbnailUrl, cancellationToken);
        }
        catch (ExternalServiceException)
        {
            return Result.Failure<PlaceVideo>("youtube_unavailable", "YouTube did not answer (quota reached or service down). Try again later.");
        }

        if (image is null)
        {
            return Result.Failure<PlaceVideo>("thumbnail_unavailable", "The thumbnail could not be copied.");
        }

        var path = await storage.SaveAsync($"thumbs/{place.Id}/{candidate.VideoId}.jpg", image, cancellationToken);
        var video = new PlaceVideo(place.Id, candidate.VideoId, candidate.Title, candidate.Channel, path, VideoRules.WatchUrl(candidate.VideoId), clock.GetUtcNow());
        await videos.AddAsync(video, cancellationToken);

        await RepublishAsync(place, places, videos, destinations, clock, cancellationToken);
        return Result.Success(video);
    }

    public static async Task<Result<bool>> Handle(
        RemoveVideoCommand command, IPlaceStore places, IVideoStore videos, IDestinationCatalog destinations, TimeProvider clock, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return Result.Failure<bool>("place_not_found", "Place not found.");
        }

        if (await videos.RemoveAsync(place.Id, command.VideoId, cancellationToken) is null)
        {
            return Result.Failure<bool>("video_not_found", "This video is not selected for the place.");
        }

        await RepublishAsync(place, places, videos, destinations, clock, cancellationToken);
        return Result.Success(true);
    }

    private static async Task RepublishAsync(PlaceRecord place, IPlaceStore places, IVideoStore videos, IDestinationCatalog destinations, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (place.Status == PlaceStatus.Published)
        {
            await PublishPlaceHandler.Handle(new PublishPlaceCommand(place.Id), places, destinations, videos, clock, cancellationToken);
        }
    }
}
