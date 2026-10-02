using Microsoft.Extensions.Logging;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Content;
using OnVoyage.Factory.Application.Ports;
using OnVoyage.Factory.Domain.Content;
using Wolverine;

namespace OnVoyage.Factory.Application.Features.Batches;

public enum JobState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    /// <summary>Stopped by an administrator, or because the batch budget was reached; can be put back in the queue by a retry.</summary>
    Cancelled,
}

/// <param name="BudgetUsd">Optional ceiling for the estimated model cost of the batch: once reached, the jobs that did not start are cancelled (<c>budget_exhausted</c>).</param>
public sealed record BatchCriteria(string Destination, int? MinImportance, IReadOnlyList<PlaceStatus> PlaceStatuses, string Lang, StoryKind Kind, int Limit, double? BudgetUsd = null);

public sealed record GenerationBatch(Guid Id, DateTimeOffset CreatedAt, string CreatedBy, BatchCriteria Criteria, int Total);

public sealed record GenerationJob(
    Guid Id, Guid BatchId, Guid PlaceId, string PlaceName, string Lang, StoryKind Kind, JobState State, string Step, int Attempts,
    string? LastError, Guid? StoryId, string? Outcome, DateTimeOffset UpdatedAt);

/// <summary>"87 done, 8 failed, 5 to review" (F-25). <c>Succeeded</c> includes the stories that wait for a person (<c>ToReview</c>).</summary>
public sealed record BatchProgress(GenerationBatch Batch, int Pending, int Running, int Succeeded, int ToReview, int Failed, int Cancelled = 0, double CostUsd = 0d)
{
    public bool IsFinished => Pending + Running == 0;

    /// <summary><c>running</c>, <c>completed</c>, <c>completed_with_failures</c> or <c>cancelled</c> (nothing left to run and some jobs were stopped).</summary>
    public string Status => !IsFinished ? "running" : Cancelled > 0 ? "cancelled" : Failed > 0 ? "completed_with_failures" : "completed";
}

/// <summary>A message the queue gave up on (after its attempts) as Wolverine recorded it, linked to the batch job when it was one.</summary>
public sealed record DeadLetterEntry(Guid Id, string MessageType, DateTimeOffset? At, string? ExceptionType, string? ExceptionMessage, Guid? JobId, Guid? BatchId, string? PlaceName);

public sealed record BatchDetail(BatchProgress Progress, IReadOnlyList<GenerationJob> Jobs);

public interface IBatchStore
{
    /// <summary>Stores the batch, its jobs and the messages that start them in one transaction (outbox): a batch is never half created.</summary>
    Task CreateAsync(GenerationBatch batch, IReadOnlyList<GenerationJob> jobs, IReadOnlyList<object> messages, CancellationToken cancellationToken);

    Task<GenerationBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken);

    Task<IReadOnlyList<BatchProgress>> ListAsync(int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<GenerationJob>> ListJobsAsync(Guid batchId, CancellationToken cancellationToken);

    Task<GenerationJob?> FindJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task SaveJobAsync(GenerationJob job, CancellationToken cancellationToken);

    /// <summary>Puts the failed and cancelled jobs back to pending and queues them again, atomically.</summary>
    Task<int> RetryFailedAsync(Guid batchId, CancellationToken cancellationToken);

    /// <summary>Same, for one job (failed or cancelled). False when the job is in another state.</summary>
    Task<bool> RetryJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Cancels the jobs that did not start; a running job finishes its current attempt. Returns how many were cancelled.</summary>
    Task<int> CancelPendingAsync(Guid batchId, string reason, CancellationToken cancellationToken);

    /// <summary>Estimated cost (<c>factory.llm_call</c>) of the model calls made while the batch was running.</summary>
    Task<double> CostAsync(GenerationBatch batch, DateTimeOffset? until, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken cancellationToken);
}

public sealed record CreateBatchCommand(BatchCriteria Criteria, string Actor);

public sealed record RetryFailedJobsCommand(Guid BatchId);

public sealed record CancelBatchCommand(Guid BatchId);

public sealed record RetryJobCommand(Guid JobId);

public sealed record ListDeadLettersQuery(int Limit);

public sealed record RunBatchJobCommand(Guid JobId);

public sealed record GetBatchQuery(Guid BatchId);

public sealed record ListBatchesQuery(int Limit);

/// <summary>A job used up its attempts. It is not retried again and goes to the dead-letter queue; the job row already says <c>Failed</c>.</summary>
public sealed class BatchJobExhaustedException(string message, Exception inner) : Exception(message, inner);

public static class CreateBatchHandler
{
    public const int MaxJobs = 200;

    /// <summary>
    /// Picks the places (most important first) that do not already have a live story of that language and kind, then queues one job per
    /// place. Cost is bounded: at most <see cref="MaxJobs"/> places per batch.
    /// </summary>
    public static async Task<Result<GenerationBatch>> Handle(
        CreateBatchCommand command, IPlaceStore places, IContentStore content, IDestinationCatalog destinations, IBatchStore batches, TimeProvider clock, CancellationToken cancellationToken)
    {
        var criteria = command.Criteria;
        if (criteria.Lang is not ("fr" or "en") || criteria.Limit is < 1 or > MaxJobs || criteria.MinImportance is < 0 or > 100 || criteria.PlaceStatuses.Count == 0)
        {
            return Result.Failure<GenerationBatch>("validation", $"Choose fr or en, 1 to {MaxJobs} places and at least one place status.");
        }

        if (criteria.BudgetUsd is <= 0 or > 10_000)
        {
            return Result.Failure<GenerationBatch>("validation", "The budget must be above zero.");
        }

        if (criteria.PlaceStatuses.Any(status => status is PlaceStatus.Merged or PlaceStatus.Rejected or PlaceStatus.Unpublished or PlaceStatus.NeedsReview))
        {
            return Result.Failure<GenerationBatch>("validation", "Only candidate and published places can be written.");
        }

        if (await destinations.FindAsync(criteria.Destination, cancellationToken) is null)
        {
            return Result.Failure<GenerationBatch>("destination_not_found", "Unknown destination.");
        }

        var candidates = new List<PlaceRecord>();
        foreach (var status in criteria.PlaceStatuses.Distinct())
        {
            candidates.AddRange(await places.ListAsync(criteria.Destination, status, 5000, cancellationToken));
        }

        var live = new[] { ContentStatus.Draft, ContentStatus.AiGenerated, ContentStatus.Checked, ContentStatus.NeedsReview, ContentStatus.Approved, ContentStatus.AudioReady, ContentStatus.Published };
        var chosen = new List<PlaceRecord>();
        foreach (var place in candidates
            .Where(place => criteria.MinImportance is null || (place.ImportanceOverride ?? place.ImportanceScore ?? 0) >= criteria.MinImportance)
            .OrderByDescending(place => place.ImportanceOverride ?? place.ImportanceScore ?? 0).ThenBy(place => place.Name, StringComparer.Ordinal))
        {
            if (chosen.Count == criteria.Limit)
            {
                break;
            }

            var stories = await content.ListStoriesAsync(place.Id, cancellationToken);
            if (!stories.Any(story => story.Lang == criteria.Lang && story.Kind == criteria.Kind && live.Contains(story.Status)))
            {
                chosen.Add(place);
            }
        }

        if (chosen.Count == 0)
        {
            return Result.Failure<GenerationBatch>("no_place", "No place matches: they all have a story already, or none has been scored.");
        }

        var now = clock.GetUtcNow();
        var batch = new GenerationBatch(Guid.CreateVersion7(), now, command.Actor, criteria, chosen.Count);
        var jobs = chosen.Select(place => new GenerationJob(Guid.CreateVersion7(), batch.Id, place.Id, place.Name, criteria.Lang, criteria.Kind, JobState.Pending, "queued", 0, null, null, null, now)).ToList();
        await batches.CreateAsync(batch, jobs, [.. jobs.Select(job => new RunBatchJobCommand(job.Id))], cancellationToken);
        return Result.Success(batch);
    }
}

public static class BatchJobHandler
{
    /// <summary>
    /// One place, all the way to a checked draft: sources, facts, story. Each step is idempotent, so a retry resumes where it stopped.
    /// A provider outage is retried by the queue (10 s, 60 s…); a decision a person must take (not enough facts) fails the job at once;
    /// after <see cref="WriteStoryHandler.MaxJobAttempts"/> attempts the job is <c>Failed</c> and dead-lettered. Nothing is voiced or published.
    /// </summary>
    public static async Task<Result<GenerationJob>> Handle(
        RunBatchJobCommand command,
        Envelope? envelope,
        IBatchStore batches,
        IPlaceStore places,
        IDestinationCatalog destinations,
        IContentStore content,
        IWikipediaTextClient wikipedia,
        IFactExtractor extractor,
        IStoryWriter writer,
        IStoryVerifier verifier,
        IContentSettingsProvider settings,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var job = await batches.FindJobAsync(command.JobId, cancellationToken);
        if (job is null)
        {
            return Result.Failure<GenerationJob>("job_not_found", "Job not found.");
        }

        if (job.State is JobState.Succeeded or JobState.Cancelled)
        {
            return Result.Success(job); // duplicate delivery, or stopped before it started
        }

        var batch = await batches.FindBatchAsync(job.BatchId, cancellationToken);
        if (batch?.Criteria.BudgetUsd is { } budget && await batches.CostAsync(batch, null, cancellationToken) >= budget)
        {
            var stopped = job with { State = JobState.Cancelled, Step = "cancelled", LastError = "budget_exhausted", UpdatedAt = clock.GetUtcNow() };
            await batches.SaveJobAsync(stopped, cancellationToken);
            return Result.Success(stopped);
        }

        var attempt = Math.Max(envelope?.Attempts ?? 1, 1);
        job = job with { State = JobState.Running, Attempts = attempt, UpdatedAt = clock.GetUtcNow() };
        await batches.SaveJobAsync(job, cancellationToken);

        try
        {
            job = job with { Step = "sources" };
            await batches.SaveJobAsync(job, cancellationToken);
            var (fetched, _) = await FetchSourcesHandler.Handle(new FetchSourcesCommand(job.PlaceId), places, wikipedia, content, settings, clock, cancellationToken);
            if (!fetched.IsSuccess)
            {
                return await FailAsync(batches, job, fetched.Error!.Message, clock, cancellationToken);
            }

            job = job with { Step = "facts" };
            await batches.SaveJobAsync(job, cancellationToken);
            var extracted = await ExtractFactsHandler.Handle(new ExtractFactsCommand(job.PlaceId), places, content, extractor, loggers.CreateLogger<ExtractFactsCommand>(), cancellationToken);
            if (!extracted.IsSuccess)
            {
                return await FailAsync(batches, job, extracted.Error!.Message, clock, cancellationToken);
            }

            job = job with { Step = "story" };
            await batches.SaveJobAsync(job, cancellationToken);
            var written = await WriteStoryHandler.Handle(
                new WriteStoryCommand(job.PlaceId, job.Lang, job.Kind), envelope, places, destinations, content, writer, verifier, settings, clock, loggers.CreateLogger<WriteStoryCommand>(), cancellationToken);
            if (!written.IsSuccess)
            {
                return await FailAsync(batches, job, written.Error!.Message, clock, cancellationToken);
            }

            job = job with
            {
                State = JobState.Succeeded,
                Step = "done",
                LastError = null,
                StoryId = written.Value!.StoryId,
                Outcome = written.Value.Status == ContentStatus.Checked ? "Checked" : "NeedsReview",
                UpdatedAt = clock.GetUtcNow(),
            };
            await batches.SaveJobAsync(job, cancellationToken);
            return Result.Success(job);
        }
        catch (ExternalServiceException exception)
        {
            if (attempt >= WriteStoryHandler.MaxJobAttempts)
            {
                await batches.SaveJobAsync(job with { State = JobState.Failed, LastError = Truncate(exception.Message), UpdatedAt = clock.GetUtcNow() }, cancellationToken);
                throw new BatchJobExhaustedException($"Job {job.Id} failed {attempt} times.", exception);
            }

            await batches.SaveJobAsync(job with { State = JobState.Pending, LastError = Truncate(exception.Message), UpdatedAt = clock.GetUtcNow() }, cancellationToken);
            throw;
        }
    }

    private static async Task<Result<GenerationJob>> FailAsync(IBatchStore batches, GenerationJob job, string error, TimeProvider clock, CancellationToken cancellationToken)
    {
        var failed = job with { State = JobState.Failed, LastError = Truncate(error), UpdatedAt = clock.GetUtcNow() };
        await batches.SaveJobAsync(failed, cancellationToken);
        return Result.Success(failed);
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}

public static class BatchAdminHandler
{
    public static async Task<Result<int>> Handle(RetryFailedJobsCommand command, IBatchStore batches, CancellationToken cancellationToken)
    {
        if (await batches.FindBatchAsync(command.BatchId, cancellationToken) is null)
        {
            return Result.Failure<int>("batch_not_found", "Batch not found.");
        }

        var count = await batches.RetryFailedAsync(command.BatchId, cancellationToken);
        return count == 0 ? Result.Failure<int>("nothing_to_retry", "No failed job to retry.") : Result.Success(count);
    }

    public static async Task<Result<int>> Handle(CancelBatchCommand command, IBatchStore batches, CancellationToken cancellationToken)
    {
        if (await batches.FindBatchAsync(command.BatchId, cancellationToken) is null)
        {
            return Result.Failure<int>("batch_not_found", "Batch not found.");
        }

        var count = await batches.CancelPendingAsync(command.BatchId, "cancelled_by_admin", cancellationToken);
        return count == 0 ? Result.Failure<int>("nothing_to_cancel", "No job is waiting: the batch is already running its last jobs or finished.") : Result.Success(count);
    }

    public static async Task<Result<bool>> Handle(RetryJobCommand command, IBatchStore batches, CancellationToken cancellationToken)
    {
        if (await batches.FindJobAsync(command.JobId, cancellationToken) is null)
        {
            return Result.Failure<bool>("job_not_found", "Job not found.");
        }

        return await batches.RetryJobAsync(command.JobId, cancellationToken) ? Result.Success(true) : Result.Failure<bool>("not_retryable", "Only a failed or cancelled job can be retried.");
    }

    public static async Task<Result<IReadOnlyList<DeadLetterEntry>>> Handle(ListDeadLettersQuery query, IBatchStore batches, CancellationToken cancellationToken) =>
        Result.Success(await batches.ListDeadLettersAsync(Math.Clamp(query.Limit, 1, 200), cancellationToken));

    public static async Task<Result<BatchDetail>> Handle(GetBatchQuery query, IBatchStore batches, CancellationToken cancellationToken)
    {
        var batch = await batches.FindBatchAsync(query.BatchId, cancellationToken);
        if (batch is null)
        {
            return Result.Failure<BatchDetail>("batch_not_found", "Batch not found.");
        }

        var jobs = await batches.ListJobsAsync(batch.Id, cancellationToken);
        var until = jobs.Count > 0 && jobs.All(job => job.State is not (JobState.Pending or JobState.Running)) ? jobs.Max(job => job.UpdatedAt) : (DateTimeOffset?)null;
        return Result.Success(new BatchDetail(Progress(batch, jobs, await batches.CostAsync(batch, until, cancellationToken)), jobs));
    }

    public static async Task<Result<IReadOnlyList<BatchProgress>>> Handle(ListBatchesQuery query, IBatchStore batches, CancellationToken cancellationToken) =>
        Result.Success(await batches.ListAsync(Math.Clamp(query.Limit, 1, 100), cancellationToken));

    public static BatchProgress Progress(GenerationBatch batch, IReadOnlyList<GenerationJob> jobs, double costUsd = 0d) => new(
        batch,
        jobs.Count(job => job.State == JobState.Pending),
        jobs.Count(job => job.State == JobState.Running),
        jobs.Count(job => job.State == JobState.Succeeded),
        jobs.Count(job => job.State == JobState.Succeeded && job.Outcome == "NeedsReview"),
        jobs.Count(job => job.State == JobState.Failed),
        jobs.Count(job => job.State == JobState.Cancelled),
        costUsd);
}
