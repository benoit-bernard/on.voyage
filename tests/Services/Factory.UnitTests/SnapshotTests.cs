using Microsoft.Extensions.Configuration;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Snapshot;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.Factory.Infrastructure.Offline;
using OnVoyage.Factory.Infrastructure.Snapshot;

namespace Factory.UnitTests;

/// <summary>The committed Marseille snapshot is data a person reviews: these checks keep it loadable, inside the city and in the house style.</summary>
public sealed class SnapshotTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Bounding box of the Marseille destination in Factory:Destinations.
    private const double MinLongitude = 5.2285, MinLatitude = 43.1696, MaxLongitude = 5.5324, MaxLatitude = 43.3910;

    private static async Task<SnapshotBundle> LoadAsync()
    {
        var bundle = await new FileSnapshotSource(new ConfigurationBuilder().Build()).LoadAsync("marseille", Ct);
        return bundle.ShouldNotBeNull("data-pipeline/marseille/ must be found from the test directory upwards");
    }

    [Fact]
    public async Task The_committed_snapshot_loads_and_passes_the_static_checks()
    {
        var bundle = await LoadAsync();

        bundle.Destination.Slug.ShouldBe("marseille");
        bundle.Destination.Review.ShouldBe("ai_draft_needs_human_review");
        bundle.Pois.Count.ShouldBeGreaterThanOrEqualTo(30);
        ImportSnapshotHandler.FindProblem(bundle).ShouldBeNull();
    }

    [Fact]
    public async Task Every_place_is_inside_the_destination_box_and_ethics_fields_are_filled_in()
    {
        var bundle = await LoadAsync();

        foreach (var poi in bundle.Pois)
        {
            poi.Longitude.ShouldBeInRange(MinLongitude, MaxLongitude, poi.Slug);
            poi.Latitude.ShouldBeInRange(MinLatitude, MaxLatitude, poi.Slug);
            poi.Crowd.Offpeak.ShouldBeLessThanOrEqualTo(poi.Crowd.Peak, poi.Slug);
        }

        // Where the access is regulated or the nature fragile, the story must say how to behave.
        bundle.Pois.Where(poi => (poi.Fragile || poi.AccessRegulated) && string.IsNullOrWhiteSpace(poi.Story.CareNote)).Select(poi => poi.Slug).ShouldBeEmpty();
        bundle.Pois.Count(poi => poi.HiddenGem).ShouldBeGreaterThanOrEqualTo(5);
        bundle.Pois.Select(poi => poi.Interests.Keys.First().Split('.')[0]).Distinct().Count().ShouldBeGreaterThanOrEqualTo(6, "the snapshot should cover many families of interests");
    }

    [Fact]
    public async Task Every_story_is_an_original_french_text_with_a_wikipedia_source_and_no_forbidden_phrase()
    {
        var bundle = await LoadAsync();

        List<string> problems = [];
        foreach (var poi in bundle.Pois)
        {
            var story = poi.Story;
            var words = LengthCheck.CountWords(story.Text);
            if (words is < 90 or > 260)
            {
                problems.Add($"{poi.Slug}: {words} words");
            }

            problems.AddRange(StyleCheck.Check(story.Text).Select(issue => $"{poi.Slug}: {issue.Detail} {issue.Sentence}"));
            problems.AddRange(SafetyCheck.Check(story.Title + "\n" + story.Text + "\n" + story.CareNote).Select(issue => $"{poi.Slug}: {issue.Detail}"));
            if (!poi.Sources.Any(source => source.Url.StartsWith("https://fr.wikipedia.org/wiki/", StringComparison.Ordinal) && source.License == "CC BY-SA 4.0"))
            {
                problems.Add($"{poi.Slug}: no Wikipedia source");
            }

            if (new[] { story.RemoteIntro, story.AnnounceFront, story.AnnounceLeft, story.AnnounceRight }.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"{poi.Slug}: missing announcement");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Identifiers_are_deterministic_and_distinct()
    {
        var place = SnapshotIds.Place("marseille", "vieux-port");

        place.ShouldBe(SnapshotIds.Place("marseille", "vieux-port"));
        place.ShouldNotBe(SnapshotIds.Place("marseille", "mucem"));
        place.ShouldNotBe(SnapshotIds.Place("nice", "vieux-port"));
        SnapshotIds.Story(place, "fr", "standard", 1).ShouldNotBe(SnapshotIds.Story(place, "fr", "standard", 2));
        SnapshotIds.SyntheticOsmId("marseille", "vieux-port").ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_snapshot_with_an_unknown_taxonomy_code_a_bad_coordinate_or_no_source_is_refused()
    {
        var bundle = await LoadAsync();
        var first = bundle.Pois[0];

        ImportSnapshotHandler.FindProblem(bundle with { Pois = [first with { Interests = new Dictionary<string, double> { ["history.nonsense"] = 1 } }] }).ShouldNotBeNull();
        ImportSnapshotHandler.FindProblem(bundle with { Pois = [first with { Latitude = 91 }] }).ShouldNotBeNull();
        ImportSnapshotHandler.FindProblem(bundle with { Pois = [first with { Sources = [] }] }).ShouldNotBeNull();
        ImportSnapshotHandler.FindProblem(bundle with { Pois = [first, first] }).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_offline_providers_are_deterministic_and_their_quotes_are_found_word_for_word()
    {
        var bundle = await LoadAsync();
        var text = bundle.Pois[0].Story.Text;
        var extractor = new OfflineFactExtractor();

        var first = await extractor.ExtractAsync(new FactExtractionRequest("X", "fr", "Titre", text, Guid.Empty), Ct);
        var second = await extractor.ExtractAsync(new FactExtractionRequest("X", "fr", "Titre", text, Guid.Empty), Ct);

        first.ShouldNotBeEmpty();
        first.Select(fact => fact.Statement).ShouldBe(second.Select(fact => fact.Statement));
        first.ShouldAllBe(fact => QuoteValidator.IsExactQuote(fact.Quote, text));

        var facts = first.Select(fact => new WriteFact(Guid.NewGuid(), fact.Statement)).ToList();
        var draft = await new OfflineStoryWriter().WriteAsync(new StoryWriteRequest("X", "Marseille", "fr", StoryKind.Standard, [], facts, LengthTarget.For(StoryKind.Standard), Guid.Empty, null), Ct);
        draft.FactsUsed.ShouldBe(facts.Select(fact => fact.Id));
        draft.Model.ShouldBe("offline");

        var verdicts = await new OfflineStoryVerifier().VerifyAsync(new StoryVerifyRequest("fr", StyleCheck.Sentences(draft.Story), facts, Guid.Empty), Ct);
        verdicts.ShouldAllBe(verdict => verdict.Verdict != SentenceVerdict.Unsupported);
    }
}
