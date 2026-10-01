using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OnVoyage.Catalog.Api;
using OnVoyage.Factory.Api;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Worker;
using OnVoyage.TestInfrastructure;

namespace Factory.IntegrationTests;

internal sealed class FakeWikidata : IWikidataClient
{
    public Dictionary<string, PlaceEnrichment> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<IReadOnlyList<string>> Requests { get; } = [];

    public Task<IReadOnlyList<PlaceEnrichment>> GetEntitiesAsync(IReadOnlyList<string> qids, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(qids);
        }

        return Task.FromResult<IReadOnlyList<PlaceEnrichment>>([.. qids.Where(Entities.ContainsKey).Select(qid => Entities[qid])]);
    }
}

internal sealed class FakePageviews : IPageviewsClient
{
    public Dictionary<string, long> ByTitle { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<long> GetAnnualViewsAsync(string language, string title, CancellationToken cancellationToken) =>
        Task.FromResult(ByTitle.GetValueOrDefault(title));
}

/// <summary>Factory API + worker + Catalog on one database, talking through the real queues, with the Wikimedia calls faked.</summary>
internal sealed class FactoryHarness : IAsyncDisposable
{
    private readonly WebApplicationFactory<FactoryApiMarker> _api;
    private readonly WebApplicationFactory<FactoryWorkerMarker> _worker;
    private readonly WebApplicationFactory<CatalogApiMarker> _catalog;

    private FactoryHarness(string connection, FakeWikidata wikidata, FakePageviews pageviews, WebApplicationFactory<FactoryApiMarker> api, WebApplicationFactory<FactoryWorkerMarker> worker, WebApplicationFactory<CatalogApiMarker> catalog)
    {
        Connection = connection;
        Wikidata = wikidata;
        Pageviews = pageviews;
        _api = api;
        _worker = worker;
        _catalog = catalog;
        Admin = api.CreateClient();
        Admin.Authenticate(TestTokens.Mint(roles: ["admin"], anonymous: false));
        Traveler = catalog.CreateClient();
        Traveler.Authenticate();
        _worker.CreateClient().Dispose();
    }

    public string Connection { get; }

    public FakeWikidata Wikidata { get; }

    public FakePageviews Pageviews { get; }

    public HttpClient Admin { get; }

    public HttpClient Traveler { get; }

    public HttpClient ApiClient(string? token = null)
    {
        var client = _api.CreateClient();
        if (token is not null)
        {
            client.Authenticate(token);
        }

        return client;
    }

    public static bool Osm2pgsqlAvailable { get; } = ProbeOsm2pgsql();

    private static bool ProbeOsm2pgsql()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("osm2pgsql", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static async Task<FactoryHarness> StartAsync(PostgresFixture postgres)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var wikidata = new FakeWikidata();
        var pageviews = new FakePageviews();
        var sample = Path.Combine(AppContext.BaseDirectory, "Data", "marseille-sample.osm");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "onvoyage-factory-tests", Guid.NewGuid().ToString("N"));

        void Configure(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Factory:DataDirectory", dataDirectory);
            builder.UseSetting("Factory:Destinations:0:OsmExtractFile", sample);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IWikidataClient>();
                services.AddSingleton<IWikidataClient>(wikidata);
                services.RemoveAll<IPageviewsClient>();
                services.AddSingleton<IPageviewsClient>(pageviews);
            });
        }

        // The worker goes first: it creates the queues it listens to and migrates the factory schema before the API starts publishing.
        var worker = new WebApplicationFactory<FactoryWorkerMarker>().WithWebHostBuilder(Configure);
        worker.CreateClient().Dispose();
        var api = new WebApplicationFactory<FactoryApiMarker>().WithWebHostBuilder(Configure);
        var catalog = new WebApplicationFactory<CatalogApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Catalog:SeedDemoData", "false");
        });

        return new FactoryHarness(connection, wikidata, pageviews, api, worker, catalog);
    }

    public async Task<T> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return read(reader);
    }

    public Task<long> CountAsync(string sql) => QueryAsync(sql, reader => reader.GetInt64(0));

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return false;
    }

    public async Task RunPipelineAsync()
    {
        var response = await Admin.PostAsJsonAsync("/api/factory/v1/admin/imports", new { destination = "marseille" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        (await EventuallyAsync(async () => await CountAsync("select count(*) from factory.place where importance_score is not null") >= 9))
            .ShouldBeTrue("the pipeline did not finish: import, enrichment and scoring run through the worker");
    }

    public async Task<JsonElement> GetPlaceAsync(string slug)
    {
        var list = await Admin.GetFromJsonAsync<JsonElement>("/api/factory/v1/admin/places?destination=marseille&limit=100", TestContext.Current.CancellationToken);
        return list.EnumerateArray().First(place => place.GetProperty("slug").GetString() == slug);
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        Traveler.Dispose();
        await _api.DisposeAsync();
        await _worker.DisposeAsync();
        await _catalog.DisposeAsync();
    }
}
