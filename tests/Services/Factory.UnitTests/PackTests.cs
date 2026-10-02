using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Packs;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.Factory.Domain.Geo;
using OnVoyage.Factory.Infrastructure.Packs;
using OnVoyage.Packs;

namespace Factory.UnitTests;

public sealed class PackTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DestinationConfig Marseille = new("marseille", "Marseille", new GeoPoint(43.2965, 5.3698), 5.2285, 43.1696, 5.5324, 43.3910, "https://example.test/x.pbf", null);

    private readonly IDestinationCatalog _destinations = Substitute.For<IDestinationCatalog>();
    private readonly IPlaceStore _places = Substitute.For<IPlaceStore>();
    private readonly IContentStore _content = Substitute.For<IContentStore>();
    private readonly IPackPublisher _publisher = Substitute.For<IPackPublisher>();
    private readonly IMapExtractor _maps = Substitute.For<IMapExtractor>();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"factory-pack-{Guid.NewGuid():N}");

    public PackTests()
    {
        _destinations.FindAsync("marseille", Arg.Any<CancellationToken>()).Returns(Marseille);
        _publisher.PublishAsync(Arg.Any<PackContent>(), Arg.Any<CancellationToken>()).Returns(call => Info(call.Arg<PackContent>().Version));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static PackInfo Info(int version) => new("marseille", "fr", version, $"packs/marseille/fr/pack_marseille_fr_v{version}.zip", 10, "abc", 20, null, Now);

    private static PlaceRecord Place(string slug, string name) =>
        new(Guid.CreateVersion7(), "marseille", slug, name, null, new GeoPoint(43.3, 5.37), null, null, "node", 1, new Dictionary<string, string>(), PlaceStatus.Published,
            null, 0, null, false, 1, 80, 50, false, null);

    private static StoryRecord Story(PlaceRecord place, string lang, StoryKind kind, int version, ContentStatus status = ContentStatus.Published) =>
        new(Guid.CreateVersion7(), place.Id, lang, kind, version, status, $"Titre v{version}", "Accroche", $"Texte v{version}", "Avant d'y aller", "Devant", "À gauche", "À droite", null, [], 75, "p1", "m", 0.8,
            CheckReport.Empty, "marin", 0.9, null, Now, Now, Now);

    private void Arrange(PlaceRecord place, params StoryRecord[] stories)
    {
        _places.ListAsync("marseille", PlaceStatus.Published, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([place]);
        _places.GetInterestsAsync(place.Id, Arg.Any<CancellationToken>()).Returns([("history.military", 0.9), ("architecture", 0.5), ("history", 0.9)]);
        _places.GetScoreDetailAsync(place.Id, Arg.Any<CancellationToken>()).Returns(new PlaceScoreDetail(80, 50, true, 1, 2, 4, true, false));
        _content.ListStoriesAsync(place.Id, Arg.Any<CancellationToken>()).Returns(stories);
        _content.ListFactsAsync(place.Id, Arg.Any<CancellationToken>()).Returns([]);
        _content.ListDocumentsAsync(place.Id, Arg.Any<CancellationToken>()).Returns([]);
        foreach (var story in stories)
        {
            _content.ListAudioPartsAsync(story.Id, Arg.Any<CancellationToken>()).Returns([new AudioPartRecord("main", $"stories/{story.Id:N}/main.mp3", "sha", 88, 1000), new AudioPartRecord("announce_front", $"stories/{story.Id:N}/front.mp3", "sha", 5, 100)]);
        }
    }

    private Task<Result<PackInfo>> BuildAsync(string slug = "marseille", string lang = "fr") =>
        BuildPackHandler.Handle(new BuildPackCommand(slug, lang), _destinations, _places, _content, _publisher, _maps, Ct);

    [Fact]
    public async Task The_pack_holds_the_published_places_with_the_latest_story_of_each_kind_in_the_language()
    {
        var fort = Place("fort-saint-jean", "Fort Saint-Jean");
        Arrange(fort, Story(fort, "fr", StoryKind.Standard, 1), Story(fort, "fr", StoryKind.Standard, 2), Story(fort, "fr", StoryKind.Anecdote, 1), Story(fort, "en", StoryKind.Standard, 3),
            Story(fort, "fr", StoryKind.Standard, 4, ContentStatus.Draft), Story(fort, "fr", StoryKind.OnboardingClip, 1));

        var result = await BuildAsync();

        result.IsSuccess.ShouldBeTrue();
        var content = _publisher.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "PublishAsync").GetArguments()[0].ShouldBeOfType<PackContent>();
        content.Destination.ShouldBe("marseille");
        content.Version.ShouldBe(1);
        var place = content.Places.ShouldHaveSingleItem();
        place.Category.ShouldBe("history", "level-1 code of the heaviest interest");
        place.CrowdLevel.ShouldBe(4, "the peak, like the catalog");
        place.Fragile.ShouldBeTrue();
        place.Stories.Select(story => (story.Kind, story.Version)).ShouldBe([("standard", 2), ("anecdote", 1)], ignoreOrder: true);
        place.Stories.First(story => story.Kind == "standard").Audio.Select(audio => audio.Part).ShouldBe(["main", "announce_front"]);
        place.Interests.Keys.ShouldContain("history.military");
    }

    [Fact]
    public async Task The_next_version_follows_the_published_one_and_the_map_extract_is_included()
    {
        var fort = Place("fort-saint-jean", "Fort Saint-Jean");
        Arrange(fort, Story(fort, "fr", StoryKind.Standard, 1));
        _publisher.LatestAsync("marseille", "fr", Arg.Any<CancellationToken>()).Returns(Info(4));
        _maps.ExtractAsync(Marseille, Arg.Any<CancellationToken>()).Returns("/tmp/map.pmtiles");

        await BuildAsync();

        var content = _publisher.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "PublishAsync").GetArguments()[0].ShouldBeOfType<PackContent>();
        content.Version.ShouldBe(5);
        content.MapPath.ShouldBe("/tmp/map.pmtiles");
    }

    [Fact]
    public async Task A_place_that_was_never_classified_stays_out_of_the_pack()
    {
        var fort = Place("fort-saint-jean", "Fort Saint-Jean");
        Arrange(fort);
        _places.GetInterestsAsync(fort.Id, Arg.Any<CancellationToken>()).Returns([]);

        var result = await BuildAsync();

        result.Error!.Code.ShouldBe("pack_empty");
    }

    [Fact]
    public async Task An_unknown_destination_or_language_is_refused()
    {
        (await BuildAsync("atlantis")).Error!.Code.ShouldBe("destination_not_found");
        (await BuildAsync("marseille", "de")).Error!.Code.ShouldBe("validation");
    }

    // ---- publisher

    private FilePackPublisher Publisher() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Factory:MediaDirectory"] = Path.Combine(_root, "media") }).Build(), new FakeTimeProvider(Now), NullLogger<FilePackPublisher>.Instance);

    private PackContent Content(int version)
    {
        var audio = Path.Combine(_root, "media", "stories", "a", "main.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
        File.WriteAllBytes(audio, [1, 2, 3]);
        var story = new PackStory(Guid.NewGuid(), 1, "standard", "Titre", "Texte", "Intro", 60, true, [], [new PackAudio("main", "stories/a/main.mp3", 60)]);
        var place = new PackPlace(Guid.NewGuid(), "fort", "Fort", "history", 43.29, 5.36, 80, 2, false, false, false, 0.8, false, false, "Un fort.", new Dictionary<string, double> { ["history"] = 1d }, [story]);
        return new PackContent("marseille", "fr", version, "1", null, [place]);
    }

    [Fact]
    public async Task Publishing_writes_the_archive_and_latest_json_that_describe_each_other()
    {
        var publisher = Publisher();

        var info = await publisher.PublishAsync(Content(1), Ct);

        var directory = Path.Combine(_root, "media", "packs", "marseille", "fr");
        var archive = Path.Combine(directory, "pack_marseille_fr_v1.zip");
        File.Exists(archive).ShouldBeTrue();
        File.Exists(archive + ".tmp").ShouldBeFalse();
        info.ArchivePath.ShouldBe("packs/marseille/fr/pack_marseille_fr_v1.zip");
        info.ArchiveSha256.ShouldBe(await PackManifests.HashFileAsync(archive, Ct));
        info.ArchiveSize.ShouldBe(new FileInfo(archive).Length);
        info.CreatedAt.ShouldBe(Now);

        var latest = JsonSerializer.Deserialize<PackInfo>(await File.ReadAllTextAsync(Path.Combine(directory, "latest.json"), Ct), PackManifests.Json);
        latest.ShouldBe(info);
        (await publisher.LatestAsync("marseille", "fr", Ct)).ShouldBe(info);
        (await publisher.LatestAsync("marseille", "en", Ct)).ShouldBeNull();

        using var zip = ZipFile.OpenRead(archive);
        zip.Entries.Select(entry => entry.FullName).ShouldContain("manifest.json");
        zip.Entries.Select(entry => entry.FullName).ShouldContain("pack.db");
        zip.Entries.Count(entry => entry.FullName.StartsWith("audio/", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_previous_version_is_kept_next_to_the_new_one()
    {
        var publisher = Publisher();
        for (var version = 1; version <= 4; version++)
        {
            await publisher.PublishAsync(Content(version), Ct);
        }

        Directory.EnumerateFiles(Path.Combine(_root, "media", "packs", "marseille", "fr"), "*.zip").Select(Path.GetFileName).Order().ShouldBe(["pack_marseille_fr_v3.zip", "pack_marseille_fr_v4.zip"]);
    }

    [Fact]
    public async Task An_audio_path_that_leaves_the_media_directory_is_refused_and_nothing_is_left_behind()
    {
        var content = Content(1);
        var bad = content with { Places = [content.Places[0] with { Stories = [content.Places[0].Stories[0] with { Audio = [new PackAudio("main", "../../etc/passwd", 1)] }] }] };

        await Should.ThrowAsync<InvalidOperationException>(() => Publisher().PublishAsync(bad, Ct));

        Directory.Exists(Path.Combine(_root, "media", "packs")).ShouldBeFalse();
    }

    [Fact]
    public void The_map_extract_is_asked_with_an_argument_list_and_the_destination_box()
    {
        PmtilesCliMapExtractor.Arguments("https://maps.example/planet.pmtiles", "/tmp/out.pmtiles", Marseille).ShouldBe(
            ["extract", "https://maps.example/planet.pmtiles", "/tmp/out.pmtiles", "--bbox=5.2285,43.1696,5.5324,43.391", "--maxzoom=15"]);
    }

    [Fact]
    public async Task Without_a_configured_map_tool_there_is_no_extract()
    {
        var extractor = new PmtilesCliMapExtractor(new ConfigurationBuilder().Build(), NullLogger<PmtilesCliMapExtractor>.Instance);

        (await extractor.ExtractAsync(Marseille, Ct)).ShouldBeNull();
    }
}
