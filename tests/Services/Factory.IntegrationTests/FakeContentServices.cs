using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Bootstrap;
using OnVoyage.Factory.Application.Features.Videos;

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

    public FakeUsage Usage { get; } = new();

    public FakeWriter Writer { get; }

    public FakeVerifier Verifier { get; } = new();

    public FakeSpeech Speech { get; } = new();

    public FakeVideoSearch Videos { get; } = new();

    public FakeContentServices() => Writer = new FakeWriter { Usage = Usage };

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
        services.RemoveAll<IUsageReader>();
        services.AddSingleton<IUsageReader>(Usage);
        services.RemoveAll<IVideoSearch>();
        services.AddSingleton<IVideoSearch>(Videos);
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

    /// <summary>Places (by name) for which the provider is "down": the call raises the error a real outage would.</summary>
    public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> CallsByPlace { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<CandidateFact>> ExtractAsync(FactExtractionRequest request, CancellationToken cancellationToken)
    {
        lock (CallsByPlace)
        {
            Calls++;
            CallsByPlace[request.PlaceName] = CallsByPlace.GetValueOrDefault(request.PlaceName) + 1;
        }

        if (FailFor.Contains(request.PlaceName))
        {
            throw new OnVoyage.Factory.Application.Content.ExternalServiceException($"provider down for {request.PlaceName}");
        }

        return Task.FromResult<IReadOnlyList<CandidateFact>>(
        [
            new CandidateFact("La construction du fort commence en 1660, sur ordre de Louis XIV.", "Date", FakeContentServices.Quotes[0], 0.95),
            new CandidateFact("Une tour médiévale du fort a été bâtie par les chevaliers de Saint-Jean de Jérusalem.", "Architecture", FakeContentServices.Quotes[1], 0.9),
            new CandidateFact("Une passerelle relie le fort au musée des civilisations depuis 2013.", "Date", FakeContentServices.Quotes[2], 0.9),
            new CandidateFact("Le fort abrite un trésor caché.", "Anecdote", "Un trésor est caché sous la chapelle du fort", 0.8),
        ]);
    }
}

/// <summary>The cost of the model calls, as <c>factory.llm_call</c> would report it: each story written costs <see cref="CostPerStory"/>.</summary>
internal sealed class FakeUsage : IUsageReader
{
    private readonly List<(DateTimeOffset At, double Cost)> _calls = [];

    public double CostPerStory { get; set; } = 1d;

    public string? Problem { get; set; }

    public void Record() => _calls.Add((DateTimeOffset.UtcNow, CostPerStory));

    public Task<double> CostSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.FromResult(_calls.Where(call => call.At >= since).Sum(call => call.Cost));

    public string? PriceProblem() => Problem;
}

internal sealed class FakeWriter : IStoryWriter
{
    public List<StoryWriteRequest> Requests { get; } = [];

    public FakeUsage? Usage { get; init; }

    public Task<StoryDraft> WriteAsync(StoryWriteRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Usage?.Record();
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

    /// <summary>False simulates a deployment with no voice: stories are then published as text only.</summary>
    public bool Available { get; set; } = true;

    public bool IsAvailable => Available;

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

internal sealed class FakeVideoSearch : IVideoSearch
{
    public bool IsConfigured { get; set; } = true;

    public List<string> Searches { get; } = [];

    public Dictionary<string, VideoCandidate> Known { get; } = new(StringComparer.Ordinal)
    {
        ["abcdefghijk"] = new("abcdefghijk", "La Bonne Mère racontée", "Marseille Tourisme", "https://i.ytimg.com/vi/abcdefghijk/mqdefault.jpg", new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero)),
        ["ZYXWVUTSRQP"] = new("ZYXWVUTSRQP", "Vue depuis la colline", "Provence Drone", "https://i.ytimg.com/vi/ZYXWVUTSRQP/mqdefault.jpg", null),
        ["QQQQQQQQQQQ"] = new("QQQQQQQQQQQ", "Une troisième vidéo", "Autre", "https://i.ytimg.com/vi/QQQQQQQQQQQ/mqdefault.jpg", null),
    };

    public Task<IReadOnlyList<VideoCandidate>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        Searches.Add(query);
        return Task.FromResult<IReadOnlyList<VideoCandidate>>([.. Known.Values]);
    }

    public Task<VideoCandidate?> GetAsync(string videoId, CancellationToken cancellationToken) => Task.FromResult(Known.GetValueOrDefault(videoId));

    public Task<byte[]?> DownloadThumbnailAsync(string url, CancellationToken cancellationToken) => Task.FromResult<byte[]?>([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]);
}
