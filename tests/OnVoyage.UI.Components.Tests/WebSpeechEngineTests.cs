using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OnVoyage.App.Core.Audio;
using OnVoyage.UI.Components.Audio;

namespace OnVoyage.UI.Components.Tests;

public sealed class WebSpeechEngineTests : BunitContext
{
    private const string Module = "./_content/OnVoyage.UI.Components/js/device.js";
    private readonly BunitJSModuleInterop _device;

    public WebSpeechEngineTests() => _device = JSInterop.SetupModule(Module);

    private WebSpeechEngine Engine() => new(JSInterop.JSRuntime);

    [Fact]
    public async Task It_is_available_only_when_the_browser_has_a_local_voice_for_the_language()
    {
        _device.Setup<bool>("localVoiceAvailable", "fr").SetResult(true);
        _device.Setup<bool>("localVoiceAvailable", "en").SetResult(false);
        var engine = Engine();
        engine.IsAvailable.ShouldBeFalse(); // until the browser has loaded its voices

        await engine.InitializeAsync("en");
        engine.IsAvailable.ShouldBeFalse();
        await engine.InitializeAsync("fr");

        engine.IsAvailable.ShouldBeTrue();
        engine.SupportsRate.ShouldBeTrue();
    }

    [Fact]
    public async Task A_piece_is_read_by_the_browser_and_reports_whether_it_ended_or_was_cancelled()
    {
        _device.Setup<string>("speak", "Une phrase.", "fr", 1.25).SetResult("ended");
        _device.Setup<string>("speak", "Autre phrase.", "fr", 1.0).SetResult("cancelled");
        var engine = Engine();

        (await engine.SpeakAsync("Une phrase.", "fr", 1.25, CancellationToken.None)).ShouldBeTrue();
        (await engine.SpeakAsync("Autre phrase.", "fr", 1.0, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_cancelled_token_asks_the_browser_to_stop_and_nothing_is_read_when_it_is_already_cancelled()
    {
        var pending = _device.Setup<string>("speak", "Longue phrase.", "fr", 1.0);
        _device.SetupVoid("cancelSpeech").SetVoidResult();
        var engine = Engine();
        using var source = new CancellationTokenSource();

        var reading = engine.SpeakAsync("Longue phrase.", "fr", 1.0, source.Token);
        await source.CancelAsync();
        pending.SetResult("cancelled");

        (await reading).ShouldBeFalse();
        _device.VerifyInvoke("cancelSpeech");

        using var already = new CancellationTokenSource();
        await already.CancelAsync();
        (await engine.SpeakAsync("Jamais lue.", "fr", 1.0, already.Token)).ShouldBeFalse();
        _device.Invocations["speak"].Count.ShouldBe(1);
    }

    [Fact]
    public async Task Through_the_narrator_a_story_is_read_piece_by_piece_with_the_browser_voice()
    {
        _device.Setup<string>("speak", "Première phrase. Deuxième phrase.", "fr", 1.0).SetResult("ended");
        var narrator = new SequentialTextNarrator(Engine());
        List<NarrationEvent> events = [];
        narrator.Event += events.Add;

        await narrator.SpeakAsync("Première phrase. Deuxième phrase.", "fr", 1.0, CancellationToken.None);
        for (var i = 0; i < 200 && events.Count < 2; i++)
        {
            await Task.Delay(5, Xunit.TestContext.Current.CancellationToken);
        }

        events.Select(e => e.GetType()).ShouldBe([typeof(NarrationEvent.Progress), typeof(NarrationEvent.Ended)]);
    }

    [Fact]
    public async Task The_registration_makes_the_browser_voice_the_narrator_of_the_pwa()
    {
        var services = new ServiceCollection();
        services.AddSingleton(JSInterop.JSRuntime);
        services.AddBrowserTextNarration();
        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ITextNarrator>().ShouldBeOfType<SequentialTextNarrator>();
        provider.GetRequiredService<ISpeechEngine>().ShouldBeOfType<WebSpeechEngine>();
    }
}
