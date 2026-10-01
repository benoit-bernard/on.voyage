using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OnVoyage.Platform.Application.Features.DataRights;
using OnVoyage.Platform.Contracts;
using OnVoyage.ServiceDefaults.Exports;
using Wolverine.EntityFrameworkCore;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class DataRightsStore(IDbContextOutbox<PlatformDbContext> outbox) : IDataRightsStore
{
    private PlatformDbContext Db => outbox.DbContext;

    public async Task<DeletionRecord?> FindDeletionAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var row = await Db.DeletionRequests.AsNoTracking().FirstOrDefaultAsync(r => r.TravelerId == travelerId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var acks = await Db.DeletionAcks.AsNoTracking().Where(a => a.TravelerId == travelerId).Select(a => a.Service).ToListAsync(cancellationToken);
        return new DeletionRecord(row.TravelerId, row.RequestedAt, row.RequiredServices, acks);
    }

    public async Task StartDeletionAsync(DeletionRecord record, IReadOnlyList<TravelerDeletionRequestedV1> events, CancellationToken cancellationToken)
    {
        Db.DeletionRequests.Add(new DeletionRequestRow { TravelerId = record.TravelerId, RequestedAt = record.RequestedAt, RequiredServices = [.. record.RequiredServices] });
        foreach (var message in events)
        {
            await outbox.PublishAsync(message);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task<DeletionRecord?> AcknowledgeAsync(Guid travelerId, string service, CancellationToken cancellationToken)
    {
        if (!await Db.DeletionRequests.AnyAsync(r => r.TravelerId == travelerId, cancellationToken))
        {
            return null; // a late or repeated answer after completion
        }

        if (!await Db.DeletionAcks.AnyAsync(a => a.TravelerId == travelerId && a.Service == service, cancellationToken))
        {
            Db.DeletionAcks.Add(new DeletionAckRow { TravelerId = travelerId, Service = service, At = DateTimeOffset.UtcNow });
            await Db.SaveChangesAsync(cancellationToken);
        }

        return await FindDeletionAsync(travelerId, cancellationToken);
    }

    public async Task CompleteDeletionAsync(Guid travelerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(cancellationToken);
        var accounts = await Db.Accounts.AsNoTracking().Where(a => a.Id == travelerId || a.ReplacedBy == travelerId).Select(a => new { a.Id, a.Email }).ToListAsync(cancellationToken);
        var ids = accounts.Select(a => a.Id).ToArray();
        var emails = accounts.Where(a => a.Email != null).Select(a => a.Email!).ToArray();
        var services = await Db.DeletionRequests.Where(r => r.TravelerId == travelerId).Select(r => r.RequiredServices.Length).FirstOrDefaultAsync(cancellationToken);

        await Db.RefreshTokens.Where(t => ids.Contains(t.AccountId)).ExecuteDeleteAsync(cancellationToken);
        await Db.OtpChallenges.Where(o => emails.Contains(o.Email)).ExecuteDeleteAsync(cancellationToken);
        await Db.Consents.Where(c => ids.Contains(c.TravelerId)).ExecuteDeleteAsync(cancellationToken);

        // The journal of administrator actions stays (SEC-10) but no longer names a deleted account.
        var asText = ids.Select(i => i.ToString()).ToArray();
        await Db.AdminAudit.Where(a => asText.Contains(a.Actor)).ExecuteUpdateAsync(set => set.SetProperty(a => a.Actor, "deleted-account"), cancellationToken);

        var exports = await Db.ExportRequests.Where(e => ids.Contains(e.TravelerId)).Select(e => e.Id).ToListAsync(cancellationToken);
        await Db.ExportParts.Where(p => exports.Contains(p.ExportId)).ExecuteDeleteAsync(cancellationToken);
        await Db.ExportRequests.Where(e => ids.Contains(e.TravelerId)).ExecuteDeleteAsync(cancellationToken);
        await Db.DeletionAcks.Where(a => a.TravelerId == travelerId).ExecuteDeleteAsync(cancellationToken);
        await Db.DeletionRequests.Where(r => r.TravelerId == travelerId).ExecuteDeleteAsync(cancellationToken);
        await Db.Accounts.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);

        Db.DeletionLog.Add(new DeletionLogRow { CompletedAt = now, ServiceCount = services });
        await Db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ExportRecord?> FindExportAsync(Guid exportId, CancellationToken cancellationToken)
    {
        var row = await Db.ExportRequests.AsNoTracking().FirstOrDefaultAsync(e => e.Id == exportId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var parts = await Db.ExportParts.AsNoTracking().Where(p => p.ExportId == exportId).OrderBy(p => p.Service).Select(p => new ExportPart(p.Service, p.Path)).ToListAsync(cancellationToken);
        return new ExportRecord(row.Id, row.TravelerId, row.RequestedAt, row.ExpiresAt, row.RequiredServices, parts);
    }

    public async Task StartExportAsync(ExportRecord record, IReadOnlyList<TravelerExportRequestedV1> events, CancellationToken cancellationToken)
    {
        Db.ExportRequests.Add(new ExportRequestRow { Id = record.Id, TravelerId = record.TravelerId, RequestedAt = record.RequestedAt, ExpiresAt = record.ExpiresAt, RequiredServices = [.. record.RequiredServices] });
        foreach (var message in events)
        {
            await outbox.PublishAsync(message);
        }

        await outbox.SaveChangesAndFlushMessagesAsync(cancellationToken);
    }

    public async Task<ExportRecord?> RecordPartAsync(Guid exportId, string service, string path, CancellationToken cancellationToken)
    {
        if (await Db.ExportRequests.AnyAsync(e => e.Id == exportId, cancellationToken)
            && !await Db.ExportParts.AnyAsync(p => p.ExportId == exportId && p.Service == service, cancellationToken))
        {
            Db.ExportParts.Add(new ExportPartRow { ExportId = exportId, Service = service, Path = path, At = DateTimeOffset.UtcNow });
            await Db.SaveChangesAsync(cancellationToken);
        }

        return await FindExportAsync(exportId, cancellationToken);
    }

    public async Task<string> ExportOwnDataAsync(Guid travelerId, CancellationToken cancellationToken)
    {
        var account = await Db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == travelerId, cancellationToken);
        var consents = await Db.Consents.AsNoTracking().Where(c => c.TravelerId == travelerId).OrderBy(c => c.Kind).Select(c => new { c.Kind, c.Granted, c.TextVersion, c.UpdatedAt }).ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            account = account is null ? null : new { account.Id, account.Email, account.EmailVerifiedAt, account.Roles, account.CreatedAt, account.LastActiveAt },
            consents,
        });
    }

    public async Task<IReadOnlyList<Guid>> DeleteExpiredExportsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await Db.ExportRequests.Where(e => e.ExpiresAt <= now).Select(e => e.Id).ToListAsync(cancellationToken);
        if (expired.Count > 0)
        {
            await Db.ExportParts.Where(p => expired.Contains(p.ExportId)).ExecuteDeleteAsync(cancellationToken);
            await Db.ExportRequests.Where(e => expired.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return expired;
    }

    public async Task<IReadOnlyList<Guid>> FindInactiveAnonymousAccountsAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken) =>
        await Db.Accounts.AsNoTracking().Where(a => a.EmailVerifiedAt == null && a.LastActiveAt < cutoff && a.ReplacedBy == null).OrderBy(a => a.LastActiveAt).Select(a => a.Id).Take(limit).ToListAsync(cancellationToken);
}

internal sealed class ExportFiles(ExportStorage storage) : IExportFiles
{
    public Task<string> ReadAsync(string path, CancellationToken cancellationToken) => storage.ReadAsync(path, cancellationToken);

    public void Delete(Guid exportId) => storage.Delete(exportId);
}
