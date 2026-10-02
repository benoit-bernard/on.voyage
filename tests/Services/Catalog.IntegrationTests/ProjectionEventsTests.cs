using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Catalog.Api;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;

namespace Catalog.IntegrationTests;

/// <summary>After a publication or a withdrawal of Factory, the Catalog announces its view of the place to Discovery and Creators (§13).</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ProjectionEventsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<CatalogApiMarker> _factory = null!;
    private string _connection = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", _connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
        });
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private async Task InvokeAsync(object message)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, Ct);
    }

    /// <summary>The projection events queued for a service, as JSON (the queue stores Wolverine's envelope: headers, then the JSON message).</summary>
    private async Task<List<string>> QueuedAsync(string queue, Guid poi)
    {
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"select body from wolverine_queues.wolverine_queue_{queue} where message_type like '%PoiProjectionChangedV1%' and position(convert_to('{poi}', 'UTF8') in body) > 0", connection);
        var bodies = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            var envelope = System.Text.Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(0));
            bodies.Add(envelope[envelope.IndexOf('{', StringComparison.Ordinal)..(envelope.LastIndexOf('}') + 1)]);
        }

        return bodies;
    }

    private static PoiPublishedV1 Published(Guid poi, int version, string name = "Fort Saint-Jean") =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, version, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), "fort-saint-jean", name, "Fort Saint-Jean (en)", 43.2905, 5.3611, 80, 20, true, 0.9f, 1,
            [new PoiInterestV1("history.military", 0.9f), new PoiInterestV1("architecture.defensive", 0.6f)], new PoiCrowdProfileV1(1, 2, 4), true, true);

    [Fact]
    public async Task A_publication_is_announced_to_creators_and_discovery_with_names_flags_and_weights()
    {
        var poi = Guid.NewGuid();

        await InvokeAsync(Published(poi, 1));

        foreach (var queue in new[] { "creators", "discovery" })
        {
            var json = (await QueuedAsync(queue, poi)).ShouldHaveSingleItem();
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            root.GetProperty("poiId").GetGuid().ShouldBe(poi);
            root.GetProperty("version").GetInt32().ShouldBe(1);
            root.GetProperty("nameFr").GetString().ShouldBe("Fort Saint-Jean");
            root.GetProperty("nameEn").GetString().ShouldBe("Fort Saint-Jean (en)");
            root.GetProperty("destinationSlug").GetString().ShouldBe("marseille");
            root.GetProperty("city").GetString().ShouldBe("Marseille");
            root.GetProperty("isPublished").GetBoolean().ShouldBeTrue();
            root.GetProperty("crowdPeak").GetInt32().ShouldBe(4);
            root.GetProperty("importanceScore").GetInt32().ShouldBe(80);
            root.GetProperty("flags").EnumerateArray().Select(flag => flag.GetString()).ShouldBe(["fragile", "access_regulated", "hidden_gem"], ignoreOrder: true);
            root.GetProperty("weights").GetProperty("history.military").GetDouble().ShouldBe(0.9, 0.001);
            root.GetProperty("latitude").GetDouble().ShouldBe(43.2905, 0.0001);
        }
    }

    [Fact]
    public async Task A_withdrawal_is_announced_with_is_published_false_and_the_new_version()
    {
        var poi = Guid.NewGuid();
        await InvokeAsync(Published(poi, 1));

        await InvokeAsync(new PoiUnpublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, poi, 2, "erreur"));

        var announcements = await QueuedAsync("creators", poi);
        announcements.Count.ShouldBe(2);
        var documents = announcements.Select(json => System.Text.Json.JsonDocument.Parse(json).RootElement).ToList();
        documents.Select(root => (root.GetProperty("version").GetInt32(), root.GetProperty("isPublished").GetBoolean())).ShouldBe([(1, true), (2, false)], ignoreOrder: true);
        documents.ShouldAllBe(root => root.GetProperty("nameFr").GetString() == "Fort Saint-Jean"); // a withdrawal still says which place
        (await QueuedAsync("discovery", poi)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_redelivered_event_announces_the_current_state_again_and_an_unknown_withdrawal_announces_nothing()
    {
        var poi = Guid.NewGuid();
        var unknown = Guid.NewGuid();

        await InvokeAsync(Published(poi, 3));
        await InvokeAsync(Published(poi, 3)); // redelivery: nothing changes, the (identical) state is announced again
        await InvokeAsync(Published(poi, 2, "Nom périmé")); // stale: the catalog keeps version 3 and announces it
        await InvokeAsync(new PoiUnpublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, unknown, 1, "inconnu"));

        var announcements = await QueuedAsync("creators", poi);
        announcements.Count.ShouldBe(3);
        announcements.ShouldAllBe(json => json.Contains("\"version\":3", StringComparison.Ordinal) && json.Contains("\"nameFr\":\"Fort Saint-Jean\"", StringComparison.Ordinal));
        (await QueuedAsync("creators", unknown)).ShouldBeEmpty();
    }
}
