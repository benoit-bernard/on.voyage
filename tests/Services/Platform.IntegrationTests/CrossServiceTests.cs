using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Catalog.Api;
using OnVoyage.Platform.Api;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

/// <summary>Both services on one database, talking only through the PostgreSQL queue transport and outbox (§9.5).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CrossServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string AdminEmail = "boss@example.org";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformHarness _platform = null!;
    private WebApplicationFactory<CatalogApiMarker> _catalog = null!;
    private HttpClient _catalogClient = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _platform = await PlatformHarness.StartAsync(postgres, connection, AdminEmail);

        _catalog = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
        });
        _catalogClient = _catalog.CreateClient();
        _catalogClient.Authenticate();
    }

    public async ValueTask DisposeAsync()
    {
        _catalogClient.Dispose();
        await _platform.DisposeAsync();
        await _catalog.DisposeAsync();
    }

    private async Task<HttpStatusCode> CatalogStatusAsync(string appVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/destinations/marseille");
        request.Headers.Add("X-App-Version", appVersion);
        return (await _catalogClient.SendAsync(request, Ct)).StatusCode;
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250, Ct);
        }

        return false;
    }

    [Fact]
    public async Task Seeded_minimum_version_reaches_catalog_and_later_changes_follow()
    {
        // Platform seeded annexe E (app.min_app_version = 1.0.0) and published it; Catalog's snapshot catches up through the queue.
        (await EventuallyAsync(async () => await CatalogStatusAsync("0.9.0") == HttpStatusCode.UpgradeRequired)).ShouldBeTrue("seeded config never reached catalog");
        (await CatalogStatusAsync("1.0.0")).ShouldBe(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/destinations/marseille");
        request.Headers.Add("X-App-Version", "0.1.0");
        var problem = await _catalogClient.SendAsync(request, Ct);
        (await problem.Content.ReadAsStringAsync(Ct)).ShouldContain("https://on.voyage/problems/upgrade_required");

        var admin = await _platform.SignInAsync(AdminEmail, await _platform.AnonymousAsync());
        (await _platform.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/app", admin, new { value = new { min_app_version = "2.0.0" } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventuallyAsync(async () => await CatalogStatusAsync("1.5.0") == HttpStatusCode.UpgradeRequired)).ShouldBeTrue("config change never reached catalog");

        (await _platform.SendAsync(HttpMethod.Put, "/api/platform/v1/admin/config/app", admin, new { value = new { min_app_version = "0.5.0" } })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventuallyAsync(async () => await CatalogStatusAsync("1.5.0") == HttpStatusCode.OK)).ShouldBeTrue("lowering the minimum never reached catalog");
    }

    [Fact]
    public async Task A_token_issued_by_platform_is_accepted_by_catalog_and_an_anonymous_one_too()
    {
        var session = await _platform.AnonymousAsync();
        using var client = _catalog.CreateClient();
        client.Authenticate(session.AccessToken);

        (await client.GetAsync("/api/catalog/v1/destinations/marseille", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var verified = await _platform.SignInAsync("claire@example.org", session);
        client.Authenticate(verified.AccessToken);
        (await client.GetAsync("/api/catalog/v1/pois/mucem", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requests_without_an_app_version_are_never_gated()
    {
        (await _catalogClient.GetAsync("/api/catalog/v1/destinations/marseille", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
