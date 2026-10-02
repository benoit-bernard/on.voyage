using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OnVoyage.Creators.Api;
using OnVoyage.Creators.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

/// <summary>Creators tells Platform (queue <c>platform</c>) that the terms are accepted; Platform adds the <c>creator</c> role to a verified account only (F-26).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CreatorRoleTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformHarness _platform = null!;
    private WebApplicationFactory<CreatorsApiMarker> _creators = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _platform = await PlatformHarness.StartAsync(postgres, connection);
        _creators = new WebApplicationFactory<CreatorsApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
        });
        _admin = _creators.CreateClient();
        _admin.Authenticate(TestTokens.Mint(roles: ["admin"]));
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _platform.DisposeAsync();
        await _creators.DisposeAsync();
    }

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(40);
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

    /// <summary>Every message addressed to Platform has been handled (and so removed from its queue).</summary>
    private async Task<bool> PlatformQueueIsDrainedAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(_platform.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("select count(*) from wolverine_queues.wolverine_queue_platform", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))! == 0;
    }

    private async Task<IReadOnlyList<string>> RolesAsync(AuthSessionDto session) =>
        (await (await _platform.SendAsync(HttpMethod.Get, "/api/platform/v1/me", session)).Content.ReadFromJsonAsync<AccountDto>(Ct))!.Roles;

    private async Task FounderWithConsentAsync(Guid account)
    {
        var created = await _admin.PostAsJsonAsync("/api/creators/v1/admin/creators", new CreatorProfileRequest($"f{Guid.NewGuid():N}"[..14], "Marie", null, null, ["fr"], ["nature"], null, null), Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<AdminCreatorDetailDto>(Ct))!.Id;
        (await _admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{id}/account", new LinkAccountRequest(account), Ct)).EnsureSuccessStatusCode();
        (await _admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{id}/consent", new FounderConsentRequest("H-009/consentement.pdf", null), Ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_verified_account_gets_the_creator_role_once_the_founder_consent_is_recorded()
    {
        var verified = await _platform.SignInAsync("marie@example.org", await _platform.AnonymousAsync());
        (await RolesAsync(verified)).ShouldNotContain("creator");

        await FounderWithConsentAsync(verified.TravelerId);

        (await EventuallyAsync(async () => (await RolesAsync(verified)).Contains("creator"))).ShouldBeTrue("the creator role never reached the account");
        // The next sign-in carries the role in the token, next to any other role.
        var next = await _platform.SignInAsync("marie@example.org");
        next.Roles.ShouldContain("creator");
    }

    [Fact]
    public async Task An_anonymous_account_never_gets_the_creator_role()
    {
        var anonymous = await _platform.AnonymousAsync();
        var verified = await _platform.SignInAsync("claire@example.org", await _platform.AnonymousAsync());

        await FounderWithConsentAsync(anonymous.TravelerId);
        await FounderWithConsentAsync(verified.TravelerId); // the second event proves the first one was handled by the time the role shows up

        (await EventuallyAsync(async () => (await RolesAsync(verified)).Contains("creator") && await PlatformQueueIsDrainedAsync())).ShouldBeTrue();
        (await RolesAsync(anonymous)).ShouldNotContain("creator");
    }
}
