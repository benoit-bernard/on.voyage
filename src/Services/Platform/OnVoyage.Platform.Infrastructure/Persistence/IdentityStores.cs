using Microsoft.EntityFrameworkCore;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Infrastructure.Persistence;

internal sealed class AccountStore(PlatformDbContext db) : IAccountStore
{
    public async Task<Account?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Id == id, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task<Account?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(account => account.Email == email, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task SaveAsync(Account account, CancellationToken cancellationToken)
    {
        var row = await db.Accounts.FirstOrDefaultAsync(existing => existing.Id == account.Id, cancellationToken);
        if (row is null)
        {
            row = new AccountRow { Id = account.Id, CreatedAt = account.CreatedAt };
            db.Accounts.Add(row);
        }

        row.Email = account.Email;
        row.EmailVerifiedAt = account.EmailVerifiedAt;
        row.Roles = [.. account.Roles];
        row.LastActiveAt = account.LastActiveAt;
        row.ReplacedBy = account.ReplacedBy;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Account ToDomain(AccountRow row) => new(row.Id, row.Email, row.EmailVerifiedAt, row.Roles, row.CreatedAt, row.LastActiveAt, row.ReplacedBy);
}

internal sealed class OtpStore(PlatformDbContext db) : IOtpStore
{
    public async Task<OtpChallenge?> LatestAsync(string email, CancellationToken cancellationToken) =>
        Map(await db.OtpChallenges.AsNoTracking().Where(row => row.Email == email).OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(cancellationToken));

    public async Task<OtpChallenge?> FindActiveAsync(string email, CancellationToken cancellationToken) =>
        Map(await db.OtpChallenges.AsNoTracking().Where(row => row.Email == email && row.ConsumedAt == null).OrderByDescending(row => row.CreatedAt).FirstOrDefaultAsync(cancellationToken));

    public Task<int> CountSinceAsync(string email, DateTimeOffset since, CancellationToken cancellationToken) =>
        db.OtpChallenges.CountAsync(row => row.Email == email && row.CreatedAt >= since, cancellationToken);

    public async Task AddAsync(OtpChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.OtpChallenges.Where(row => row.Email == challenge.Email && row.ConsumedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ConsumedAt, now), cancellationToken);
        db.OtpChallenges.Add(new OtpChallengeRow
        {
            Id = challenge.Id,
            Email = challenge.Email,
            CodeHash = challenge.CodeHash,
            CreatedAt = challenge.CreatedAt,
            ExpiresAt = challenge.ExpiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(OtpChallenge challenge, CancellationToken cancellationToken) =>
        await db.OtpChallenges.Where(row => row.Id == challenge.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Attempts, challenge.Attempts).SetProperty(row => row.ConsumedAt, challenge.ConsumedAt), cancellationToken);

    private static OtpChallenge? Map(OtpChallengeRow? row) =>
        row is null ? null : new OtpChallenge(row.Id, row.Email, row.CodeHash, row.CreatedAt, row.ExpiresAt, row.Attempts, row.ConsumedAt);
}

internal sealed class RefreshTokenStore(PlatformDbContext db) : IRefreshTokenStore
{
    public async Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken)
    {
        db.RefreshTokens.Add(new RefreshTokenRow
        {
            Id = record.Id,
            AccountId = record.AccountId,
            FamilyId = record.FamilyId,
            TokenHash = record.TokenHash,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(token => token.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : new RefreshTokenRecord(row.Id, row.AccountId, row.FamilyId, row.TokenHash, row.CreatedAt, row.ExpiresAt, row.UsedAt, row.RevokedAt);
    }

    public async Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(row => row.Id == id && row.UsedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.UsedAt, now), cancellationToken) == 1;

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(row => row.FamilyId == familyId && row.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAt, now), cancellationToken);

    public async Task RevokeAllForAccountAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.RefreshTokens.Where(row => row.AccountId == accountId && row.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAt, now), cancellationToken);
}
