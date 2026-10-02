using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Snapshot;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Infrastructure.Offline;

// Deterministic stand-ins for the language-model ports (Factory:Llm:Provider=offline). They call no network service and cost nothing; their text
// is assembled mechanically from the facts, so it is meant to exercise the pipeline (states, checks, audio, publication), never to be shipped.
// Real content comes from the committed snapshot (ADR-0017) or from the OpenAI provider.

internal static class OfflineText
{
    public const string PromptVersion = "offline-1";
    public const string Model = "offline";

    public static string Truncate(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    public static int Seconds(string text) => (int)Math.Round(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length / 150d * 60d);
}

/// <summary>Every sentence of the document that is long enough becomes a fact whose quote is the sentence itself (so the quote check passes).</summary>
internal sealed class OfflineFactExtractor : IFactExtractor
{
    private const int MaxFacts = 12;

    public Task<IReadOnlyList<CandidateFact>> ExtractAsync(FactExtractionRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<CandidateFact> facts =
        [
            .. StyleCheck.Sentences(request.DocumentText)
                .Select(sentence => sentence.Trim())
                .Where(sentence => sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 5 and <= 45 && sentence.Length <= 200)
                .Take(MaxFacts)
                .Select(sentence => new CandidateFact(sentence, nameof(FactType.Event), sentence, 0.7)),
        ];
        return Task.FromResult(facts);
    }
}

internal sealed class OfflineStoryWriter : IStoryWriter
{
    public Task<StoryDraft> WriteAsync(StoryWriteRequest request, CancellationToken cancellationToken)
    {
        var statements = request.Facts.Select(fact => fact.Statement.Trim().TrimEnd('.') + ".").ToList();
        var story = string.Join(' ', statements);
        var name = request.PlaceName;
        var hook = OfflineText.Truncate(statements.FirstOrDefault() ?? name, 140);
        var draft = new StoryDraft(
            name,
            hook,
            story,
            $"Nous allons parler de {name}, à {request.DestinationName}.",
            $"Devant vous, {name}.",
            $"Sur votre gauche, {name}.",
            $"Sur votre droite, {name}.",
            request.Fragile ? "Lieu fragile : restez sur les sentiers et gardez le silence." : null,
            [.. request.Facts.Select(fact => fact.Id)],
            [],
            ["Texte assemblé hors ligne, sans modèle de langage."],
            OfflineText.Seconds(story),
            OfflineText.PromptVersion,
            OfflineText.Model);
        return Task.FromResult(draft);
    }
}

internal sealed class OfflineStoryVerifier : IStoryVerifier
{
    public Task<IReadOnlyList<VerifiedSentence>> VerifyAsync(StoryVerifyRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<VerifiedSentence> verdicts =
        [
            .. request.Sentences.Select(sentence =>
            {
                var support = request.Facts
                    .Where(fact => fact.Statement.Contains(sentence.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) || sentence.Contains(fact.Statement.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                    .Select(fact => fact.Id)
                    .ToList();
                return support.Count > 0
                    ? new VerifiedSentence(sentence, SentenceVerdict.Supported, support)
                    : new VerifiedSentence(sentence, SentenceVerdict.Generic, []);
            }),
        ];
        return Task.FromResult(verdicts);
    }
}

/// <summary>
/// Serves an article from the committed snapshot instead of Wikipedia: the story of the place whose <c>wikipediaFr</c> title matches. Lets the
/// whole fetch, extract, write chain run with no network (the snapshot text is our own, so the result is flagged by the overlap check, as it should).
/// </summary>
internal sealed class OfflineWikipediaTextClient(ISnapshotSource snapshots, Microsoft.Extensions.Configuration.IConfiguration configuration) : IWikipediaTextClient
{
    public async Task<WikipediaText?> GetExtractAsync(string language, string title, CancellationToken cancellationToken)
    {
        if (language != "fr")
        {
            return null;
        }

        var slug = configuration["Factory:Offline:Destination"] ?? "marseille";
        var bundle = await snapshots.LoadAsync(slug, cancellationToken);
        var poi = bundle?.Pois.FirstOrDefault(item => string.Equals(item.WikipediaFr, title, StringComparison.OrdinalIgnoreCase));
        return poi is null
            ? null
            : new WikipediaText("fr", poi.WikipediaFr!, $"https://fr.wikipedia.org/wiki/{Uri.EscapeDataString(poi.WikipediaFr!.Replace(' ', '_'))}", poi.Story.Text, "snapshot");
    }
}
