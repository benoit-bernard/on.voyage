using Microsoft.EntityFrameworkCore;
using Npgsql;
using OnVoyage.Platform.Application.Features.Audit;
using OnVoyage.Platform.Contracts;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class AdminAuditStore(PlatformDbContext db) : IAdminAuditStore
{
    public async Task<bool> AddAsync(AdminActionRecordedV1 action, CancellationToken cancellationToken)
    {
        if (await db.AdminAudit.AnyAsync(row => row.EventId == action.EventId, cancellationToken))
        {
            return false;
        }

        db.AdminAudit.Add(new AdminAuditRow
        {
            EventId = action.EventId, At = action.OccurredAt, Service = action.Service, Actor = action.Actor, Action = action.Action,
            Target = action.Target, Status = action.Status, Summary = action.Summary,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same event raced: the other one won.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<IReadOnlyList<AdminActionDto>> ListAsync(int limit, string? service, string? actor, CancellationToken cancellationToken)
    {
        var query = db.AdminAudit.AsNoTracking();
        if (service is not null)
        {
            query = query.Where(row => row.Service == service);
        }

        if (actor is not null)
        {
            query = query.Where(row => row.Actor == actor);
        }

        return [.. (await query.OrderByDescending(row => row.At).Take(limit).ToListAsync(cancellationToken))
            .Select(row => new AdminActionDto(row.EventId, row.At, row.Service, row.Actor, row.Action, row.Target, row.Status, row.Summary))];
    }
}
