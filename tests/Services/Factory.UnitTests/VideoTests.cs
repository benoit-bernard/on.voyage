using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Videos;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Contracts;
using OnVoyage.Factory.Domain.Geo;
using OnVoyage.Factory.Infrastructure.Sources;

namespace Factory.UnitTests;

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

public sealed class YouTubeClientTests
{
    private const string SearchAnswer = """
        {
          "items": [
            { "id": { "kind": "youtube#video", "videoId": "abcdefghijk" },
              "snippet": { "title": "Fort Saint-Jean &amp; le Vieux-Port &#39;visite&#39;", "channelTitle": "Marseille Tourisme", "publishedAt": "2024-05-01T10:00:00Z",
                           "thumbnails": { "default": { "url": "https://i.ytimg.com/vi/abcdefghijk/default.jpg" }, "medium": { "url": "https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg" } } } },
            { "id": { "kind": "youtube#channel", "channelId": "UC123" }, "snippet": { "title": "Une chaîne", "thumbnails": { "default": { "url": "https://i.ytimg.com/x.jpg" } } } },
            { "id": { "kind": "youtube#video", "videoId": "short" }, "snippet": { "title": "id invalide", "thumbnails": { "default": { "url": "https://i.ytimg.com/x.jpg" } } } },
            { "id": { "kind": "youtube#video", "videoId": "ZYXWVUTSRQP" },
              "snippet": { "title": "Sans vignette", "channelTitle": "X" } }
          ]
        }
        """;

    private static (YouTubeClient Client, StubHandler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string? key = "secret-key")
    {
        var handler = new StubHandler(respond);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["YouTube:ApiKey"] = key }).Build();
        return (new YouTubeClient(new HttpClient(handler), configuration), handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public void A_search_answer_gives_videos_only_decoded_and_with_the_medium_thumbnail()
    {
        using var document = JsonDocument.Parse(SearchAnswer);

        var candidates = YouTubeClient.Parse(document.RootElement, searchResult: true);

        var video = candidates.ShouldHaveSingleItem();
        video.VideoId.ShouldBe("abcdefghijk");
        video.Title.ShouldBe("Fort Saint-Jean & le Vieux-Port 'visite'");
        video.Channel.ShouldBe("Marseille Tourisme");
        video.ThumbnailUrl.ShouldBe("https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg");
        video.PublishedAt.ShouldBe(new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_videos_answer_carries_the_id_as_a_plain_string()
    {
        using var document = JsonDocument.Parse("""{ "items": [ { "id": "abcdefghijk", "snippet": { "title": "T", "channelTitle": "C", "thumbnails": { "high": { "url": "https://i.ytimg.com/h.jpg" } } } } ] }""");

        YouTubeClient.Parse(document.RootElement, searchResult: false).ShouldHaveSingleItem().ThumbnailUrl.ShouldBe("https://i.ytimg.com/h.jpg");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "items": "nope" }""")]
    [InlineData("""{ "items": [] }""")]
    public void An_answer_without_items_gives_nothing(string json)
    {
        using var document = JsonDocument.Parse(json);

        YouTubeClient.Parse(document.RootElement, searchResult: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_key_travels_in_a_header_and_never_in_the_address_that_gets_logged()
    {
        var (client, handler) = Client(_ => Json(SearchAnswer));

        var found = await client.SearchAsync("fort saint-jean", 8, TestContext.Current.CancellationToken);

        found.Count.ShouldBe(1);
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Headers.GetValues("x-goog-api-key").ShouldBe(["secret-key"]);
        request.RequestUri!.ToString().ShouldNotContain("secret-key");
        request.RequestUri.ToString().ShouldNotContain("key=");
        request.RequestUri.Query.ShouldContain("q=fort%20saint-jean");
        request.RequestUri.Query.ShouldContain("safeSearch=strict");
    }

    [Fact]
    public async Task Without_a_key_the_client_says_it_is_not_configured()
    {
        var (client, _) = Client(_ => Json("{}"), key: null);

        client.IsConfigured.ShouldBeFalse();
        (await Task.FromResult(Client(_ => Json("{}"), "k").Client.IsConfigured)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)] // a refused key, not a quota
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task A_refusal_or_an_outage_is_an_external_service_error(HttpStatusCode status)
    {
        var (client, _) = Client(_ => Json("""{ "error": { "message": "quotaExceeded" } }""", status));

        await Should.ThrowAsync<ExternalServiceException>(() => client.SearchAsync("fort", 8, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("quotaExceeded")]
    [InlineData("dailyLimitExceeded")]
    public async Task A_daily_quota_refusal_is_told_apart_from_an_outage(string reason)
    {
        var (client, _) = Client(_ => Json($$"""{ "error": { "code": 403, "errors": [ { "domain": "youtube.quota", "reason": "{{reason}}" } ] } }""", HttpStatusCode.Forbidden));

        await Should.ThrowAsync<VideoQuotaExceededException>(() => client.SearchAsync("fort", 8, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<VideoQuotaExceededException>(() => client.GetAsync("abcdefghijk", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_forbidden_answer_for_another_reason_is_not_a_quota_problem()
    {
        var (client, _) = Client(_ => Json("""{ "error": { "code": 403, "errors": [ { "reason": "accessNotConfigured" } ] } }""", HttpStatusCode.Forbidden));

        await Should.ThrowAsync<ExternalServiceException>(() => client.SearchAsync("fort", 8, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unreadable_answer_is_an_external_service_error()
    {
        var (client, _) = Client(_ => Json("<html>oops</html>"));

        await Should.ThrowAsync<ExternalServiceException>(() => client.GetAsync("abcdefghijk", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg", true)]
    [InlineData("https://img.youtube.com/vi/abcdefghijk/0.jpg", true)]
    [InlineData("http://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg", false)]
    [InlineData("https://ytimg.com.evil.example/x.jpg", false)]
    [InlineData("https://evil.example/i.ytimg.com/x.jpg", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void Only_youtube_image_hosts_over_https_are_downloaded(string url, bool allowed) => YouTubeClient.IsYouTubeImage(url).ShouldBe(allowed);

    [Fact]
    public async Task A_thumbnail_is_downloaded_only_when_it_is_a_small_jpeg_from_youtube()
    {
        byte[] image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
        var (client, handler) = Client(request => request.RequestUri!.AbsolutePath.EndsWith("big.jpg", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1_500_000]) { Headers = { ContentType = new("image/jpeg") } } }
            : request.RequestUri.AbsolutePath.EndsWith("page.jpg", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", Encoding.UTF8, "text/html") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) { Headers = { ContentType = new("image/jpeg") } } });
        var ct = TestContext.Current.CancellationToken;

        (await client.DownloadThumbnailAsync("https://i.ytimg.com/vi/x/mqdefault.jpg", ct)).ShouldBe(image);
        (await client.DownloadThumbnailAsync("https://i.ytimg.com/vi/x/big.jpg", ct)).ShouldBeNull();
        (await client.DownloadThumbnailAsync("https://i.ytimg.com/vi/x/page.jpg", ct)).ShouldBeNull();
        var before = handler.Requests.Count;
        (await client.DownloadThumbnailAsync("https://evil.example/x.jpg", ct)).ShouldBeNull();
        handler.Requests.Count.ShouldBe(before, "a foreign address is never requested");
    }
}

public sealed class VideoHandlerTests
{
    private const string VideoId = "abcdefghijk";
    private static readonly Guid PlaceId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IPlaceStore _places = Substitute.For<IPlaceStore>();
    private readonly IVideoSearch _youtube = Substitute.For<IVideoSearch>();
    private readonly IMediaStorage _storage = Substitute.For<IMediaStorage>();
    private readonly IDestinationCatalog _destinations = Substitute.For<IDestinationCatalog>();
    private readonly InMemoryVideos _videos = new();
    private readonly InMemoryQuota _quota = new();
    private readonly VideoQuotaOptions _options = new();
    private readonly TimeProvider _clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);

    private sealed class InMemoryQuota : IVideoQuotaStore
    {
        public Dictionary<DateOnly, int> Units { get; } = [];

        public Dictionary<string, (DateTimeOffset At, IReadOnlyList<VideoCandidate> Results)> Searches { get; } = [];

        public Task<int> UnitsUsedAsync(DateOnly day, CancellationToken cancellationToken) => Task.FromResult(Units.GetValueOrDefault(day));

        public Task AddUnitsAsync(DateOnly day, int units, CancellationToken cancellationToken)
        {
            Units[day] = Units.GetValueOrDefault(day) + units;
            return Task.CompletedTask;
        }

        public Task FillDayAsync(DateOnly day, int dailyUnits, CancellationToken cancellationToken)
        {
            Units[day] = Math.Max(Units.GetValueOrDefault(day), dailyUnits);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VideoCandidate>?> FindSearchAsync(string key, DateTimeOffset notBefore, CancellationToken cancellationToken) =>
            Task.FromResult(Searches.TryGetValue(key, out var found) && found.At >= notBefore ? found.Results : null);

        public Task SaveSearchAsync(string key, IReadOnlyList<VideoCandidate> results, DateTimeOffset at, CancellationToken cancellationToken)
        {
            Searches[key] = (at, results);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryVideos : IVideoStore
    {
        public List<PlaceVideo> Items { get; } = [];

        public Task<IReadOnlyList<PlaceVideo>> ListAsync(Guid placeId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PlaceVideo>>([.. Items.Where(item => item.PlaceId == placeId)]);

        public Task AddAsync(PlaceVideo video, CancellationToken cancellationToken)
        {
            Items.Add(video);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SelectedVideo>> ListAllAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SelectedVideo>>([.. Items.Select(item => new SelectedVideo(item, "Lieu", "marseille"))]);

        public Task<PlaceVideo?> RemoveAsync(Guid placeId, string videoId, CancellationToken cancellationToken)
        {
            var found = Items.FirstOrDefault(item => item.PlaceId == placeId && item.VideoId == videoId);
            if (found is not null)
            {
                Items.Remove(found);
            }

            return Task.FromResult(found);
        }
    }

    private static PlaceRecord Place(PlaceStatus status) => new(
        PlaceId, "marseille", "fort-saint-jean", "Fort Saint-Jean", null, new GeoPoint(43.29, 5.36), null, "Q1457372", "way", 1, new Dictionary<string, string>(), status,
        new PlaceEnrichment("Q1457372", "Fort Saint-Jean", null, null, null, [], [], null, 40, "Fort Saint-Jean (Marseille)", null, null, null, Now), 0, null, false, 0, 70, 50, false, "Rules");

    public VideoHandlerTests()
    {
        _youtube.IsConfigured.Returns(true);
        _youtube.GetAsync(VideoId, Arg.Any<CancellationToken>()).Returns(new VideoCandidate(VideoId, "Le fort", "Chaîne", "https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg", null));
        _youtube.DownloadThumbnailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
        _storage.SaveAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(call => call.Arg<string>());
        _places.FindAsync(PlaceId, Arg.Any<CancellationToken>()).Returns(Place(PlaceStatus.Candidate));
    }

    private Task<Result<PlaceVideo>> SelectAsync(string videoId = VideoId) =>
        VideoHandler.Handle(new SelectVideoCommand(PlaceId, videoId), _places, _videos, _youtube, _storage, _destinations, _quota, _options, _clock, CancellationToken.None);

    [Fact]
    public async Task Selecting_a_video_stores_what_youtube_says_and_copies_the_thumbnail_to_our_storage()
    {
        var result = await SelectAsync();

        result.IsSuccess.ShouldBeTrue();
        var video = _videos.Items.ShouldHaveSingleItem();
        video.Title.ShouldBe("Le fort");
        video.Url.ShouldBe("https://www.youtube.com/watch?v=abcdefghijk");
        video.ThumbnailPath.ShouldBe($"thumbs/{PlaceId}/{VideoId}.jpg");
        await _storage.Received(1).SaveAsync($"thumbs/{PlaceId}/{VideoId}.jpg", Arg.Is<byte[]>(bytes => bytes.Length == 3), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("abcdefghijk&autoplay=1")]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    public async Task Only_a_real_video_id_is_accepted_so_the_path_and_the_url_cannot_be_forged(string videoId)
    {
        var result = await SelectAsync(videoId);

        result.Error!.Code.ShouldBe("validation");
        await _youtube.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_place_has_at_most_two_videos_and_never_the_same_one_twice()
    {
        (await SelectAsync()).IsSuccess.ShouldBeTrue();
        (await SelectAsync()).Error!.Code.ShouldBe("video_already_selected");

        _youtube.GetAsync("ZYXWVUTSRQP", Arg.Any<CancellationToken>()).Returns(new VideoCandidate("ZYXWVUTSRQP", "Autre", "C", "https://i.ytimg.com/x.jpg", null));
        (await SelectAsync("ZYXWVUTSRQP")).IsSuccess.ShouldBeTrue();
        _youtube.GetAsync("QQQQQQQQQQQ", Arg.Any<CancellationToken>()).Returns(new VideoCandidate("QQQQQQQQQQQ", "Trop", "C", "https://i.ytimg.com/y.jpg", null));
        (await SelectAsync("QQQQQQQQQQQ")).Error!.Code.ShouldBe("too_many_videos");
        _videos.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Without_a_server_key_or_a_known_video_or_a_copyable_thumbnail_nothing_is_stored()
    {
        _youtube.IsConfigured.Returns(false);
        (await SelectAsync()).Error!.Code.ShouldBe("youtube_not_configured");

        _youtube.IsConfigured.Returns(true);
        _youtube.GetAsync(VideoId, Arg.Any<CancellationToken>()).Returns((VideoCandidate?)null);
        (await SelectAsync()).Error!.Code.ShouldBe("video_not_found");

        _youtube.GetAsync(VideoId, Arg.Any<CancellationToken>()).Returns(new VideoCandidate(VideoId, "Le fort", "C", "https://evil.example/x.jpg", null));
        _youtube.DownloadThumbnailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        (await SelectAsync()).Error!.Code.ShouldBe("thumbnail_unavailable");

        _videos.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_youtube_outage_is_reported_as_unavailable_not_as_a_crash()
    {
        _youtube.GetAsync(VideoId, Arg.Any<CancellationToken>()).Returns<Task<VideoCandidate?>>(_ => throw new ExternalServiceException("quota"));

        (await SelectAsync()).Error!.Code.ShouldBe("youtube_unavailable");
        (await VideoHandler.Handle(new SearchVideosQuery("fort"), Failing(), _quota, _options, _clock, CancellationToken.None)).Error!.Code.ShouldBe("youtube_unavailable");
    }

    private static readonly DateOnly Today = VideoQuotaClock.DayOf(Now);

    private static VideoCandidate Candidate(string id) => new(id, "Titre " + id, "Chaîne", $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", null);

    [Fact]
    public async Task A_search_costs_one_hundred_units_and_a_repeated_search_is_free_for_a_day()
    {
        _youtube.SearchAsync(Arg.Any<string>(), 8, Arg.Any<CancellationToken>()).Returns([Candidate("abcdefghijk")]);

        var first = await VideoHandler.Handle(new SearchVideosQuery("  Fort  Saint-Jean "), _youtube, _quota, _options, _clock, CancellationToken.None);
        var again = await VideoHandler.Handle(new SearchVideosQuery("fort saint-jean"), _youtube, _quota, _options, _clock, CancellationToken.None);

        first.Value!.ShouldHaveSingleItem().VideoId.ShouldBe("abcdefghijk");
        again.Value!.ShouldHaveSingleItem().VideoId.ShouldBe("abcdefghijk");
        await _youtube.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        _quota.Units[Today].ShouldBe(100);
    }

    [Fact]
    public async Task A_cached_search_is_not_reused_after_the_cache_window()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        _youtube.SearchAsync(Arg.Any<string>(), 8, Arg.Any<CancellationToken>()).Returns([Candidate("abcdefghijk")]);

        await VideoHandler.Handle(new SearchVideosQuery("fort"), _youtube, _quota, _options, time, CancellationToken.None);
        time.Advance(TimeSpan.FromHours(25));
        await VideoHandler.Handle(new SearchVideosQuery("fort"), _youtube, _quota, _options, time, CancellationToken.None);

        await _youtube.Received(2).SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_search_that_would_go_over_the_day_is_refused_before_it_is_sent_and_tomorrow_it_works_again()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now);
        _quota.Units[Today] = 9_950;
        _youtube.SearchAsync(Arg.Any<string>(), 8, Arg.Any<CancellationToken>()).Returns([Candidate("abcdefghijk")]);

        var refused = await VideoHandler.Handle(new SearchVideosQuery("fort"), _youtube, _quota, _options, time, CancellationToken.None);

        refused.Error!.Code.ShouldBe("youtube_quota_exhausted");
        refused.Error.Message.ShouldContain("9950 of 10000");
        await _youtube.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        time.Advance(TimeSpan.FromHours(24));
        (await VideoHandler.Handle(new SearchVideosQuery("fort"), _youtube, _quota, _options, time, CancellationToken.None)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task When_youtube_says_the_quota_is_used_up_the_day_is_marked_full()
    {
        _youtube.SearchAsync(Arg.Any<string>(), 8, Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VideoCandidate>>>(_ => throw new VideoQuotaExceededException("full"));

        var result = await VideoHandler.Handle(new SearchVideosQuery("fort"), _youtube, _quota, _options, _clock, CancellationToken.None);

        result.Error!.Code.ShouldBe("youtube_quota_exhausted");
        _quota.Units[Today].ShouldBe(10_000);
        _quota.Searches.ShouldBeEmpty("a failed search is not cached");
        (await VideoHandler.Handle(new SearchVideosQuery("autre"), _youtube, _quota, _options, _clock, CancellationToken.None)).Error!.Code.ShouldBe("youtube_quota_exhausted");
        await _youtube.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_outage_costs_nothing_and_is_not_cached()
    {
        (await VideoHandler.Handle(new SearchVideosQuery("fort"), Failing(), _quota, _options, _clock, CancellationToken.None)).Error!.Code.ShouldBe("youtube_unavailable");

        _quota.Units.ShouldBeEmpty();
        _quota.Searches.ShouldBeEmpty();
    }

    [Fact]
    public async Task Selecting_a_video_costs_one_unit_and_stops_when_the_day_is_full()
    {
        (await SelectAsync()).IsSuccess.ShouldBeTrue();
        _quota.Units[Today].ShouldBe(1);

        _quota.Units[Today] = 10_000;
        _youtube.GetAsync("ZYXWVUTSRQP", Arg.Any<CancellationToken>()).Returns(Candidate("ZYXWVUTSRQP"));
        (await SelectAsync("ZYXWVUTSRQP")).Error!.Code.ShouldBe("youtube_quota_exhausted");
        await _youtube.DidNotReceive().GetAsync("ZYXWVUTSRQP", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_quota_status_says_what_is_left_and_when_it_resets()
    {
        _quota.Units[Today] = 2_350;

        var status = (await VideoQuotaHandler.Handle(new GetVideoQuotaQuery(), _quota, _options, _clock, CancellationToken.None)).Value!;

        status.UnitsUsed.ShouldBe(2_350);
        status.Remaining.ShouldBe(7_650);
        status.SearchesLeft.ShouldBe(76);
        status.ResetsAt.ShouldBeGreaterThan(Now);
        status.ResetsAt.ShouldBeLessThanOrEqualTo(Now.AddHours(25));
    }

    [Fact]
    public void The_quota_day_follows_midnight_in_california_not_utc()
    {
        // 06:59 UTC on 1 Oct is 23:59 on 30 Sep in Pacific daylight time; one minute later the quota resets.
        VideoQuotaClock.DayOf(new DateTimeOffset(2026, 10, 1, 6, 59, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 9, 30));
        VideoQuotaClock.DayOf(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero)).ShouldBe(new DateOnly(2026, 10, 1));
        VideoQuotaClock.ResetOf(new DateOnly(2026, 9, 30)).ShouldBe(new DateTimeOffset(2026, 10, 1, 7, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Fort  Saint-Jean ", "fort saint-jean")]
    [InlineData("  LA GARDE", "la garde")]
    public void Queries_differing_by_case_and_spaces_share_one_cache_key(string typed, string key) => VideoQuotaClock.Key(typed).ShouldBe(key);

    private static IVideoSearch Failing()
    {
        var search = Substitute.For<IVideoSearch>();
        search.IsConfigured.Returns(true);
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VideoCandidate>>>(_ => throw new ExternalServiceException("down"));
        return search;
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task A_search_needs_a_few_characters(string query) =>
        (await VideoHandler.Handle(new SearchVideosQuery(query), _youtube, _quota, _options, _clock, CancellationToken.None)).Error!.Code.ShouldBe("validation");

    [Fact]
    public async Task A_published_place_is_published_again_with_its_wikipedia_and_video_links()
    {
        _places.FindAsync(PlaceId, Arg.Any<CancellationToken>()).Returns(Place(PlaceStatus.Published));
        _places.GetInterestsAsync(PlaceId, Arg.Any<CancellationToken>()).Returns([("history.military", 0.9)]);
        _places.GetScoreDetailAsync(PlaceId, Arg.Any<CancellationToken>()).Returns(new PlaceScoreDetail(70, 50, false, 1, 2, 3, false, false));
        _destinations.FindAsync("marseille", Arg.Any<CancellationToken>()).Returns(new DestinationConfig("marseille", "Marseille", new GeoPoint(43.3, 5.4), 5, 43, 6, 44, "https://x", null));

        (await SelectAsync()).IsSuccess.ShouldBeTrue();

        await _places.Received(1).PublishAsync(PlaceId, 1, Arg.Is<object>(published =>
            published is PoiPublishedV1 && ((PoiPublishedV1)published).Links!.Count == 2
            && ((PoiPublishedV1)published).Links!.Any(link => link.Kind == "wikipedia" && link.Url == "https://fr.wikipedia.org/wiki/Fort_Saint-Jean_%28Marseille%29")
            && ((PoiPublishedV1)published).Links!.Any(link => link.Kind == "youtube" && link.VideoId == VideoId && link.ThumbnailPath == $"thumbs/{PlaceId}/{VideoId}.jpg")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_place_that_is_not_published_is_not_published_by_a_video_choice()
    {
        (await SelectAsync()).IsSuccess.ShouldBeTrue();

        await _places.DidNotReceive().PublishAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_video_forgets_it_and_an_unknown_one_is_refused()
    {
        await SelectAsync();

        (await VideoHandler.Handle(new RemoveVideoCommand(PlaceId, "ZYXWVUTSRQP"), _places, _videos, _destinations, _clock, CancellationToken.None)).Error!.Code.ShouldBe("video_not_found");
        (await VideoHandler.Handle(new RemoveVideoCommand(PlaceId, VideoId), _places, _videos, _destinations, _clock, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        _videos.Items.ShouldBeEmpty();
    }
}
