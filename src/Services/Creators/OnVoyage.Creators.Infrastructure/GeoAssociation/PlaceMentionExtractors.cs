using System.ClientModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;

namespace OnVoyage.Creators.Infrastructure.GeoAssociation;

/// <summary>What the model may not do: say a place that is not in the text. A mention whose name does not appear in the content is dropped.</summary>
internal static class PlaceMentionSource
{
    public static string Text(GeotagInput input) =>
        string.Join('\n', new[] { input.Title, input.Caption }.Concat(input.Chapters.Select(chapter => chapter.Title)).Where(part => !string.IsNullOrWhiteSpace(part)));

    public static IReadOnlyList<PlaceMention> Keep(IEnumerable<PlaceMention> mentions, GeotagInput input)
    {
        var source = " " + PlaceMatcher.Normalize(Text(input)) + " ";
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<PlaceMention> kept = [];
        foreach (var mention in mentions)
        {
            var name = PlaceMatcher.Normalize(mention.Name);
            if (name.Length < 2 || name.Length > 100 || !source.Contains(" " + name + " ", StringComparison.Ordinal) || !seen.Add(name))
            {
                continue;
            }

            kept.Add(mention with { Confidence = Math.Clamp(mention.Confidence, 0d, 1d), Evidence = mention.Evidence.Length > 200 ? mention.Evidence[..200] : mention.Evidence });
        }

        return kept;
    }
}

/// <summary>
/// The language-model reader (F-28): one structured-output call per content, in the background. The text it receives is the creator's public title,
/// caption excerpt and chapters; nothing about any traveler. Failures are the caller's to handle (the content stays « not analysed »).
/// </summary>
internal sealed class ChatPlaceMentionExtractor(IChatClient chat, IConfiguration configuration, ILogger<ChatPlaceMentionExtractor> logger) : IPlaceMentionExtractor
{
    private static readonly Meter Meter = new("OnVoyage.Creators");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("onvoyage.creators.llm.tokens", "token", "Tokens used by the geo-association reader");
    private static readonly PlaceMentionPrompt Prompt = PlaceMentionPrompt.LoadEmbedded();

    public async Task<IReadOnlyList<PlaceMention>> ExtractAsync(GeotagInput input, CancellationToken cancellationToken)
    {
        var model = configuration[Prompt.ModelSetting];
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException($"{Prompt.ModelSetting} is not configured.");
        }

        var (system, user) = Prompt.Render(new Dictionary<string, string>
        {
            ["language"] = input.Language,
            ["title"] = input.Title,
            ["caption"] = string.IsNullOrWhiteSpace(input.Caption) ? "(aucune)" : input.Caption,
            ["chapters"] = input.Chapters.Count == 0 ? "(aucun)" : string.Join('\n', input.Chapters.Select(chapter => $"{chapter.StartSeconds / 60:00}:{chapter.StartSeconds % 60:00} {chapter.Title}")),
        });
        using var schema = JsonDocument.Parse(Prompt.SchemaJson);
        var options = new ChatOptions
        {
            ModelId = model,
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), Prompt.SchemaName, $"Structured output of {Prompt.Id} v{Prompt.Version}"),
        };

        try
        {
            var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)], options, cancellationToken);
            Tokens.Add((response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0), new KeyValuePair<string, object?>("model", model));
            using var json = JsonDocument.Parse(response.Text);
            var read = json.RootElement.GetProperty("mentions").EnumerateArray().Select(item => new PlaceMention(
                item.GetProperty("name").GetString() ?? string.Empty,
                item.GetProperty("type").GetString(),
                item.TryGetProperty("city", out var city) && city.ValueKind == JsonValueKind.String ? city.GetString() : null,
                item.GetProperty("evidence").GetString() ?? string.Empty,
                item.GetProperty("confidence").GetDouble()));
            return PlaceMentionSource.Keep(read, input);
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The geo-association model call failed: {Error}", ex.GetType().Name);
            throw;
        }
    }
}

/// <summary>
/// The offline reader (<c>Creators:GeoAssociation:Provider = offline</c>): deterministic, no network, no key. It finds capitalised place-like phrases
/// (« Notre-Dame de la Garde », « Gordes »), the word after a place preposition (« à Cassis », « sur la Corniche »), and hashtags. It is a stand-in that
/// lets the whole chain run in tests and on a laptop, not a rival of the model: its confidence is its own estimate that a phrase is a place.
/// </summary>
internal sealed partial class OfflinePlaceMentionExtractor : IPlaceMentionExtractor
{
    private static readonly string[] Stop =
    [
        "je", "tu", "il", "elle", "nous", "vous", "ils", "elles", "on", "ce", "cet", "cette", "ces", "mon", "ma", "mes", "ton", "ta", "tes", "son", "sa", "ses", "notre", "votre", "leur",
        "le", "la", "les", "un", "une", "des", "du", "de", "et", "ou", "mais", "donc", "car", "voici", "voila", "merci", "bonjour", "aujourd hui", "salut", "hier", "aujourd", "demain", "ici", "la-bas",
        "retrouvez", "abonnez", "suivez", "decouvrez", "regardez", "partagez", "likez", "commentez", "mon", "ma", "mes", "episode", "partie", "chapitre", "intro", "introduction", "conclusion",
        "instagram", "youtube", "tiktok", "facebook", "twitter", "reels", "shorts", "vlog", "voyage", "travel", "video", "photo", "lien", "bio", "story", "stories", "pub", "ad", "sponsorise",
        "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi", "dimanche", "janvier", "fevrier", "mars", "avril", "mai", "juin", "juillet", "aout", "septembre", "octobre", "novembre", "decembre",
        "the", "this", "that", "here", "thanks", "hello", "welcome", "my", "our", "in", "on", "at", "to", "for", "with", "from", "and", "but", "day", "days", "week",
    ];

    // A capitalised word, then any number of: connectors (de, du, la…), an elision (d', l') and another capitalised word. « Château d'If », « Notre-Dame de la Garde », « Aix-en-Provence ».
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[A-ZÀ-ÖØ-Þ][\p{L}'’]*(?:-[\p{L}]+)*(?:[ \t]+(?:(?:de|du|des|la|le|les|del|di|van|von|the|of)[ \t]+)*(?:[dl]['’])?[A-ZÀ-ÖØ-Þ][\p{L}'’]*(?:-[\p{L}]+)*)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Capitalised();

    [GeneratedRegex(@"#([\p{L}][\p{L}\p{N}_]{2,})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Hashtag();

    [GeneratedRegex(@"(?:\b(?:à|au|aux|en|sur|vers|dans|depuis|devant|près de|autour de|jusqu'à|jusqu’à|direction|at|in|near|to)\s+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlacePreposition();

    [GeneratedRegex(@"(?<=[\p{Ll}])(?=[A-ZÀ-ÖØ-Þ])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CamelCase();

    public Task<IReadOnlyList<PlaceMention>> ExtractAsync(GeotagInput input, CancellationToken cancellationToken)
    {
        var text = string.Join('\n', new[] { input.Title, input.Caption }.Where(part => !string.IsNullOrWhiteSpace(part)));
        List<PlaceMention> found = [];
        List<PlaceMention> tags = [];
        foreach (Match match in Capitalised().Matches(text))
        {
            var phrase = Trim(match.Value);
            var first = PlaceMatcher.Normalize(phrase.Split(' ')[0]);
            var multiWord = phrase.Contains(' ', StringComparison.Ordinal) || phrase.Contains('-', StringComparison.Ordinal);
            var sentenceStart = SentenceStart(text, match.Index);
            var afterPreposition = PlacePreposition().IsMatch(text[Math.Max(0, match.Index - 20)..match.Index]);
            if (phrase.Length < 3 || Stop.Contains(first, StringComparer.Ordinal) && !multiWord)
            {
                continue;
            }

            found.Add(new PlaceMention(phrase, null, null, Window(text, match.Index, match.Length), afterPreposition ? 0.85 : multiWord ? 0.6 : sentenceStart ? 0.4 : 0.5));

            // « Gordes Roussillon Lourmarin »: a list with no separator reads as one phrase. Each capitalised word is also tried, as a whole name only.
            var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => char.IsUpper(word[0]) && word.Length >= 4 && !Stop.Contains(PlaceMatcher.Normalize(word), StringComparer.Ordinal)).ToList();
            if (words.Count >= 2)
            {
                found.AddRange(words.Select(word => new PlaceMention(word, null, null, Window(text, match.Index, match.Length), 0.5, null, ExactOnly: true)));
            }
        }

        foreach (Match match in Hashtag().Matches(text))
        {
            var tag = match.Groups[1].Value;
            var words = string.Join(' ', CamelCase().Split(tag));
            if (Stop.Contains(PlaceMatcher.Normalize(words), StringComparer.Ordinal) || tag.Length < 4)
            {
                continue;
            }

            tags.Add(new PlaceMention(words, null, null, Window(text, match.Index, match.Length), 0.5));
        }

        // A hashtag is in the text by construction (its camel-case split is not, hence the separate path).
        var result = PlaceMentionSource.Keep(found, input).ToList();
        var known = result.Select(mention => PlaceMatcher.Normalize(mention.Name)).ToHashSet(StringComparer.Ordinal);
        result.AddRange(tags.Where(tag => known.Add(PlaceMatcher.Normalize(tag.Name))));
        return Task.FromResult<IReadOnlyList<PlaceMention>>(result);
    }

    private static bool SentenceStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] is ' ' or '\t')
        {
            i--;
        }

        return i < 0 || ".!?:\n\r•*-–—".Contains(text[i], StringComparison.Ordinal);
    }

    /// <summary>Drops a leading stop word of a phrase (« Mon Vieux-Port » → « Vieux-Port »).</summary>
    private static string Trim(string phrase)
    {
        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && Stop.Contains(PlaceMatcher.Normalize(words[0]), StringComparer.Ordinal))
        {
            words.RemoveAt(0);
        }

        return string.Join(' ', words);
    }

    private static string Window(string text, int index, int length)
    {
        var start = Math.Max(0, index - 40);
        var end = Math.Min(text.Length, index + length + 40);
        return text[start..end].Replace('\n', ' ').Trim();
    }
}

/// <summary>No model configured: contents stay « not analysed » (<see cref="IGeoAssociationSettings.Enabled"/> is false, this is never called).</summary>
internal sealed class DisabledPlaceMentionExtractor : IPlaceMentionExtractor
{
    public Task<IReadOnlyList<PlaceMention>> ExtractAsync(GeotagInput input, CancellationToken cancellationToken) => throw new InvalidOperationException("The geo-association is disabled (Creators:GeoAssociation:Provider).");
}

internal sealed record ConfiguredGeoAssociationSettings(bool Enabled, double BulkThreshold, double MinProposal, int MaxPerContent, double SuggestionConfidence) : IGeoAssociationSettings
{
    public static ConfiguredGeoAssociationSettings From(IConfiguration configuration, bool enabled) => new(
        enabled,
        Math.Clamp(configuration.GetValue("Creators:GeoAssociation:BulkValidateThreshold", 0.9d), 0.5d, 1d),
        Math.Clamp(configuration.GetValue("Creators:GeoAssociation:MinProposalConfidence", PlaceMatcher.MinProposal), 0.1d, 1d),
        Math.Clamp(configuration.GetValue("Creators:GeoAssociation:MaxProposalsPerContent", 50), 1, 200),
        Math.Clamp(configuration.GetValue("Creators:GeoAssociation:SuggestionConfidence", 0.7d), 0.1d, 1d));
}
