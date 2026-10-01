using OnVoyage.Platform.Contracts;

namespace OnVoyage.App.Core.Auth;

/// <summary>A failed call to the identity API. <c>Code</c> is the stable Problem Details type suffix (<c>otp_expired</c>, …).</summary>
public sealed record AuthFailure(string Code, string Message, int? RetryAfterSeconds = null)
{
    public const string Network = "network";

    public const string InvalidRefreshToken = "invalid_refresh_token";
}

public sealed record AuthResult<T>
{
    internal AuthResult(T? value, AuthFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public AuthFailure? Failure { get; }

    public bool IsSuccess => Failure is null;
}

public static class AuthResult
{
    public static AuthResult<T> Success<T>(T value) => new(value, null);

    public static AuthResult<T> Fail<T>(AuthFailure failure) => new(default, failure);
}

/// <summary>Identity API of the Platform service, reached through the Gateway. Methods that need a session take the access token explicitly.</summary>
public interface IAuthClient
{
    Task<AuthResult<AuthSessionDto>> StartAnonymousAsync(CancellationToken cancellationToken);

    Task<AuthResult<AuthSessionDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken);

    Task<AuthResult<bool>> RequestCodeAsync(string email, string? accessToken, CancellationToken cancellationToken);

    Task<AuthResult<AuthSessionDto>> VerifyCodeAsync(string email, string code, string? accessToken, CancellationToken cancellationToken);

    Task SignOutAsync(string refreshToken, CancellationToken cancellationToken);
}

/// <summary>Where the session tokens live on the device (SecureStorage on mobile, localStorage in the PWA).</summary>
public interface ISessionStore
{
    Task<AuthSessionDto?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AuthSessionDto session, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

public interface ISessionProvider
{
    /// <summary>The current session, creating an anonymous one on first use (F-01: no input needed).</summary>
    Task<AuthSessionDto> EnsureSessionAsync(CancellationToken cancellationToken);

    /// <summary>A valid access token, refreshed when it is about to expire.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached access token so the next call refreshes (used after a 401).</summary>
    void InvalidateAccessToken();
}

public sealed class InMemorySessionStore : ISessionStore
{
    private AuthSessionDto? _session;

    public Task<AuthSessionDto?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_session);

    public Task SaveAsync(AuthSessionDto session, CancellationToken cancellationToken)
    {
        _session = session;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        _session = null;
        return Task.CompletedTask;
    }
}
