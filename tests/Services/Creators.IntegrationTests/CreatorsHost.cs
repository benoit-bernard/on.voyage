using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Catalog.Contracts;
using OnVoyage.Creators.Api;
using OnVoyage.Creators.Contracts;
using OnVoyage.TestInfrastructure;
using Wolverine;
using Wolverine.Runtime;

namespace Creators.IntegrationTests;

/// <summary>
/// A running Creators service on its own database. A host costs a Wolverine runtime with its generated handlers (hundreds of MB that .NET
/// never gives back), so every test class shares this one and keeps to travelers, creators and places of its own.
/// Events for Discovery land in the <c>discovery</c> queue (nobody listens to it here) and Platform's in <c>platform</c>, where the tests count them.
/// </summary>
public sealed class CreatorsHost : IAsyncDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CreatorsHost? _shared;

    private CreatorsHost(WebApplicationFactory<CreatorsApiMarker> factory, string connection, string exports)
    {
        Factory = factory;
        Connection = connection;
        Exports = exports;
    }

    public WebApplicationFactory<CreatorsApiMarker> Factory { get; }
    public string Connection { get; }
    public string Exports { get; }

    public static async Task<CreatorsHost> SharedAsync(PostgresFixture postgres)
    {
        await Gate.WaitAsync();
        try
        {
            if (_shared is not null)
            {
                return _shared;
            }

            var connection = await postgres.CreateDatabaseAsync();
            var exports = Path.Combine(Path.GetTempPath(), $"creators-exports-{Guid.NewGuid():N}");
            var factory = new WebApplicationFactory<CreatorsApiMarker>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:onvoyage", connection);
                builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
                builder.UseSetting("Exports:Directory", exports);
                builder.UseSetting("Messaging:CreatorSubscribers:0", "discovery");
                builder.UseSetting("Logging:LogLevel:Default", "Error");

                // Imports run against the deterministic adapters (no network): both platforms open, tokens encrypted with keys kept in a temporary folder.
                builder.UseSetting("Creators:Social:Provider", "fake");
                foreach (var platform in new[] { "Instagram", "YouTube" })
                {
                    builder.UseSetting($"Creators:Social:{platform}:Enabled", "true");
                    builder.UseSetting($"Creators:Social:{platform}:ClientId", $"{platform}-client");
                    builder.UseSetting($"Creators:Social:{platform}:ClientSecret", $"{platform}-secret");
                    builder.UseSetting($"Creators:Social:{platform}:RedirectUri", $"https://studio.onvoyage.test/studio/connections/{platform.ToLowerInvariant()}/callback");
                }

                builder.UseSetting("Creators:DataProtection:KeysDirectory", Path.Combine(exports, "keys"));
                builder.UseSetting("Creators:Social:MediaDirectory", Path.Combine(exports, "media"));
            });
            _ = factory.Server; // starts the host: migrations
            return _shared = new CreatorsHost(factory, connection, exports);
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

    /// <summary>The platforms of the fake adapters: tests fill what an account has published and make its tokens fail.</summary>
    internal OnVoyage.Creators.Infrastructure.Social.FakeSocialWorld World => Factory.Services.GetRequiredService<OnVoyage.Creators.Infrastructure.Social.FakeSocialWorld>();

    // A fresh bus per call, like a handler invoked from the outside.
    public MessageBus Bus => new(Factory.Services.GetRequiredService<IWolverineRuntime>());

    public HttpClient Traveler(Guid? id = null, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? Guid.NewGuid(), roles: roles));
        return client;
    }

    public HttpClient Admin() => Traveler(null, "admin");

    /// <summary>A signed-in account (verified e-mail), with the given roles: what Studio presents once the creator has signed in.</summary>
    public HttpClient Account(Guid? id = null, params string[] roles)
    {
        var client = Factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? Guid.NewGuid(), anonymous: false, roles: roles));
        return client;
    }

    public static string Unique(string prefix = "t") => prefix + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A place of the catalog, as the Creators service learns of it: through the projection event.</summary>
    public async Task<Guid> PoiAsync(string name, bool published = true, string destination = "marseille", int version = 1, Guid? id = null, string? city = "Marseille")
    {
        var poi = id ?? Guid.NewGuid();
        await Bus.InvokeAsync(new PoiProjectionChangedV1(
            Guid.NewGuid(), DateTimeOffset.UtcNow, poi, version, DestinationId(destination), destination, name.ToLowerInvariant().Replace(' ', '-'), name, null, [], city,
            43.2965, 5.3698, 70, false, 0.8f, 3, new Dictionary<string, float> { ["history"] = 0.8f }, [], published));
        return poi;
    }

    public static Guid DestinationId(string slug) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(slug))[..16]);

    public static CreatorProfileRequest Profile(string? handle = null, string name = "Marie", params string[] specialties) =>
        new(handle ?? Unique(), name, "Marseillaise.", null, ["fr"], specialties.Length == 0 ? ["history.maritime"] : specialties, null, null);

    /// <summary>A founding creator created by the admin; with <paramref name="consent"/> its signed consent is recorded and with <paramref name="publish"/> it is public.</summary>
    public async Task<AdminCreatorDetailDto> FounderAsync(string? handle = null, bool consent = true, bool publish = false, Guid? account = null, params string[] specialties)
    {
        using var admin = Admin();
        var created = await Read<AdminCreatorDetailDto>(await admin.PostAsJsonAsync("/api/creators/v1/admin/creators", Profile(handle, "Marie", specialties)), HttpStatusCode.Created);
        if (account is { } accountId)
        {
            await Read<AdminCreatorDetailDto>(await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{created.Id}/account", new LinkAccountRequest(accountId)));
        }

        if (consent)
        {
            await Read<AdminCreatorDetailDto>(await admin.PutAsJsonAsync($"/api/creators/v1/admin/creators/{created.Id}/consent", new FounderConsentRequest("H-009/consentement-signe.pdf", null)));
        }

        return publish ? await Read<AdminCreatorDetailDto>(await admin.PostAsync($"/api/creators/v1/admin/creators/{created.Id}/publish", null)) : await Detail(created.Id);
    }

    public async Task<AdminCreatorDetailDto> Detail(Guid creatorId)
    {
        using var admin = Admin();
        return await Read<AdminCreatorDetailDto>(await admin.GetAsync($"/api/creators/v1/admin/creators/{creatorId}"));
    }

    /// <summary>A validated link between the creator and the place, with a content when one is given.</summary>
    public async Task<AdminPlaceLinkDto> LinkAsync(Guid creatorId, Guid poi, Guid? content = null, int? start = null, string? status = null)
    {
        using var admin = Admin();
        return await Read<AdminPlaceLinkDto>(await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{creatorId}/place-links", new AddPlaceLinkRequest(poi, content, start, status)));
    }

    public async Task<AdminContentDto> ContentAsync(Guid creatorId, string? videoId = null, int? duration = 600, bool commercial = false, params ChapterDto[] chapters)
    {
        using var admin = Admin();
        var request = new AddContentRequest($"https://www.youtube.com/watch?v={videoId ?? Unique("v")[..11].PadRight(11, 'x')}", "Marseille en 48 h", null, "covers/marseille.jpg", DateTimeOffset.UtcNow.AddDays(-3), duration, null, commercial, chapters);
        return await Read<AdminContentDto>(await admin.PostAsJsonAsync($"/api/creators/v1/admin/creators/{creatorId}/contents", request), HttpStatusCode.Created);
    }

    public static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
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

    /// <summary>How many messages of a type, mentioning <paramref name="needle"/> (an identifier), wait in a queue nobody listens to.</summary>
    public Task<long> Queued(string queue, string messageType, object needle) =>
        Scalar<long>($"select count(*) from wolverine_queues.wolverine_queue_{queue} where message_type like '%{messageType}%' and position(convert_to('{needle}', 'UTF8') in body) > 0");
}
