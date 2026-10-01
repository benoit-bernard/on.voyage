using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Catalog.Api;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;

namespace Catalog.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class ConfigSnapshotTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<CatalogApiMarker> _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Catalog:SeedDemoData", "true");
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
        });
        _client = _factory.CreateClient();
        _client.Authenticate();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<HttpStatusCode> StatusAsync(string appVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/v1/destinations/marseille");
        request.Headers.Add("X-App-Version", appVersion);
        return (await _client.SendAsync(request, Ct)).StatusCode;
    }

    private async Task ConsumeAsync(string json, int version)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new ConfigChangedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, "app", json, version), Ct);
    }

    [Fact]
    public async Task Without_a_snapshot_nothing_is_gated()
    {
        (await StatusAsync("0.0.1")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Newer_versions_apply_while_duplicates_and_stale_events_are_ignored()
    {
        await ConsumeAsync("""{"min_app_version":"2.0.0"}""", 2);
        (await StatusAsync("1.0.0")).ShouldBe(HttpStatusCode.UpgradeRequired);
        (await StatusAsync("2.0.0")).ShouldBe(HttpStatusCode.OK);

        // Redelivery of the same event and an older one arriving late must not change anything.
        await ConsumeAsync("""{"min_app_version":"2.0.0"}""", 2);
        await ConsumeAsync("""{"min_app_version":"0.1.0"}""", 1);
        (await StatusAsync("1.0.0")).ShouldBe(HttpStatusCode.UpgradeRequired);

        await ConsumeAsync("""{"min_app_version":"0.1.0"}""", 3);
        (await StatusAsync("1.0.0")).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Malformed_version_headers_and_values_do_not_block_requests()
    {
        await ConsumeAsync("""{"min_app_version":"not-a-version"}""", 1);

        (await StatusAsync("1.0.0")).ShouldBe(HttpStatusCode.OK);
        (await StatusAsync("garbage")).ShouldBe(HttpStatusCode.OK);
    }
}
