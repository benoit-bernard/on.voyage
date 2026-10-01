using OnVoyage.Platform.Contracts;
using OnVoyage.Platform.Domain;

namespace OnVoyage.Platform.Application.Ports;

public interface IRemoteConfigStore
{
    Task<IReadOnlyList<RemoteConfigEntry>> ListAsync(CancellationToken cancellationToken);

    Task<RemoteConfigEntry?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every version ever published for a key, newest first (history screen, T-408).</summary>
    Task<IReadOnlyList<RemoteConfigEntry>> HistoryAsync(string key, CancellationToken cancellationToken);

    /// <summary>Persists the new version, its history row and the integration event in one transaction (outbox).</summary>
    Task SaveAsync(RemoteConfigEntry entry, ConfigChangedV1 changed, CancellationToken cancellationToken);
}

public interface IFeatureFlagStore
{
    Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(FeatureFlag flag, CancellationToken cancellationToken);
}

public interface IConsentStore
{
    Task<IReadOnlyList<Consent>> ListAsync(Guid travelerId, CancellationToken cancellationToken);

    /// <summary>Persists the consent and the integration event in one transaction (outbox).</summary>
    Task SaveAsync(Consent consent, ConsentChangedV1 changed, CancellationToken cancellationToken);
}

// Identity ports.

public interface IAccountStore
{
    Task<Account?> FindAsync(Guid id, CancellationToken cancellationToken);

    Task<Account?> FindByEmailAsync(string email, CancellationToken cancellationToken);

    Task SaveAsync(Account account, CancellationToken cancellationToken);
}

public interface IOtpStore
{
    /// <summary>Most recent challenge for this e-mail, consumed or not.</summary>
    Task<OtpChallenge?> LatestAsync(string email, CancellationToken cancellationToken);

    /// <summary>Most recent challenge that has not been consumed.</summary>
    Task<OtpChallenge?> FindActiveAsync(string email, CancellationToken cancellationToken);

    Task<int> CountSinceAsync(string email, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Adds the new challenge and consumes every previous unconsumed one for the same e-mail.</summary>
    Task AddAsync(OtpChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken);

    Task SaveAsync(OtpChallenge challenge, CancellationToken cancellationToken);
}

public interface IRefreshTokenStore
{
    Task AddAsync(RefreshTokenRecord record, CancellationToken cancellationToken);

    Task<RefreshTokenRecord?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Atomically marks the token as used; false when it already was (a replay).</summary>
    Task<bool> TryMarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);

    Task RevokeAllForAccountAsync(Guid accountId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface ITokenIssuer
{
    (string Token, DateTimeOffset ExpiresAt) IssueAccessToken(Account account, TimeSpan lifetime, DateTimeOffset now);
}

/// <summary>Randomness and hashing for one-time codes and refresh tokens. Secrets are stored only as keyed hashes.</summary>
public interface ICredentialService
{
    string NewOtpCode();

    string NewRefreshToken();

    string HashOtp(Guid challengeId, string email, string code);

    string HashRefreshToken(string token);

    bool FixedTimeEquals(string left, string right);
}

public interface IAuthSettingsProvider
{
    Task<AuthSettings> GetAsync(CancellationToken cancellationToken);
}

public interface IEmailSender
{
    /// <summary>Sends the sign-in code. Throws <see cref="EmailDeliveryException"/> when the provider rejects or cannot be reached.</summary>
    Task SendOtpAsync(string email, string code, TimeSpan lifetime, CancellationToken cancellationToken);
}

public sealed class EmailDeliveryException(string message, Exception? inner = null) : Exception(message, inner);
