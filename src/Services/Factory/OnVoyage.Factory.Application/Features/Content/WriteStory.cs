using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using OnVoyage.Taxonomy;
using Wolverine;

namespace OnVoyage.Factory.Application.Features.Content;

public sealed record WriteStoryCommand(Guid PlaceId, string Lang, StoryKind Kind);

public sealed record WriteStorySummary(Guid StoryId, int Version, ContentStatus Status, double QualityScore, int Issues);

public static class WriteStoryHandler
{
    /// <summary>Retries of the whole job (§8.10) after which the story is marked failed and the admin is alerted.</summary>
    public const int MaxJobAttempts = 3;

    /// <summary>
    /// "Facts first, closed world" (§8.1): the writer receives validated facts and nothing else, then the story goes through the
    /// automatic checks of §8.6. A story that passes them all is <c>Checked</c>; anything else is <c>NeedsReview</c> with the problems listed.
    /// Nothing is ever published or voiced by this handler.
    /// </summary>
    public static async Task<Result<WriteStorySummary>> Handle(
        WriteStoryCommand command,
        Envelope? envelope,
        IPlaceStore places,
        IDestinationCatalog destinations,
        IContentStore content,
        IStoryWriter writer,
        IStoryVerifier verifier,
        IContentSettingsProvider settingsProvider,
        TimeProvider clock,
        ILogger<WriteStoryCommand> logger,
        CancellationToken cancellationToken)
    {
        var settings = settingsProvider.Current;
        var place = await places.FindAsync(command.PlaceId, cancellationToken);
        if (place is null || place.Status is PlaceStatus.Merged or PlaceStatus.Rejected)
        {
            return Result.Failure<WriteStorySummary>("place_not_found", "Place not found.");
        }

        var facts = (await content.ListFactsAsync(place.Id, cancellationToken)).Where(fact => fact.Status == FactStatus.Validated).ToList();
        if (facts.Count < settings.MinFacts)
        {
            return Result.Failure<WriteStorySummary>("not_enough_facts", $"At least {settings.MinFacts} validated facts are needed (found {facts.Count}).");
        }

        var destination = await destinations.FindAsync(place.DestinationSlug, cancellationToken);
        var documents = await content.ListDocumentsAsync(place.Id, cancellationToken);
        var interests = await places.GetInterestsAsync(place.Id, cancellationToken);
        var categories = interests.Where(item => !item.Code.Contains('.', StringComparison.Ordinal)).OrderByDescending(item => item.Weight).Take(3).Select(item => item.Code).ToArray();

        // A retried job reuses the draft its first attempt created instead of piling up versions.
        var existing = (await content.ListStoriesAsync(place.Id, cancellationToken))
            .FirstOrDefault(story => story.Lang == command.Lang && story.Kind == command.Kind && story.Status == ContentStatus.Draft);
        var now = clock.GetUtcNow();
        var story = existing ?? new StoryRecord(
            Guid.CreateVersion7(), place.Id, command.Lang, command.Kind, await content.NextVersionAsync(place.Id, command.Lang, command.Kind, cancellationToken),
            ContentStatus.Draft, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, [], 0, string.Empty, string.Empty,
            0d, CheckReport.Empty, settings.VoiceFor(command.Lang), 0.8d, null, now, now, null);
        if (existing is null)
        {
            await content.SaveStoryAsync(story, cancellationToken);
        }

        try
        {
            var target = LengthTarget.For(command.Kind);
            var writeFacts = facts.Select(fact => new WriteFact(fact.Id, fact.Statement)).ToList();
            var allowed = facts.Select(fact => fact.Id).ToHashSet();
            var issues = new List<CheckIssue>();

            var (draft, attempts) = await WriteWithRetriesAsync(
                writer, place.Enrichment?.LabelFr ?? place.Name, destination?.Name ?? place.DestinationSlug, command, categories, writeFacts, allowed, target, settings, story.Id, documents, issues, cancellationToken);

            var sentences = StyleCheck.Sentences(draft.Story);
            var verified = await verifier.VerifyAsync(new StoryVerifyRequest(command.Lang, sentences, writeFacts, story.Id), cancellationToken);
            foreach (var sentence in verified.Where(item => item.Verdict == SentenceVerdict.Unsupported))
            {
                issues.Add(new CheckIssue("verifier", "No validated fact supports this sentence.", sentence.Sentence));
            }

            var spoken = string.Join('\n', draft.Title, draft.Hook, draft.Story, draft.RemoteIntro, draft.AnnounceFront, draft.AnnounceLeft, draft.AnnounceRight, draft.CareNote ?? string.Empty);
            var styleIssues = StyleCheck.Check(draft.Story);
            issues.AddRange(styleIssues);
            issues.AddRange(SafetyCheck.Check(spoken));

            var overlap = VerbatimOverlapDetector.Worst(draft.Story, documents.Select(document => document.Text));
            var used = facts.Where(fact => draft.FactsUsed.Contains(fact.Id)).ToList();
            var documentQuality = documents.ToDictionary(document => document.Id, document => document.Quality);
            var sourceQuality = used.Count == 0 ? 0d : used.Average(fact => documentQuality.GetValueOrDefault(fact.DocumentId, 0.5));
            var genericShare = verified.Count == 0 ? 0d : verified.Count(item => item.Verdict == SentenceVerdict.Generic) / (double)verified.Count;
            var quality = QualityScore.Compute(
                sourceQuality,
                QualityScore.FactualConfidence([.. used.Select(fact => fact.Confidence)], genericShare),
                QualityScore.TextQuality(styleIssues.Count),
                1d,
                story.EditorialScore);

            var status = issues.Count == 0 ? ContentStatus.Checked : ContentStatus.NeedsReview;
            var written = story with
            {
                Status = status,
                Title = draft.Title,
                Hook = draft.Hook,
                Text = draft.Story,
                RemoteIntro = draft.RemoteIntro,
                AnnounceFront = draft.AnnounceFront,
                AnnounceLeft = draft.AnnounceLeft,
                AnnounceRight = draft.AnnounceRight,
                CareNote = draft.CareNote,
                FactsUsed = draft.FactsUsed,
                EstimatedDurationSeconds = draft.EstimatedDurationSeconds,
                PromptVersion = draft.PromptVersion,
                Model = draft.Model,
                QualityScore = quality,
                Report = new CheckReport(issues, verified, overlap, attempts, draft.Uncertainties),
                UpdatedAt = clock.GetUtcNow(),
            };
            await content.SaveStoryAsync(written, cancellationToken);

            logger.LogInformation("Story {Story} v{Version} for {Place}: {Status}, {Issues} issue(s), quality {Quality}.", written.Id, written.Version, place.Id, status, issues.Count, quality);
            return Result.Success(new WriteStorySummary(written.Id, written.Version, status, quality, issues.Count));
        }
        catch (ExternalServiceException) when (envelope is { Attempts: >= MaxJobAttempts })
        {
            // Last attempt: park the story as failed so the back office shows it, then let Wolverine dead-letter the job.
            await content.SaveStoryAsync(story with { Status = ContentStatus.Failed, UpdatedAt = clock.GetUtcNow() }, cancellationToken);
            throw;
        }
    }

    private static async Task<(StoryDraft Draft, int Attempts)> WriteWithRetriesAsync(
        IStoryWriter writer, string placeName, string destinationName, WriteStoryCommand command, IReadOnlyList<string> categories,
        IReadOnlyList<WriteFact> facts, HashSet<Guid> allowed, LengthTarget target, ContentSettings settings, Guid contentId,
        IReadOnlyList<SourceDocument> documents, List<CheckIssue> issues, CancellationToken cancellationToken)
    {
        string? feedback = null;
        StoryDraft? draft = null;
        var attempts = 0;
        var rewrittenForOverlap = false;

        while (attempts < settings.WriterAttempts + 1)
        {
            attempts++;
            draft = await writer.WriteAsync(new StoryWriteRequest(placeName, destinationName, command.Lang, command.Kind, categories, facts, target, contentId, feedback), cancellationToken);

            var problems = new List<CheckIssue>();
            var used = draft.FactsUsed.Distinct().ToList();
            if (used.Any(id => !allowed.Contains(id)))
            {
                problems.Add(new CheckIssue("coverage", "The story cites facts that were not provided."));
            }

            if (used.Count(allowed.Contains) < settings.MinFacts)
            {
                problems.Add(new CheckIssue("coverage", $"The story must use at least {settings.MinFacts} of the provided facts."));
            }

            if (LengthCheck.Check(draft.Story, command.Kind) is { } tooShortOrLong)
            {
                problems.Add(tooShortOrLong);
            }

            var overlap = VerbatimOverlapDetector.Worst(draft.Story, documents.Select(document => document.Text));
            if (!overlap.Passes && !rewrittenForOverlap)
            {
                rewrittenForOverlap = true;
                problems.Add(new CheckIssue("overlap", "Too close to a source text; tell the story in different words and structure."));
            }

            if (problems.Count == 0 || attempts > settings.WriterAttempts)
            {
                // Out of retries: what is still wrong is reported to the editor rather than hidden.
                issues.AddRange(problems.Where(problem => problem.Check != "overlap" || !overlap.Passes));
                if (!overlap.Passes && problems.All(problem => problem.Check != "overlap"))
                {
                    issues.Add(new CheckIssue("overlap", $"Shares {overlap.LongestSharedWords} consecutive words or {overlap.FiveGramJaccard:0.000} of its 5-grams with a source."));
                }

                break;
            }

            feedback = string.Join(" ", problems.Select(problem => problem.Detail));
        }

        return (draft!, attempts);
    }
}
