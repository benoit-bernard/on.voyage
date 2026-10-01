using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.TestInfrastructure;

namespace Discovery.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class DiscoveryApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddHours(-3);
    private WebApplicationFactory<DiscoveryApiMarker> _factory = null!;
    private HttpClient _client = null!;
    private string _connection = null!;

    private static readonly Guid Fort = Guid.NewGuid();
    private static readonly Guid Calanque = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();
    private static readonly Guid Gastro = Guid.NewGuid();
    private static readonly Guid Chapel = Guid.NewGuid();
    private static readonly Guid Fort2 = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", _connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Media:PublicBaseUrl", "https://media.test/media");
        });
        _client = _factory.CreateClient();
        _client.Authenticate();
        await SeedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static PoiPublishedV1 Poi(Guid id, string slug, int version, double quality, params (string Code, float Weight)[] interests) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, id, version, new PoiDestinationV1("marseille", "Marseille", 43.3, 5.37), slug, slug, null, 43.3, 5.37, 80, 90, false,
        (float)quality, 1, [.. interests.Select(i => new PoiInterestV1(i.Code, i.Weight))], new PoiCrowdProfileV1(1, 2, 3), false, false);

    private static StoryPublishedV1 Clip(Guid poi, string title, int version = 1) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), poi, "fr", "onboarding_clip", version, title, "", "", "", 15, "voice", true, false,
        [new StoryAudioPartV1("main", $"clips/{title}.mp3", "sha", 15)], []);

    private readonly Dictionary<Guid, StoryPublishedV1> _clips = [];

    private async Task SeedAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();
        var places = new[]
        {
            Poi(Fort, "fort", 1, 0.9, ("history", 0.9f), ("history.military", 1f), ("architecture", 0.7f), ("architecture.defensive", 0.8f)),
            Poi(Fort2, "fort-2", 1, 0.5, ("history", 0.8f), ("history.military", 0.9f)),
            Poi(Calanque, "calanque", 1, 0.8, ("nature", 1f), ("nature.coast", 1f)),
            Poi(Art, "art", 1, 0.85, ("culture", 0.9f), ("culture.contemporary_art", 0.9f)),
            Poi(Gastro, "gastro", 1, 0.6, ("gastronomy", 0.9f), ("gastronomy.markets", 0.9f)),
            Poi(Chapel, "chapel", 1, 0.75, ("religion", 0.9f), ("religion.churches", 0.9f)),
        };
        foreach (var place in places)
        {
            await PoiPublishedHandler.Handle(place, writer, Ct);
        }

        foreach (var (poi, name) in new[] { (Fort, "fort"), (Calanque, "calanque"), (Art, "art"), (Gastro, "gastro"), (Chapel, "chapel"), (Fort2, "fort2") })
        {
            var clip = Clip(poi, name);
            _clips[poi] = clip;
            await StoryPublishedHandler.Handle(clip, writer, Ct);
        }
    }

    private static InteractionDto Event(string kind, Guid? poi, double minutesAgo, Guid? id = null, double? confidence = null, int? dwell = null, string? category = null, double? value = null) =>
        new(id ?? Guid.NewGuid(), kind, poi, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), CategoryCode: category, Value: value, Confidence: confidence, DwellS: dwell);

    private static async Task<InteractionBatchResponse> Send(HttpClient client, params InteractionDto[] items)
    {
        var response = await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest(items), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<InteractionBatchResponse>(Ct))!;
    }

    private HttpClient NewTraveler(Guid? id = null, string[]? roles = null)
    {
        var client = _factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? Guid.NewGuid(), roles: roles));
        return client;
    }

    [Fact]
    public async Task Requests_without_a_token_are_refused()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/discovery/v1/me/profile", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_like_teaches_the_vector_and_the_depth()
    {
        using var client = NewTraveler();
        var result = await Send(client, Event("like", Fort, 10));

        result.Accepted.ShouldBe(1);
        result.Vector["history.military"].ShouldBe(0.15, 1e-6);
        result.ProfileDepth.ShouldBe(3);

        var profile = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        profile.Vector["history.military"].ShouldBe(0.15, 1e-6);
        profile.Level.ShouldBe("new");
    }

    [Fact]
    public async Task The_same_event_sent_twice_is_recorded_once()
    {
        var traveler = Guid.NewGuid();
        using var client = NewTraveler(traveler);
        var like = Event("like", Fort, 10);

        var first = await Send(client, like);
        var second = await Send(client, like, like);

        second.Accepted.ShouldBe(0);
        second.Duplicates.ShouldBe(2);
        second.Vector["history.military"].ShouldBe(first.Vector["history.military"]);
        second.ProfileDepth.ShouldBe(first.ProfileDepth);

        await using var data = new NpgsqlDataSourceBuilder(_connection).Build();
        await using var command = data.CreateCommand("select count(*) from discovery.interaction where traveler_id = $1");
        command.Parameters.AddWithValue(traveler);
        ((long)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(1);
    }

    [Fact]
    public async Task A_late_batch_gives_the_same_vector_as_the_events_in_order()
    {
        var like = Event("like", Fort, 60);
        var save = Event("save", Fort, 50);
        var dislike = Event("dislike_poi", Calanque, 40);
        var meh = Event("meh", Fort, 30);

        using var inOrder = NewTraveler();
        var expected = await Send(inOrder, like, save, dislike, meh);

        // A second device was offline: it sends the oldest events last.
        using var late = NewTraveler();
        await Send(late, dislike, meh);
        var actual = await Send(late, like, save);

        actual.Vector.Keys.OrderBy(k => k).ShouldBe(expected.Vector.Keys.OrderBy(k => k));
        foreach (var (code, value) in expected.Vector)
        {
            actual.Vector[code].ShouldBe(value, 1e-6);
        }

        actual.ProfileDepth.ShouldBe(expected.ProfileDepth);
        actual.Excluded.ShouldBe([Calanque]);
    }

    [Fact]
    public async Task Two_batches_at_once_do_not_overwrite_each_other()
    {
        using var client = NewTraveler();
        await Task.WhenAll(
            Send(client, Event("like", Fort, 20), Event("like", Calanque, 19)),
            Send(client, Event("save", Art, 18), Event("like", Chapel, 17)));

        var profile = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        profile.ProfileDepth.ShouldBe(3 + 3 + 2 + 3);
        new[] { "history.military", "nature.coast", "culture.contemporary_art", "religion.churches" }.ShouldAllBe(code => profile.Vector.ContainsKey(code));
    }

    [Fact]
    public async Task A_dislike_excludes_the_place_for_this_traveler_only()
    {
        using var client = NewTraveler();
        using var other = NewTraveler();
        var result = await Send(client, Event("dislike_poi", Art, 5));
        result.Excluded.ShouldBe([Art]);
        (await Send(other, Event("like", Fort, 5))).Excluded.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_visit_is_kept_with_its_stay_and_no_coordinates()
    {
        var traveler = Guid.NewGuid();
        using var client = NewTraveler(traveler);
        await Send(client, Event("visit", Fort, 30, confidence: 0.8, dwell: 420));

        await using var data = new NpgsqlDataSourceBuilder(_connection).Build();
        await using var visit = data.CreateCommand("select dwell_s, confidence from discovery.visit where traveler_id = $1");
        visit.Parameters.AddWithValue(traveler);
        await using var reader = await visit.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(420);
        reader.GetDouble(1).ShouldBe(0.8);
    }

    [Fact]
    public async Task No_discovery_table_holds_a_coordinate_column()
    {
        await using var data = new NpgsqlDataSourceBuilder(_connection).Build();
        await using var command = data.CreateCommand(
            "select table_name || '.' || column_name from information_schema.columns where table_schema = 'discovery' and table_name in ('visit', 'interaction', 'impression', 'traveler') and (column_name ~* '(^|_)(lat|lng|lon|latitude|longitude|location|position|geom)')");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var offenders = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            offenders.Add(reader.GetString(0));
        }

        offenders.ShouldBeEmpty();
    }

    [Fact]
    public async Task Impressions_are_stored_apart_and_teach_nothing()
    {
        using var client = NewTraveler();
        var result = await Send(client, Event("impression", Fort, 5), Event("impression", Calanque, 4));
        result.Accepted.ShouldBe(2);
        result.Vector.ShouldBeEmpty();
        result.ProfileDepth.ShouldBe(0);
    }

    [Fact]
    public async Task Invalid_batches_come_back_as_problem_details()
    {
        using var client = NewTraveler();
        var response = await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest([Event("like", null, 1)]), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("poiId");
    }

    [Fact]
    public async Task A_corrected_dimension_keeps_its_value_while_locked()
    {
        using var client = NewTraveler();
        var patched = await client.PatchAsJsonAsync("/api/discovery/v1/me/profile", new ProfileCorrectionRequest([new ProfileCorrection("history.military", -0.5)]), Ct);
        patched.EnsureSuccessStatusCode();

        var result = await Send(client, Event("like", Fort, 5), Event("like", Fort2, 4));
        result.Vector["history.military"].ShouldBe(-0.5);
        result.Vector["architecture.defensive"].ShouldBeGreaterThan(0);

        var profile = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        profile.Locks.ShouldContainKey("history.military");
    }

    [Fact]
    public async Task Unknown_dimensions_cannot_be_corrected()
    {
        using var client = NewTraveler();
        var response = await client.PatchAsJsonAsync("/api/discovery/v1/me/profile", new ProfileCorrectionRequest([new ProfileCorrection("nope", 1)]), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Onboarding_clips_cover_five_different_level_one_categories()
    {
        using var client = NewTraveler();
        var clips = (await client.GetFromJsonAsync<List<OnboardingClipDto>>("/api/discovery/v1/onboarding/clips?lang=fr", Ct))!;

        clips.Count.ShouldBe(5);
        clips.Select(c => c.Category).Distinct().Count().ShouldBe(5);
        clips.ShouldAllBe(c => c.AudioUrl.StartsWith("https://media.test/media/clips/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Onboarding_answers_build_the_initial_vector_of_the_acceptance_criteria()
    {
        using var client = NewTraveler();
        var request = new OnboardingRequest(
            [new ClipAnswer(_clips[Fort].StoryId, true), new ClipAnswer(_clips[Calanque].StoryId, true), new ClipAnswer(_clips[Art].StoryId, false)],
            [], []);

        var first = await Submit(client, request);
        first.Vector["history.military"].ShouldBeGreaterThan(0);
        first.Vector["nature.coast"].ShouldBeGreaterThan(0);
        first.Vector["culture.contemporary_art"].ShouldBeLessThan(0);

        // The screen sent again (network retry) is stored once.
        var second = await Submit(client, request);
        second.Accepted.ShouldBe(0);
        second.Vector["nature.coast"].ShouldBe(first.Vector["nature.coast"]);
        second.ProfileDepth.ShouldBe(first.ProfileDepth);
    }

    [Fact]
    public async Task Chosen_and_set_aside_categories_fix_their_dimensions()
    {
        using var client = NewTraveler();
        var result = await Submit(client, new OnboardingRequest([], ["nature"], ["culture"]));
        result.Vector["nature"].ShouldBe(0.6, 1e-6);
        result.Vector["culture"].ShouldBe(-0.6, 1e-6);
    }

    [Fact]
    public async Task Skipping_the_onboarding_leaves_the_vector_empty()
    {
        using var client = NewTraveler();
        var result = await Submit(client, new OnboardingRequest([], [], []));
        result.Vector.ShouldBeEmpty();
        result.ProfileDepth.ShouldBe(0);
    }

    [Fact]
    public async Task Onboarding_refuses_unknown_clips_and_non_level_one_categories()
    {
        using var client = NewTraveler();
        (await client.PostAsJsonAsync("/api/discovery/v1/onboarding", new OnboardingRequest([new ClipAnswer(Guid.NewGuid(), true)], [], []), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/discovery/v1/onboarding", new OnboardingRequest([], ["nature.coast"], []), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<InteractionBatchResponse> Submit(HttpClient client, OnboardingRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/discovery/v1/onboarding", request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<InteractionBatchResponse>(Ct))!;
    }

    [Fact]
    public async Task Only_an_admin_can_choose_the_active_clips_and_they_must_be_five_distinct_categories()
    {
        using var traveler = NewTraveler();
        using var admin = NewTraveler(roles: ["admin"]);
        var five = new[] { Fort, Calanque, Art, Gastro, Chapel }.Select(p => _clips[p].StoryId).ToArray();

        (await traveler.PutAsJsonAsync("/api/discovery/v1/admin/onboarding-clips", new ActiveClipsRequest(five), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var twoHistory = new[] { Fort, Fort2, Art, Gastro, Chapel }.Select(p => _clips[p].StoryId).ToArray();
        (await admin.PutAsJsonAsync("/api/discovery/v1/admin/onboarding-clips", new ActiveClipsRequest(twoHistory), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await admin.PutAsJsonAsync("/api/discovery/v1/admin/onboarding-clips", new ActiveClipsRequest(five), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var served = (await traveler.GetFromJsonAsync<List<OnboardingClipDto>>("/api/discovery/v1/onboarding/clips", Ct))!;
        served.Select(c => c.StoryId).Order().ShouldBe(five.Order());

        var listing = (await admin.GetFromJsonAsync<AdminClipsDto>("/api/discovery/v1/admin/onboarding-clips", Ct))!;
        listing.Candidates.Count.ShouldBe(6);
    }

    [Fact]
    public async Task An_unpublished_clip_or_older_event_changes_the_projection_correctly()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();

        // An older version of the place does not overwrite the newer one.
        await PoiPublishedHandler.Handle(Poi(Fort, "fort", 1, 0.1, ("nature", 1f)), writer, Ct);
        using var client = NewTraveler();
        (await Send(client, Event("like", Fort, 5))).Vector.ShouldContainKey("history.military");

        // Archiving the story removes the clip.
        await StoryArchivedHandler.Handle(new StoryArchivedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, _clips[Chapel].StoryId, Chapel, 1), writer, Ct);
        var clips = (await client.GetFromJsonAsync<List<OnboardingClipDto>>("/api/discovery/v1/onboarding/clips", Ct))!;
        clips.Select(c => c.StoryId).ShouldNotContain(_clips[Chapel].StoryId);

        // A standard story is not a clip.
        await StoryPublishedHandler.Handle(Clip(Fort, "std") with { Kind = "standard" }, writer, Ct);
        (await client.GetFromJsonAsync<List<OnboardingClipDto>>("/api/discovery/v1/onboarding/clips", Ct))!.ShouldAllBe(c => c.Title != "std");
    }
}
