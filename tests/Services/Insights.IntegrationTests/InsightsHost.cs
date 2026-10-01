using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Insights.Api;
using OnVoyage.Insights.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;
using Wolverine.Runtime;

namespace Insights.IntegrationTests;

/// <summary>
/// A running Insights service on its own database. Starting one costs a Wolverine host with its generated handlers (a few hundred MB that .NET
/// never gives back), so the tests that do not need a clean database share one and the KPI tests, which count everything, get their own.
/// </summary>
public sealed class InsightsHost : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static InsightsHost? _shared;

    private InsightsHost(WebApplicationFactory<InsightsApiMarker> factory, string connection, string exports)
    {
        Factory = factory;
        Connection = connection;
        Exports = exports;
    }

    public WebApplicationFactory<InsightsApiMarker> Factory { get; }
    public string Connection { get; }
    public string Exports { get; }

    public static async Task<InsightsHost> CreateAsync(PostgresFixture postgres)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var exports = Path.Combine(Path.GetTempPath(), $"insights-exports-{Guid.NewGuid():N}");
        var factory = new WebApplicationFactory<InsightsApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Insights:Jobs:Enabled", "false");
            builder.UseSetting("Exports:Directory", exports);
            builder.UseSetting("Logging:LogLevel:Default", "Error"); // thousands of SQL lines per run otherwise
        });
        _ = factory.Server; // starts the host: migrations and partitions
        return new InsightsHost(factory, connection, exports);
    }

    /// <summary>The host shared by the tests that use travelers of their own and never count rows of the whole database.</summary>
    public static async Task<InsightsHost> SharedAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try
        {
            return _shared ??= await CreateAsync(postgres);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        if (Directory.Exists(Exports))
        {
            Directory.Delete(Exports, recursive: true);
        }
    }

    // A fresh bus per call, like a handler invoked from the outside (the host's own bus is scoped to requests and messages).
    public MessageBus Bus => new(Factory.Services.GetRequiredService<IWolverineRuntime>());

    public HttpClient Traveler(Guid? id = null, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? Guid.NewGuid(), roles: roles));
        return client;
    }

    public HttpClient Admin() => Traveler(null, "admin");

    public async Task Consent(Guid traveler, bool granted, DateTimeOffset? at = null, string kind = "analytics") =>
        await Bus.InvokeAsync(new ConsentChangedV1(Guid.NewGuid(), at ?? DateTimeOffset.UtcNow, traveler, kind, granted, "v1"));

    public static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    public static EventDto Ev(string name, DateTimeOffset at, Guid? session = null, Dictionary<string, JsonElement>? props = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), name, at, session ?? Guid.NewGuid(), "1.0.0", Platforms.Android, props);

    public static async Task<EventBatchResponse> Post(HttpClient client, params EventDto[] events)
    {
        var response = await client.PostAsJsonAsync("/api/insights/v1/events", new EventBatchRequest(events));
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EventBatchResponse>())!;
    }

    public async Task<T> Scalar<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task<long> CountEvents(Guid traveler) => Scalar<long>($"select count(*) from insights.event where traveler_ref = '{traveler}'");
}
