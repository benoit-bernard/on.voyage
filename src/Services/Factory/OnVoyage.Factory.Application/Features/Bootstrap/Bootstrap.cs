using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Features.EnrichPlaces;
using OnVoyage.Factory.Application.Features.ImportPlaces;
using OnVoyage.Factory.Application.Features.Places;
using OnVoyage.Factory.Application.Features.ScorePlaces;
using OnVoyage.Factory.Application.Features.Videos;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Classification;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Application.Features.Bootstrap;

/// <summary>What a cost cap is checked against: the estimated cost of the model and voice calls recorded in <c>factory.llm_call</c>.</summary>
public interface IUsageReader
{
    Task<double> CostSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>A message when the configured provider is paid but no price is configured (every cost would read zero and a cap would never trigger); otherwise <c>null</c>.</summary>
    string? PriceProblem();
}

/// <summary>
/// Opens a destination end to end with the existing pipeline: OSM import, Wikidata enrichment, scoring, then for the most important places
/// sources, facts, story and, with <see cref="AutoPublish"/>, approval, voice and publication. Resumable (every step skips what is done),
/// idempotent, paced and stopped by <see cref="BudgetUsd"/>.
/// </summary>
public sealed record BootstrapDestinationCommand(
    string Destination,
    int MaxPlaces = 40,
    int? MinImportance = null,
    string Lang = "fr",
    double BudgetUsd = 5d,
    bool AutoPublish = false,
    bool ForceImport = false,
    bool SkipImport = false,
    bool AllowUnpriced = false,
    int PauseMilliseconds = 500,
    IReadOnlyList<double>? RetryDelaysSeconds = null);

public sealed record BootstrapStepReport(string Step, string Outcome, string? Detail = null);

public sealed record BootstrapReport(
    string Destination, string Outcome, double CostUsd, double BudgetUsd, int Places, int Written, int ToReview, int Published, int Failed, IReadOnlyList<BootstrapStepReport> Steps);

public static class BootstrapDestinationHandler
{
    public const string Completed = "completed";
    public const string BudgetExhausted = "budget_exhausted";
    public const string ProviderUnavailable = "provider_unavailable";

    private const int MaxConsecutiveProviderFailures = 3;

    private static readonly ContentStatus[] Live =
        [ContentStatus.Draft, ContentStatus.AiGenerated, ContentStatus.Checked, ContentStatus.NeedsReview, ContentStatus.Approved, ContentStatus.AudioReady, ContentStatus.Published];

    public static async Task<Result<BootstrapReport>> Handle(
        BootstrapDestinationCommand command,
        IDestinationCatalog destinations,
        IPlaceStore places,
        IOsmImporter osm,
        IWikidataClient wikidata,
        IPageviewsClient pageviews,
        IClassificationRuleProvider rules,
        IPlaceModelClassifier classifier,
        HeritageClasses heritage,
        ITextToSpeechProvider speech,
        IAudioProcessor processor,
        IMediaStorage storage,
        IContentSettingsProvider settings,
        IUsageReader usage,
        IServiceScopeFactory scopes,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("OnVoyage.Factory.Bootstrap");
        if (command.MaxPlaces is < 1 or > 500 || command.BudgetUsd <= 0 || command.Lang is not ("fr" or "en"))
        {
            return Result.Failure<BootstrapReport>("validation", "Choose 1 to 500 places, a budget above zero and fr or en.");
        }

        if (await destinations.FindAsync(command.Destination, cancellationToken) is null)
        {
            return Result.Failure<BootstrapReport>("destination_not_found", "Unknown destination.");
        }

        if (usage.PriceProblem() is { } problem && !command.AllowUnpriced)
        {
            return Result.Failure<BootstrapReport>("prices_missing", problem);
        }

        var started = clock.GetUtcNow();
        var steps = new List<BootstrapStepReport>();
        var delays = command.RetryDelaysSeconds is { Count: > 0 } configured ? configured : [10d, 60d, 300d];

        // 1. Places: reuse what the destination already holds, otherwise import, enrich and score.
        var active = await places.ListActiveAsync(command.Destination, cancellationToken);
        if (command.SkipImport || (active.Count > 0 && !command.ForceImport))
        {
            steps.Add(new BootstrapStepReport("places", "reused", $"{active.Count} places already known."));
        }
        else
        {
            var imported = await RetryAsync(() => ImportPlacesHandler.Handle(new ImportPlacesCommand(command.Destination), destinations, osm, places, cancellationToken), delays, logger, cancellationToken);
            if (!imported.Result.IsSuccess)
            {
                return Result.Failure<BootstrapReport>(imported.Result.Error!.Code, imported.Result.Error.Message);
            }

            steps.Add(new BootstrapStepReport("osm_import", "done", $"{imported.Result.Value!.RawRows} OSM objects, {imported.Result.Value.Created} new places."));
            var enriched = await RetryAsync(() => EnrichPlacesHandler.Handle(new EnrichPlacesCommand(command.Destination), places, wikidata, pageviews, loggers.CreateLogger<EnrichPlacesCommand>(), cancellationToken), delays, logger, cancellationToken);
            steps.Add(new BootstrapStepReport("enrichment", enriched.Result.IsSuccess ? "done" : "failed", enriched.Result.IsSuccess ? $"{enriched.Result.Value!.Found} Wikidata items." : enriched.Result.Error!.Message));
            var scored = await ScorePlacesHandler.Handle(new ScorePlacesCommand(command.Destination), places, rules, classifier, heritage, clock, loggers.CreateLogger<ScorePlacesCommand>(), cancellationToken);
            if (!scored.IsSuccess)
            {
                return Result.Failure<BootstrapReport>(scored.Error!.Code, scored.Error.Message);
            }

            steps.Add(new BootstrapStepReport("scoring", "done", $"{scored.Value!.Places} places, {scored.Value.AutoMerged} merged, {scored.Value.NeedsReview} to review."));
            active = await places.ListActiveAsync(command.Destination, cancellationToken);
        }

        // 2. The most important places that are open to the public pipeline (candidates, or already published).
        var targets = active
            .Where(place => place.Status is PlaceStatus.Candidate or PlaceStatus.Published)
            .Where(place => command.MinImportance is null || (place.ImportanceOverride ?? place.ImportanceScore ?? 0) >= command.MinImportance)
            .OrderByDescending(place => place.ImportanceOverride ?? place.ImportanceScore ?? 0)
            .ThenBy(place => place.Name, StringComparer.Ordinal)
            .Take(command.MaxPlaces)
            .ToList();
        steps.Add(new BootstrapStepReport("selection", "done", $"{targets.Count} places (most important first)."));

        // 3. Stories, then (on request) approval, voice and publication.
        int written = 0, toReview = 0, published = 0, failed = 0, consecutiveOutages = 0;
        var outcome = Completed;
        foreach (var place in targets)
        {
            if (await usage.CostSinceAsync(started, cancellationToken) >= command.BudgetUsd)
            {
                outcome = BudgetExhausted;
                break;
            }

            try
            {
                // One scope per place: its own unit of work and outbox, so a failure or a long run never leaves the next place on a broken context.
                await using var scope = scopes.CreateAsyncScope();
                var services = scope.ServiceProvider;
                var result = await ProcessPlaceAsync(
                    command, place, delays, destinations, services.GetRequiredService<IPlaceStore>(), services.GetRequiredService<IContentStore>(), services.GetRequiredService<IWikipediaTextClient>(),
                    services.GetRequiredService<IFactExtractor>(), services.GetRequiredService<IStoryWriter>(), services.GetRequiredService<IStoryVerifier>(), speech, processor, storage,
                    services.GetRequiredService<IVideoStore>(), settings, usage, started, clock, loggers, logger, cancellationToken);
                written += result.Written ? 1 : 0;
                toReview += result.NeedsReview ? 1 : 0;
                published += result.Published ? 1 : 0;
                failed += result.Failure is null ? 0 : 1;
                consecutiveOutages = 0;
                if (result.Failure is not null)
                {
                    steps.Add(new BootstrapStepReport($"place:{place.Slug}", "skipped", result.Failure));
                }
            }
            catch (ExternalServiceException exception)
            {
                failed++;
                consecutiveOutages++;
                steps.Add(new BootstrapStepReport($"place:{place.Slug}", "failed", exception.Message));
                if (consecutiveOutages >= MaxConsecutiveProviderFailures)
                {
                    outcome = ProviderUnavailable;
                    break;
                }
            }

            if (command.PauseMilliseconds > 0)
            {
                await Task.Delay(command.PauseMilliseconds, cancellationToken);
            }
        }

        var cost = await usage.CostSinceAsync(started, cancellationToken);
        logger.LogInformation(
            "Bootstrap {Destination}: {Outcome}, {Places} places, {Written} stories written ({Review} to review), {Published} published, {Failed} problems, cost {Cost:0.0000} of {Budget} USD.",
            command.Destination, outcome, targets.Count, written, toReview, published, failed, cost, command.BudgetUsd);
        return Result.Success(new BootstrapReport(command.Destination, outcome, cost, command.BudgetUsd, targets.Count, written, toReview, published, failed, steps));
    }

    private sealed record PlaceResult(bool Written, bool NeedsReview, bool Published, string? Failure);

    private static async Task<PlaceResult> ProcessPlaceAsync(
        BootstrapDestinationCommand command,
        PlaceRecord place,
        IReadOnlyList<double> delays,
        IDestinationCatalog destinations,
        IPlaceStore places,
        IContentStore content,
        IWikipediaTextClient wikipedia,
        IFactExtractor extractor,
        IStoryWriter writer,
        IStoryVerifier verifier,
        ITextToSpeechProvider speech,
        IAudioProcessor processor,
        IMediaStorage storage,
        IVideoStore videos,
        IContentSettingsProvider settings,
        IUsageReader usage,
        DateTimeOffset started,
        TimeProvider clock,
        ILoggerFactory loggers,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var story = (await content.ListStoriesAsync(place.Id, cancellationToken))
            .Where(item => item.Lang == command.Lang && item.Kind == StoryKind.Standard && Live.Contains(item.Status))
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        var written = false;
        if (story is null)
        {
            var fetched = await RetryAsync(() => FetchSourcesHandler.Handle(new FetchSourcesCommand(place.Id), places, wikipedia, content, settings, clock, cancellationToken), delays, logger, cancellationToken);
            if (!fetched.Result.IsSuccess)
            {
                return new PlaceResult(false, false, false, fetched.Result.Error!.Message);
            }

            var facts = await RetryAsync(() => ExtractFactsHandler.Handle(new ExtractFactsCommand(place.Id), places, content, extractor, loggers.CreateLogger<ExtractFactsCommand>(), cancellationToken), delays, logger, cancellationToken);
            if (!facts.IsSuccess)
            {
                return new PlaceResult(false, false, false, facts.Error!.Message);
            }

            if (await usage.CostSinceAsync(started, cancellationToken) >= command.BudgetUsd)
            {
                return new PlaceResult(false, false, false, "budget reached before writing");
            }

            var draft = await RetryAsync(
                () => WriteStoryHandler.Handle(new WriteStoryCommand(place.Id, command.Lang, StoryKind.Standard), null, places, destinations, content, writer, verifier, settings, clock, loggers.CreateLogger<WriteStoryCommand>(), cancellationToken),
                delays, logger, cancellationToken);
            if (!draft.IsSuccess)
            {
                return new PlaceResult(false, false, false, draft.Error!.Message); // typically not enough facts: a person decides
            }

            story = await content.FindStoryAsync(draft.Value!.StoryId, cancellationToken);
            written = true;
        }

        if (story is null)
        {
            return new PlaceResult(written, false, false, "story vanished");
        }

        var needsReview = story.Status == ContentStatus.NeedsReview;
        if (!command.AutoPublish || story.Status is not (ContentStatus.Checked or ContentStatus.Approved or ContentStatus.AudioReady or ContentStatus.Published))
        {
            return new PlaceResult(written, needsReview, story.Status == ContentStatus.Published, null);
        }

        if (story.Status == ContentStatus.Published)
        {
            return new PlaceResult(written, false, true, null);
        }

        // Only a story that passed every automatic check is approved here; whoever runs the bootstrap with --auto-publish accepts that the
        // text is an AI draft (flagged aiGenerated in the catalog) and reviews it afterwards.
        if (place.Status != PlaceStatus.Published)
        {
            var publishedPlace = await PublishPlaceHandler.Handle(new PublishPlaceCommand(place.Id), places, destinations, videos, clock, cancellationToken);
            if (!publishedPlace.IsSuccess)
            {
                return new PlaceResult(written, false, false, publishedPlace.Error!.Message);
            }
        }

        if (story.Status == ContentStatus.Checked)
        {
            var approved = await StoryEditorialHandler.Handle(new ApproveStoryCommand(story.Id, null), content, clock, cancellationToken);
            if (!approved.IsSuccess)
            {
                return new PlaceResult(written, false, false, approved.Error!.Message);
            }

            story = approved.Value!;
        }

        if (story.Status == ContentStatus.Approved && speech.IsAvailable)
        {
            if (await usage.CostSinceAsync(started, cancellationToken) >= command.BudgetUsd)
            {
                return new PlaceResult(written, false, false, "budget reached before the voice");
            }

            var audio = await RetryAsync(
                () => GenerateAudioHandler.Handle(new GenerateAudioCommand(story.Id), null, content, places, speech, processor, storage, settings, clock, cancellationToken),
                delays, logger, cancellationToken);
            if (!audio.IsSuccess)
            {
                return new PlaceResult(written, false, false, audio.Error!.Message);
            }
        }

        var publication = await StoryPublicationHandler.Handle(new PublishStoryCommand(story.Id, AllowTextOnly: !speech.IsAvailable), content, places, clock, cancellationToken);
        return publication.IsSuccess
            ? new PlaceResult(written, false, true, null)
            : new PlaceResult(written, false, false, publication.Error!.Message);
    }

    /// <summary>Provider outages are retried in place with growing delays (the same ones the queue uses); the last failure is thrown.</summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, IReadOnlyList<double> delays, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ExternalServiceException exception) when (attempt < delays.Count)
            {
                logger.LogWarning("Provider failure ({Message}); retry {Attempt} in {Delay} s.", exception.Message, attempt + 1, delays[attempt]);
                await Task.Delay(TimeSpan.FromSeconds(delays[attempt]), cancellationToken);
            }
        }
    }
}
