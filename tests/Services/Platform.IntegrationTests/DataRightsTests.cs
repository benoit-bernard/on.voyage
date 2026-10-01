using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Platform.Application.Features.DataRights;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;

namespace Platform.IntegrationTests;

/// <summary>Right of access and right to be forgotten end to end: Platform and Discovery on one database, talking only through the queues (F-22, T-507).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DataRightsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _exports = Path.Combine(Path.GetTempPath(), $"onvoyage-exports-{Guid.NewGuid():N}");
    private PlatformHarness _platform = null!;
    private WebApplicationFactory<DiscoveryApiMarker> _discovery = null!;
    private HttpClient _discoveryClient = null!;
    private string _connection = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        _platform = await PlatformHarness.StartAsync(postgres, _connection, new Dictionary<string, string>
        {
            ["Platform:DeletionRequiredServices:0"] = "discovery",
            ["Messaging:DataRightsSubscribers:0"] = "discovery",
            ["Platform:DataRightsJob"] = "false",
            ["Exports:Directory"] = _exports,
        });
        _discovery = new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", _connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Exports:Directory", _exports);
        });
        _discoveryClient = _discovery.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _discoveryClient.Dispose();
        await _platform.DisposeAsync();
        await _discovery.DisposeAsync();
        if (Directory.Exists(_exports))
        {
            Directory.Delete(_exports, recursive: true);
        }
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

    /// <summary>An anonymous traveler who has used the app: a consent, interactions, a wish and a visit.</summary>
    private async Task<AuthSessionDto> TravelerWithDataAsync()
    {
        var session = await _platform.AnonymousAsync();
        (await _platform.SendAsync(HttpMethod.Put, "/api/platform/v1/me/consents/analytics", session, new SetConsentRequest(true, "analytics-2026-10"))).EnsureSuccessStatusCode();

        using var request = PlatformHarness.Request(HttpMethod.Post, "/api/discovery/v1/me/interactions", session, new InteractionBatchRequest(
        [
            new InteractionDto(Guid.NewGuid(), "like", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-30)),
            new InteractionDto(Guid.NewGuid(), "save", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-20)),
            new InteractionDto(Guid.NewGuid(), "visit", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-10), Confidence: 0.8, DwellS: 600),
            new InteractionDto(Guid.NewGuid(), "impression", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-5), Surface: "home"),
        ]));
        (await _discoveryClient.SendAsync(request, Ct)).EnsureSuccessStatusCode();
        return session;
    }

    private async Task<ExportStatusDto> ExportAsync(AuthSessionDto session, Guid id) =>
        (await (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{id}", session)).Content.ReadFromJsonAsync<ExportStatusDto>(Ct))!;

    /// <summary>Counts, in the schemas of the services, every cell that holds this traveler's identifier (uuid and text columns).</summary>
    private async Task<List<string>> RowsMentioningAsync(Guid traveler, params string[] schemas)
    {
        await using var data = new NpgsqlDataSourceBuilder(_connection).Build();
        var columns = new List<(string Schema, string Table, string Column)>();
        await using (var command = data.CreateCommand("select table_schema, table_name, column_name from information_schema.columns where table_schema = any($1) and data_type in ('uuid', 'text', 'character varying', 'jsonb')"))
        {
            command.Parameters.AddWithValue(schemas);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var found = new List<string>();
        foreach (var (schema, table, column) in columns)
        {
            await using var count = data.CreateCommand($"select count(*) from \"{schema}\".\"{table}\" where \"{column}\"::text like '%' || $1 || '%'");
            count.Parameters.AddWithValue(traveler.ToString());
            if ((long)(await count.ExecuteScalarAsync(Ct))! > 0)
            {
                found.Add($"{schema}.{table}.{column}");
            }
        }

        return found;
    }

    [Fact]
    public async Task An_export_gathers_platform_and_discovery_data_into_one_archive_for_24_hours()
    {
        var session = await TravelerWithDataAsync();

        var started = await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/export", session);
        started.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var export = (await started.Content.ReadFromJsonAsync<ExportStatusDto>(Ct))!;
        export.Status.ShouldBe("pending");

        (await EventuallyAsync(async () => (await ExportAsync(session, export.ExportId)).Status == "ready")).ShouldBeTrue("the export never became ready");

        var archive = await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}/archive", session);
        archive.StatusCode.ShouldBe(HttpStatusCode.OK);
        archive.Headers.CacheControl!.NoStore.ShouldBeTrue();
        using var document = JsonDocument.Parse(await archive.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        root.GetProperty("travelerId").GetGuid().ShouldBe(session.TravelerId);
        root.GetProperty("platform").GetProperty("account").GetProperty("Id").GetGuid().ShouldBe(session.TravelerId);
        root.GetProperty("platform").GetProperty("consents")[0].GetProperty("Kind").GetString().ShouldBe("analytics");
        var discovery = root.GetProperty("services").GetProperty("discovery");
        discovery.GetProperty("interactions").GetArrayLength().ShouldBe(3);
        discovery.GetProperty("saved").GetArrayLength().ShouldBe(1);
        discovery.GetProperty("visits").GetArrayLength().ShouldBe(1);
        discovery.GetProperty("impressions").GetArrayLength().ShouldBe(1);
        discovery.GetProperty("interestVector").EnumerateObject().ShouldNotBeEmpty();

        // The export expires: after 24 hours the status says so and the archive is refused.
        _platform.Clock.Advance(TimeSpan.FromHours(25));
        (await ExportAsync(session, export.ExportId)).Status.ShouldBe("expired");
        (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}/archive", session)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Somebody_elses_export_does_not_exist_for_you()
    {
        var owner = await TravelerWithDataAsync();
        var stranger = await _platform.AnonymousAsync();
        var export = (await (await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/export", owner)).Content.ReadFromJsonAsync<ExportStatusDto>(Ct))!;

        (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}/archive", stranger)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deletion_removes_every_trace_of_the_traveler_from_platform_and_discovery()
    {
        var session = await TravelerWithDataAsync();
        var other = await TravelerWithDataAsync();
        (await RowsMentioningAsync(session.TravelerId, "platform", "discovery")).ShouldNotBeEmpty();

        var requested = await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/deletion", session);
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await requested.Content.ReadFromJsonAsync<DeletionStatusDto>(Ct))!.PendingServices.ShouldBe(["discovery"]);

        (await EventuallyAsync(async () => (await RowsMentioningAsync(session.TravelerId, "platform", "discovery")).Count == 0)).ShouldBeTrue(
            "rows remain: " + string.Join(", ", await RowsMentioningAsync(session.TravelerId, "platform", "discovery")));

        // The account is gone, and what belongs to somebody else is untouched.
        (await RowsMentioningAsync(other.TravelerId, "platform", "discovery")).ShouldNotBeEmpty();
        (await _platform.SendAsync(HttpMethod.Get, "/api/platform/v1/me/deletion", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var data = new NpgsqlDataSourceBuilder(_connection).Build();
        await using var log = data.CreateCommand("select count(*) from platform.deletion_log");
        ((long)(await log.ExecuteScalarAsync(Ct))!).ShouldBe(1);
    }

    [Fact]
    public async Task Asking_twice_returns_the_request_under_way_and_publishes_nothing_more()
    {
        var session = await _platform.AnonymousAsync();
        // Without a discovery answer yet the request stays pending: stop the answering host to keep it so.
        await _discovery.DisposeAsync();

        var first = (await (await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/deletion", session)).Content.ReadFromJsonAsync<DeletionStatusDto>(Ct))!;
        var second = (await (await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/deletion", session)).Content.ReadFromJsonAsync<DeletionStatusDto>(Ct))!;
        var status = (await (await _platform.SendAsync(HttpMethod.Get, "/api/platform/v1/me/deletion", session)).Content.ReadFromJsonAsync<DeletionStatusDto>(Ct))!;

        second.RequestedAt.ShouldBe(first.RequestedAt);
        status.Status.ShouldBe("pending");
        status.PendingServices.ShouldBe(["discovery"]);
    }

    [Fact]
    public async Task Anonymous_accounts_unused_for_24_months_are_deleted_by_the_same_path()
    {
        var idle = await TravelerWithDataAsync();
        _platform.Clock.Advance(TimeSpan.FromDays(31 * 25));
        var active = await _platform.AnonymousAsync(); // created now: not idle

        await using var scope = _platform.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<int>(new PurgeInactiveAnonymousAccountsCommand(), Ct);
        started.ShouldBe(1);

        (await EventuallyAsync(async () => (await RowsMentioningAsync(idle.TravelerId, "platform", "discovery")).Count == 0)).ShouldBeTrue("the idle account was not deleted");
        (await RowsMentioningAsync(active.TravelerId, "platform")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Expired_exports_are_cleaned_up_with_their_files()
    {
        var session = await TravelerWithDataAsync();
        var export = (await (await _platform.SendAsync(HttpMethod.Post, "/api/platform/v1/me/export", session)).Content.ReadFromJsonAsync<ExportStatusDto>(Ct))!;
        (await EventuallyAsync(async () => (await ExportAsync(session, export.ExportId)).Status == "ready")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_exports, export.ExportId.ToString("N"))).ShouldBeTrue();

        _platform.Clock.Advance(TimeSpan.FromHours(25));
        await using var scope = _platform.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<int>(new CleanUpExportsCommand(), Ct)).ShouldBe(1);

        Directory.Exists(Path.Combine(_exports, export.ExportId.ToString("N"))).ShouldBeFalse();
        (await _platform.SendAsync(HttpMethod.Get, $"/api/platform/v1/me/export/{export.ExportId}", session)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
