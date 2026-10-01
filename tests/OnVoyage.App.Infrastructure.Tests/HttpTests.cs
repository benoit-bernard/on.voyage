using System.Net;
using System.Net.Http.Json;
using NSubstitute;
using OnVoyage.App.Core.Auth;
using OnVoyage.App.Infrastructure.Auth;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Infrastructure.Tests;

public sealed class HttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string title = "Title") =>
        new(status) { Content = JsonContent.Create(new { type = $"https://on.voyage/problems/{code}", title, status = (int)status }) };

    private static HttpAuthClient Client(StubHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") });

    // ---- HttpAuthClient

    [Fact]
    public async Task Problem_details_become_stable_failure_codes()
    {
        var client = Client(new StubHandler(_ => Problem(HttpStatusCode.BadRequest, "otp_expired", "Expired")));

        var result = await client.VerifyCodeAsync("a@b.org", "123456", "token", Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Failure!.Code.ShouldBe("otp_expired");
        result.Failure.Message.ShouldBe("Expired");
    }

    [Fact]
    public async Task Retry_after_is_carried_on_throttling()
    {
        var response = Problem(HttpStatusCode.TooManyRequests, "otp_cooldown");
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));

        var result = await Client(new StubHandler(_ => response)).RequestCodeAsync("a@b.org", null, Ct);

        result.Failure!.Code.ShouldBe("otp_cooldown");
        result.Failure.RetryAfterSeconds.ShouldBe(42);
    }

    [Fact]
    public async Task Accepted_without_a_body_is_a_success()
    {
        var result = await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted))).RequestCodeAsync("a@b.org", null, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Non_problem_errors_and_network_faults_do_not_throw()
    {
        (await Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>") })).StartAnonymousAsync(Ct))
            .Failure!.Code.ShouldBe("http_502");

        var offline = Client(new StubHandler(_ => throw new HttpRequestException("down")));
        (await offline.StartAnonymousAsync(Ct)).Failure!.Code.ShouldBe(AuthFailure.Network);
    }

    [Fact]
    public async Task The_access_token_is_sent_only_where_one_is_given_and_the_code_goes_in_the_body_not_the_url()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        var client = Client(handler);

        await client.RequestCodeAsync("a@b.org", "tok", Ct);
        await client.RefreshAsync("refresh", Ct);

        handler.Requests[0].Headers.Authorization!.Parameter.ShouldBe("tok");
        handler.Requests[1].Headers.Authorization.ShouldBeNull();
        handler.Requests[0].RequestUri!.Query.ShouldBeEmpty();
    }

    // ---- BearerTokenHandler

    private static (HttpClient Client, StubHandler Inner, ISessionProvider Sessions) Bearer(Func<HttpRequestMessage, HttpResponseMessage> respond, params string[] tokens)
    {
        var sessions = Substitute.For<ISessionProvider>();
        var queue = new Queue<string>(tokens);
        sessions.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(_ => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        var inner = new StubHandler(respond);
        var handler = new BearerTokenHandler(sessions, new AppVersion("1.2.3")) { InnerHandler = inner };
        return (new HttpClient(handler) { BaseAddress = new Uri("http://gateway/") }, inner, sessions);
    }

    [Fact]
    public async Task Requests_carry_the_bearer_token_and_the_app_version()
    {
        var (client, inner, _) = Bearer(_ => new HttpResponseMessage(HttpStatusCode.OK), "tok-1");

        await client.GetAsync("api/catalog/v1/x", Ct);

        inner.Requests.Single().Headers.Authorization!.Parameter.ShouldBe("tok-1");
        inner.Requests.Single().Headers.GetValues("X-App-Version").ShouldBe(["1.2.3"]);
    }

    [Fact]
    public async Task A_401_triggers_one_refresh_and_one_retry_with_the_new_token()
    {
        var calls = 0;
        var (client, inner, sessions) = Bearer(request => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.OK), "old", "new");

        var response = await client.PostAsJsonAsync("api/catalog/v1/x", new { a = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Requests.Count.ShouldBe(2);
        inner.Requests[0].Headers.Authorization!.Parameter.ShouldBe("old");
        inner.Requests[1].Headers.Authorization!.Parameter.ShouldBe("new");
        sessions.Received(1).InvalidateAccessToken();
    }

    [Fact]
    public async Task A_second_401_is_returned_to_the_caller_without_looping()
    {
        var (client, inner, _) = Bearer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized), "a", "b");

        var response = await client.GetAsync("api/catalog/v1/x", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        inner.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_retry_resends_the_original_body()
    {
        var bodies = new List<string>();
        var calls = 0;
        var (client, _, _) = Bearer(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync(Ct).GetAwaiter().GetResult());
            return ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : new HttpResponseMessage(HttpStatusCode.OK);
        }, "old", "new");

        await client.PostAsJsonAsync("api/catalog/v1/x", new { value = 7 }, Ct);

        bodies.ShouldBe(["{\"value\":7}", "{\"value\":7}"]);
    }
}
