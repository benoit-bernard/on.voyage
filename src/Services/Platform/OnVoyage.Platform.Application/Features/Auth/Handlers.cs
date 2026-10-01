using System.Net.Mail;
using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Auth;

public sealed record StartAnonymousSessionCommand;

public sealed record RefreshSessionCommand(string RefreshToken);

public sealed record SignOutCommand(string RefreshToken);

public sealed record GetAccountQuery(Guid TravelerId);

/// <summary>Anonymous session created at first launch, without any input (F-01).</summary>
public static class StartAnonymousSessionHandler
{
    public static async Task<Result<AuthSessionDto>> Handle(
        StartAnonymousSessionCommand command, IAccountStore accounts, IRefreshTokenStore refreshTokens, ITokenIssuer tokens,
        ICredentialService credentials, IAuthSettingsProvider settingsProvider, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var account = Account.NewAnonymous(Guid.CreateVersion7(), now);
        await accounts.SaveAsync(account, cancellationToken);
        var settings = await settingsProvider.GetAsync(cancellationToken);
        return Result.Success(await SessionIssuer.IssueAsync(account, null, settings, now, tokens, credentials, refreshTokens, cancellationToken));
    }
}

public static class RefreshSessionHandler
{
    private const string Invalid = "invalid_refresh_token";

    /// <summary>Single-use refresh tokens: each refresh rotates the token; replaying a used token revokes the whole family.</summary>
    public static async Task<Result<AuthSessionDto>> Handle(
        RefreshSessionCommand command, IAccountStore accounts, IRefreshTokenStore refreshTokens, ITokenIssuer tokens,
        ICredentialService credentials, IAuthSettingsProvider settingsProvider, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || command.RefreshToken.Length > 256)
        {
            return Result.Failure<AuthSessionDto>(Invalid, "The refresh token is not valid.");
        }

        var now = clock.GetUtcNow();
        var record = await refreshTokens.FindByHashAsync(credentials.HashRefreshToken(command.RefreshToken), cancellationToken);
        if (record is null || record.RevokedAt is not null || record.ExpiresAt <= now)
        {
            return Result.Failure<AuthSessionDto>(Invalid, "The refresh token is not valid.");
        }

        if (record.UsedAt is not null || !await refreshTokens.TryMarkUsedAsync(record.Id, now, cancellationToken))
        {
            await refreshTokens.RevokeFamilyAsync(record.FamilyId, now, cancellationToken);
            return Result.Failure<AuthSessionDto>(Invalid, "The refresh token is not valid.");
        }

        var account = await accounts.FindAsync(record.AccountId, cancellationToken);
        if (account is null || account.ReplacedBy is not null)
        {
            await refreshTokens.RevokeFamilyAsync(record.FamilyId, now, cancellationToken);
            return Result.Failure<AuthSessionDto>(Invalid, "The refresh token is not valid.");
        }

        account = account with { LastActiveAt = now };
        await accounts.SaveAsync(account, cancellationToken);
        var settings = await settingsProvider.GetAsync(cancellationToken);
        return Result.Success(await SessionIssuer.IssueAsync(account, record.FamilyId, settings, now, tokens, credentials, refreshTokens, cancellationToken));
    }
}

public static class SignOutHandler
{
    public static async Task<Result<bool>> Handle(SignOutCommand command, IRefreshTokenStore refreshTokens, ICredentialService credentials, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.RefreshToken) && command.RefreshToken.Length <= 256)
        {
            var record = await refreshTokens.FindByHashAsync(credentials.HashRefreshToken(command.RefreshToken), cancellationToken);
            if (record is not null)
            {
                await refreshTokens.RevokeFamilyAsync(record.FamilyId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return Result.Success(true);
    }
}

public static class GetAccountHandler
{
    public static async Task<Result<AccountDto>> Handle(GetAccountQuery query, IAccountStore accounts, CancellationToken cancellationToken)
    {
        var account = await accounts.FindAsync(query.TravelerId, cancellationToken);
        return account is null || account.ReplacedBy is not null
            ? Result.Failure<AccountDto>("account_not_found", "Account not found.")
            : Result.Success(new AccountDto(account.Id, account.IsAnonymous, account.Email, account.Roles, account.CreatedAt));
    }
}

internal static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>Trimmed, lower-cased e-mail, or <c>null</c> when it is not a plain address.</summary>
    public static string? Normalize(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return null;
        }

        return MailAddress.TryCreate(candidate, out var parsed) && parsed.Address == candidate && parsed.Host.Contains('.', StringComparison.Ordinal) ? candidate : null;
    }
}
