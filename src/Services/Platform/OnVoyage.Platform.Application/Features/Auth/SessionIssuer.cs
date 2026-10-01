using OnVoyage.Platform.Application.Ports;
using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Features.Auth;

/// <summary>Creates the access/refresh token pair of a session and records the refresh token.</summary>
internal static class SessionIssuer
{
    public static async Task<AuthSessionDto> IssueAsync(
        Account account, Guid? familyId, AuthSettings settings, DateTimeOffset now,
        ITokenIssuer tokens, ICredentialService credentials, IRefreshTokenStore refreshTokens, CancellationToken cancellationToken)
    {
        var (accessToken, accessExpires) = tokens.IssueAccessToken(account, settings.AccessTokenLifetime, now);
        var refreshToken = credentials.NewRefreshToken();
        var refreshExpires = now + settings.RefreshTokenLifetime;

        await refreshTokens.AddAsync(
            new RefreshTokenRecord(Guid.CreateVersion7(), account.Id, familyId ?? Guid.CreateVersion7(), credentials.HashRefreshToken(refreshToken), now, refreshExpires, null, null),
            cancellationToken);

        return new AuthSessionDto(accessToken, accessExpires, refreshToken, refreshExpires, account.Id, account.IsAnonymous, account.Email, account.Roles);
    }
}
