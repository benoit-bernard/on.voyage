using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Infrastructure.Http;
using OnVoyage.Creators.Contracts;

namespace OnVoyage.App.Infrastructure.Tests;

public sealed class CreatorsClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static HttpCreatorsClient Client(StubHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") });

    [Fact]
    public async Task The_block_of_a_place_is_asked_by_identifier_only_and_a_missing_creator_is_null()
    {
        var poi = Guid.NewGuid();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.Contains("/pois/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new PoiCreatorsDto(poi, 0, [])) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);

        (await client.GetPoiCreatorsAsync(poi, 50, Ct))!.Total.ShouldBe(0);
        (await client.GetCreatorAsync("ghost", Ct)).ShouldBeNull();

        handler.Requests[0].Url.ShouldBe($"/api/creators/v1/pois/{poi}/contents?limit=50");
        handler.Requests[1].Url.ShouldBe("/api/creators/v1/creators/ghost");
        handler.Requests.ShouldAllBe(r => !r.Url.Contains("lat", StringComparison.OrdinalIgnoreCase) && !r.Url.Contains("lng", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Following_is_a_put_and_unfollowing_a_delete_and_a_withdrawn_creator_gives_null()
    {
        var creator = Guid.NewGuid();
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith(creator.ToString(), StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new FollowStateDto(creator, request.Method == HttpMethod.Put)) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = Client(handler);

        (await client.SetFollowAsync(creator, true, Ct))!.Following.ShouldBeTrue();
        (await client.SetFollowAsync(creator, false, Ct))!.Following.ShouldBeFalse();
        (await client.SetFollowAsync(Guid.NewGuid(), true, Ct)).ShouldBeNull();

        handler.Requests[0].Method.ShouldBe(HttpMethod.Put);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Delete);
        handler.Requests[0].Url.ShouldBe($"/api/creators/v1/me/follows/{creator}");
    }

    [Fact]
    public async Task A_report_posts_the_target_and_a_reason_from_the_closed_list_and_nothing_else()
    {
        var target = Guid.NewGuid();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new ReportReceiptDto(Guid.NewGuid())) });

        await Client(handler).ReportAsync(new ReportRequest("content", target, ReportReasons.Misleading), Ct);

        handler.Requests.ShouldHaveSingleItem().Url.ShouldBe("/api/creators/v1/reports");
        handler.Requests[0].Body.ShouldBe($$"""{"targetType":"content","targetId":"{{target}}","reason":"misleading"}""");
    }

    [Fact]
    public async Task Affinities_come_from_discovery_and_a_server_error_is_an_exception_the_service_can_absorb()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = Client(handler);

        await Should.ThrowAsync<HttpRequestException>(() => client.GetCreatorsForMeAsync("marseille", Ct));

        handler.Requests.ShouldHaveSingleItem().Url.ShouldBe("/api/discovery/v1/creators/for-me?destination=marseille&limit=50");
    }
}
