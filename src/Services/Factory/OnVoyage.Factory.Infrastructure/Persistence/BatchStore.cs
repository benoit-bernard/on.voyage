using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Features.Batches;
using OnVoyage.Factory.Domain.Content;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class BatchStore(IDbContextOutbox<FactoryDbContext> outbox, TimeProvider clock) : IBatchStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private FactoryDbContext Db => outbox.DbContext;

    public async Task CreateAsync(GenerationBatch batch, IReadOnlyList<GenerationJob> jobs, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        Db.GenerationBatches.Add(new GenerationBatchRow { Id = batch.Id, CreatedAt = batch.CreatedAt, CreatedBy = batch.CreatedBy, Criteria = JsonSerializer.Serialize(batch.Criteria, Json), Total = batch.Total });
        Db.GenerationJobs.AddRange(jobs.Select(ToRow));
        foreach (var message in messages)
        {
            await outbox.PublishAsync(message);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task<GenerationBatch?> FindBatchAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var row = await Db.GenerationBatches.AsNoTracking().FirstOrDefaultAsync(batch => batch.Id == batchId, cancellationToken);
        return row is null ? null : ToBatch(row);
    }

    public async Task<IReadOnlyList<BatchProgress>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var batches = await Db.GenerationBatches.AsNoTracking().OrderByDescending(batch => batch.CreatedAt).Take(limit).ToListAsync(cancellationToken);
        var ids = batches.Select(batch => batch.Id).ToArray();
        var jobs = (await Db.GenerationJobs.AsNoTracking().Where(job => ids.Contains(job.BatchId)).ToListAsync(cancellationToken)).ToLookup(job => job.BatchId);
        return [.. batches.Select(batch => BatchAdminHandler.Progress(ToBatch(batch), [.. jobs[batch.Id].Select(ToJob)]))];
    }

    public async Task<IReadOnlyList<GenerationJob>> ListJobsAsync(Guid batchId, CancellationToken cancellationToken) =>
        [.. (await Db.GenerationJobs.AsNoTracking().Where(job => job.BatchId == batchId).OrderBy(job => job.PlaceName).ToListAsync(cancellationToken)).Select(ToJob)];

    public async Task<GenerationJob?> FindJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var row = await Db.GenerationJobs.AsNoTracking().FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);
        return row is null ? null : ToJob(row);
    }

    public async Task SaveJobAsync(GenerationJob job, CancellationToken cancellationToken)
    {
        var row = await Db.GenerationJobs.FirstAsync(item => item.Id == job.Id, cancellationToken);
        row.State = job.State.ToString();
        row.Step = job.Step;
        row.Attempts = job.Attempts;
        row.LastError = job.LastError;
        row.StoryId = job.StoryId;
        row.Outcome = job.Outcome;
        row.UpdatedAt = job.UpdatedAt;
        await Db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RetryFailedAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var failed = nameof(JobState.Failed);
        var rows = await Db.GenerationJobs.Where(job => job.BatchId == batchId && job.State == failed).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            row.State = nameof(JobState.Pending);
            row.Step = "queued";
            row.Attempts = 0;
            row.UpdatedAt = now;
            await outbox.PublishAsync(new RunBatchJobCommand(row.Id));
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
        return rows.Count;
    }

    private static GenerationBatch ToBatch(GenerationBatchRow row) =>
        new(row.Id, row.CreatedAt, row.CreatedBy, JsonSerializer.Deserialize<BatchCriteria>(row.Criteria, Json)!, row.Total);

    private static GenerationJob ToJob(GenerationJobRow row) => new(
        row.Id, row.BatchId, row.PlaceId, row.PlaceName, row.Lang, Enum.Parse<StoryKind>(row.Kind), Enum.Parse<JobState>(row.State), row.Step, row.Attempts, row.LastError, row.StoryId, row.Outcome, row.UpdatedAt);

    private static GenerationJobRow ToRow(GenerationJob job) => new()
    {
        Id = job.Id, BatchId = job.BatchId, PlaceId = job.PlaceId, PlaceName = job.PlaceName, Lang = job.Lang, Kind = job.Kind.ToString(), State = job.State.ToString(),
        Step = job.Step, Attempts = job.Attempts, LastError = job.LastError, StoryId = job.StoryId, Outcome = job.Outcome, UpdatedAt = job.UpdatedAt,
    };
}
