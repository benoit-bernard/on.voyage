using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Creators.Contracts;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.Features;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.Platform.Contracts;
using OnVoyage.Recommendation.Engine;
using OnVoyage.TestInfrastructure;

namespace Discovery.IntegrationTests;

[Collection(PostgresTestGroup.Name)]
public sealed class CreatorsProjectionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private WebApplicationFactory<DiscoveryApiMarker> _factory = null!;
    private readonly List<(Guid Id, string Category)> _places = [];
    private int _tick;

    public async ValueTask InitializeAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Logging:LogLevel:Default", "Error");
        });
        _ = _factory.Server;

        // Eight history places and eight nature places, each with one standard story: the shape the ranking needs.
        await using var scope = _factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();
        for (var i = 0; i < 16; i++)
        {
            var category = i < 8 ? "history" : "nature";
            var id = Guid.NewGuid();
            _places.Add((id, category));
            await PoiPublishedHandler.Handle(
                new PoiPublishedV1(
                    Guid.NewGuid(), T0, id, 1, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), $"lieu-{i}", $"Lieu {i}", null,
                    43.2965 + (i * 0.002), 5.3698, 50, 50, false, 0.7f, 1, [new PoiInterestV1(category, 0.9f)], new PoiCrowdProfileV1(1, 2, 2), false, false),
                writer, Ct);
            await StoryPublishedHandler.Handle(
                new StoryPublishedV1(Guid.NewGuid(), T0, Guid.NewGuid(), id, "fr", "standard", 1, $"Lieu {i}", "", "", "", 100, "v", true, false, [new StoryAudioPartV1("main", $"s/{i}.mp3", "x", 100)], []),
                writer, Ct);
        }
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private DateTimeOffset Next() => T0.AddMinutes(++_tick);

    private IEnumerable<Guid> Places(string category) => _places.Where(p => p.Category == category).Select(p => p.Id);

    private async Task Publish(Guid creator, string handle, DateTimeOffset? at = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await CreatorPublishedHandler.Handle(new CreatorPublishedV1(Guid.NewGuid(), at ?? Next(), creator, handle, $"Name {handle}", null, ["history"]), scope.ServiceProvider.GetRequiredService<ICreatorProjectionWriter>(), Ct);
    }

    private async Task Unpublish(Guid creator, string handle, DateTimeOffset? at = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await CreatorUnpublishedHandler.Handle(new CreatorUnpublishedV1(Guid.NewGuid(), at ?? Next(), creator, handle, "test"), scope.ServiceProvider.GetRequiredService<ICreatorProjectionWriter>(), Ct);
    }

    private async Task Link(Guid creator, Guid poi, string status = "validated", bool commercial = false, string kind = "video", Guid? content = null, DateTimeOffset? at = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await CreatorPlaceLinkChangedHandler.Handle(
            new CreatorPlaceLinkChangedV1(Guid.NewGuid(), at ?? Next(), creator, poi, kind == "tip" ? null : content ?? poi, kind, commercial, status),
            scope.ServiceProvider.GetRequiredService<ICreatorProjectionWriter>(), Ct);
    }

    private async Task Follow(Guid traveler, Guid creator, bool following = true, DateTimeOffset? at = null)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await FollowChangedHandler.Handle(new FollowChangedV1(Guid.NewGuid(), at ?? Next(), traveler, creator, following), scope.ServiceProvider.GetRequiredService<IDiscoveryStore>(), Ct);
    }

    private HttpClient Traveler(Guid? id = null)
    {
        var client = _factory.CreateClient();
        client.Authenticate(TestTokens.Mint(id ?? PersonalizedTraveler()));
        return client;
    }

    private static Guid PersonalizedTraveler() => Another(false);

    private static Guid Another(bool control)
    {
        while (true)
        {
            var id = Guid.NewGuid();
            if (ControlCohort.Contains(id) == control)
            {
                return id;
            }
        }
    }

    private static async Task Like(HttpClient client, params Guid[] places)
    {
        var batch = places.Select((p, i) => new InteractionDto(Guid.NewGuid(), "like", p, DateTimeOffset.UtcNow.AddMinutes(-60 + i))).ToArray();
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest(batch), Ct)).EnsureSuccessStatusCode();
    }

    private static async Task<RecommendationItemDto> Recommended(HttpClient client, Guid poi)
    {
        var all = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=50&radius=20000", Ct))!;
        return all.Items.First(i => i.PoiId == poi);
    }

    private static async Task<double> BaseScore(HttpClient client, Guid poi) =>
        (await client.GetFromJsonAsync<CandidatesDto>("/api/discovery/v1/me/candidates?destination=marseille", Ct))!.Items.First(c => c.PoiId == poi).BaseScore;

    [Fact]
    public async Task A_followed_creator_raises_the_place_and_explains_it_by_name_until_the_creator_is_unpublished()
    {
        var creator = Guid.NewGuid();
        var place = Places("history").First();
        await Publish(creator, "marie");
        await Link(creator, place);
        var traveler = PersonalizedTraveler();
        using var client = Traveler(traveler);
        var before = await BaseScore(client, place);
        (await Recommended(client, place)).Why.Template.ShouldNotBe("creator_followed");

        await Follow(traveler, creator);

        var item = await Recommended(client, place);
        item.Why.Template.ShouldBe("creator_followed");
        item.Why.Params["creator"].ShouldBe("marie");
        (await BaseScore(client, place)).ShouldBeGreaterThan(before);

        await Unpublish(creator, "marie");
        (await Recommended(client, place)).Why.Template.ShouldNotBe("creator_followed");
    }

    [Fact]
    public async Task A_creator_close_to_the_traveler_tastes_explains_a_place_even_without_a_follow()
    {
        var creator = Guid.NewGuid();
        await Publish(creator, "marco");
        var history = Places("history").ToArray();
        await Link(creator, history[0]);
        await Link(creator, history[1]);
        using var client = Traveler();
        // Onboarding answers move the vector without rating places, so "a place you liked" (first in priority) does not take the explanation.
        var answers = history.Skip(4).Take(3).Select((p, i) => new InteractionDto(Guid.NewGuid(), "onboarding_up", p, DateTimeOffset.UtcNow.AddMinutes(-30 + i))).ToArray();
        (await client.PostAsJsonAsync("/api/discovery/v1/me/interactions", new InteractionBatchRequest(answers), Ct)).EnsureSuccessStatusCode();

        var item = await Recommended(client, history[0]);

        item.Why.Template.ShouldBe("creator_similar");
        item.Why.Params["creator"].ShouldBe("marco");
        var unrelated = await Recommended(client, Places("nature").First());
        unrelated.Why.Template.ShouldNotStartWith("creator_");
    }

    [Fact]
    public async Task Advertising_content_gives_no_signal_even_to_a_followed_creator()
    {
        var creator = Guid.NewGuid();
        var place = Places("history").First();
        await Publish(creator, "sponsored");
        await Link(creator, place, commercial: true);
        var traveler = PersonalizedTraveler();
        using var client = Traveler(traveler);
        var before = await BaseScore(client, place);
        await Follow(traveler, creator);

        (await Recommended(client, place)).Why.Template.ShouldNotStartWith("creator_");
        (await BaseScore(client, place)).ShouldBe(before, 1e-9);
        var scores = (await client.GetFromJsonAsync<CfScoresDto>("/api/discovery/v1/me/cf-scores?destination=marseille", Ct))!;
        scores.Items.First(s => s.PoiId == place).CreatorSignal.ShouldBe(0d);

        // Once an editorial link exists besides the paid one, the place counts.
        await Link(creator, place, commercial: false, content: Guid.NewGuid());
        (await Recommended(client, place)).Why.Template.ShouldBe("creator_followed");
    }

    [Fact]
    public async Task A_removed_link_withdraws_the_signal_and_an_older_event_cannot_bring_it_back()
    {
        var creator = Guid.NewGuid();
        var place = Places("nature").First();
        var traveler = PersonalizedTraveler();
        await Publish(creator, "anna");
        var validated = Next();
        var removed = Next();
        await Link(creator, place, at: validated);
        await Follow(traveler, creator);
        using var client = Traveler(traveler);
        (await Recommended(client, place)).Why.Template.ShouldBe("creator_followed");

        await Link(creator, place, status: "removed", at: removed);
        await Link(creator, place, at: validated); // redelivered after the removal: older, ignored

        (await Recommended(client, place)).Why.Template.ShouldNotBe("creator_followed");
    }

    [Fact]
    public async Task The_control_cohort_gets_no_creator_signal_and_no_personal_order()
    {
        var creator = Guid.NewGuid();
        var place = Places("history").First();
        await Publish(creator, "marie");
        await Link(creator, place);
        var traveler = Another(control: true);
        await Follow(traveler, creator);
        using var client = Traveler(traveler);

        (await Recommended(client, place)).Why.Template.ShouldBe("cold_start");
        var scores = (await client.GetFromJsonAsync<CfScoresDto>("/api/discovery/v1/me/cf-scores?destination=marseille", Ct))!;
        scores.Items.ShouldAllBe(s => s.CreatorSignal == 0d && s.Cf == null);
        var creators = (await client.GetFromJsonAsync<CreatorsForMeDto>("/api/discovery/v1/creators/for-me?destination=marseille", Ct))!;
        creators.Cohort.ShouldBe("control");
        creators.Items.ShouldAllBe(c => c.Affinity == 0 && !c.Following);
    }

    [Fact]
    public async Task Following_writes_one_follow_creator_interaction_with_the_frozen_vector_and_unfollowing_does_not_undo_it()
    {
        var creator = Guid.NewGuid();
        await Publish(creator, "marie");
        foreach (var poi in Places("history").Take(3))
        {
            await Link(creator, poi);
        }

        var traveler = PersonalizedTraveler();
        using var client = Traveler(traveler);
        var empty = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        empty.Vector.GetValueOrDefault("history").ShouldBe(0d);

        await Follow(traveler, creator);
        var followed = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        followed.Vector["history"].ShouldBeGreaterThan(0d);
        followed.ProfileDepth.ShouldBe(0); // a follow is not explicit feedback (§6.13)

        await Follow(traveler, creator, following: false);
        await Follow(traveler, creator);
        var again = (await client.GetFromJsonAsync<ProfileDto>("/api/discovery/v1/me/profile", Ct))!;
        again.Vector["history"].ShouldBe(followed.Vector["history"], 1e-6);

        await using var scope = _factory.Services.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<IDiscoveryStore>().ExclusiveAsync(traveler, session => session.HistoryAsync(Ct), Ct);
        history.Count(i => i.Kind == "follow_creator").ShouldBe(1);
        history.Single(i => i.Kind == "follow_creator").Weights!["history"].ShouldBeGreaterThan(0d);
    }

    [Fact]
    public async Task A_follow_event_older_than_the_stored_one_changes_nothing_and_a_creator_without_places_writes_no_interaction()
    {
        var empty = Guid.NewGuid();
        var traveler = PersonalizedTraveler();
        await Publish(empty, "newbie");
        var first = Next();
        var second = Next();
        await Follow(traveler, empty, following: false, at: second);
        await Follow(traveler, empty, following: true, at: first); // older: ignored

        using var client = Traveler(traveler);
        var creators = (await client.GetFromJsonAsync<CreatorsForMeDto>("/api/discovery/v1/creators/for-me?destination=marseille", Ct))!;
        creators.Items.ShouldBeEmpty(); // no validated place in the destination: not listed
        await using var scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<ICreatorReader>().FollowedAsync(traveler, Ct)).ShouldBeEmpty();
        var history = await scope.ServiceProvider.GetRequiredService<IDiscoveryStore>().ExclusiveAsync(traveler, session => session.HistoryAsync(Ct), Ct);
        history.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_creators_for_me_list_is_ordered_by_affinity_marks_follows_and_hides_unpublished_creators()
    {
        var historian = Guid.NewGuid();
        var naturalist = Guid.NewGuid();
        var gone = Guid.NewGuid();
        await Publish(historian, "historian");
        await Publish(naturalist, "naturalist");
        await Publish(gone, "gone");
        foreach (var poi in Places("history").Take(3))
        {
            await Link(historian, poi);
            await Link(gone, poi);
        }

        foreach (var poi in Places("nature").Take(2))
        {
            await Link(naturalist, poi);
        }

        await Unpublish(gone, "gone");
        var traveler = PersonalizedTraveler();
        using var client = Traveler(traveler);
        await Like(client, Places("history").Skip(3).Take(4).ToArray());
        await Follow(traveler, naturalist);

        var result = (await client.GetFromJsonAsync<CreatorsForMeDto>("/api/discovery/v1/creators/for-me?destination=marseille", Ct))!;

        result.Items.Select(c => c.Handle).ShouldBe(["historian", "naturalist"]);
        result.Items[0].Affinity.ShouldBeGreaterThan(result.Items[1].Affinity);
        result.Items[0].PlaceCount.ShouldBe(3);
        result.Items[0].Following.ShouldBeFalse();
        result.Items[1].Following.ShouldBeTrue();
        (await client.GetFromJsonAsync<CreatorsForMeDto>("/api/discovery/v1/creators/for-me?destination=lyon", Ct))!.Items.ShouldBeEmpty();
        (await client.GetAsync("/api/discovery/v1/creators/for-me?limit=0", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_creator_vector_follows_the_places_it_links_to_even_when_the_links_arrive_first_and_a_tip_weighs_more()
    {
        // The link arrives before the creator and before the place is republished: the vector is complete once everything is there.
        var creator = Guid.NewGuid();
        var history = Places("history").First();
        var nature = Places("nature").First();
        await Link(creator, history);
        await Link(creator, nature, kind: "tip");
        await Publish(creator, "mixed");

        await using var scope = _factory.Services.CreateAsyncScope();
        var info = (await scope.ServiceProvider.GetRequiredService<ICreatorReader>().CreatorsAsync("marseille", Ct)).Single();

        info.PlaceCount.ShouldBe(2);
        info.Vector["nature"].ShouldBeGreaterThan(info.Vector["history"]); // 1.5 against 1
        info.Vector["nature"].ShouldBe(0.9 * 1.5 / 2.5, 1e-4);
    }

    [Fact]
    public async Task Follows_are_exported_for_the_traveler_and_deleted_with_the_account()
    {
        var creator = Guid.NewGuid();
        await Publish(creator, "marie");
        await Link(creator, Places("history").First());
        var traveler = PersonalizedTraveler();
        await Follow(traveler, creator);

        await using var scope = _factory.Services.CreateAsyncScope();
        var rights = scope.ServiceProvider.GetRequiredService<IDataRightsStore>();
        var export = JsonDocument.Parse(await rights.ExportAsync(traveler, Ct)).RootElement;
        export.GetProperty("follows").GetArrayLength().ShouldBe(1);
        export.GetProperty("follows")[0].GetProperty("CreatorId").GetGuid().ShouldBe(creator);

        var reply = await TravelerDeletionRequestedHandler.Handle(new TravelerDeletionRequestedV1(Guid.NewGuid(), T0, traveler, T0), rights, TimeProvider.System, Ct);
        reply.Service.ShouldBe("discovery");

        var reader = scope.ServiceProvider.GetRequiredService<ICreatorReader>();
        (await reader.FollowedAsync(traveler, Ct)).ShouldBeEmpty();
        JsonDocument.Parse(await rights.ExportAsync(traveler, Ct)).RootElement.GetProperty("follows").GetArrayLength().ShouldBe(0);
    }
}
