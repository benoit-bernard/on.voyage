using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OnVoyage.Discovery.Api;
using OnVoyage.Discovery.Application.IntegrationEvents;
using OnVoyage.Discovery.Application.Ports;
using OnVoyage.Discovery.Contracts;
using OnVoyage.Factory.Contracts;
using OnVoyage.TestInfrastructure;

namespace Discovery.IntegrationTests;

/// <summary>
/// Stories published without audio (no TTS voice when they were produced: the Marseille snapshot) are proposed to the discovery mode while
/// <c>Discovery:AllowTextOnlyStories</c> is on, flagged so the device reads their text; with the flag off only recorded stories remain.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class TextOnlyStoriesTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(WebApplicationFactory<DiscoveryApiMarker> Factory, Guid Voiced, Guid TextOnly, Guid Anecdote)> StartAsync(string? allowTextOnly)
    {
        var connection = await postgres.CreateDatabaseAsync();
        var factory = new WebApplicationFactory<DiscoveryApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:onvoyage", connection);
            builder.UseSetting("Auth:JwtSecret", TestTokens.Secret);
            builder.UseSetting("Media:PublicBaseUrl", "https://media.test/media");
            builder.UseSetting("Logging:LogLevel:Default", "Error");
            if (allowTextOnly is not null)
            {
                builder.UseSetting("Discovery:AllowTextOnlyStories", allowTextOnly);
            }
        });
        _ = factory.Server;

        await using var scope = factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IProjectionWriter>();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (var i = 0; i < ids.Length; i++)
        {
            await PoiPublishedHandler.Handle(
                new PoiPublishedV1(
                    Guid.NewGuid(), DateTimeOffset.UtcNow, ids[i], 1, new PoiDestinationV1("marseille", "Marseille", 43.2965, 5.3698), $"lieu-{i}", $"Lieu {i}", null,
                    43.2965 + (i * 0.002), 5.3698, 60, 50, false, 0.7f, 1, [new PoiInterestV1("history", 1f)], new PoiCrowdProfileV1(1, 2, 2), false, false),
                writer, Ct);
        }

        Task Story(Guid poi, string kind, params StoryAudioPartV1[] parts) => StoryPublishedHandler.Handle(
            new StoryPublishedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), poi, "fr", kind, 1, "Titre", "", "", "", 100, "v", true, false, parts, []), writer, Ct);

        await Story(ids[0], "standard", new StoryAudioPartV1("main", "s/voiced.mp3", "x", 100), new StoryAudioPartV1("announce_front", "s/voiced-f.mp3", "x", 5));
        await Story(ids[1], "standard"); // text only: published without any audio part
        await Story(ids[2], "anecdote");  // an anecdote alone is not a story to tell on the road
        return (factory, ids[0], ids[1], ids[2]);
    }

    private static async Task<CandidatesDto> CandidatesAsync(WebApplicationFactory<DiscoveryApiMarker> factory)
    {
        using var client = factory.CreateClient();
        client.Authenticate(TestTokens.Mint(Guid.NewGuid()));
        return (await client.GetFromJsonAsync<CandidatesDto>("/api/discovery/v1/me/candidates?destination=marseille", Ct))!;
    }

    [Fact]
    public async Task By_default_a_story_without_audio_is_a_candidate_flagged_text_only_and_a_voiced_one_is_not()
    {
        var (factory, voiced, textOnly, anecdote) = await StartAsync(allowTextOnly: null);
        await using var _ = factory;

        var result = await CandidatesAsync(factory);

        result.Items.Select(i => i.PoiId).ShouldBe([voiced, textOnly], ignoreOrder: true);
        result.Items.ShouldNotContain(i => i.PoiId == anecdote);
        var voicedStory = result.Items.Single(i => i.PoiId == voiced).Stories.Single();
        voicedStory.TextOnly.ShouldBeFalse();
        voicedStory.AudioParts["main"].ShouldBe("https://media.test/media/s/voiced.mp3");
        var textStory = result.Items.Single(i => i.PoiId == textOnly).Stories.Single();
        textStory.TextOnly.ShouldBeTrue();
        textStory.AudioParts.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_flag_off_only_recorded_stories_remain()
    {
        var (factory, voiced, _, _) = await StartAsync(allowTextOnly: "false");
        await using var _ = factory;

        var result = await CandidatesAsync(factory);

        result.Items.Select(i => i.PoiId).ShouldBe([voiced]);
    }

    [Fact]
    public async Task A_story_without_audio_is_still_recommended_and_counts_in_the_cf_scores_whatever_the_flag()
    {
        // The flag only concerns what the discovery mode is allowed to announce; lists of places have never required audio.
        var (factory, _, textOnly, _) = await StartAsync(allowTextOnly: "false");
        await using var _ = factory;
        using var client = factory.CreateClient();
        client.Authenticate(TestTokens.Mint(Guid.NewGuid()));

        var recommendations = (await client.GetFromJsonAsync<RecommendationsDto>("/api/discovery/v1/recommendations?limit=10&radius=20000", Ct))!;
        var scores = (await client.GetFromJsonAsync<CfScoresDto>("/api/discovery/v1/me/cf-scores?destination=marseille", Ct))!;

        recommendations.Items.ShouldContain(i => i.PoiId == textOnly);
        scores.Items.ShouldContain(i => i.PoiId == textOnly);
    }
}
