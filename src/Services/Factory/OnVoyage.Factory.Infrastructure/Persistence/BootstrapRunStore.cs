using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Features.Bootstrap;

namespace OnVoyage.Factory.Infrastructure.Persistence;

/// <summary>
/// Bootstrap runs last minutes to hours inside one message handler, so every write here is its own statement
/// (<c>ExecuteUpdate</c>) and never depends on the change tracker of the handler's unit of work.
/// </summary>
internal sealed class BootstrapRunStore(FactoryDbContext db) : IBootstrapRunStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task QueueAsync(Guid id, BootstrapDestinationCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = ToRow(id, command, now);
        db.BootstrapRuns.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        db.Entry(row).State = EntityState.Detached;
    }

    public async Task BeginAsync(Guid id, BootstrapDestinationCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await db.BootstrapRuns.AnyAsync(row => row.Id == id, cancellationToken))
        {
            await QueueAsync(id, command, now, cancellationToken);
        }

        await db.BootstrapRuns.Where(row => row.Id == id && row.Status == BootstrapRun.Queued)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, BootstrapRun.Running).SetProperty(row => row.StartedAt, now), cancellationToken);
    }

    public Task ProgressAsync(Guid id, BootstrapProgress progress, CancellationToken cancellationToken)
    {
        var steps = JsonSerializer.Serialize(progress.Steps, Json);
        return db.BootstrapRuns.Where(row => row.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.PlacesTotal, progress.PlacesTotal)
            .SetProperty(row => row.PlacesDone, progress.PlacesDone)
            .SetProperty(row => row.Written, progress.Written)
            .SetProperty(row => row.ToReview, progress.ToReview)
            .SetProperty(row => row.Published, progress.Published)
            .SetProperty(row => row.Failed, progress.Failed)
            .SetProperty(row => row.CostUsd, progress.CostUsd)
            .SetProperty(row => row.Steps, steps), cancellationToken);
    }

    public Task FinishAsync(Guid id, BootstrapReport? report, string? error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var status = report is null ? BootstrapRun.Errored : report.Outcome == BootstrapDestinationHandler.Completed ? BootstrapRun.Completed : BootstrapRun.Stopped;
        var steps = JsonSerializer.Serialize(report?.Steps ?? [], Json);
        var message = error is { Length: > 500 } ? error[..500] : error;
        return db.BootstrapRuns.Where(row => row.Id == id).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.Status, status)
            .SetProperty(row => row.FinishedAt, now)
            .SetProperty(row => row.Outcome, report == null ? null : report.Outcome)
            .SetProperty(row => row.Error, message)
            .SetProperty(row => row.PlacesTotal, report == null ? 0 : report.Places)
            .SetProperty(row => row.PlacesDone, report == null ? 0 : report.Places)
            .SetProperty(row => row.Written, report == null ? 0 : report.Written)
            .SetProperty(row => row.ToReview, report == null ? 0 : report.ToReview)
            .SetProperty(row => row.Published, report == null ? 0 : report.Published)
            .SetProperty(row => row.Failed, report == null ? 0 : report.Failed)
            .SetProperty(row => row.CostUsd, report == null ? 0d : report.CostUsd)
            .SetProperty(row => row.Steps, steps), cancellationToken);
    }

    public async Task<BootstrapRun?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.BootstrapRuns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return row is null ? null : ToRun(row);
    }

    public async Task<IReadOnlyList<BootstrapRun>> ListAsync(int limit, CancellationToken cancellationToken) =>
        [.. (await db.BootstrapRuns.AsNoTracking().OrderByDescending(row => row.RequestedAt).Take(limit).ToListAsync(cancellationToken)).Select(ToRun)];

    public async Task<bool> RequestCancelAsync(Guid id, CancellationToken cancellationToken) =>
        await db.BootstrapRuns.Where(row => row.Id == id && (row.Status == BootstrapRun.Queued || row.Status == BootstrapRun.Running))
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.CancelRequested, true), cancellationToken) > 0;

    public Task<bool> IsCancelRequestedAsync(Guid id, CancellationToken cancellationToken) =>
        db.BootstrapRuns.AsNoTracking().Where(row => row.Id == id).Select(row => row.CancelRequested).FirstOrDefaultAsync(cancellationToken);

    private static BootstrapRunRow ToRow(Guid id, BootstrapDestinationCommand command, DateTimeOffset now) => new()
    {
        Id = id,
        Destination = command.Destination,
        Status = BootstrapRun.Queued,
        RequestedBy = command.RequestedBy,
        RequestedAt = now,
        MaxPlaces = command.MaxPlaces,
        MinImportance = command.MinImportance,
        Lang = command.Lang,
        AutoPublish = command.AutoPublish,
        BudgetUsd = command.BudgetUsd,
    };

    private static BootstrapRun ToRun(BootstrapRunRow row) => new(
        row.Id, row.Destination, row.Status, row.RequestedBy, row.RequestedAt, row.StartedAt, row.FinishedAt, row.MaxPlaces, row.MinImportance, row.Lang, row.AutoPublish,
        row.BudgetUsd, row.CostUsd, row.PlacesTotal, row.PlacesDone, row.Written, row.ToReview, row.Published, row.Failed, row.Outcome, row.Error, row.CancelRequested,
        JsonSerializer.Deserialize<List<BootstrapStepReport>>(row.Steps, Json) ?? []);
}
