using Microsoft.Maui.Media;
using OnVoyage.App.Core.Audio;

namespace OnVoyage.App;

/// <summary>
/// <see cref="ISpeechEngine"/> over MAUI <c>TextToSpeech</c> (Android <c>TextToSpeech</c>, iOS <c>AVSpeechSynthesizer</c>): the voice of the device reads
/// a story that was published without audio. The text stays on the device. MAUI offers no rate, so <see cref="SupportsRate"/> is false and the
/// speed button is hidden while such a story is read. Not compiled in the repository's own CI image: see docs/MOBILE.md.
/// </summary>
internal sealed class MauiSpeechEngine : ISpeechEngine
{
    private readonly Dictionary<string, Locale?> _locales = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAvailable => true;

    public bool SupportsRate => false;

    public async Task<bool> SpeakAsync(string text, string language, double rate, CancellationToken cancellationToken)
    {
        var locale = await LocaleAsync(language) ?? throw new InvalidOperationException($"No voice for '{language}' on this device.");
        try
        {
            await TextToSpeech.Default.SpeakAsync(text, new SpeechOptions { Locale = locale }, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // cancelled by pause, stop or a newer piece
        }
    }

    /// <summary>The first installed voice of the language ("fr" for French, whatever the country).</summary>
    private async Task<Locale?> LocaleAsync(string language)
    {
        if (_locales.TryGetValue(language, out var known))
        {
            return known;
        }

        var all = await TextToSpeech.Default.GetLocalesAsync();
        return _locales[language] = all.FirstOrDefault(locale => string.Equals(locale.Language, language, StringComparison.OrdinalIgnoreCase));
    }
}
