using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Map;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

public sealed class MapPageTests : BunitContext
{
    private static readonly Guid FortId = Guid.NewGuid();
    private static readonly Guid CalanqueId = Guid.NewGuid();
    private readonly ICatalogClient _catalog = Substitute.For<ICatalogClient>();
    private readonly RecordingPlayer _player = new();
    private readonly BunitJSModuleInterop _map;

    private sealed class RecordingPlayer : IAudioPlayer
    {
        public List<string> Played { get; } = [];

        public event Action<AudioPlayerEvent>? Event
        {
            add { }
            remove { }
        }

        public Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
        {
            Played.Add(source.Uri);
            return Task.CompletedTask;
        }

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;

        public Task SetSpeedAsync(double speed) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    public MapPageTests()
    {
        _map = JSInterop.SetupModule(MapView.ModulePath);
        _map.SetupVoid("init", _ => true).SetVoidResult();
        _map.SetupVoid("setPois", _ => true).SetVoidResult();
        _map.SetupVoid("fitBounds", _ => true).SetVoidResult();
        _map.SetupVoid("setUserLocation", _ => true).SetVoidResult();
        _map.SetupVoid("dispose", _ => true).SetVoidResult();

        PoiSummaryDto Make(Guid id, string name, string category, int crowd, bool story) => new(
            id, name.ToLowerInvariant(), name, category, 43.3, 5.37, 0.7, 0.8, crowd, false, null, story ? 90 : null, new Dictionary<string, double>(),
            story ? Guid.NewGuid() : null, false, story ? new Dictionary<string, string> { ["main"] = "https://media/fort.mp3" } : null);
        _catalog.GetPoisAsync("marseille", Arg.Any<double?>(), Arg.Any<double?>(), Arg.Any<CancellationToken>())
            .Returns([Make(FortId, "Fort", "monument", 4, true), Make(CalanqueId, "Calanque", "nature", 1, false)]);

        var profiles = new InMemoryProfileStore();
        Services.AddSingleton<IProfileStore>(profiles);
        Services.AddSingleton(_catalog);
        Services.AddSingleton<ILocationProvider>(new NoLocationProvider());
        Services.AddSingleton(new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), new FakeTimeProvider(DateTimeOffset.UnixEpoch)));
        Services.AddSingleton(new MapSettings { TilesUrl = "https://tiles.example/marseille.pmtiles" });
    }

    private static MapGeoJson LastPois(BunitJSModuleInterop map) => (MapGeoJson)map.Invocations["setPois"][^1].Arguments[0]!;

    [Fact]
    public void Without_a_tiles_url_the_page_says_the_map_is_not_configured()
    {
        Services.AddSingleton(new MapSettings());
        var cut = Render<MapPage>();
        cut.Markup.ShouldContain("pas encore configurée");
        _map.Invocations["init"].ShouldBeEmpty();
    }

    [Fact]
    public async Task The_map_starts_with_the_tiles_url_and_our_own_glyphs_and_draws_the_places()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => _map.Invocations["setPois"].Count.ShouldBeGreaterThan(0));

        var options = _map.Invocations["init"].Single().Arguments[2]!;
        options.ToString()!.ShouldContain("marseille.pmtiles");
        options.ToString()!.ShouldContain("_content/OnVoyage.UI.Components/fonts");
        LastPois(_map).Features.Count.ShouldBe(2);
        _map.Invocations["fitBounds"].Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_attribution_is_always_visible()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => cut.Find(".attribution-fixed").TextContent.ShouldContain("OpenStreetMap contributors"));
        cut.Markup.ShouldContain("Protomaps");
    }

    [Fact]
    public async Task A_filter_chip_sends_the_narrowed_places_to_the_map()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => LastPois(_map).Features.Count.ShouldBe(2));

        cut.FindAll("button.chip").Single(b => b.TextContent == "Moins fréquentés").Click();
        await cut.WaitForAssertionAsync(() => LastPois(_map).Features.Select(f => f.Properties.Name).ShouldBe(["Calanque"]));
        cut.FindAll("button.chip").Single(b => b.TextContent == "Moins fréquentés").GetAttribute("aria-pressed").ShouldBe("true");
    }

    [Fact]
    public async Task Tapping_a_place_shows_a_preview_and_the_play_button_starts_its_story()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => LastPois(_map).Features.Count.ShouldBe(2));

        await cut.InvokeAsync(() => cut.FindComponent<MapView>().Instance.OnPoiTapped(FortId.ToString()));
        cut.Find(".preview h2").TextContent.ShouldBe("Fort");
        cut.Find(".preview a").GetAttribute("href").ShouldBe("lieu/fort");

        cut.Find(".preview .play").Click();
        await cut.WaitForAssertionAsync(() => _player.Played.ShouldContain("https://media/fort.mp3"));
    }

    [Fact]
    public async Task A_place_without_a_story_has_no_play_button()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => LastPois(_map).Features.Count.ShouldBe(2));
        await cut.InvokeAsync(() => cut.FindComponent<MapView>().Instance.OnPoiTapped(CalanqueId.ToString()));
        cut.FindAll(".preview .play").ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposing_the_page_releases_the_map()
    {
        var cut = Render<MapPage>();
        await cut.WaitForAssertionAsync(() => _map.Invocations["init"].Count.ShouldBe(1));
        await DisposeComponentsAsync();
        _map.Invocations["dispose"].Count.ShouldBe(1);
    }
}
