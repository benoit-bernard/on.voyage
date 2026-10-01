using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using OnVoyage.App.Core.Audio;
using OnVoyage.UI.Components.Shared;

namespace OnVoyage.UI.Components.Tests;

internal sealed class SilentPlayer : IAudioPlayer
{
    public event Action<AudioPlayerEvent>? Event;

    public List<string> Calls { get; } = [];

    public Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
    {
        Calls.Add($"play:{source.Role}");
        return Task.CompletedTask;
    }

    public Task PauseAsync() => Add("pause");

    public Task ResumeAsync() => Add("resume");

    public Task SeekAsync(TimeSpan position) => Add($"seek:{position.TotalSeconds:0}");

    public Task SetSpeedAsync(double speed) => Add($"speed:{speed}");

    public Task StopAsync() => Add("stop");

    public void Raise(AudioPlayerEvent playerEvent) => Event?.Invoke(playerEvent);

    public void At(double seconds, double duration) => Event?.Invoke(new AudioPlayerEvent.PositionChanged(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(duration)));

    private Task Add(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}

public sealed class PlayerBarTests : BunitContext
{
    private readonly SilentPlayer _player = new();
    private readonly AudioPlaybackController _controller;

    public PlayerBarTests()
    {
        _controller = new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), new FakeTimeProvider());
        Services.AddSingleton(_controller);
    }

    private static PlayRequest Story(string title = "Fort Saint-Jean") =>
        new(Guid.NewGuid(), Guid.NewGuid(), title, [new AudioSource("https://media/main.mp3", AudioRole.Main)], PlayOrigin.Manual);

    [Fact]
    public void Nothing_is_shown_while_nothing_plays()
    {
        var cut = Render<PlayerBar>();

        cut.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_bar_shows_the_title_the_time_and_the_permanent_voice_mention()
    {
        var cut = Render<PlayerBar>();

        await cut.InvokeAsync(() => _controller.PlayNowAsync(Story()));
        await cut.InvokeAsync(() => _player.At(34, 88));

        cut.WaitForAssertion(() =>
        {
            cut.Find(".player-title strong").TextContent.ShouldBe("Fort Saint-Jean");
            cut.Find(".time").TextContent.ShouldBe("00:34 / 01:28");
            cut.Find(".ai").TextContent.ShouldBe("Voix générée par intelligence artificielle");
            cut.Find("button.play").GetAttribute("aria-label").ShouldBe("Pause");
        });
    }

    [Fact]
    public async Task The_buttons_drive_the_player()
    {
        var cut = Render<PlayerBar>();
        await cut.InvokeAsync(() => _controller.PlayNowAsync(Story()));
        await cut.InvokeAsync(() => _player.At(30, 90));

        cut.Find("[aria-label='Reculer de 10 secondes']").Click();
        cut.Find("[aria-label='Avancer de 10 secondes']").Click();
        cut.Find("button.speed").Click();
        cut.Find("button.play").Click();

        cut.WaitForAssertion(() => cut.Find("button.play").GetAttribute("aria-label").ShouldBe("Lecture"));
        _player.Calls.ShouldContain("seek:20");
        _player.Calls.ShouldContain("seek:30");
        _player.Calls.ShouldContain("speed:1.25");
        _player.Calls.ShouldContain("pause");
        cut.Find("button.speed").TextContent.ShouldBe("1,25×");
    }

    [Fact]
    public async Task A_waiting_story_can_be_started_with_next_and_stop_empties_the_bar()
    {
        var cut = Render<PlayerBar>();
        await cut.InvokeAsync(() => _controller.EnqueueAsync(Story("Premier")));
        await cut.InvokeAsync(() => _controller.EnqueueAsync(Story("Deuxième")));

        cut.Find("[aria-label='Histoire suivante : Deuxième']").Click();
        cut.WaitForAssertion(() => cut.Find(".player-title strong").TextContent.ShouldBe("Deuxième"));

        cut.Find("[aria-label='Arrêter']").Click();
        cut.WaitForAssertion(() => cut.FindAll(".player").ShouldBeEmpty());
    }

    [Fact]
    public async Task The_first_listening_shows_the_voice_notice_once_until_it_is_acknowledged()
    {
        var cut = Render<PlayerBar>();

        await cut.InvokeAsync(() => _controller.PlayNowAsync(Story()));
        cut.WaitForAssertion(() => cut.Find(".notice strong").TextContent.ShouldBe("Voix générée par intelligence artificielle"));

        cut.Find(".notice button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".notice").ShouldBeEmpty());
    }

    [Fact]
    public async Task A_story_without_audio_shows_a_message_instead_of_a_player()
    {
        var cut = Render<PlayerBar>();

        await cut.InvokeAsync(() => _controller.PlayNowAsync(new PlayRequest(Guid.NewGuid(), Guid.NewGuid(), "Muette", [], PlayOrigin.Manual)));

        cut.WaitForAssertion(() =>
        {
            cut.Find("[role=alert]").TextContent.ShouldBe("Cette histoire n'a pas d'audio.");
            cut.FindAll(".player").ShouldBeEmpty();
        });
    }
}
