using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Web.Admin.Tests;

/// <summary>Stands in for the Gateway: Platform's sign-in and the few back-office reads the home page makes.</summary>
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

            var admin = verify.Email.StartsWith("admin", StringComparison.Ordinal);
            return Task.FromResult(Json(new AuthSessionDto(
                admin ? "access-admin" : "access-user", DateTimeOffset.UtcNow.AddHours(1), "refresh", DateTimeOffset.UtcNow.AddDays(30), Guid.NewGuid(), false, verify.Email, admin ? ["admin"] : [])));
        }

        if (path.EndsWith("/admin/destinations", StringComparison.Ordinal))
        {
            return Task.FromResult(Json(new[] { new { slug = "marseille", name = "Marseille", latitude = 43.3, longitude = 5.4 } }));
        }

        return Task.FromResult(Json(Array.Empty<object>()));
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Warnings { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (owner.Warnings)
                {
                    owner.Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}

public sealed partial class AdminAppTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly WebApplicationFactory<AdminAppMarker> _factory;

    public AdminAppTests()
    {
        _factory = new WebApplicationFactory<AdminAppMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:BaseUrl", "http://gateway.test");
            builder.ConfigureServices(services => services.AddLogging(logging => logging.AddProvider(_logs)));
            builder.ConfigureTestServices(services => services.AddHttpClient("gateway").ConfigurePrimaryHttpMessageHandler(() => _gateway));
        });
    }

    public void Dispose() => _factory.Dispose();

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"|<input[^>]*value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex TokenPattern();

    private HttpClient NewBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private static async Task<string> TokenAsync(HttpClient browser, string url)
    {
        var html = await browser.GetStringAsync(url, TestContext.Current.CancellationToken);
        var match = TokenPattern().Match(html);
        match.Success.ShouldBeTrue($"no antiforgery token on {url}");
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient browser, string url, string tokenPage, params (string Key, string Value)[] fields)
    {
        var token = await TokenAsync(browser, tokenPage);
        var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)), new KeyValuePair<string, string>("__RequestVerificationToken", token)]);
        return await browser.PostAsync(url, form, TestContext.Current.CancellationToken);
    }

    private static async Task SignInAsync(HttpClient browser, string email, string code = FakeGateway.Code)
    {
        (await PostFormAsync(browser, "/login/code", "/login", ("email", email))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var response = await PostFormAsync(browser, "/login/verify", $"/login?step=code&email={Uri.EscapeDataString(email)}", ("email", email), ("code", code));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldStartWith(code == FakeGateway.Code ? "/admin" : "/login");
    }

    [Fact]
    public async Task Someone_who_is_not_signed_in_is_sent_to_the_sign_in_page()
    {
        using var browser = NewBrowser();

        var response = await browser.GetAsync("/admin/places", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.ShouldBe("/login");
    }

    [Fact]
    public async Task A_signed_in_account_without_the_admin_role_gets_403_and_the_attempt_is_logged()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "voyageur@onvoyage.test");

        foreach (var path in new[] { "/admin", "/admin/places", "/admin/workshop/story/" + Guid.NewGuid(), "/admin/config" })
        {
            (await browser.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        _logs.Warnings.ShouldContain(warning => warning.Contains("Admin access denied", StringComparison.Ordinal) && warning.Contains("/admin/config", StringComparison.Ordinal));
        _gateway.Calls.ShouldNotContain(call => call.Path.Contains("/admin/", StringComparison.Ordinal), "a denied page must not call the back-office APIs");
    }

    [Fact]
    public async Task An_administrator_signs_in_with_the_code_and_the_pages_call_the_gateway_with_their_own_token()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "admin@onvoyage.test");

        var home = await browser.GetAsync("/admin", TestContext.Current.CancellationToken);

        home.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await home.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Accueil");
        _gateway.Calls.ShouldContain(call => call.Path.EndsWith("/admin/destinations", StringComparison.Ordinal) && call.Authorization == "Bearer access-admin");
    }

    [Fact]
    public async Task The_creator_screens_are_for_administrators_only_and_are_in_the_menu()
    {
        using var traveler = NewBrowser();
        await SignInAsync(traveler, "voyageur@onvoyage.test");
        foreach (var path in new[] { "/admin/creators", "/admin/creators/" + Guid.NewGuid(), "/admin/moderation" })
        {
            (await traveler.GetAsync(path, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        using var admin = NewBrowser();
        await SignInAsync(admin, "admin@onvoyage.test");

        var creators = await admin.GetAsync("/admin/creators", TestContext.Current.CancellationToken);
        var moderation = await admin.GetAsync("/admin/moderation", TestContext.Current.CancellationToken);

        creators.StatusCode.ShouldBe(HttpStatusCode.OK);
        moderation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await creators.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Créateurs fondateurs");
        html.ShouldContain("href=\"/admin/creators\"");
        html.ShouldContain("href=\"/admin/moderation\"");
        (await moderation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("Modération des créateurs");
        _gateway.Calls.ShouldContain(call => call.Path == "/api/creators/v1/admin/creators" && call.Authorization == "Bearer access-admin");
        _gateway.Calls.ShouldContain(call => call.Path == "/api/creators/v1/admin/moderation" && call.Authorization == "Bearer access-admin");
    }

    [Fact]
    public async Task A_wrong_code_does_not_sign_anyone_in()
    {
        using var browser = NewBrowser();

        await SignInAsync(browser, "admin@onvoyage.test", code: "000000");

        (await browser.GetAsync("/admin", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_for_good()
    {
        using var browser = NewBrowser();
        await SignInAsync(browser, "admin@onvoyage.test");
        (await browser.GetAsync("/admin", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await PostFormAsync(browser, "/logout", "/admin");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await browser.GetAsync("/admin", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        _gateway.Calls.ShouldContain(call => call.Path.EndsWith("/auth/signout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_form_post_without_the_antiforgery_token_is_refused()
    {
        using var browser = NewBrowser();

        var response = await browser.PostAsync("/login/code", new FormUrlEncodedContent([new KeyValuePair<string, string>("email", "admin@onvoyage.test")]), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_sign_in_redirect_never_leaves_the_site()
    {
        using var browser = NewBrowser();
        await PostFormAsync(browser, "/login/code", "/login", ("email", "admin@onvoyage.test"));

        var response = await PostFormAsync(browser, "/login/verify", "/login?step=code&email=admin%40onvoyage.test", ("email", "admin@onvoyage.test"), ("code", FakeGateway.Code), ("returnUrl", "https://evil.example/"));

        response.Headers.Location!.OriginalString.ShouldBe("/admin");
    }
}
