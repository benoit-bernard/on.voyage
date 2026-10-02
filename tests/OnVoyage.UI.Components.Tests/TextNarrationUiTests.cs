using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Catalog;
using OnVoyage.App.Core.Profile;
using OnVoyage.Catalog.Contracts;
using OnVoyage.UI.Components.Pages;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

internal sealed class RecordingNarrator : ITextNarrator
{
    public event Action<NarrationEvent>? Event
    {
        add { }
        remove { }
    }

    public bool IsAvailable { get; set; } = true;

    public bool SupportsSpeed { get; set; } = true;

    public List<string> Calls { get; } = [];

    public Task SpeakAsync(string text, string language, double speed, CancellationToken cancellationToken)
    {
        Calls.Add($"speak:{language}:{text}");
        return Task.CompletedTask;
    }

    public Task PauseAsync() => Add("pause");

    public Task ResumeAsync() => Add("resume");

    public Task SetSpeedAsync(double speed) => Add($"speed:{speed}");

    public Task StopAsync() => Add("stop");

    private Task Add(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

public sealed class TextNarrationUiTests : BunitContext
{
    private readonly RecordingNarrator _narrator = new();
    private readonly SilentPlayer _player = new();

    private IRenderedComponent<Place> Arrange(string? audioUrl, string text = "Louis XIV fait bâtir le fort.")
    {
        var id = Guid.NewGuid();
        var catalog = Substitute.For<ICatalogClient>();
        catalog.GetPoiAsync("fort", Arg.Any<CancellationToken>()).Returns(new PoiDetailDto(
            id, "fort", "Fort Saint-Jean", "history", 43.29, 5.36, 0.8, 2, false,
            [new StoryDto(Guid.NewGuid(), "fr", "Le fort", text, 90, audioUrl, true)], ["© OpenStreetMap contributors"]));
        catalog.GetPoisAsync("marseille", null, null, Arg.Any<CancellationToken>()).Returns([]);
        var profiles = new InMemoryProfileStore();
        Services.AddSingleton<IProfileStore>(profiles);
        Services.AddSingleton(catalog);
        this.AddLearning(profiles);
        Services.AddSingleton(new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), new FakeTimeProvider(), narrator: _narrator));
        return Render<Place>(p => p.Add(x => x.Slug, "fort"));
    }

    [Fact]
    public void A_story_without_audio_offers_the_device_voice_and_says_whose_voice_it_is()
    {
        var cut = Arrange(audioUrl: null);

        cut.WaitForAssertion(() => cut.Find("button.narrate").TextContent.ShouldContain("Écouter avec la voix de l'appareil"));
        cut.Find("article .note").TextContent.ShouldContain("Voix de synthèse de votre appareil");
        cut.FindAll("audio").ShouldBeEmpty();
    }

    [Fact]
    public void Tapping_it_has_the_device_read_the_text_of_the_story_in_its_language()
    {
        var cut = Arrange(audioUrl: null);
        cut.WaitForAssertion(() => cut.FindAll("button.narrate").Count.ShouldBe(1));

        cut.Find("button.narrate").Click();

        cut.WaitForAssertion(() => _narrator.Calls.ShouldBe(["speak:fr:Louis XIV fait bâtir le fort."]));
        _player.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_story_with_audio_keeps_its_player_and_a_device_without_a_voice_keeps_the_waiting_message()
    {
        var withAudio = Arrange(audioUrl: "https://media/main.mp3");
        withAudio.WaitForAssertion(() => withAudio.Find("audio").GetAttribute("src").ShouldBe("https://media/main.mp3"));
        withAudio.FindAll("button.narrate").ShouldBeEmpty();
    }

    [Fact]
    public void Without_a_device_voice_the_waiting_message_stays()
    {
        _narrator.IsAvailable = false;

        var cut = Arrange(audioUrl: null);

        cut.WaitForAssertion(() => cut.Find(".pending").TextContent.ShouldContain("en préparation"));
        cut.FindAll("button.narrate").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_player_bar_labels_the_device_voice_and_drops_the_controls_it_cannot_honour()
    {
        _narrator.SupportsSpeed = false;
        var controller = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), new FakeTimeProvider(), narrator: _narrator);
        Services.AddSingleton(controller);
        var bar = Render<PlayerBar>();

        await bar.InvokeAsync(() => controller.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Le fort", [], PlayOrigin.Manual, 90, null, "Un texte.", "fr")));

        bar.WaitForAssertion(() =>
        {
            bar.FindAll(".ai").Select(span => span.TextContent).ShouldBe(["Voix générée par intelligence artificielle", "Voix de synthèse de votre appareil"]);
            bar.FindAll("button[aria-label*='10 secondes']").ShouldBeEmpty();
            bar.FindAll("button.speed").ShouldBeEmpty();
            bar.Find("button.play").GetAttribute("aria-label").ShouldBe("Pause");
        });
    }

    [Fact]
    public async Task The_player_bar_keeps_every_control_for_a_recorded_story()
    {
        var controller = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), new FakeTimeProvider(), narrator: _narrator);
        Services.AddSingleton(controller);
        var bar = Render<PlayerBar>();

        await bar.InvokeAsync(() => controller.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Le fort", [new AudioSource("https://media/main.mp3", AudioRole.Main)], PlayOrigin.Manual)));

        bar.WaitForAssertion(() =>
        {
            bar.FindAll("button[aria-label*='10 secondes']").Count.ShouldBe(2);
            bar.FindAll("button.speed").Count.ShouldBe(1);
            bar.FindAll(".device-voice").ShouldBeEmpty();
        });
    }
}
