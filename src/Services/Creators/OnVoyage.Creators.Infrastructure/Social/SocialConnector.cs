using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OnVoyage.Creators.Application.Ports;
using OnVoyage.Creators.Domain;
using OnVoyage.Creators.Infrastructure.Persistence;

namespace OnVoyage.Creators.Infrastructure.Social;

/// <summary>
/// The only code that sees an OAuth token (§23.2). Tokens are encrypted with ASP.NET Core Data Protection before they reach the table, decrypted
/// for the length of one call, renewed before they expire, and never logged or returned. The authorization <c>state</c> is encrypted too: it carries
/// the creator, the platform, the PKCE verifier and a ten-minute lifetime, so it can only be completed by the creator who asked for it.
/// </summary>
internal sealed class SocialConnector(CreatorsDbContext db, IDataProtectionProvider protection, IEnumerable<ISocialProvider> providers, TimeProvider clock, ILogger<SocialConnector> logger) : ISocialConnector
{
    public const string TokenPurpose = "OnVoyage.Creators.Social.Tokens.v1";
    public const string StatePurpose = "OnVoyage.Creators.Social.State.v1";

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IDataProtector _tokens = protection.CreateProtector(TokenPurpose);
    private readonly IDataProtector _state = protection.CreateProtector(StatePurpose);

    private sealed record StatePayload(Guid Creator, string Platform, string Nonce, string? Verifier, long ExpiresAt);

    public bool IsEnabled(string platform) => Find(platform) is { Options: { Enabled: true } options } && options.IsConfigured;

    public Uri Begin(string platform, Guid creatorId)
    {
        var provider = Find(platform) ?? throw new InvalidOperationException($"Unknown platform {platform}.");
        string? verifier = null;
        string? challenge = null;
        if (provider.UsesPkce)
        {
            verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        }

        var payload = new StatePayload(creatorId, platform, Base64Url(RandomNumberGenerator.GetBytes(12)), verifier, clock.GetUtcNow().Add(StateLifetime).ToUnixTimeSeconds());
        return provider.AuthorizeUrl(_state.Protect(JsonSerializer.Serialize(payload, Json)), challenge);
    }

    public async Task<ConnectOutcome> CompleteAsync(string platform, Guid creatorId, string code, string state, CancellationToken cancellationToken)
    {
        if (Find(platform) is not { } provider || !IsEnabled(platform))
        {
            return Refused(ConnectRefusal.Disabled);
        }

        if (ReadState(state) is not { } payload || payload.Platform != platform || payload.Creator != creatorId || payload.ExpiresAt < clock.GetUtcNow().ToUnixTimeSeconds())
        {
            return Refused(ConnectRefusal.InvalidState);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Refused(ConnectRefusal.AccessDenied);
        }

        TokenSet tokens;
        SocialProfile profile;
        try
        {
            tokens = await provider.ExchangeCodeAsync(code, payload.Verifier, cancellationToken);
            profile = await provider.GetProfileAsync(tokens, cancellationToken);
        }
        catch (ProviderException exception)
        {
            logger.LogWarning("Connection to {Platform} refused by the platform: {Code}.", platform, exception.Code);
            return Refused(exception.Code is "no_channel" or "scope_missing" or "invalid_grant" ? ConnectRefusal.AccessDenied : ConnectRefusal.ProviderError);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Connection to {Platform} failed: {Error}.", platform, exception.GetType().Name);
            return Refused(ConnectRefusal.ProviderError);
        }

        if (!profile.IsProfessional)
        {
            await TryRevokeAsync(provider, tokens, cancellationToken);
            return Refused(ConnectRefusal.ProfessionalAccountRequired);
        }

        if (await db.ConnectedAccounts.AnyAsync(account => account.Platform == platform && account.ExternalUserId == profile.ExternalUserId && account.CreatorId != creatorId, cancellationToken))
        {
            await TryRevokeAsync(provider, tokens, cancellationToken);
            return Refused(ConnectRefusal.AccountInUse);
        }

        var now = clock.GetUtcNow();
        var row = await db.ConnectedAccounts.FirstOrDefaultAsync(account => account.CreatorId == creatorId && account.Platform == platform, cancellationToken);
        if (row is null)
        {
            row = new ConnectedAccountRow { Id = Guid.CreateVersion7(), CreatorId = creatorId, Platform = platform, CreatedAt = now };
            db.ConnectedAccounts.Add(row);
        }

        row.ExternalUserId = profile.ExternalUserId;
        row.Username = profile.Username;
        row.AccessTokenProtected = _tokens.Protect(tokens.AccessToken);
        row.RefreshTokenProtected = tokens.RefreshToken is { } refresh ? _tokens.Protect(refresh) : row.RefreshTokenProtected;
        row.ExpiresAt = tokens.ExpiresAt;
        row.Scopes = [.. tokens.Scopes];
        row.Status = ConnectionStatuses.Active;
        row.LastError = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return Refused(ConnectRefusal.AccountInUse);
        }

        return new ConnectOutcome(ConnectRefusal.None, row.ToDomain());
    }

    public async Task<RemoteFetch> FetchAsync(Guid connectedAccountId, int limit, CancellationToken cancellationToken)
    {
        var row = await db.ConnectedAccounts.FindAsync([connectedAccountId], cancellationToken);
        if (row is null || Find(row.Platform) is not { } provider)
        {
            return RemoteFetch.Failed(FetchStatus.Unavailable, "unknown_account");
        }

        if (row.AccessTokenProtected is null)
        {
            return RemoteFetch.Failed(FetchStatus.NeedsReauth, "no_token");
        }

        try
        {
            var tokens = Unprotect(row);
            if (row.ExpiresAt is { } expiry && expiry - clock.GetUtcNow() < provider.RefreshMargin)
            {
                tokens = await provider.RefreshAsync(tokens, cancellationToken);
                row.AccessTokenProtected = _tokens.Protect(tokens.AccessToken);
                row.RefreshTokenProtected = tokens.RefreshToken is { } refresh ? _tokens.Protect(refresh) : row.RefreshTokenProtected;
                row.ExpiresAt = tokens.ExpiresAt;
                await db.SaveChangesAsync(cancellationToken);
            }

            return await provider.ListAsync(tokens, row.ExternalUserId, limit, cancellationToken);
        }
        catch (ProviderException exception) when (exception.ReauthorizationRequired)
        {
            return RemoteFetch.Failed(FetchStatus.NeedsReauth, exception.Code);
        }
        catch (ProviderException exception)
        {
            logger.LogWarning("Import from {Platform} failed: {Code}.", row.Platform, exception.Code);
            return RemoteFetch.Failed(FetchStatus.Unavailable, exception.Code);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Import from {Platform} failed: {Error}.", row.Platform, exception.GetType().Name);
            return RemoteFetch.Failed(FetchStatus.Unavailable, "network");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring that encrypted the tokens is gone: nothing but a new authorization works.
            logger.LogError("The tokens of connected account {AccountId} cannot be decrypted.", connectedAccountId);
            return RemoteFetch.Failed(FetchStatus.NeedsReauth, "tokens_unreadable");
        }
    }

    public async Task RevokeAsync(Guid connectedAccountId, CancellationToken cancellationToken)
    {
        var row = await db.ConnectedAccounts.AsNoTracking().FirstOrDefaultAsync(account => account.Id == connectedAccountId, cancellationToken);
        if (row?.AccessTokenProtected is null || Find(row.Platform) is not { } provider)
        {
            return;
        }

        try
        {
            await TryRevokeAsync(provider, Unprotect(row), cancellationToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            logger.LogWarning("Tokens of connected account {AccountId} cannot be decrypted: nothing to revoke.", connectedAccountId);
        }
    }

    private async Task TryRevokeAsync(ISocialProvider provider, TokenSet tokens, CancellationToken cancellationToken)
    {
        try
        {
            await provider.RevokeAsync(tokens, cancellationToken);
        }
        catch (Exception exception) when (exception is ProviderException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Revocation at the platform is best effort: the tokens are deleted here in any case.
            logger.LogWarning("Revocation at {Platform} failed: {Error}.", provider.Platform, exception.GetType().Name);
        }
    }

    private TokenSet Unprotect(ConnectedAccountRow row) =>
        new(_tokens.Unprotect(row.AccessTokenProtected!), row.RefreshTokenProtected is { } refresh ? _tokens.Unprotect(refresh) : null, row.ExpiresAt, row.Scopes);

    private StatePayload? ReadState(string state)
    {
        try
        {
            return JsonSerializer.Deserialize<StatePayload>(_state.Unprotect(state), Json);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    private ISocialProvider? Find(string platform) => providers.FirstOrDefault(provider => provider.Platform == platform);

    private static ConnectOutcome Refused(ConnectRefusal refusal) => new(refusal, null);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
