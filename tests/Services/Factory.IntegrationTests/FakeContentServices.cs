using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnVoyage.Factory.Application.Content;

namespace Factory.IntegrationTests;

/// <summary>Stands in for Wikipedia, the language model and the speech provider. The text, the quotes and the tone are deterministic.</summary>
internal sealed class FakeContentServices
{
    public const string Article =
        "Le fort Saint-Jean est une forteresse construite à l'entrée du Vieux-Port de Marseille. " +
        "Sa construction commence en 1660 sur ordre de Louis XIV. " +
        "Le fort comprend une tour médiévale bâtie par les chevaliers de l'ordre de Saint-Jean de Jérusalem. " +
        "Une passerelle moderne le relie au musée des civilisations depuis 2013.";

    public static readonly string[] Quotes =
    [
        "Sa construction commence en 1660 sur ordre de Louis XIV",
        "Le fort comprend une tour médiévale bâtie par les chevaliers de l'ordre de Saint-Jean de Jérusalem",
        "Une passerelle moderne le relie au musée des civilisations depuis 2013",
    ];

    public FakeWikipedia Wikipedia { get; } = new();

    public FakeExtractor Extractor { get; } = new();

    public FakeWriter Writer { get; } = new();

    public FakeVerifier Verifier { get; } = new();

    public FakeSpeech Speech { get; } = new();

    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IWikipediaTextClient>();
        services.AddSingleton<IWikipediaTextClient>(Wikipedia);
        services.RemoveAll<IFactExtractor>();
        services.AddSingleton<IFactExtractor>(Extractor);
        services.RemoveAll<IStoryWriter>();
        services.AddSingleton<IStoryWriter>(Writer);
        services.RemoveAll<IStoryVerifier>();
        services.AddSingleton<IStoryVerifier>(Verifier);
        services.RemoveAll<ITextToSpeechProvider>();
        services.AddSingleton<ITextToSpeechProvider>(Speech);
    }
}

internal sealed class FakeWikipedia : IWikipediaTextClient
{
    public Task<WikipediaText?> GetExtractAsync(string language, string title, CancellationToken cancellationToken) =>
        Task.FromResult<WikipediaText?>(new WikipediaText(language, title, $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title)}", FakeContentServices.Article, "123456"));
}

internal sealed class FakeExtractor : IFactExtractor
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<CandidateFact>> ExtractAsync(FactExtractionRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<CandidateFact>>(
        [
            new CandidateFact("La construction du fort commence en 1660, sur ordre de Louis XIV.", "Date", FakeContentServices.Quotes[0], 0.95),
            new CandidateFact("Une tour médiévale du fort a été bâtie par les chevaliers de Saint-Jean de Jérusalem.", "Architecture", FakeContentServices.Quotes[1], 0.9),
            new CandidateFact("Une passerelle relie le fort au musée des civilisations depuis 2013.", "Date", FakeContentServices.Quotes[2], 0.9),
            new CandidateFact("Le fort abrite un trésor caché.", "Anecdote", "Un trésor est caché sous la chapelle du fort", 0.8),
        ]);
    }
}

internal sealed class FakeWriter : IStoryWriter
{
    public List<StoryWriteRequest> Requests { get; } = [];

    public Task<StoryDraft> WriteAsync(StoryWriteRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var words = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            words.Append(System.Globalization.CultureInfo.InvariantCulture, $"Au pied du port, le visiteur longe les remparts et découvre, étape {i + 1}, un détail de l'histoire des lieux. ");
        }

        return Task.FromResult(new StoryDraft(
            "Le fort de l'entrée du port", "Une sentinelle de pierre.", words.ToString().Trim(), "Imaginez la rade, il y a quatre siècles.",
            "Le fort est devant vous.", "Le fort est sur votre gauche.", "Le fort est sur votre droite.", null,
            [.. request.Facts.Select(fact => fact.Id)], ["history.military"], [], 100, "write-story@1", "fake-writer"));
    }
}

internal sealed class FakeVerifier : IStoryVerifier
{
    public Task<IReadOnlyList<VerifiedSentence>> VerifyAsync(StoryVerifyRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VerifiedSentence>>([.. request.Sentences.Select(sentence => new VerifiedSentence(sentence, SentenceVerdict.Supported, [request.Facts[0].Id]))]);
}

internal sealed class FakeSpeech : ITextToSpeechProvider
{
    public List<SpeechRequest> Requests { get; } = [];

    public Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(new SpeechResult(Tone(2.0), "fake", "fake-tts", request.Text.Length));
    }

    /// <summary>A 24 kHz mono 16-bit WAV with a 440 Hz tone, loud enough for ffmpeg to measure.</summary>
    private static byte[] Tone(double seconds)
    {
        const int rate = 24000;
        var samples = (int)(seconds * rate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
        }

        return stream.ToArray();
    }
}
