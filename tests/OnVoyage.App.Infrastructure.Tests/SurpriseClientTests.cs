using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Infrastructure.Http;
using OnVoyage.Discovery.Contracts;

namespace OnVoyage.App.Infrastructure.Tests;

public sealed class SurpriseClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpDiscoveryClient Client(StubHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") });

    [Fact]
    public async Task The_position_goes_as_query_parameters_rounded_to_three_decimals()
    {
        var item = new RecommendationItemDto(Guid.NewGuid(), "fort", "Fort", 0.5, 70, new WhyDto("hidden_gem", new Dictionary<string, string>()), true, null);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(item) });

        var result = await Client(handler).GetSurpriseAsync(43.296482, 5.369781, 5000, Ct);

        handler.Urls.ShouldBe(["/api/discovery/v1/surprise?radius=5000&lat=43.296&lng=5.37"]);
        result!.Slug.ShouldBe("fort");
    }

    [Fact]
    public async Task Without_a_position_only_the_radius_is_sent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await Client(handler).GetSurpriseAsync(null, null, 5000, Ct)).ShouldBeNull("404 surprise_not_found: nothing left to propose");

        handler.Urls.ShouldBe(["/api/discovery/v1/surprise?radius=5000"]);
    }

    [Fact]
    public async Task A_server_error_is_a_network_failure_for_the_caller()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Should.ThrowAsync<HttpRequestException>(() => Client(handler).GetSurpriseAsync(null, null, 5000, Ct));
    }
}
