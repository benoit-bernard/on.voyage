using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OnVoyage.App.Core.Analytics;
using OnVoyage.App.Infrastructure.Http;
using OnVoyage.Insights.Contracts;

namespace OnVoyage.App.Infrastructure.Tests;

public sealed class AnalyticsTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }

    private static readonly EventDto[] Batch =
    [
        new(Guid.NewGuid(), "poi_viewed", DateTimeOffset.UtcNow, Guid.NewGuid(), "1.0.0", Platforms.Ios, new Dictionary<string, JsonElement> { ["poi_id"] = JsonSerializer.SerializeToElement("p1") }),
    ];

    private static HttpAnalyticsTransport Transport(StubHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") });

    [Fact]
    public async Task A_batch_is_posted_to_insights_in_the_shape_the_service_reads()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EventBatchResponse(1, 0, 0)) });

        (await Transport(handler).SendAsync(Batch, Ct)).ShouldBe(AnalyticsSendOutcome.Sent);

        var request = handler.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri!.AbsolutePath.ShouldBe("/api/insights/v1/events");
        using var body = JsonDocument.Parse(request.Body);
        var sent = body.RootElement.GetProperty("events")[0];
        sent.GetProperty("name").GetString().ShouldBe("poi_viewed");
        sent.GetProperty("platform").GetString().ShouldBe("ios");
        sent.GetProperty("props").GetProperty("poi_id").GetString().ShouldBe("p1");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, AnalyticsSendOutcome.Sent)]
    [InlineData(HttpStatusCode.BadRequest, AnalyticsSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.UpgradeRequired, AnalyticsSendOutcome.Rejected)]
    [InlineData(HttpStatusCode.Unauthorized, AnalyticsSendOutcome.Retry)]
    [InlineData(HttpStatusCode.Forbidden, AnalyticsSendOutcome.Retry)]
    [InlineData(HttpStatusCode.RequestTimeout, AnalyticsSendOutcome.Retry)]
    [InlineData(HttpStatusCode.TooManyRequests, AnalyticsSendOutcome.Retry)]
    [InlineData(HttpStatusCode.BadGateway, AnalyticsSendOutcome.Retry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AnalyticsSendOutcome.Retry)]
    public async Task Statuses_tell_whether_to_keep_the_events(HttpStatusCode status, AnalyticsSendOutcome expected) =>
        (await Transport(new StubHandler(_ => new HttpResponseMessage(status))).SendAsync(Batch, Ct)).ShouldBe(expected);

    [Fact]
    public async Task Network_faults_and_timeouts_mean_try_again_later_and_never_throw()
    {
        (await Transport(new StubHandler(_ => throw new HttpRequestException("down"))).SendAsync(Batch, Ct)).ShouldBe(AnalyticsSendOutcome.Retry);
        (await Transport(new StubHandler(_ => throw new TaskCanceledException("timeout"))).SendAsync(Batch, Ct)).ShouldBe(AnalyticsSendOutcome.Retry);
    }
}
