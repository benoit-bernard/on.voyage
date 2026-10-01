using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Platform.Api;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

/// <summary>Factory and Platform on one database: an admin write in Factory reaches Platform's journal through the queue (SEC-10, T-409).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuditJournalTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private FactoryHarness _f = null!;
    private WebApplicationFactory<PlatformApiMarker> _platform = null!;
    private HttpClient _platformAdmin = null!;
    private Guid _adminId;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(FactoryHarness.Osm2pgsqlAvailable, "osm2pgsql is not installed (apt install osm2pgsql).");
        _f = await FactoryHarness.StartAsync(postgres);
        await _f.RunPipelineAsync();
        _platform = new WebApplicationFactory<PlatformApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", _f.Connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
        });
        _adminId = Guid.NewGuid();
        _platformAdmin = _platform.CreateClient();
        _platformAdmin.Authenticate(TestTokens.Mint(_adminId, anonymous: false, roles: ["admin"]));
        await _platformAdmin.GetAsync("/api/platform/v1/admin/flags", Ct); // starts the host and its queue listener
    }

    public async ValueTask DisposeAsync()
    {
        _platformAdmin?.Dispose();
        if (_platform is not null)
        {
            await _platform.DisposeAsync();
        }

        if (_f is not null)
        {
            await _f.DisposeAsync();
        }
    }

    private async Task<List<AdminActionDto>> JournalAsync(string query) =>
        (await _platformAdmin.GetFromJsonAsync<List<AdminActionDto>>($"/api/platform/v1/admin/audit?{query}", Ct))!;

    [Fact]
    public async Task An_admin_action_in_factory_appears_in_the_platform_journal_with_its_actor_and_request()
    {
        using var admin = _f.ApiClient(TestTokens.Mint(_adminId, anonymous: false, roles: ["admin"]));
        var garde = (await _f.GetPlaceAsync("notre-dame-de-la-garde")).GetProperty("id").GetGuid();

        (await admin.GetAsync($"/api/factory/v1/admin/places/{garde}", Ct)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/factory/v1/admin/places/{garde}/editorial", new { importanceOverride = 42, editoriallySaturated = true }, Ct)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"/api/factory/v1/admin/places/{garde}/editorial", new { importanceOverride = 420 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        List<AdminActionDto> entries = [];
        (await FactoryHarness.EventuallyAsync(async () =>
        {
            entries = [.. (await JournalAsync("service=factory")).Where(entry => entry.Target.Contains(garde.ToString(), StringComparison.Ordinal))];
            return entries.Count == 2;
        })).ShouldBeTrue("the journal never received the two writes");

        entries.ShouldAllBe(entry => entry.Actor == _adminId.ToString() && entry.Action.StartsWith("PUT", StringComparison.Ordinal));
        entries.Select(entry => entry.Status).Order().ShouldBe([200, 400]);
        var accepted = entries.Single(entry => entry.Status == 200);
        accepted.Summary.ShouldNotBeNull().ShouldContain("42");
        accepted.Summary.ShouldContain("importanceOverride");

        // The local log and the projection agree, and the projection holds each event once.
        (await _f.CountAsync("select count(*) from factory.audit_log")).ShouldBe(await _f.CountAsync("select count(*) from platform.admin_audit where service = 'factory'"));
    }

    [Fact]
    public async Task Writes_from_both_services_share_one_journal_that_only_an_admin_can_read()
    {
        using var admin = _f.ApiClient(TestTokens.Mint(_adminId, anonymous: false, roles: ["admin"]));
        (await admin.PutAsJsonAsync("/api/factory/v1/admin/pronunciations/marseille/Canebière", new { replacement = "Canebiaire" }, Ct)).EnsureSuccessStatusCode();
        (await _platformAdmin.PutAsJsonAsync("/api/platform/v1/admin/config/reco", new { value = new { radius_m = 999 } }, Ct)).EnsureSuccessStatusCode();

        (await FactoryHarness.EventuallyAsync(async () => (await JournalAsync("limit=50")).Select(entry => entry.Service).Distinct().Count() == 2)).ShouldBeTrue();

        using var traveler = _platform.CreateClient();
        traveler.Authenticate(TestTokens.Mint());
        (await traveler.GetAsync("/api/platform/v1/admin/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
