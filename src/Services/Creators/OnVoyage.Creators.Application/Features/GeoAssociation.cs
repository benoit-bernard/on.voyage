using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Contracts;
using OnVoyage.Creators.Domain;
using Wolverine;

namespace OnVoyage.Creators.Application.Features;

/// <summary>Internal message: look for the places a content talks about. Sent after an import, after a content is added, and by « Analyser mes contenus ».</summary>
public sealed record AnalyzeContentCommand(Guid ContentId);

/// <summary>The creator asks for their contents to be analysed: the ones not analysed yet, or all of them with <c>Force</c>.</summary>
public sealed record AnalyzeContentsCommand(Guid AccountId, bool Force);

public sealed record ListProposalsQuery(Guid AccountId);

public sealed record ValidateProposalsCommand(Guid AccountId, ReviewPlaceLinksRequest Request);

public sealed record RejectProposalsCommand(Guid AccountId, ReviewPlaceLinksRequest Request);

public sealed record CorrectProposalCommand(Guid AccountId, Guid LinkId, Guid PoiId);

/// <summary>Meters of the geo-association (§17): contents analysed, proposals made, places suggested to the editors, proposals decided.</summary>
public static class GeoMetrics
{
    public static readonly Meter Meter = new("OnVoyage.Creators");
    public static readonly Counter<long> Analyzed = Meter.CreateCounter<long>("onvoyage.creators.geotag.contents", "content", "Contents analysed by the assistant");
    public static readonly Counter<long> Proposals = Meter.CreateCounter<long>("onvoyage.creators.geotag.proposals", "proposal", "Place proposals made, by confidence band");
    public static readonly Counter<long> Suggestions = Meter.CreateCounter<long>("onvoyage.creators.geotag.suggestions", "place", "Unknown places suggested to the editorial team");
    public static readonly Counter<long> Reviews = Meter.CreateCounter<long>("onvoyage.creators.geotag.reviews", "proposal", "Proposals decided by the creator");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("onvoyage.creators.geotag.failures", "content", "Analyses that failed");
}

/// <summary>
/// Geo-association assisted by AI (F-28, T-1209). In the background, for every online content: a reader finds the places it mentions (the text, and the
/// chapters of a video), the matcher compares them with the directory of the catalog, and what is plausible becomes a <b>proposal</b> with a confidence
/// and its evidence. A proposal is never published: only the creator (or an administrator) turns it into a validated link, one by one or in bulk above
/// 0.9; what the catalog does not know is suggested to the editorial team (<see cref="PlaceSuggestedV1"/>). No traveler data is involved at any point.
/// </summary>
public static class GeoAssociationHandler
{
    private const int MaxMentions = 60;
    private const int CandidatesPerMention = 12;
    private const int AnalyzeBatch = 200;

    public static async Task<Result<bool>> Handle(AnalyzeContentCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, IUnmatchedMentionRepository unmatched, IPlaceMentionExtractor extractor, IGeoAssociationSettings settings, ICreatorsUnitOfWork unit, TimeProvider clock, ILogger<AnalyzeContentCommand> logger, CancellationToken cancellationToken)
    {
        if (!settings.Enabled || await contents.FindContentAsync(command.ContentId, cancellationToken) is not { IsOnline: true } content || await creators.FindAsync(content.CreatorId, cancellationToken) is not { } creator)
        {
            return Result.Success(false);
        }

        var now = clock.GetUtcNow();
        var existing = await contents.ListLinksAsync(creator.Id, cancellationToken);
        var knownPois = existing.Where(link => link.IsValidated).Select(link => link.PoiId).Distinct().ToList();
        var destinations = new HashSet<Guid>(await directory.DestinationsOfAsync(knownPois, cancellationToken));
        destinations.UnionWith(creator.Profile.DestinationIds);

        IReadOnlyList<PlaceMention> read;
        try
        {
            read = await extractor.ExtractAsync(new GeotagInput(content.Title, content.CaptionExcerpt, content.Chapters, creator.Profile.Languages is { Count: > 0 } languages ? languages[0] : "fr"), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GeoMetrics.Failures.Add(1);
            logger.LogWarning("The analysis of content {ContentId} failed: {Error}.", content.Id, exception.GetType().Name);
            return Result.Failure<bool>("analysis_failed", "L'analyse du contenu a échoué : elle sera retentée à la prochaine demande.");
        }

        var mentions = MergeChapters(read, content.Chapters);
        List<(PlaceMention Mention, IReadOnlyList<PoiEntry> Candidates)> items = [];
        foreach (var mention in mentions)
        {
            items.Add((mention, await directory.FindCandidatesAsync(PlaceMatcher.Normalize(mention.Name), CandidatesPerMention, cancellationToken)));
        }

        var matches = PlaceMatcher.Match(items, new MatchContext(destinations));
        var dominantDestination = matches.Where(match => match.Best is { Confidence: >= 0.8 }).GroupBy(match => match.Best!.Poi.DestinationSlug).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;
        List<object> events = [];
        HashSet<(Guid, int?)> proposed = [];
        int proposals = 0, suggested = 0;
        foreach (var match in matches)
        {
            if (match.Best is { } best && best.Confidence >= settings.MinProposal)
            {
                if (proposals >= settings.MaxPerContent || !proposed.Add((best.Poi.PoiId, match.Mention.StartSeconds)) || await contents.FindLinkAsync(creator.Id, best.Poi.PoiId, content.Id, match.Mention.StartSeconds, cancellationToken) is not null)
                {
                    continue; // already proposed, validated or rejected: the creator's decision stands
                }

                await contents.StageLinkAsync(new PlaceLink(Guid.CreateVersion7(), creator.Id, best.Poi.PoiId, content.Id, match.Mention.StartSeconds, best.Confidence, PlaceLinkStatuses.Proposed, null, now, Signals(match, best)), cancellationToken);
                proposals++;
                GeoMetrics.Proposals.Add(1, new KeyValuePair<string, object?>("band", best.Confidence >= settings.BulkThreshold ? "high" : "review"));
            }
            else if (match.Mention.Confidence >= settings.SuggestionConfidence && !match.Mention.FromChapter && await unmatched.StageIfNewAsync(creator.Id, content.Id, PlaceMatcher.Normalize(match.Mention.Name), match.Mention.Name, match.Mention.City, Clip(match.Mention.Evidence, 200), now, cancellationToken))
            {
                events.Add(new PlaceSuggestedV1(Guid.CreateVersion7(), now, Clip(match.Mention.Name, 100)!, Clip(match.Mention.City, 100), Clip(match.Mention.Evidence, 200), creator.Id, content.Id, dominantDestination));
                suggested++;
                GeoMetrics.Suggestions.Add(1);
            }
        }

        await contents.StageContentAsync(content with { GeotaggedAt = now }, cancellationToken);
        await unit.CommitAsync(events, cancellationToken);
        GeoMetrics.Analyzed.Add(1);
        logger.LogInformation("Analysed content {ContentId}: {Mentions} mentions, {Proposals} proposals, {Suggestions} unknown places.", content.Id, mentions.Count, proposals, suggested);
        return Result.Success(true);
    }

    public static async Task<Result<AnalysisRequestedDto>> Handle(AnalyzeContentsCommand command, ICreatorRepository creators, IContentRepository contents, IMessageBus bus, CancellationToken cancellationToken)
    {
        var (creator, failure) = await StudioHandler.Own<AnalysisRequestedDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var ids = await contents.ListContentIdsToAnalyzeAsync(creator.Id, command.Force, AnalyzeBatch, cancellationToken);
        foreach (var id in ids)
        {
            await bus.PublishAsync(new AnalyzeContentCommand(id));
        }

        return Result.Success(new AnalysisRequestedDto(ids.Count));
    }

    public static async Task<Result<PlaceProposalsDto>> Handle(ListProposalsQuery query, ICreatorRepository creators, ICreatorQueries queries, IGeoAssociationSettings settings, CancellationToken cancellationToken) =>
        await creators.FindByAccountAsync(query.AccountId, cancellationToken) is { } creator
            ? Result.Success(await queries.ListProposalsAsync(creator.Id, settings.BulkThreshold, cancellationToken))
            : Result.Failure<PlaceProposalsDto>("creator_not_found", "Aucun espace créateur pour ce compte.");

    /// <summary>
    /// The creator validates proposals: those they name, or all above a confidence (« Tout valider », never below the bulk threshold). Only a
    /// <c>proposed</c> link of this creator can be reached; each validation is announced like any validated association.
    /// </summary>
    public static async Task<Result<ReviewResultDto>> Handle(ValidateProposalsCommand command, ICreatorRepository creators, IContentRepository contents, IGeoAssociationSettings settings, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await StudioHandler.Own<ReviewResultDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var now = clock.GetUtcNow();
        var chosen = await Select(command.Request, creator.Id, contents, settings, cancellationToken);
        if (chosen is null)
        {
            return Result.Failure<ReviewResultDto>("validation", "Indiquez les propositions à valider, ou une confiance minimale.");
        }

        List<object> events = [];
        foreach (var link in chosen)
        {
            var content = link.ContentId is { } id ? await contents.FindContentAsync(id, cancellationToken) : null;
            var validated = link.WithStatus(PlaceLinkStatuses.Validated, now);
            await contents.StageLinkAsync(validated, cancellationToken);
            events.AddRange(CreatorContentHandler.StatusChange(link, validated, content, now));
        }

        if (chosen.Count > 0)
        {
            events.Insert(0, CreatorEvents.Audit(command.AccountId, "place_link.validate", $"creator:{creator.Id}", $"count={chosen.Count}", now));
            GeoMetrics.Reviews.Add(chosen.Count, new KeyValuePair<string, object?>("decision", "validated"));
        }

        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(new ReviewResultDto(chosen.Count, 0));
    }

    public static async Task<Result<ReviewResultDto>> Handle(RejectProposalsCommand command, ICreatorRepository creators, IContentRepository contents, IGeoAssociationSettings settings, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await StudioHandler.Own<ReviewResultDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        var now = clock.GetUtcNow();
        var chosen = await Select(command.Request with { MinConfidence = null }, creator.Id, contents, settings, cancellationToken);
        if (chosen is null)
        {
            return Result.Failure<ReviewResultDto>("validation", "Indiquez les propositions à refuser.");
        }

        // A rejected proposal is kept (as rejected): the assistant never proposes it again.
        foreach (var link in chosen)
        {
            await contents.StageLinkAsync(link.WithStatus(PlaceLinkStatuses.Rejected, now), cancellationToken);
        }

        if (chosen.Count > 0)
        {
            GeoMetrics.Reviews.Add(chosen.Count, new KeyValuePair<string, object?>("decision", "rejected"));
            await unit.CommitAsync([CreatorEvents.Audit(command.AccountId, "place_link.reject", $"creator:{creator.Id}", $"count={chosen.Count}", now)], cancellationToken);
        }

        return Result.Success(new ReviewResultDto(0, chosen.Count));
    }

    public static async Task<Result<AdminPlaceLinkDto>> Handle(CorrectProposalCommand command, ICreatorRepository creators, IContentRepository contents, IPoiDirectory directory, ICreatorsUnitOfWork unit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (creator, failure) = await StudioHandler.Own<AdminPlaceLinkDto>(command.AccountId, creators, cancellationToken);
        if (creator is null)
        {
            return failure!;
        }

        if (await contents.FindLinkAsync(command.LinkId, cancellationToken) is not { } wrong || wrong.CreatorId != creator.Id || wrong.Status != PlaceLinkStatuses.Proposed)
        {
            return Result.Failure<AdminPlaceLinkDto>("place_link_not_found", "Proposition introuvable.");
        }

        if (await directory.FindAsync(command.PoiId, cancellationToken) is not { } place)
        {
            return Result.Failure<AdminPlaceLinkDto>("place_not_found", "Lieu inconnu du répertoire.");
        }

        var now = clock.GetUtcNow();
        var content = wrong.ContentId is { } id ? await contents.FindContentAsync(id, cancellationToken) : null;
        var current = await contents.FindLinkAsync(creator.Id, place.PoiId, wrong.ContentId, wrong.StartSeconds, cancellationToken);
        var corrected = current?.WithStatus(PlaceLinkStatuses.Validated, now)
            ?? new PlaceLink(Guid.CreateVersion7(), creator.Id, place.PoiId, wrong.ContentId, wrong.StartSeconds, 1d, PlaceLinkStatuses.Validated, now, now, JsonSerializer.Serialize(new { source = "creator_correction", replaced = wrong.PoiId }));
        await contents.StageLinkAsync(wrong.WithStatus(PlaceLinkStatuses.Rejected, now), cancellationToken);
        await contents.StageLinkAsync(corrected, cancellationToken);
        List<object> events = [CreatorEvents.Audit(command.AccountId, "place_link.correct", $"creator:{creator.Id}", $"link={wrong.Id}; poi={wrong.PoiId}->{place.PoiId}", now)];
        events.AddRange(CreatorContentHandler.StatusChange(current, corrected, content, now));
        GeoMetrics.Reviews.Add(1, new KeyValuePair<string, object?>("decision", "corrected"));
        await unit.CommitAsync(events, cancellationToken);
        return Result.Success(new AdminPlaceLinkDto(corrected.Id, corrected.PoiId, place.DisplayName, corrected.ContentId, content?.Title, corrected.StartSeconds, corrected.Confidence, corrected.Status, corrected.ValidatedAt));
    }

    private static async Task<List<PlaceLink>?> Select(ReviewPlaceLinksRequest request, Guid creatorId, IContentRepository contents, IGeoAssociationSettings settings, CancellationToken cancellationToken)
    {
        var proposed = (await contents.ListLinksAsync(creatorId, cancellationToken)).Where(link => link.CreatorId == creatorId && link.Status == PlaceLinkStatuses.Proposed);
        if (request.LinkIds is { Count: > 0 } ids)
        {
            return [.. proposed.Where(link => ids.Contains(link.Id))];
        }

        if (request.MinConfidence is { } minimum)
        {
            var threshold = Math.Max(minimum, settings.BulkThreshold);
            return [.. proposed.Where(link => link.Confidence >= threshold)];
        }

        return null;
    }

    /// <summary>Chapters are places by nature (« un lieu par chapitre »): a mention with a chapter's name takes its time code, and a chapter nobody mentioned becomes a mention of its own.</summary>
    internal static IReadOnlyList<PlaceMention> MergeChapters(IReadOnlyList<PlaceMention> mentions, IReadOnlyList<Chapter> chapters)
    {
        List<PlaceMention> merged = [];
        var usedChapters = new HashSet<int>();
        foreach (var mention in mentions)
        {
            var key = PlaceMatcher.Normalize(mention.Name);
            if (mention.StartSeconds is null && chapters.FirstOrDefault(chapter => PlaceMatcher.Normalize(chapter.Title) == key) is { } chapter)
            {
                usedChapters.Add(chapter.StartSeconds);
                merged.Add(mention with { StartSeconds = chapter.StartSeconds });
            }
            else
            {
                merged.Add(mention);
            }
        }

        foreach (var chapter in chapters.Where(chapter => !usedChapters.Contains(chapter.StartSeconds)))
        {
            merged.Add(new PlaceMention(chapter.Title, null, null, $"Chapitre {chapter.StartSeconds / 60:00}:{chapter.StartSeconds % 60:00} : {chapter.Title}", 0.5, chapter.StartSeconds));
        }

        return [.. merged.Where(mention => PlaceMatcher.Normalize(mention.Name).Length >= 3).DistinctBy(mention => (PlaceMatcher.Normalize(mention.Name), mention.StartSeconds)).Take(MaxMentions)];
    }

    private static string Signals(PlaceMatch match, PlaceCandidateScore best) =>
        JsonSerializer.Serialize(new { source = "assistant", signals = best.Signals.Where(signal => signal != "exact").Distinct(), evidence = Clip(match.Mention.Evidence, 200), mention = Clip(match.Mention.Name, 100), alternatives = match.Alternatives.Take(3).Select(alternative => alternative.Poi.PoiId) });

    private static string? Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length > max ? text.Trim()[..max].TrimEnd() : text.Trim();
}
