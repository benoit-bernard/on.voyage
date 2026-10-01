using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Features.Content;

public sealed record FetchSourcesCommand(Guid PlaceId);

public sealed record ExtractFactsCommand(Guid PlaceId);

public sealed record DecideFactCommand(Guid FactId, bool Accept, string? Reason);

public sealed record ListFactsQuery(Guid PlaceId);

public sealed record FetchSummary(int Fetched, int Unchanged, int Missing);

public sealed record ExtractSummary(int Documents, int Validated, int Rejected, int Conflicts);

public sealed record PlaceFacts(IReadOnlyList<SourceDocument> Documents, IReadOnlyList<FactRecord> Facts);

public static class FetchSourcesHandler
{
    /// <summary>Stores the Wikipedia articles of a place as source documents (§8.3), with revision, date and licence. Unchanged text is not stored again.</summary>
    public static async Task<(Result<FetchSummary> Result, ExtractFactsCommand? Next)> Handle(
        FetchSourcesCommand command, IPlaceStore places, IWikipediaTextClient wikipedia, IContentStore content, IContentSettingsProvider settings, TimeProvider clock, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return (Result.Failure<FetchSummary>("place_not_found", "Place not found."), null);
        }

        var articles = new List<(string Language, string Title)>();
        if (place.Enrichment?.WikipediaFr is { } fr)
        {
            articles.Add(("fr", fr));
        }

        if (place.Enrichment?.WikipediaEn is { } en)
        {
            articles.Add(("en", en));
        }

        int fetched = 0, unchanged = 0, missing = 0;
        foreach (var (language, title) in articles)
        {
            var text = await wikipedia.GetExtractAsync(language, title, cancellationToken);
            if (text is null || string.IsNullOrWhiteSpace(text.Text))
            {
                missing++;
                continue;
            }

            var document = new SourceDocument(
                Guid.CreateVersion7(), place.Id, "wikipedia", text.Url, text.Title, "Wikipédia", "CC BY-SA 4.0", language, text.Revision, clock.GetUtcNow(),
                text.Text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Text))).ToLowerInvariant(), settings.Current.SourceQuality.GetValueOrDefault("wikipedia", 0.7));

            if (await content.SaveDocumentAsync(document, cancellationToken))
            {
                fetched++;
            }
            else
            {
                unchanged++;
            }
        }

        return (Result.Success(new FetchSummary(fetched, unchanged, missing)), new ExtractFactsCommand(place.Id));
    }
}

public static class ExtractFactsHandler
{
    private const int MaxStatementLength = 300;

    /// <summary>
    /// One structured-output call per document that has no facts yet. The model's claims are only kept when their quote is found, word for
    /// word, in the document; then facts that contradict another source are set aside for a person.
    /// </summary>
    public static async Task<Result<ExtractSummary>> Handle(
        ExtractFactsCommand command, IPlaceStore places, IContentStore content, IFactExtractor extractor, ILogger<ExtractFactsCommand> logger, CancellationToken cancellationToken)
    {
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null)
        {
            return Result.Failure<ExtractSummary>("place_not_found", "Place not found.");
        }

        var documents = await content.ListDocumentsAsync(place.Id, cancellationToken);
        var existing = await content.ListFactsAsync(place.Id, cancellationToken);
        var processed = existing.Select(fact => fact.DocumentId).ToHashSet();

        int validated = 0, rejected = 0, extracted = 0;
        foreach (var document in documents.Where(document => !processed.Contains(document.Id)))
        {
            var candidates = await extractor.ExtractAsync(new FactExtractionRequest(place.Enrichment?.LabelFr ?? place.Name, document.Language, document.Title, document.Text, document.Id), cancellationToken);
            var facts = candidates.Select(candidate => Judge(place.Id, document, candidate)).ToList();
            await content.AddFactsAsync(facts, cancellationToken);
            validated += facts.Count(fact => fact.Status == FactStatus.Validated);
            rejected += facts.Count(fact => fact.Status == FactStatus.Rejected);
            extracted++;
        }

        var conflicts = await MarkConflictsAsync(place.Id, content, cancellationToken);
        logger.LogInformation("Facts for place {Place}: {Validated} validated, {Rejected} rejected, {Conflicts} conflicting.", place.Id, validated, rejected, conflicts);
        return Result.Success(new ExtractSummary(extracted, validated, rejected, conflicts));
    }

    internal static FactRecord Judge(Guid placeId, SourceDocument document, CandidateFact candidate)
    {
        var confidence = Math.Clamp(double.IsNaN(candidate.Confidence) ? 0d : candidate.Confidence, 0d, 1d);
        var statement = candidate.Statement?.Trim() ?? string.Empty;
        var quote = candidate.Quote?.Trim() ?? string.Empty;

        if (!Enum.TryParse<FactType>(candidate.Type, true, out var type) || !Enum.IsDefined(type))
        {
            return new FactRecord(Guid.CreateVersion7(), placeId, document.Id, statement, FactType.Anecdote, quote, confidence, FactStatus.Rejected, "unknown_type");
        }

        if (statement.Length == 0 || statement.Length > MaxStatementLength)
        {
            return new FactRecord(Guid.CreateVersion7(), placeId, document.Id, statement, type, quote, confidence, FactStatus.Rejected, "invalid_statement");
        }

        return QuoteValidator.IsExactQuote(quote, document.Text)
            ? new FactRecord(Guid.CreateVersion7(), placeId, document.Id, statement, type, quote, confidence, FactStatus.Validated, null)
            : new FactRecord(Guid.CreateVersion7(), placeId, document.Id, statement, type, quote, confidence, FactStatus.Rejected, "quote_not_found");
    }

    internal static async Task<int> MarkConflictsAsync(Guid placeId, IContentStore content, CancellationToken cancellationToken)
    {
        var facts = await content.ListFactsAsync(placeId, cancellationToken);
        var candidates = facts.Where(fact => fact.Status is FactStatus.Validated or FactStatus.Conflict).Select(fact => new FactSnapshot(fact.Id, fact.DocumentId, fact.Type, fact.Statement)).ToList();
        var conflicting = FactConflictDetector.Detect(candidates);
        var toMark = facts.Where(fact => fact.Status == FactStatus.Validated && conflicting.Contains(fact.Id)).Select(fact => fact.Id).ToArray();
        if (toMark.Length > 0)
        {
            await content.SetFactStatusAsync(toMark, FactStatus.Conflict, "contradicts_another_source", cancellationToken);
        }

        return facts.Count(fact => fact.Status == FactStatus.Conflict) + toMark.Length;
    }
}

public static class FactAdminHandler
{
    /// <summary>An editor settles a fact: accept it (a conflict resolved in its favour) or reject it.</summary>
    public static async Task<Result<bool>> Handle(DecideFactCommand command, IContentStore content, CancellationToken cancellationToken)
    {
        var fact = await content.FindFactAsync(command.FactId, cancellationToken);
        if (fact is null)
        {
            return Result.Failure<bool>("fact_not_found", "Fact not found.");
        }

        if (!command.Accept && string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure<bool>("validation", "A reason is required to reject a fact.");
        }

        if (command.Accept && !QuoteValidatorAcceptsEditorOverride(fact))
        {
            return Result.Failure<bool>("validation", "A fact without a usable statement cannot be accepted.");
        }

        await content.SetFactStatusAsync([fact.Id], command.Accept ? FactStatus.Validated : FactStatus.Rejected, command.Accept ? null : $"editor: {command.Reason!.Trim()}", cancellationToken);
        return Result.Success(true);
    }

    private static bool QuoteValidatorAcceptsEditorOverride(FactRecord fact) => !string.IsNullOrWhiteSpace(fact.Statement);

    public static async Task<Result<PlaceFacts>> Handle(ListFactsQuery query, IContentStore content, CancellationToken cancellationToken) =>
        Result.Success(new PlaceFacts(await content.ListDocumentsAsync(query.PlaceId, cancellationToken), await content.ListFactsAsync(query.PlaceId, cancellationToken)));
}
