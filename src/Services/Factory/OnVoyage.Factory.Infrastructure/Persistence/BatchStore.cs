using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application;
using OnVoyage.Factory.Application.Features.Batches;
using OnVoyage.Factory.Domain.Content;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed partial class BatchStore(IDbContextOutbox<FactoryDbContext> outbox, TimeProvider clock) : IBatchStore
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
        List<BatchProgress> progress = [];
        foreach (var row in batches)
        {
            var batch = ToBatch(row);
            var list = jobs[row.Id].Select(ToJob).ToList();
            var until = list.Count > 0 && list.All(job => job.State is not (JobState.Pending or JobState.Running)) ? list.Max(job => job.UpdatedAt) : (DateTimeOffset?)null;
            progress.Add(BatchAdminHandler.Progress(batch, list, await CostAsync(batch, until, cancellationToken)));
        }

        return progress;
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
        var cancelled = nameof(JobState.Cancelled);
        var rows = await Db.GenerationJobs.Where(job => job.BatchId == batchId && (job.State == failed || job.State == cancelled)).ToListAsync(cancellationToken);
        await RequeueAsync(rows, cancellationToken);
        return rows.Count;
    }

    public async Task<bool> RetryJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var failed = nameof(JobState.Failed);
        var cancelled = nameof(JobState.Cancelled);
        var rows = await Db.GenerationJobs.Where(job => job.Id == jobId && (job.State == failed || job.State == cancelled)).ToListAsync(cancellationToken);
        await RequeueAsync(rows, cancellationToken);
        return rows.Count > 0;
    }

    public async Task<int> CancelPendingAsync(Guid batchId, string reason, CancellationToken cancellationToken)
    {
        var pending = nameof(JobState.Pending);
        var now = clock.GetUtcNow();
        return await Db.GenerationJobs.Where(job => job.BatchId == batchId && job.State == pending)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.State, nameof(JobState.Cancelled)).SetProperty(job => job.Step, "cancelled").SetProperty(job => job.LastError, reason).SetProperty(job => job.UpdatedAt, now), cancellationToken);
    }

    public async Task<double> CostAsync(GenerationBatch batch, DateTimeOffset? until, CancellationToken cancellationToken)
    {
        var end = until ?? clock.GetUtcNow();
        return await Db.LlmCalls.AsNoTracking().Where(call => call.CreatedAt >= batch.CreatedAt && call.CreatedAt <= end).SumAsync(call => (double?)call.CostUsd, cancellationToken) ?? 0d;
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken cancellationToken)
    {
        // Wolverine's own table (schema of this service).
        var rows = new List<(Guid Id, string Type, DateTimeOffset? At, string? ExceptionType, string? ExceptionMessage)>();
        var connection = Db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "select id, message_type, coalesce(sent_at, execution_time), exception_type, exception_message from factory.wolverine_dead_letters order by coalesce(sent_at, execution_time) desc nulls last limit @limit";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "limit";
            parameter.Value = limit;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }

        var jobIds = rows.Select(row => JobIdOf(row.ExceptionMessage)).Where(id => id is not null).Select(id => id!.Value).Distinct().ToArray();
        var jobs = await Db.GenerationJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id)).ToDictionaryAsync(job => job.Id, cancellationToken);
        return [.. rows.Select(row =>
        {
            var jobId = JobIdOf(row.ExceptionMessage);
            jobs.TryGetValue(jobId ?? Guid.Empty, out var job);
            return new DeadLetterEntry(row.Id, ShortType(row.Type), row.At, row.ExceptionType, row.ExceptionMessage is { Length: > 500 } message ? message[..500] : row.ExceptionMessage, job?.Id, job?.BatchId, job?.PlaceName);
        })];
    }

    private static string ShortType(string type) => type[(type.LastIndexOf('.') + 1)..];

    /// <summary>The queue keeps the message as binary; the exhausted-job error names its job (<c>Job {id} failed n times.</c>), which is what links the entry to a batch.</summary>
    private static Guid? JobIdOf(string? exceptionMessage)
    {
        var match = exceptionMessage is null ? null : JobIdPattern().Match(exceptionMessage);
        return match is { Success: true } && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bJob ([0-9a-fA-F-]{36})\b")]
    private static partial System.Text.RegularExpressions.Regex JobIdPattern();

    private async Task RequeueAsync(List<GenerationJobRow> rows, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var row in rows)
        {
            row.State = nameof(JobState.Pending);
            row.Step = "queued";
            row.Attempts = 0;
            row.LastError = null;
            row.UpdatedAt = now;
            await outbox.PublishAsync(new RunBatchJobCommand(row.Id));
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    private static GenerationBatch ToBatch(GenerationBatchRow row) =>
        new(row.Id, row.CreatedAt, row.CreatedBy, JsonSerializer.Deserialize<BatchCriteria>(row.Criteria, Json)!, row.Total);

    private static GenerationJob ToJob(GenerationJobRow row) => new(
        row.Id, row.BatchId, row.PlaceId, row.PlaceName, row.Lang, Enum.Parse<StoryKind>(row.Kind), Enum.Parse<JobState>(row.State), row.Step, row.Attempts, row.LastError, row.StoryId, row.Outcome, row.UpdatedAt);

    private static GenerationJobRow ToRow(GenerationJob job) => new()
    {
        Id = job.Id,
        BatchId = job.BatchId,
        PlaceId = job.PlaceId,
        PlaceName = job.PlaceName,
        Lang = job.Lang,
        Kind = job.Kind.ToString(),
        State = job.State.ToString(),
        Step = job.Step,
        Attempts = job.Attempts,
        LastError = job.LastError,
        StoryId = job.StoryId,
        Outcome = job.Outcome,
        UpdatedAt = job.UpdatedAt,
    };
}
