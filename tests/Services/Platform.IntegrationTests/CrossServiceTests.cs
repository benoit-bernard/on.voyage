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
    private const string AdminKey = "test-admin-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<PlatformApiMarker> _platform = null!;
    private WebApplicationFactory<CatalogApiMarker> _catalog = null!;
    private HttpClient _platformClient = null!;
    private HttpClient _catalogClient = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _platform = new WebApplicationFactory<PlatformApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:AdminApiKey", AdminKey);
        });
        _platformClient = _platform.CreateClient();

        _catalog = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
        });
        _catalogClient = _catalog.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _platformClient.Dispose();
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

        var problem = await _catalogClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/destinations/marseille") { Headers = { { "X-App-Version", "0.1.0" } } }, Ct);
        (await problem.Content.ReadAsStringAsync(Ct)).ShouldContain("https://on.voyage/problems/upgrade_required");

        var raise = new HttpRequestMessage(HttpMethod.Put, "/api/platform/v1/admin/config/app")
        {
            Headers = { { "X-Admin-Key", AdminKey } },
            Content = JsonContent.Create(new { value = new { min_app_version = "2.0.0" } }),
        };
        (await _platformClient.SendAsync(raise, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventuallyAsync(async () => await CatalogStatusAsync("1.5.0") == HttpStatusCode.UpgradeRequired)).ShouldBeTrue("config change never reached catalog");

        var lower = new HttpRequestMessage(HttpMethod.Put, "/api/platform/v1/admin/config/app")
        {
            Headers = { { "X-Admin-Key", AdminKey } },
            Content = JsonContent.Create(new { value = new { min_app_version = "0.5.0" } }),
        };
        (await _platformClient.SendAsync(lower, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EventuallyAsync(async () => await CatalogStatusAsync("1.5.0") == HttpStatusCode.OK)).ShouldBeTrue("lowering the minimum never reached catalog");
    }

    [Fact]
    public async Task Requests_without_an_app_version_are_never_gated()
    {
        (await _catalogClient.GetAsync("/api/catalog/v1/destinations/marseille", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
