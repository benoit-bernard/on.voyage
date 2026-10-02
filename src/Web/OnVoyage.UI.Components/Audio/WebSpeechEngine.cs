using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.UI.Components.Audio;

/// <summary>
/// <see cref="ISpeechEngine"/> over the browser's Web Speech API (<c>wwwroot/js/device.js</c>), for the stories published without audio. Only a
/// voice that runs on the device is used, so the text of a story is never sent to a speech service. <see cref="IsAvailable"/> becomes true
/// once <see cref="InitializeAsync"/> has found such a voice for the language: browsers load their voices late.
/// </summary>
public sealed class WebSpeechEngine(IJSRuntime js) : ISpeechEngine, IAsyncDisposable
{
    private const string Module = "./_content/OnVoyage.UI.Components/js/device.js";
    private IJSObjectReference? _module;
    private bool _available;

    public bool IsAvailable => _available;

    /// <summary>The playback rate is applied by the browser to each piece.</summary>
    public bool SupportsRate => true;

    public async Task InitializeAsync(string language = "fr")
    {
        try
        {
            _available = await (await ModuleAsync()).InvokeAsync<bool>("localVoiceAvailable", language);
        }
        catch (JSException)
        {
            _available = false;
        }
    }

    public async Task<bool> SpeakAsync(string text, string language, double rate, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var module = await ModuleAsync();

        // The token is not given to the call: cancelling asks the browser to stop, and the pending promise then answers "cancelled".
        await using var registration = cancellationToken.Register(() => _ = module.InvokeVoidAsync("cancelSpeech").AsTask());
        var outcome = await module.InvokeAsync<string>("speak", text, language, rate);
        return outcome == "ended";
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.InvokeVoidAsync("cancelSpeech");
            await _module.DisposeAsync();
        }
    }

    private async Task<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", Module);
}

public static class BrowserNarrationExtensions
{
    /// <summary>Registers the browser voice as the <see cref="ITextNarrator"/> of the PWA. Call <see cref="InitializeBrowserNarration"/> once the host is built.</summary>
    public static IServiceCollection AddBrowserTextNarration(this IServiceCollection services)
    {
        services.AddSingleton<WebSpeechEngine>();
        services.AddSingleton<ISpeechEngine>(provider => provider.GetRequiredService<WebSpeechEngine>());
        services.AddSingleton<ITextNarrator, SequentialTextNarrator>();
        return services;
    }

    /// <summary>Looks for a local French voice without holding up the start of the app: the story page offers the button once it is found.</summary>
    public static void InitializeBrowserNarration(this IServiceProvider services) =>
        _ = services.GetRequiredService<WebSpeechEngine>().InitializeAsync();
}
