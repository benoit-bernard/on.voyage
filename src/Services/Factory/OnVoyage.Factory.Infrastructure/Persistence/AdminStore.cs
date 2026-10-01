using Microsoft.EntityFrameworkCore;
using OnVoyage.Factory.Application.Content;
using OnVoyage.Factory.Application.Features.Admin;
using OnVoyage.Factory.Domain.Content;

namespace OnVoyage.Factory.Infrastructure.Persistence;

internal sealed class AdminStore(FactoryDbContext db) : IAdminStore
{
    public async Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        db.AuditLog.Add(new AuditLogRow { Id = entry.Id, At = entry.At, Actor = entry.Actor, Action = entry.Action, Target = entry.Target, Status = entry.Status, Detail = entry.Detail });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit, string? actor, CancellationToken cancellationToken)
    {
        var query = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(actor))
        {
            query = query.Where(row => row.Actor == actor);
        }

        return [.. (await query.OrderByDescending(row => row.At).Take(limit).ToListAsync(cancellationToken))
            .Select(row => new AuditEntry(row.Id, row.At, row.Actor, row.Action, row.Target, row.Status, row.Detail))];
    }

    public async Task<IReadOnlyList<PronunciationEntry>> ListPronunciationsAsync(string destination, CancellationToken cancellationToken) =>
        [.. (await db.Pronunciations.AsNoTracking().Where(row => row.DestinationSlug == destination).OrderBy(row => row.Term).ToListAsync(cancellationToken))
            .Select(row => new PronunciationEntry(row.DestinationSlug, row.Term, row.Replacement))];

    public async Task UpsertPronunciationAsync(PronunciationEntry entry, CancellationToken cancellationToken)
    {
        var row = await db.Pronunciations.FirstOrDefaultAsync(item => item.DestinationSlug == entry.Destination && item.Term == entry.Term, cancellationToken);
        if (row is null)
        {
            db.Pronunciations.Add(new PronunciationRow { DestinationSlug = entry.Destination, Term = entry.Term, Replacement = entry.Replacement });
        }
        else
        {
            row.Replacement = entry.Replacement;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeletePronunciationAsync(string destination, string term, CancellationToken cancellationToken) =>
        await db.Pronunciations.Where(row => row.DestinationSlug == destination && row.Term == term).ExecuteDeleteAsync(cancellationToken) > 0;

    public async Task<IReadOnlyList<StoryRecord>> ListStoriesByStatusAsync(ContentStatus status, int limit, CancellationToken cancellationToken)
    {
        var name = status.ToString();
        return [.. (await db.Stories.AsNoTracking().Where(row => row.Status == name).OrderByDescending(row => row.UpdatedAt).Take(limit).ToListAsync(cancellationToken)).Select(ContentStore.ToStory)];
    }
}
