using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OnVoyage.App.Core.Audio;
using OnVoyage.App.Core.Interactions;
using OnVoyage.App.Core.Onboarding;
using OnVoyage.App.Core.Profile;
using OnVoyage.Discovery.Contracts;
using OnVoyage.UI.Components.Pages;

namespace OnVoyage.UI.Components.Tests;

public sealed class OnboardingTests : BunitContext
{
    private readonly IDiscoveryClient _client = Substitute.For<IDiscoveryClient>();
    private readonly InMemoryProfileStore _profiles = new();
    private readonly FailableSilentPlayer _player = new();
    private readonly List<OnboardingRequest> _posted = [];
    private readonly List<OnboardingClipDto> _clips;

    private sealed class FailableSilentPlayer : IAudioPlayer
    {
        public event Action<AudioPlayerEvent>? Event
        {
            add { }
            remove { }
        }

        public bool Fail { get; set; }

        public List<string> Played { get; } = [];

        public Task PlayAsync(AudioSource source, double speed, string title, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("autoplay blocked");
            }

            Played.Add(source.Uri);
            return Task.CompletedTask;
        }

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;

        public Task SetSpeedAsync(double speed) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }

    public OnboardingTests()
    {
        _clips = [.. new[] { "history", "nature", "culture", "gastronomy", "religion" }.Select(c =>
            new OnboardingClipDto(Guid.NewGuid(), Guid.NewGuid(), $"Extrait {c}", "fr", $"https://media/{c}.mp3", 15, c, [c]))];
        _client.GetOnboardingClipsAsync("fr", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OnboardingClipDto>>(_clips));
        _client.PostOnboardingAsync(Arg.Any<OnboardingRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _posted.Add(call.Arg<OnboardingRequest>());
            return Task.FromResult(new InteractionBatchResponse(new Dictionary<string, double> { ["history"] = 0.4 }, 7, 1, 1, 0, []));
        });
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var catalog = Substitute.For<OnVoyage.App.Core.Catalog.ICatalogClient>();
        catalog.GetDestinationAsync("marseille", Arg.Any<CancellationToken>()).Returns(new OnVoyage.Catalog.Contracts.DestinationDto("marseille", "Marseille", 43.2, 5.3, 15));

        Services.AddSingleton<IProfileStore>(_profiles);
        Services.AddSingleton(_client);
        Services.AddSingleton(catalog);
        Services.AddSingleton(new AudioPlaybackController(_player, new NullAnalyticsSink(), new InMemoryFlagStore(), clock));
        Services.AddSingleton(new InteractionSender(_client, _profiles));
        Services.AddScoped<OnboardingService>();
        Services.AddSingleton<OnVoyage.App.Core.Profile.IProfileStore>(_profiles);
    }

    private IRenderedComponent<Onboarding> Start()
    {
        var cut = Render<Onboarding>();
        cut.WaitForAssertion(() => cut.Find(".clip h2").TextContent.ShouldBe("Extrait history"));
        return cut;
    }

    [Fact]
    public void The_first_clip_plays_by_itself_and_shows_the_two_buttons()
    {
        var cut = Start();
        _player.Played.ShouldBe(["https://media/history.mp3"]);
        cut.Find("button[aria-label='J\\'aime']").ShouldNotBeNull();
        cut.Find("button[aria-label='Pas pour moi']").ShouldNotBeNull();
        cut.Find(".count").TextContent.ShouldBe("1 / 5");
    }

    [Fact]
    public async Task Five_ratings_lead_to_the_categories_then_the_destination_and_send_the_answers_once()
    {
        var cut = Start();
        for (var i = 0; i < 5; i++)
        {
            cut.Find(i % 2 == 0 ? "button[aria-label='J\\'aime']" : "button[aria-label='Pas pour moi']").Click();
        }

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Affiner (facultatif)"));
        cut.FindAll("button.chip")[0].Click(); // history liked
        cut.Find(".actions button").Click();
        cut.Find("h1").TextContent.ShouldBe("Où partez-vous ?");
        cut.FindAll(".actions button").First(b => b.TextContent == "Marseille").Click();

        _posted.ShouldHaveSingleItem();
        _posted[0].Clips.Select(c => c.Liked).ShouldBe([true, false, true, false, true]);
        _posted[0].Clips.Select(c => c.StoryId).ShouldBe(_clips.Select(c => c.StoryId));
        _posted[0].LikedCategories.ShouldBe(["history"]);
        (await _profiles.LoadAsync(CancellationToken.None)).Affinities["history"].ShouldBe(0.4); // the server's vector replaced the local one
    }

    [Fact]
    public async Task Passer_skips_everything_and_leaves_a_zero_vector()
    {
        var cut = Start();
        cut.Find("button.link").Click();

        _posted.ShouldBeEmpty();
        var profile = (await _profiles.LoadAsync(CancellationToken.None));
        profile.OnboardingDone.ShouldBeTrue();
        profile.Affinities.ShouldBeEmpty();
    }

    [Fact]
    public void Swiping_right_likes_and_swiping_left_passes()
    {
        var cut = Start();
        var section = cut.Find("section.clip");
        section.TouchStart(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 50 }] });
        section.TouchEnd(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 200 }] });
        cut.WaitForAssertion(() => cut.Find(".clip h2").TextContent.ShouldBe("Extrait nature"));

        cut.Find("section.clip").TouchStart(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 200 }] });
        cut.Find("section.clip").TouchEnd(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 60 }] });
        cut.WaitForAssertion(() => cut.Find(".clip h2").TextContent.ShouldBe("Extrait culture"));

        // A tiny movement is a tap, not a swipe.
        cut.Find("section.clip").TouchStart(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 100 }] });
        cut.Find("section.clip").TouchEnd(new Microsoft.AspNetCore.Components.Web.TouchEventArgs { ChangedTouches = [new Microsoft.AspNetCore.Components.Web.TouchPoint { ClientX = 110 }] });
        cut.Find(".clip h2").TextContent.ShouldBe("Extrait culture");
    }

    [Fact]
    public void A_blocked_autoplay_shows_touch_to_listen()
    {
        _player.Fail = true;
        var cut = Render<Onboarding>();
        cut.WaitForAssertion(() => cut.Find("button.play").TextContent.Trim().ShouldBe("Touchez pour écouter"));
        cut.Markup.ShouldContain("La lecture automatique est bloquée");

        _player.Fail = false;
        cut.Find("button.play").Click();
        cut.WaitForAssertion(() => _player.Played.ShouldContain("https://media/history.mp3"));
    }

    [Fact]
    public async Task Offline_the_answers_wait_on_the_device_and_the_categories_still_count_locally()
    {
        _client.PostOnboardingAsync(Arg.Any<OnboardingRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<InteractionBatchResponse>(new HttpRequestException("offline")));
        var cut = Start();
        for (var i = 0; i < 5; i++)
        {
            cut.Find("button[aria-label='J\\'aime']").Click();
        }

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.ShouldBe("Affiner (facultatif)"));
        cut.FindAll("button.chip")[1].Click(); // architecture liked
        cut.Find(".actions button").Click();
        cut.FindAll(".actions button").First(b => b.TextContent == "Je prépare un voyage").Click();

        var profile = (await _profiles.LoadAsync(CancellationToken.None));
        profile.PendingOnboardingJson.ShouldNotBeNull();
        profile.Affinities["architecture"].ShouldBe(0.6);
        profile.OnboardingDone.ShouldBeTrue();
    }

    [Fact]
    public async Task Pending_answers_are_sent_again_later_and_forgotten_once_accepted()
    {
        var service = new OnboardingService(_client, _profiles, new InteractionSender(_client, _profiles));
        _client.PostOnboardingAsync(Arg.Any<OnboardingRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<InteractionBatchResponse>(new HttpRequestException("offline")));
        (await service.SubmitAsync([(_clips[0].StoryId, ClipVerdict.Liked)], ["nature"], [], CancellationToken.None)).ShouldBeFalse();

        _client.PostOnboardingAsync(Arg.Any<OnboardingRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _posted.Add(call.Arg<OnboardingRequest>());
            return Task.FromResult(new InteractionBatchResponse(new Dictionary<string, double> { ["nature"] = 0.7 }, 3, 1, 1, 0, []));
        });
        await service.RetryPendingAsync(CancellationToken.None);
        await service.RetryPendingAsync(CancellationToken.None); // nothing left: no second call

        _posted.ShouldHaveSingleItem().LikedCategories.ShouldBe(["nature"]);
        var profile = await _profiles.LoadAsync(CancellationToken.None);
        profile.PendingOnboardingJson.ShouldBeNull();
        profile.Affinities["nature"].ShouldBe(0.7);
    }

    [Fact]
    public async Task The_clips_are_kept_for_offline_use()
    {
        var service = new OnboardingService(_client, _profiles, new InteractionSender(_client, _profiles));
        (await service.LoadClipsAsync("fr", CancellationToken.None)).Count.ShouldBe(5);

        _client.GetOnboardingClipsAsync("fr", Arg.Any<CancellationToken>()).Returns(Task.FromException<IReadOnlyList<OnboardingClipDto>>(new HttpRequestException("offline")));
        (await service.LoadClipsAsync("fr", CancellationToken.None)).Select(c => c.StoryId).ShouldBe(_clips.Select(c => c.StoryId));
    }
}
