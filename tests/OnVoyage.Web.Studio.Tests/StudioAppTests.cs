using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Web.Studio.Tests;

/// <summary>Stands in for the Gateway: Platform's sign-in, whose session carries the roles of the e-mail, and the Studio reads.</summary>
internal sealed class FakeGateway : HttpMessageHandler
{
    public const string Code = "123456";

    public List<(string Method, string Path, string? Authorization)> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Calls)
        {
            Calls.Add((request.Method.Method, path, request.Headers.Authorization?.ToString()));
        }

        HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };

        if (path.EndsWith("/auth/otp/request", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new { }));
        }

        if (path.EndsWith("/auth/otp/verify", StringComparison.Ordinal))
        {
            var verify = request.Content!.ReadFromJsonAsync<VerifyOtpRequest>(cancellationToken).GetAwaiter().GetResult()!;
            if (verify.Code != Code)
            {
                return Task.FromResult(Json(new { title = "Code incorrect ou expiré." }, HttpStatusCode.BadRequest));
            }

            var creator = verify.Email.StartsWith("creator", StringComparison.Ordinal);
            return Task.FromResult(Json(new AuthSessionDto(
                creator ? "access-creator" : "access-user", DateTimeOffset.UtcNow.AddHours(1), "refresh", DateTimeOffset.UtcNow.AddDays(30), Guid.NewGuid(), false, verify.Email, creator ? ["creator"] : [])));
        }

        if (path.EndsWith("/studio/registration", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new { registered = false, currentTermsVersion = "2026-10", creatorId = (Guid?)null, handle = (string?)null, status = (string?)null, termsAccepted = false }));
        }

        if (path.EndsWith("/studio/profile", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new
            {
                id = Guid.NewGuid(),
                handle = "marie",
                displayName = "Marie",
                languages = new[] { "fr" },
                specialties = new[] { "history" },
                destinationIds = Array.Empty<Guid>(),
                links = Array.Empty<object>(),
                status = "draft",
                founding = false,
                termsVersion = "2026-10",
                isNew = true,
                contents = Array.Empty<object>(),
                placeLinks = Array.Empty<object>(),
                tips = Array.Empty<object>(),
            }));
        }

        return Task.FromResult(Json(Array.Empty<object>()));
    }
}

public sealed partial class StudioAppTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly WebApplicationFactory<StudioAppMarker> _factory;

    public StudioAppTests()
    {
        _factory = new WebApplicationFactory<StudioAppMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:BaseUrl", "http://gateway.test");
            builder.ConfigureTestServices(services => services.AddHttpClient("gateway").ConfigurePrimaryHttpMessageHandler(() => _gateway));
        });
    }

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"|<input[^>]*value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenPattern();

    private HttpClient NewBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string tokenPage, params (string Key, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(tokenPage, TestContext.Current.CancellationToken);
        var match = TokenPattern().Match(html);
        match.Success.ShouldBeTrue($"no antiforgery token on {tokenPage}");
        var token = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), new KeyValuePair<string, string>("__RequestVerificationToken", token)]);
        return await browser.PostAsync(url, form, TestContext.Current.CancellationToken);
    }

    private static async Task SignInAsync(HttpClient browser, string email)
    {
        (await PostFormAsync(browser, "/login/code", "/login", ("email", email))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var response = await PostFormAsync(browser, "/login/verify", $"/login?step=code&email={Uri.EscapeDataString(email)}", ("email", email), ("code", FakeGateway.Code));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/studio");
    }

    [Fact]
    public async Task Someone_who_is_not_signed_in_is_sent_to_the_sign_in_page()
    {
        using var browser = NewBrowser();

        var response = await browser.GetAsync("/studio/profile", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.ShouldBe("/login");
    }

    [Fact]
    public async Task A_signed_in_account_without_the_creator_role_gets_403_on_every_studio_page_but_the_two_doors()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "voyageur@onvoyage.test");

        foreach (var path in new[] { "/studio/profile", "/studio/contents", "/studio/tips", "/studio/anything" })
        {
            (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        (await browser.GetAsync("/studio/join", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _gateway.Calls.ShouldNotContain(call => call.Path.Contains("/studio/profile", StringComparison.Ordinal), "a denied page must not call the creator APIs");
    }

    [Fact]
    public async Task A_creator_signs_in_with_the_code_and_the_pages_call_the_gateway_with_their_own_token()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "creator@onvoyage.test");

        var home = await browser.GetAsync("/studio", TestContext.Current.CancellationToken);
        var profile = await browser.GetAsync("/studio/profile", TestContext.Current.CancellationToken);

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        profile.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await profile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Votre profil");
        html.ShouldContain("href=\"/studio/contents\"");
        _gateway.Calls.ShouldContain(call => call.Path == "/api/creators/v1/studio/profile" && call.Authorization == "Bearer access-creator");
    }

    [Fact]
    public async Task The_sign_up_page_shows_the_terms_to_an_account_that_is_not_a_creator_yet()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "voyageur@onvoyage.test");

        var html = await browser.GetStringAsync("/studio/join", TestContext.Current.CancellationToken);

        html.ShouldContain("CGU créateurs");
        html.ShouldContain("2026-10");
        _gateway.Calls.ShouldContain(call => call.Path == "/api/creators/v1/studio/registration" && call.Authorization == "Bearer access-user");
    }

    [Fact]
    public async Task Signing_out_ends_the_session_for_good_and_the_sign_in_redirect_never_leaves_the_site()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "creator@onvoyage.test");
        (await browser.GetAsync("/studio", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await PostFormAsync(browser, "/logout", "/studio")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await browser.GetAsync("/studio", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Redirect);

        using var other = NewBrowser();
        await PostFormAsync(other, "/login/code", "/login", ("email", "creator@onvoyage.test"));
        var response = await PostFormAsync(other, "/login/verify", "/login?step=code&email=creator%40onvoyage.test", ("email", "creator@onvoyage.test"), ("code", FakeGateway.Code), ("returnUrl", "https://evil.example/"));
        response.Headers.Location!.OriginalString.ShouldBe("/studio");
    }

    [Fact]
    public async Task A_form_post_without_the_antiforgery_token_is_refused()
    {
        using var browser = NewBrowser();

        var response = await browser.PostAsync("/login/code", new FormUrlEncodedContent([new KeyValuePair<string, string>("email", "creator@onvoyage.test")]), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _gateway.Calls.ShouldBeEmpty();
    }
}
