using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OnVoyage.Platform.Contracts;
using OnVoyage.Web.Admin.Api;
using OnVoyage.Web.Admin.Components.Pages;

namespace OnVoyage.Web.Admin.Tests;

public sealed class PageTests : BunitContext
{
    private readonly IAdminApi _api = Substitute.For<IAdminApi>();

    public PageTests()
    {
        Services.AddSingleton(_api);
        Services.AddSingleton(new AdminOptions("https://gateway.test"));
        _api.GetDestinationsAsync(Arg.Any<CancellationToken>()).Returns([new DestinationItem("marseille", "Marseille", 43.3, 5.4)]);
    }

    private static PlaceItem Place(string name, string status = "Candidate") =>
        new(Guid.CreateVersion7(), name.ToLowerInvariant(), name, null, 43.29, 5.37, null, status, 60, 50, false, "Rules", 1000, 0, null, []);

    private static CheckReportItem Report(params CheckIssueItem[] issues) => new(issues, [new VerifiedSentenceItem("Le fort date de 1660.", "Supported"), new VerifiedSentenceItem("Il est le plus beau.", "Unsupported")], new OverlapItem(3, 0.0, true), 1, []);

    private static StoryItem Story(string status, CheckReportItem? report = null, string text = "Le fort date de 1660. Il est le plus beau.") =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "fr", "Standard", 1, status, "Le fort", "Accroche", text, "Intro", "Devant", "Gauche", "Droite", null, [], 100, "write-story@1", "model", 0.8, report ?? Report(), "marin", 0.8, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    [Fact]
    public void Home_counts_places_by_status_and_lists_the_stories_waiting_for_a_person()
    {
        _api.ListPlacesAsync("marseille", null, 500, Arg.Any<CancellationToken>()).Returns([Place("A"), Place("B"), Place("C", "Published")]);
        _api.ListStoriesByStatusAsync("NeedsReview", Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([Story("NeedsReview")]);
        _api.ListStoriesByStatusAsync(Arg.Is<string>(status => status != "NeedsReview"), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);

        var cut = Render<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("Candidat");
            cut.Find("ul.counts").TextContent.ShouldContain("2");
            cut.Markup.ShouldContain("Le fort (fr, v1)");
        });
    }

    [Fact]
    public void An_api_refusal_is_shown_to_the_editor_instead_of_crashing()
    {
        _api.ListPlacesAsync("marseille", null, 500, Arg.Any<CancellationToken>()).Returns([]);
        _api.ListStoriesByStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        _api.StartPipelineStepAsync("imports", "marseille", Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new AdminApiException("Import déjà en cours.", 409));
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll("button").ShouldNotBeEmpty());

        cut.FindAll("button").First(button => button.TextContent == "Importer OSM").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldBe("Import déjà en cours."));
    }

    [Fact]
    public void Places_can_be_filtered_by_name()
    {
        _api.ListPlacesAsync("marseille", null, 500, Arg.Any<CancellationToken>()).Returns([Place("Fort Saint-Jean"), Place("Plage des Catalans")]);
        _api.ListDedupAsync("marseille", Arg.Any<CancellationToken>()).Returns([]);
        var cut = Render<Places>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Plage des Catalans"));

        cut.Find("#search").Input("fort");

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody").TextContent.ShouldContain("Fort Saint-Jean");
            cut.Find("tbody").TextContent.ShouldNotContain("Catalans");
        });
    }

    [Fact]
    public void Editing_the_categories_of_a_place_sends_the_weights_the_editor_chose()
    {
        var place = Place("Lieu mystérieux", "NeedsReview");
        _api.GetPlaceAsync(place.Id, Arg.Any<CancellationToken>()).Returns(new PlaceDetailItem(place, [], new EthicsItem(false, false), new CrowdItem(1, 1, 2), null, false));
        var cut = Render<PlaceDetail>(parameters => parameters.Add(p => p.Id, place.Id));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Aucune règle ne couvre ce lieu"));

        cut.Find("select[aria-label='Ajouter une catégorie']").Change("history.military");
        cut.FindAll("button").First(button => button.TextContent == "Ajouter").Click();
        cut.FindAll("button").First(button => button.TextContent == "Enregistrer les poids").Click();

        _api.Received(1).SetInterestsAsync(place.Id, Arg.Is<IReadOnlyDictionary<string, double>>(weights => weights.Count == 1 && weights["history.military"] == 0.7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Ethics_flags_are_saved_with_the_place()
    {
        var place = Place("Calanque", "Published");
        _api.GetPlaceAsync(place.Id, Arg.Any<CancellationToken>()).Returns(new PlaceDetailItem(place, [], new EthicsItem(false, false), new CrowdItem(1, 2, 3), null, false));
        var cut = Render<PlaceDetail>(parameters => parameters.Add(p => p.Id, place.Id));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Site fragile"));

        cut.FindAll("input[type=checkbox]").First(box => box.ParentElement!.TextContent.Contains("fragile", StringComparison.Ordinal)).Change(true);
        cut.FindAll("section").First(section => section.TextContent.Contains("Éthique", StringComparison.Ordinal)).QuerySelector("button")!.Click();

        _api.Received(1).SetEthicsAsync(place.Id, true, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_story_shows_failed_checks_and_the_sentence_no_fact_supports()
    {
        var story = Story("NeedsReview", Report(new CheckIssueItem("style", "Terme interdit", "Il est le plus beau.")));
        _api.GetStoryAsync(story.Id, Arg.Any<CancellationToken>()).Returns(new StoryDetailItem(story, [], [], []));
        _api.ListStoriesAsync(story.PlaceId, Arg.Any<CancellationToken>()).Returns([story]);

        var cut = Render<StoryEditor>(parameters => parameters.Add(p => p.Id, story.Id));

        cut.WaitForAssertion(() =>
        {
            cut.Find("ul.issues").TextContent.ShouldContain("Terme interdit");
            cut.Find("li.unsupported").TextContent.ShouldContain("non appuyée");
            cut.Find("#text").GetAttribute("rows").ShouldBe("16");
        });
    }

    [Fact]
    public void A_published_story_cannot_be_edited_only_suspended()
    {
        var story = Story("Published");
        _api.GetStoryAsync(story.Id, Arg.Any<CancellationToken>()).Returns(new StoryDetailItem(story, [new AudioPartItem("main", "audio/x/main.mp3", "sha", 100, 1000)], [], []));
        _api.ListStoriesAsync(story.PlaceId, Arg.Any<CancellationToken>()).Returns([story]);

        var cut = Render<StoryEditor>(parameters => parameters.Add(p => p.Id, story.Id));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#text").ShouldBeEmpty();
            cut.Find("audio").GetAttribute("src").ShouldBe("https://gateway.test/media/audio/x/main.mp3");
            cut.FindAll("button").ShouldContain(button => button.TextContent == "Suspendre");
        });
    }

    [Fact]
    public void Approving_sends_the_editorial_note()
    {
        var story = Story("Checked", Report());
        _api.GetStoryAsync(story.Id, Arg.Any<CancellationToken>()).Returns(new StoryDetailItem(story, [], [], []));
        _api.ListStoriesAsync(story.PlaceId, Arg.Any<CancellationToken>()).Returns([story]);
        var cut = Render<StoryEditor>(parameters => parameters.Add(p => p.Id, story.Id));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Approuver le texte"));

        cut.Find("#score").Change("0.9");
        cut.FindAll("button").First(button => button.TextContent == "Approuver le texte").Click();

        _api.Received(1).ApproveStoryAsync(story.Id, 0.9, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Comparing_two_versions_marks_what_changed()
    {
        var current = Story("Checked", Report(), "Le fort date de 1668.");
        var previous = current with { Id = Guid.CreateVersion7(), Version = 0, Text = "Le fort date de 1660." };
        _api.GetStoryAsync(current.Id, Arg.Any<CancellationToken>()).Returns(new StoryDetailItem(current, [], [], []));
        _api.ListStoriesAsync(current.PlaceId, Arg.Any<CancellationToken>()).Returns([current, previous]);
        var cut = Render<StoryEditor>(parameters => parameters.Add(p => p.Id, current.Id));
        cut.WaitForAssertion(() => cut.FindAll("#other").ShouldNotBeEmpty());

        cut.Find("#other").Change(previous.Id.ToString());

        cut.Find("p.diff .removed").TextContent.Trim().ShouldBe("1660.");
        cut.Find("p.diff .added").TextContent.Trim().ShouldBe("1668.");
    }

    [Fact]
    public void A_config_value_that_is_not_json_is_refused_before_calling_the_api()
    {
        var entry = new ConfigEntryDto("recommendation", JsonDocument.Parse("""{"radius":500}""").RootElement.Clone(), 3, "seed", DateTimeOffset.UtcNow);
        _api.ListConfigAsync(Arg.Any<CancellationToken>()).Returns([entry]);
        _api.ListFlagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _api.GetConfigHistoryAsync("recommendation", Arg.Any<CancellationToken>()).Returns([entry]);
        var cut = Render<Config>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("recommendation"));
        cut.Find(".keys button").Click();

        cut.Find("#json").Change("{ pas du json");
        cut.FindAll("button").First(button => button.TextContent.StartsWith("Enregistrer une nouvelle", StringComparison.Ordinal)).Click();

        cut.Find("[role=alert]").TextContent.ShouldContain("pas du JSON valide".Replace("pas du", "n'est pas du"));
        _api.DidNotReceive().SetConfigAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_valid_config_value_is_saved_as_a_new_version()
    {
        var entry = new ConfigEntryDto("recommendation", JsonDocument.Parse("""{"radius":500}""").RootElement.Clone(), 3, "seed", DateTimeOffset.UtcNow);
        _api.ListConfigAsync(Arg.Any<CancellationToken>()).Returns([entry]);
        _api.ListFlagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _api.GetConfigHistoryAsync("recommendation", Arg.Any<CancellationToken>()).Returns([entry]);
        var cut = Render<Config>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("recommendation"));
        cut.Find(".keys button").Click();

        cut.Find("#json").Change("""{"radius":800}""");
        cut.FindAll("button").First(button => button.TextContent.StartsWith("Enregistrer une nouvelle", StringComparison.Ordinal)).Click();

        _api.Received(1).SetConfigAsync("recommendation", Arg.Is<JsonElement>(value => value.GetProperty("radius").GetInt32() == 800), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_feature_flag_is_saved_with_its_platforms()
    {
        _api.ListConfigAsync(Arg.Any<CancellationToken>()).Returns([]);
        _api.ListFlagsAsync(Arg.Any<CancellationToken>()).Returns([new FeatureFlagDto("offline_packs", false, 0, ["ios"], null)]);
        var cut = Render<Config>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("offline_packs"));

        cut.Find("input[aria-label='offline_packs actif']").Change(true);
        cut.Find("input[aria-label='offline_packs plateformes']").Change("ios, android");
        cut.FindAll("button").First(button => button.TextContent == "Enregistrer").Click();

        _api.Received(1).SetFlagAsync("offline_packs", Arg.Is<SetFeatureFlagRequest>(request => request.Enabled && request.Platforms!.SequenceEqual(new[] { "ios", "android" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_pronunciation_entry_is_added_for_the_destination()
    {
        _api.ListPronunciationsAsync("marseille", Arg.Any<CancellationToken>()).Returns([]);
        var cut = Render<References>();
        cut.WaitForAssertion(() => cut.FindAll("input[aria-label='Terme']").ShouldNotBeEmpty());

        cut.Find("input[aria-label='Terme']").Change("Canebière");
        cut.Find("input[aria-label='Prononciation']").Change("Canebiaire");
        cut.FindAll("button").First(button => button.TextContent == "Ajouter").Click();

        _api.Received(1).SetPronunciationAsync("marseille", "Canebière", "Canebiaire", Arg.Any<CancellationToken>());
    }

    private (PlaceItem Place, IRenderedComponent<PlaceDetail> Cut) ShowPlaceWithVideos(IReadOnlyList<PlaceVideoItem> selected)
    {
        var place = Place("Notre-Dame de la Garde", "Published");
        _api.GetPlaceAsync(place.Id, Arg.Any<CancellationToken>()).Returns(new PlaceDetailItem(place, [], new EthicsItem(false, false), new CrowdItem(1, 2, 3), null, false));
        _api.ListVideosAsync(place.Id, Arg.Any<CancellationToken>()).Returns(selected);
        var cut = Render<PlaceDetail>(parameters => parameters.Add(p => p.Id, place.Id));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Vidéos (0 à 2)"));
        return (place, cut);
    }

    private static VideoCandidateItem Candidate(string id, string title) => new(id, title, "Chaîne", $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero));

    private static PlaceVideoItem Selected(string id, string title) => new(id, title, "Chaîne", $"thumbs/x/{id}.jpg", $"https://www.youtube.com/watch?v={id}", DateTimeOffset.UtcNow);

    [Fact]
    public void An_editor_searches_on_the_server_and_picks_a_video()
    {
        var (place, cut) = ShowPlaceWithVideos([]);
        _api.SearchVideosAsync("garde Marseille", Arg.Any<CancellationToken>()).Returns([Candidate("abcdefghijk", "La Bonne Mère"), Candidate("ZYXWVUTSRQP", "Vue du ciel")]);

        cut.Find("input[aria-label='Recherche de vidéos']").Input("garde Marseille");
        cut.FindAll("button").First(button => button.TextContent == "Chercher sur YouTube").Click();
        cut.WaitForAssertion(() => cut.FindAll(".candidates li").Count.ShouldBe(2));
        cut.FindAll(".candidates button")[0].Click();

        _api.Received(1).SelectVideoAsync(place.Id, "abcdefghijk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Two_selected_videos_close_the_choice_and_each_can_be_removed()
    {
        var (place, cut) = ShowPlaceWithVideos([Selected("abcdefghijk", "Première"), Selected("ZYXWVUTSRQP", "Seconde")]);
        _api.SearchVideosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([Candidate("QQQQQQQQQQQ", "Troisième")]);

        cut.Find("input[aria-label='Recherche de vidéos']").Input("garde");
        cut.FindAll("button").First(button => button.TextContent == "Chercher sur YouTube").Click();

        cut.WaitForAssertion(() => cut.FindAll(".candidates button").Count.ShouldBe(1));
        cut.Find(".candidates button").HasAttribute("disabled").ShouldBeTrue("a place has at most two videos");
        cut.FindAll(".videos a").ShouldAllBe(link => link.GetAttribute("rel") == "noopener noreferrer");
        cut.FindAll(".videos button")[0].Click();
        _api.Received(1).RemoveVideoAsync(place.Id, "abcdefghijk", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_search_the_server_cannot_do_shows_why_instead_of_an_empty_list()
    {
        var (_, cut) = ShowPlaceWithVideos([]);
        _api.SearchVideosAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<VideoCandidateItem>>>(_ => throw new AdminApiException("No YouTube key is configured on the server (YouTube:ApiKey).", 503));

        cut.Find("input[aria-label='Recherche de vidéos']").Input("garde");
        cut.FindAll("button").First(button => button.TextContent == "Chercher sur YouTube").Click();

        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.ShouldContain("YouTube:ApiKey"));
        cut.FindAll(".candidates li").ShouldBeEmpty();
    }
}
