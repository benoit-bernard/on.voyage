using System.Globalization;
using System.Text.Json;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Taxonomy;

namespace OnVoyage.Factory.Infrastructure.Llm;

internal sealed class LlmFactExtractor(LlmRunner runner) : IFactExtractor
{
    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded("extract-facts.md");

    public async Task<IReadOnlyList<CandidateFact>> ExtractAsync(FactExtractionRequest request, CancellationToken cancellationToken)
    {
        var (json, _) = await runner.RunAsync(Prompt, new Dictionary<string, string>
        {
            ["name"] = request.PlaceName,
            ["destination"] = string.Empty,
            ["type"] = "wikipedia",
            ["text"] = request.DocumentText,
        }, [], request.ContentId, cancellationToken);

        using (json)
        {
            return [.. json.RootElement.GetProperty("facts").EnumerateArray().Select(item => new CandidateFact(
                item.GetProperty("statement").GetString() ?? string.Empty,
                item.GetProperty("type").GetString() ?? string.Empty,
                item.GetProperty("quote").GetString() ?? string.Empty,
                item.GetProperty("confidence").GetDouble()))];
        }
    }
}

/// <summary>
/// Builds the writer's prompt from facts only. <see cref="StoryWriteRequest"/> has no source-text field, so the "closed world" rule
/// is enforced by the type, not by discipline; a test also checks the prompt that actually leaves the process.
/// </summary>
internal sealed class LlmStoryWriter(LlmRunner runner) : IStoryWriter
{
    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded("write-story.md");

    public async Task<StoryDraft> WriteAsync(StoryWriteRequest request, CancellationToken cancellationToken)
    {
        var (json, model) = await runner.RunAsync(Prompt, new Dictionary<string, string>
        {
            ["lang"] = request.Language == "en" ? "anglais" : "français",
            ["kind"] = request.Kind.ToString().ToLowerInvariant(),
            ["target_seconds"] = ((request.Target.MinSeconds + request.Target.MaxSeconds) / 2).ToString(CultureInfo.InvariantCulture),
            ["min_words"] = request.Target.MinWords.ToString(CultureInfo.InvariantCulture),
            ["max_words"] = request.Target.MaxWords.ToString(CultureInfo.InvariantCulture),
            ["name"] = request.PlaceName,
            ["destination"] = request.DestinationName,
            ["fragile"] = request.Fragile ? "oui" : "non",
            ["codes"] = string.Join(", ", Interests.All),
            ["numbered_facts"] = string.Join('\n', request.Facts.Select((fact, index) => $"{index + 1}. {fact.Statement}")),
            ["feedback"] = string.IsNullOrWhiteSpace(request.Feedback) ? string.Empty : $"Corrige ces défauts de la version précédente : {request.Feedback}",
        }, Interests.All, request.ContentId, cancellationToken, temperature: 0.7f);

        using (json)
        {
            var root = json.RootElement;
            var factIds = root.GetProperty("facts_used").EnumerateArray()
                .Select(item => item.GetInt32())
                .Select(number => number >= 1 && number <= request.Facts.Count ? request.Facts[number - 1].Id : Guid.Empty)
                .ToArray();

            return new StoryDraft(
                root.GetProperty("title").GetString() ?? string.Empty,
                root.GetProperty("hook").GetString() ?? string.Empty,
                root.GetProperty("story").GetString() ?? string.Empty,
                root.GetProperty("remote_intro").GetString() ?? string.Empty,
                root.GetProperty("announce_front").GetString() ?? string.Empty,
                root.GetProperty("announce_left").GetString() ?? string.Empty,
                root.GetProperty("announce_right").GetString() ?? string.Empty,
                root.GetProperty("care_note").GetString() is { Length: > 0 } care ? care : null,
                factIds,
                [.. root.GetProperty("interests").EnumerateArray().Select(item => item.GetString() ?? string.Empty)],
                [.. root.GetProperty("uncertainties").EnumerateArray().Select(item => item.GetString() ?? string.Empty)],
                root.GetProperty("estimated_duration_s").GetInt32(),
                $"{Prompt.Id}@{Prompt.Version}",
                model);
        }
    }
}

internal sealed class LlmStoryVerifier(LlmRunner runner) : IStoryVerifier
{
    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded("verify-story.md");

    public async Task<IReadOnlyList<VerifiedSentence>> VerifyAsync(StoryVerifyRequest request, CancellationToken cancellationToken)
    {
        // The verifier sees the story's sentences and the facts, never the writer's prompt (C.3).
        var (json, _) = await runner.RunAsync(Prompt, new Dictionary<string, string>
        {
            ["numbered_facts"] = string.Join('\n', request.Facts.Select((fact, index) => $"{index + 1}. {fact.Statement}")),
            ["sentences"] = string.Join('\n', request.Sentences),
        }, [], request.ContentId, cancellationToken);

        using (json)
        {
            var byText = json.RootElement.GetProperty("sentences").EnumerateArray().ToList();
            return [.. request.Sentences.Select((sentence, index) =>
            {
                // Sentences are matched by position first: the model may normalise whitespace in what it echoes back.
                var item = index < byText.Count ? byText[index] : default;
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return new VerifiedSentence(sentence, SentenceVerdict.Unsupported, []);
                }

                var verdict = item.GetProperty("verdict").GetString() switch
                {
                    "SUPPORTED" => SentenceVerdict.Supported,
                    "GENERIC" => SentenceVerdict.Generic,
                    _ => SentenceVerdict.Unsupported,
                };
                var supporting = item.GetProperty("fact_ids").EnumerateArray().Select(number => number.GetInt32())
                    .Where(number => number >= 1 && number <= request.Facts.Count).Select(number => request.Facts[number - 1].Id).ToArray();
                return new VerifiedSentence(sentence, verdict, supporting);
            })];
        }
    }
}

internal sealed class LlmPlaceClassifier(LlmRunner runner) : IPlaceModelClassifier
{
    private static readonly PromptTemplate Prompt = PromptTemplate.LoadEmbedded("classify.md");

    public async Task<ModelClassification?> ClassifyAsync(PlaceDescription place, CancellationToken cancellationToken)
    {
        var (json, _) = await runner.RunAsync(Prompt, new Dictionary<string, string>
        {
            ["name"] = place.Name,
            ["description"] = place.Description ?? "(aucune)",
            ["osm_tags"] = string.Join(", ", place.OsmTags.Where(tag => tag.Key != "name" && !tag.Key.StartsWith("name:", StringComparison.Ordinal)).Select(tag => $"{tag.Key}={tag.Value}")),
            ["wikidata_classes"] = string.Join(", ", place.WikidataClasses),
        }, Interests.All, null, cancellationToken);

        using (json)
        {
            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var item in json.RootElement.GetProperty("categories").EnumerateArray())
            {
                var code = item.GetProperty("code").GetString() ?? string.Empty;
                weights[code] = Math.Max(weights.GetValueOrDefault(code), item.GetProperty("weight").GetDouble());
            }

            return new ModelClassification(weights, json.RootElement.GetProperty("confidence").GetDouble());
        }
    }
}
