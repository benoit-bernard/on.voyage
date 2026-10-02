using OnVoyage.Factory.Application.Ports;

namespace OnVoyage.Factory.Application.Features.Bootstrap;

/// <summary>What the back-office shows of a bootstrap run: queued, running (live counters), then its final state.</summary>
public sealed record BootstrapRun(
    Guid Id,
    string Destination,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int MaxPlaces,
    int? MinImportance,
    string Lang,
    bool AutoPublish,
    double BudgetUsd,
    double CostUsd,
    int PlacesTotal,
    int PlacesDone,
    int Written,
    int ToReview,
    int Published,
    int Failed,
    string? Outcome,
    string? Error,
    bool CancelRequested,
    IReadOnlyList<BootstrapStepReport> Steps)
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Stopped = "Stopped";
    public const string Errored = "Failed";

    public bool IsFinished => Status is Completed or Stopped or Errored;
}

public sealed record BootstrapProgress(int PlacesTotal, int PlacesDone, int Written, int ToReview, int Published, int Failed, double CostUsd, IReadOnlyList<BootstrapStepReport> Steps);

public interface IBootstrapRunStore
{
    /// <summary>Creates the run, or moves a queued one to running. Idempotent for a redelivered command.</summary>
    Task BeginAsync(Guid id, BootstrapDestinationCommand command, DateTimeOffset now, CancellationToken cancellationToken);

    Task QueueAsync(Guid id, BootstrapDestinationCommand command, DateTimeOffset now, CancellationToken cancellationToken);

    Task ProgressAsync(Guid id, BootstrapProgress progress, CancellationToken cancellationToken);

    Task FinishAsync(Guid id, BootstrapReport? report, string? error, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BootstrapRun?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<BootstrapRun>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Asks a queued or running run to stop before its next place. False when it is finished or unknown.</summary>
    Task<bool> RequestCancelAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> IsCancelRequestedAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Records the request and returns the command for the worker, so the back-office can follow it from the first second.</summary>
public sealed record StartBootstrapCommand(BootstrapDestinationCommand Command);

public sealed record ListBootstrapRunsQuery(int Limit);

public sealed record GetBootstrapRunQuery(Guid Id);

public sealed record CancelBootstrapRunCommand(Guid Id);

public static class BootstrapRunHandler
{
    /// <summary>Validates, records the run as queued and returns the command to send (with its run id). Nothing runs on the API process.</summary>
    public static async Task<Result<BootstrapDestinationCommand>> Handle(StartBootstrapCommand request, IDestinationCatalog destinations, IUsageReader usage, IBootstrapRunStore runs, TimeProvider clock, CancellationToken cancellationToken)
    {
        var command = request.Command;
        if (command.MaxPlaces is < 1 or > 500 || command.BudgetUsd <= 0 || command.Lang is not ("fr" or "en") || command.MinImportance is < 0 or > 100)
        {
            return Result.Failure<BootstrapDestinationCommand>("validation", "Choose 1 to 500 places, an importance from 0 to 100, a budget above zero and fr or en.");
        }

        if (await destinations.FindAsync(command.Destination, cancellationToken) is null)
        {
            return Result.Failure<BootstrapDestinationCommand>("destination_not_found", "Unknown destination.");
        }

        if (usage.PriceProblem() is { } problem && !command.AllowUnpriced)
        {
            return Result.Failure<BootstrapDestinationCommand>("prices_missing", problem);
        }

        var queued = command with { RunId = Guid.CreateVersion7() };
        await runs.QueueAsync(queued.RunId!.Value, queued, clock.GetUtcNow(), cancellationToken);
        return Result.Success(queued);
    }

    public static async Task<Result<IReadOnlyList<BootstrapRun>>> Handle(ListBootstrapRunsQuery query, IBootstrapRunStore runs, CancellationToken cancellationToken) =>
        Result.Success(await runs.ListAsync(Math.Clamp(query.Limit, 1, 100), cancellationToken));

    public static async Task<Result<BootstrapRun>> Handle(GetBootstrapRunQuery query, IBootstrapRunStore runs, CancellationToken cancellationToken)
    {
        var run = await runs.FindAsync(query.Id, cancellationToken);
        return run is null ? Result.Failure<BootstrapRun>("bootstrap_run_not_found", "Bootstrap run not found.") : Result.Success(run);
    }

    public static async Task<Result<bool>> Handle(CancelBootstrapRunCommand command, IBootstrapRunStore runs, CancellationToken cancellationToken)
    {
        if (await runs.FindAsync(command.Id, cancellationToken) is null)
        {
            return Result.Failure<bool>("bootstrap_run_not_found", "Bootstrap run not found.");
        }

        return await runs.RequestCancelAsync(command.Id, cancellationToken)
            ? Result.Success(true)
            : Result.Failure<bool>("already_finished", "The run is already finished.");
    }
}
