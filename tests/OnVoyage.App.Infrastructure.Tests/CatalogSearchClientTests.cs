using System.Net;
using System.Net.Http.Json;
using OnVoyage.App.Infrastructure.Http;
using OnVoyage.Catalog.Contracts;

namespace OnVoyage.App.Infrastructure.Tests;

public sealed class CatalogSearchClientTests
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

    [Fact]
    public async Task The_typed_text_is_url_encoded_and_nothing_else_is_sent()
    {
        var poi = new PoiSummaryDto(Guid.NewGuid(), "cathedrale-de-la-major", "Cathédrale de la Major", "religion", 43.3, 5.36, 0.7, 0.8, 3, false, null, null, new Dictionary<string, double>());
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { poi }) });
        var client = new HttpCatalogClient(new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") });

        var found = await client.SearchAsync("marseille", "cathédrale & co/é", 20, Ct);

        handler.Urls.ShouldBe(["/api/catalog/v1/search?q=cath%C3%A9drale%20%26%20co%2F%C3%A9&destination=marseille&limit=20"]);
        found.Single().Slug.ShouldBe("cathedrale-de-la-major");
    }

    [Fact]
    public async Task A_server_error_is_a_network_failure_for_the_caller()
    {
        var client = new HttpCatalogClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))) { BaseAddress = new Uri("http://gateway/") });

        await Should.ThrowAsync<HttpRequestException>(() => client.SearchAsync("marseille", "fort", 20, Ct));
    }
}
