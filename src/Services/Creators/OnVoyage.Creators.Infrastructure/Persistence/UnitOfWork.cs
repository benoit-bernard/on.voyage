using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using OnVoyage.Creators.Application.Ports;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Creators.Infrastructure.Persistence;

internal static class UniqueViolations
{
    /// <summary>The name of the unique index a database error is about, or null when it is another error.</summary>
    public static string? Of(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres ? postgres.ConstraintName ?? string.Empty : null;
}

/// <summary>
/// Saves what the repositories staged together with the integration events (Wolverine transactional outbox): the change and its events
/// are committed together, then the events are sent.
/// </summary>
internal sealed class CreatorsUnitOfWork(IDbContextOutbox<CreatorsDbContext> outbox) : ICreatorsUnitOfWork
{
    private IDbContextTransaction? _transaction;

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var db = outbox.DbContext;
        _transaction ??= await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.Of(exception) is { } constraint)
        {
            await DisposeTransactionAsync();
            throw new UniqueConflictException(constraint);
        }
    }

    public async Task CommitAsync(IReadOnlyList<object> events, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var integrationEvent in events)
            {
                await outbox.PublishAsync(integrationEvent);
            }

            await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
            if (_transaction is not null && outbox.DbContext.Database.CurrentTransaction is not null)
            {
                await _transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException exception) when (UniqueViolations.Of(exception) is { } constraint)
        {
            throw new UniqueConflictException(constraint);
        }
        finally
        {
            await DisposeTransactionAsync();
        }
    }

    private async Task DisposeTransactionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }
    }
}
