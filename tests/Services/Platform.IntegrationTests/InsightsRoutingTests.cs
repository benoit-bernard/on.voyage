using Npgsql;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;

namespace Platform.IntegrationTests;

/// <summary>Insights listens on its own queue (<c>insights</c>): Platform must put the consent changes and the configuration there (§13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class InsightsRoutingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PlatformHarness _p = null!;

    public async ValueTask InitializeAsync() => _p = await PlatformHarness.StartAsync(postgres);

    public async ValueTask DisposeAsync() => await _p.DisposeAsync();

    private async Task<long> QueuedAsync(string queue, string messageType)
    {
        await using var connection = new NpgsqlConnection(_p.Connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"select count(*) from wolverine_queues.wolverine_queue_{queue} where message_type like '%{messageType}%'", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100 && !await condition(); attempt++)
        {
            await Task.Delay(100, Ct);
        }

        (await condition()).ShouldBeTrue();
    }

    [Fact]
    public async Task A_consent_change_is_queued_for_insights()
    {
        var session = await _p.AnonymousAsync();

        (await _p.SendAsync(HttpMethod.Put, "/api/platform/v1/me/consents/analytics", session, new { granted = true, textVersion = "v1" })).EnsureSuccessStatusCode();

        await Eventually(async () => await QueuedAsync("insights", nameof(ConsentChangedV1)) == 1);
    }

    [Fact]
    public async Task The_configuration_reaches_insights_as_well_as_the_catalog()
    {
        await Eventually(async () => await QueuedAsync("insights", nameof(ConfigChangedV1)) > 0 && await QueuedAsync("catalog", nameof(ConfigChangedV1)) > 0);
    }
}
